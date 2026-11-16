using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Billing.Api.Data;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Billing.Tests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

public sealed record IdDto(Guid Id);
public sealed record PlanDto(Guid Id, string Code, string ProductCode, long PriceMinor);
public sealed record ItemDto(string Description, int Quantity, long UnitPriceMinor, long TotalMinor);
public sealed record InvoiceDto(Guid Id, string Number, string Status, long TotalMinor, List<ItemDto> Items);
public sealed record AttemptDto(Guid Id, string ProviderPaymentId, long AmountMinor, string Status);
public sealed record SubscriptionDto(Guid Id, string Status, DateTimeOffset? CurrentPeriodEnd);
public sealed record AccessDto(bool HasAccess, DateTimeOffset? ExpiresAt);
public sealed record StatusDto(string Status);
public sealed record Seed(Guid OrgId, Guid SubId, Guid InvoiceId, string ProductCode, string PaymentId, long Amount);

/// <summary>One real PostgreSQL (Testcontainers) + one in-process API shared by all tests.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Secret = "test-webhook-secret";
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Billing", _pg.GetConnectionString());
            b.UseSetting("Billing:WebhookSecret", Secret);
            b.UseSetting("Billing:RecoveryEnabled", "false"); // tests drive recovery explicitly
        });
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    // ---- HTTP helpers ------------------------------------------------------------------------------

    public async Task<T> PostAsync<T>(string url, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var res = await Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"POST {url} -> {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, J)!;
    }

    public async Task<T> GetAsync<T>(string url)
    {
        var res = await Client.GetAsync(url);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"GET {url} -> {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, J)!;
    }

    public static async Task<string> StatusOf(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<StatusDto>(await r.Content.ReadAsStringAsync(), J)!.Status;

    // ---- scenario helpers --------------------------------------------------------------------------

    /// <summary>Plan + organization + Pending subscription + Open invoice + one Pending payment attempt.</summary>
    public async Task<Seed> SeedAsync(long price = 99_000, int periodDays = 30)
    {
        var code = Guid.NewGuid().ToString("N")[..8];
        var product = $"product-{code}";
        var plan = await PostAsync<PlanDto>("/api/plans", new { code = $"plan-{code}", name = "Pro", productCode = product, priceMinor = price, currency = "RUB", periodDays });
        var org = await PostAsync<IdDto>("/api/organizations", new { name = $"Org {code}", email = $"{code}@example.com" });
        var sub = await PostAsync<IdDto>("/api/subscriptions", new { organizationId = org.Id, planCode = plan.Code });
        var inv = await PostAsync<InvoiceDto>("/api/invoices", new { organizationId = org.Id, subscriptionId = sub.Id });
        var attempt = await NewAttemptAsync(inv.Id);
        return new Seed(org.Id, sub.Id, inv.Id, product, attempt.ProviderPaymentId, inv.TotalMinor);
    }

    public Task<AttemptDto> NewAttemptAsync(Guid invoiceId, string? idempotencyKey = null) =>
        PostAsync<AttemptDto>($"/api/invoices/{invoiceId}/payment-attempts", new { }, idempotencyKey);

    public Task<AccessDto> AccessAsync(Seed s) => GetAsync<AccessDto>($"/api/organizations/{s.OrgId}/access/{s.ProductCode}");

    public static string NewEventId() => "evt_" + Guid.NewGuid().ToString("N");

    // ---- webhook helpers ---------------------------------------------------------------------------

    public static string BuildBody(string eventId, string type, string paymentId, long amount, string? refundId = null) =>
        JsonSerializer.Serialize(new { event_id = eventId, type, payment_id = paymentId, amount, currency = "RUB", refund_id = refundId });

    public Task<HttpResponseMessage> SendWebhookAsync(string eventId, string type, string paymentId, long amount,
        string? refundId = null, string secret = Secret, DateTimeOffset? at = null) =>
        SendRawAsync(BuildBody(eventId, type, paymentId, amount, refundId), secret: secret, at: at);

    /// <param name="signedBody">What the signature is computed over (differs from body in tampering tests).</param>
    public async Task<HttpResponseMessage> SendRawAsync(string body, string? signedBody = null, string secret = Secret, DateTimeOffset? at = null)
    {
        var ts = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/testpay")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add(TestPaySignature.HeaderName, TestPaySignature.BuildHeader(secret, ts, signedBody ?? body));
        return await Client.SendAsync(req);
    }

    // ---- direct DB / service access ----------------------------------------------------------------

    public async Task<T> Db<T>(Func<BillingDbContext, Task<T>> query)
    {
        var f = Factory.Services.GetRequiredService<IDbContextFactory<BillingDbContext>>();
        await using var db = await f.CreateDbContextAsync();
        return await query(db);
    }

    public Task<int> RecoverAsync(DateTimeOffset asOf) =>
        Factory.Services.GetRequiredService<WebhookProcessor>().RecoverAsync(asOf, CancellationToken.None);

    public Task<int> CountAuditAsync(string action, string entityId) =>
        Db(db => db.AuditLogs.CountAsync(a => a.Action == action && a.EntityId == entityId));
}
