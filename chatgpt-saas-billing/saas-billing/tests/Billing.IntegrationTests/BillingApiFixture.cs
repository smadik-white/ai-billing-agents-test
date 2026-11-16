using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;

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
                    ["Recovery:PollingIntervalSeconds"] = "300"
                }));
        });
        _factory = factory;
        Client = factory.CreateClient();
    }

    public async Task ResetDatabaseAsync()
    {
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

    public async Task<Scenario> CreateScenarioAsync(decimal amount = 49.90m)
    {
        var organizationResponse = await Client.PostAsJsonAsync("/api/organizations", new { name = "Acme Ltd" });
        organizationResponse.EnsureSuccessStatusCode();
        using var organizationJson = JsonDocument.Parse(await organizationResponse.Content.ReadAsStringAsync());
        var organizationId = organizationJson.RootElement.GetProperty("id").GetGuid();

        var subscriptionResponse = await Client.PostAsJsonAsync($"/api/organizations/{organizationId}/subscriptions",
            new CreateSubscriptionRequest("analytics-pro", amount, "EUR", 30));
        subscriptionResponse.EnsureSuccessStatusCode();
        using var subscriptionJson = JsonDocument.Parse(await subscriptionResponse.Content.ReadAsStringAsync());
        var subscriptionId = subscriptionJson.RootElement.GetProperty("id").GetGuid();

        var invoiceResponse = await Client.PostAsJsonAsync($"/api/subscriptions/{subscriptionId}/invoices",
            new CreateInvoiceRequest([new InvoiceLineRequest("Analytics Pro — 30 days", 1, amount)]));
        invoiceResponse.EnsureSuccessStatusCode();
        using var invoiceJson = JsonDocument.Parse(await invoiceResponse.Content.ReadAsStringAsync());
        var invoiceId = invoiceJson.RootElement.GetProperty("id").GetGuid();

        var attemptResponse = await Client.PostAsync($"/api/invoices/{invoiceId}/payment-attempts", content: null);
        attemptResponse.EnsureSuccessStatusCode();
        using var attemptJson = JsonDocument.Parse(await attemptResponse.Content.ReadAsStringAsync());
        var attemptId = attemptJson.RootElement.GetProperty("id").GetGuid();

        return new Scenario(organizationId, subscriptionId, invoiceId, attemptId, amount);
    }

    public async Task<HttpResponseMessage> SendWebhookAsync(
        string eventId, string eventType, Guid paymentId, decimal amount, string currency = "EUR")
    {
        var message = new ProviderWebhook(eventId, eventType, paymentId, amount, currency, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
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
}

public sealed record Scenario(Guid OrganizationId, Guid SubscriptionId, Guid InvoiceId, Guid PaymentId, decimal Amount);
