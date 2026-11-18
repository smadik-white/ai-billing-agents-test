using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Infrastructure;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Billing.IntegrationTests;

[CollectionDefinition("billing", DisableParallelization = true)]
public sealed class BillingCollection : ICollectionFixture<BillingApiFixture> { }

public sealed class BillingApiFixture : IAsyncLifetime
{
    public const string HmacSecret = "integration-test-secret-change-me";
    public const string OpsKey = "integration-ops-key";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("billing_tests")
        .WithUsername("billing")
        .WithPassword("billing_test_password")
        .Build();

    private WebApplicationFactory<Program>? _factory;
    private readonly FaultingIdempotentPaymentProvider _paymentProvider = new();
    private readonly OneShotWebhookFaultInjector _webhookFaultInjector = new();

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;
    public FaultingIdempotentPaymentProvider PaymentProvider => _paymentProvider;
    public OneShotWebhookFaultInjector WebhookFaultInjector => _webhookFaultInjector;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:BillingDb"] = _postgres.GetConnectionString(),
                    ["Payments:HmacSecret"] = HmacSecret,
                    ["Payments:CheckoutBaseUrl"] = "https://checkout.test/pay",
                    ["Ops:ApiKey"] = OpsKey,
                    ["Recovery:PollingIntervalSeconds"] = "300",
                    ["PaymentRecovery:PollingIntervalSeconds"] = "300",
                    ["SubscriptionExpiry:PollingIntervalSeconds"] = "300"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPaymentProvider>();
                services.AddSingleton<IPaymentProvider>(_paymentProvider);
                services.RemoveAll<IWebhookFaultInjector>();
                services.AddSingleton<IWebhookFaultInjector>(_webhookFaultInjector);
            });
        });
        _factory = factory;
        Client = factory.CreateClient();
    }

    public async Task ResetDatabaseAsync()
    {
        _paymentProvider.Reset();
        _webhookFaultInjector.Reset();
        var factory = Services.GetRequiredService<IDbContextFactory<BillingDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public async Task<Scenario> CreateScenarioAsync(decimal amount = 49.90m, int billingPeriodDays = 30, bool createInitialPayment = true)
    {
        var organizationResponse = await Client.PostAsJsonAsync("/api/organizations", new { name = "Acme Ltd" });
        organizationResponse.EnsureSuccessStatusCode();
        using var organizationJson = JsonDocument.Parse(await organizationResponse.Content.ReadAsStringAsync());
        var organizationId = organizationJson.RootElement.GetProperty("id").GetGuid();

        var subscriptionResponse = await Client.PostAsJsonAsync($"/api/organizations/{organizationId}/subscriptions",
            new CreateSubscriptionRequest("analytics-pro", amount, "EUR", billingPeriodDays));
        subscriptionResponse.EnsureSuccessStatusCode();
        using var subscriptionJson = JsonDocument.Parse(await subscriptionResponse.Content.ReadAsStringAsync());
        var subscriptionId = subscriptionJson.RootElement.GetProperty("id").GetGuid();

        var invoiceId = await CreateInvoiceAsync(subscriptionId, amount);
        var attemptId = createInitialPayment
            ? await CreatePaymentAttemptAsync(invoiceId, $"scenario-{Guid.NewGuid():N}")
            : Guid.Empty;
        return new Scenario(organizationId, subscriptionId, invoiceId, attemptId, amount);
    }

    public async Task<Guid> CreateInvoiceAsync(Guid subscriptionId, decimal amount)
    {
        var invoiceResponse = await Client.PostAsJsonAsync($"/api/subscriptions/{subscriptionId}/invoices",
            new CreateInvoiceRequest([new InvoiceLineRequest("Billing period", 1, amount)]));
        invoiceResponse.EnsureSuccessStatusCode();
        using var invoiceJson = JsonDocument.Parse(await invoiceResponse.Content.ReadAsStringAsync());
        return invoiceJson.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<Guid> CreatePaymentAttemptAsync(Guid invoiceId, string idempotencyKey, decimal? amount = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            amount is null
                ? $"/api/invoices/{invoiceId}/payment-attempts"
                : $"/api/invoices/{invoiceId}/payment-attempts?amount={amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement.TryGetProperty("paymentAttempt", out var nested) ? nested : json.RootElement;
        return body.GetProperty("id").GetGuid();
    }

    public async Task<HttpResponseMessage> SendWebhookAsync(
        string eventId,
        string eventType,
        Guid paymentId,
        decimal amount,
        string currency = "EUR",
        string? refundId = null)
    {
        var message = new ProviderWebhook(eventId, eventType, paymentId, amount, currency,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            eventType == "payment.refunded" ? refundId ?? eventId : refundId);
        return await SendWebhookPayloadAsync(message);
    }

    public async Task<HttpResponseMessage> SendWebhookPayloadAsync(ProviderWebhook message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(HmacSecret), payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/test-provider")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Test-Signature", "sha256=" + Convert.ToHexString(digest).ToLowerInvariant());
        return await Client.SendAsync(request);
    }

    public async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public async Task<T> WithDbAsync<T>(Func<BillingDbContext, Task<T>> action)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<BillingDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await action(db);
    }

    public async Task StopDatabaseAsync() => await _postgres.StopAsync();
    public async Task StartDatabaseAsync() => await _postgres.StartAsync();
}

public sealed class FaultingIdempotentPaymentProvider : IPaymentProvider
{
    private readonly ConcurrentDictionary<Guid, ProviderPayment> _payments = new();
    private int _failAfterCreate;
    private int _callCount;
    private long _nextDelayTicks;

    public string Name => "test-provider";
    public int CallCount => Volatile.Read(ref _callCount);
    public int DistinctPaymentCount => _payments.Count;

    public void FailNextAfterCreate() => Interlocked.Exchange(ref _failAfterCreate, 1);
    public void DelayNextFor(TimeSpan delay) => Interlocked.Exchange(ref _nextDelayTicks, delay.Ticks);

    public async Task<ProviderPayment> CreatePaymentAsync(Guid idempotencyKey, decimal amount, string currency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);
        var payment = _payments.GetOrAdd(idempotencyKey, id =>
            new ProviderPayment(id.ToString("D"), $"https://checkout.test/pay/{id:D}"));
        var delayTicks = Interlocked.Exchange(ref _nextDelayTicks, 0);
        if (delayTicks > 0)
            await Task.Delay(TimeSpan.FromTicks(delayTicks), cancellationToken);
        if (Interlocked.Exchange(ref _failAfterCreate, 0) == 1)
            throw new IOException("Simulated response loss after provider-side create.");
        return payment;
    }

    public void Reset()
    {
        _payments.Clear();
        Interlocked.Exchange(ref _failAfterCreate, 0);
        Interlocked.Exchange(ref _callCount, 0);
        Interlocked.Exchange(ref _nextDelayTicks, 0);
    }
}

public sealed class OneShotWebhookFaultInjector : IWebhookFaultInjector
{
    private int _shouldFail;
    public void FailNext() => Interlocked.Exchange(ref _shouldFail, 1);
    public void Reset() => Interlocked.Exchange(ref _shouldFail, 0);

    public Task AfterBusinessMutationAsync(Guid webhookEventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _shouldFail, 0) == 1)
            throw new IOException("Injected process crash after SaveChanges and before transaction commit.");
        return Task.CompletedTask;
    }
}

public sealed record Scenario(Guid OrganizationId, Guid SubscriptionId, Guid InvoiceId, Guid PaymentId, decimal Amount);
