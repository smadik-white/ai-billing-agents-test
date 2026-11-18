using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Billing.Api.Data;
using Billing.Api.Payments;
using Billing.Api.Services;
using Billing.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public sealed record SubSeed(Guid OrgId, Guid SubId, string ProductCode, string PlanCode);
public sealed record Seed(Guid OrgId, Guid SubId, Guid InvoiceId, string ProductCode, string PaymentId, long Amount);

/// <summary>One real PostgreSQL (Testcontainers) + one in-process API shared by all tests.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Secret = "test-webhook-secret";
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public FakePaymentProvider Provider { get; } = new();
    public DbFaultInterceptor DbFault { get; } = new();
    public ScriptedProcessingHook Hook { get; } = new();
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
            b.UseSetting("Billing:ProviderTimeoutSeconds", "2");
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPaymentProvider>(Provider);
                services.AddSingleton<IWebhookProcessingHook>(Hook);
                services.AddSingleton<IInterceptor>(DbFault);
            });
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

    /// <summary>Resets all fault injection; call from every test class constructor.</summary>
    public void ResetFaults()
    {
        Provider.Reset();
        Hook.Reset();
        DbFault.Down = false;
    }

    public async Task<SubSeed> NewSubscriptionAsync(long price = 99_000, int periodDays = 30)
    {
        var code = Guid.NewGuid().ToString("N")[..8];
        var product = $"product-{code}";
        var plan = await PostAsync<PlanDto>("/api/plans", new { code = $"plan-{code}", name = "Pro", productCode = product, priceMinor = price, currency = "RUB", periodDays });
        var org = await PostAsync<IdDto>("/api/organizations", new { name = $"Org {code}", email = $"{code}@example.com" });
        var sub = await PostAsync<IdDto>("/api/subscriptions", new { organizationId = org.Id, planCode = plan.Code });
        return new SubSeed(org.Id, sub.Id, product, plan.Code);
    }

    /// <summary>Invoice for the subscription's plan price (initial purchase or renewal).</summary>
    public Task<InvoiceDto> NewInvoiceAsync(SubSeed s) =>
        PostAsync<InvoiceDto>("/api/invoices", new { organizationId = s.OrgId, subscriptionId = s.SubId });

    /// <summary>Plan + organization + Pending subscription + Open invoice + one Pending full-amount payment attempt.</summary>
    public async Task<Seed> SeedAsync(long price = 99_000, int periodDays = 30)
    {
        var s = await NewSubscriptionAsync(price, periodDays);
        var inv = await NewInvoiceAsync(s);
        var attempt = await NewAttemptAsync(inv.Id);
        return new Seed(s.OrgId, s.SubId, inv.Id, s.ProductCode, attempt.ProviderPaymentId, inv.TotalMinor);
    }

    public Task<AttemptDto> NewAttemptAsync(Guid invoiceId, string? idempotencyKey = null, long? amountMinor = null) =>
        PostAsync<AttemptDto>($"/api/invoices/{invoiceId}/payment-attempts", new { amountMinor }, idempotencyKey);

    public Task<HttpResponseMessage> PostRawAsync(string url, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return Client.SendAsync(req);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<T>(await r.Content.ReadAsStringAsync(), J)!;

    public Task<AccessDto> AccessAsync(Seed s) => GetAsync<AccessDto>($"/api/organizations/{s.OrgId}/access/{s.ProductCode}");
    public Task<AccessDto> AccessAsync(SubSeed s) => GetAsync<AccessDto>($"/api/organizations/{s.OrgId}/access/{s.ProductCode}");

    public Task<HttpResponseMessage> PayAsync(string paymentId, long amount) =>
        SendWebhookAsync(NewEventId(), "payment.succeeded", paymentId, amount);

    public Task<HttpResponseMessage> RefundAsync(string paymentId, long amount, string? refundId = null) =>
        SendWebhookAsync(NewEventId(), "refund.succeeded", paymentId, amount, refundId ?? "re_" + Guid.NewGuid().ToString("N"));

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

    public Task<int> ResumeStaleCreationsAsync(DateTimeOffset createdBefore) =>
        Factory.Services.GetRequiredService<BillingService>().ResumeStaleCreationsAsync(createdBefore, CancellationToken.None);

    public Task<Subscription> SubDb(Guid id) => Db(d => d.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == id));
    public Task<Invoice> InvoiceDb(Guid id) => Db(d => d.Invoices.AsNoTracking().SingleAsync(x => x.Id == id));
    public Task<List<PaymentAttempt>> AttemptsDb(Guid invoiceId) => Db(d => d.PaymentAttempts.AsNoTracking().Where(x => x.InvoiceId == invoiceId).ToListAsync());
    public Task<PaymentAttempt> AttemptDb(string providerPaymentId) => Db(d => d.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ProviderPaymentId == providerPaymentId));
    public Task<WebhookEvent> EventDb(string providerEventId) => Db(d => d.WebhookEvents.AsNoTracking().SingleAsync(x => x.ProviderEventId == providerEventId));

    /// <summary>
    /// Financial invariants of one invoice, verified against the database (not against API answers):
    /// paid/refunded sums equal the sums of the underlying attempts and refunds, nothing exceeds its limit,
    /// statuses are consistent with the amounts.
    /// </summary>
    public Task AssertInvoiceIntegrityAsync(Guid invoiceId) => Db(async db =>
    {
        var inv = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        var items = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == invoiceId).ToListAsync();
        var attempts = await db.PaymentAttempts.AsNoTracking().Where(a => a.InvoiceId == invoiceId).ToListAsync();
        var ids = attempts.Select(a => a.Id).ToList();
        var refunds = await db.Refunds.AsNoTracking().Where(r => ids.Contains(r.PaymentAttemptId)).ToListAsync();

        static bool Confirmed(PaymentAttempt a) => a.Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.PartiallyRefunded or PaymentAttemptStatus.Refunded;

        Assert.Equal(inv.TotalMinor, items.Sum(i => i.TotalMinor));
        Assert.Equal(attempts.Where(a => Confirmed(a) && a.AppliedToInvoice).Sum(a => a.AmountMinor), inv.PaidMinor);
        Assert.True(inv.PaidMinor <= inv.TotalMinor, $"overpaid: {inv.PaidMinor} > {inv.TotalMinor}");
        var appliedIds = attempts.Where(a => a.AppliedToInvoice).Select(a => a.Id).ToHashSet();
        Assert.Equal(refunds.Where(r => appliedIds.Contains(r.PaymentAttemptId)).Sum(r => r.AmountMinor), inv.RefundedMinor);
        Assert.True(inv.RefundedMinor <= inv.PaidMinor, $"refunded {inv.RefundedMinor} > paid {inv.PaidMinor}");
        Assert.Equal(InvoiceMath.StatusFor(inv.Status, inv.TotalMinor, inv.PaidMinor, inv.RefundedMinor), inv.Status);
        Assert.Equal(inv.PaidMinor >= inv.TotalMinor, inv.PaidAt is not null);

        foreach (var a in attempts)
        {
            var refunded = refunds.Where(r => r.PaymentAttemptId == a.Id).Sum(r => r.AmountMinor);
            Assert.Equal(refunded, a.RefundedMinor);
            Assert.True(a.AmountMinor > 0);
            Assert.True(Confirmed(a) || (refunded == 0 && !a.AppliedToInvoice), $"attempt {a.Id} is {a.Status} but has refunds/credit");
            Assert.True(a.RefundedMinor <= a.AmountMinor);
            if (a.Status == PaymentAttemptStatus.Refunded) Assert.Equal(a.AmountMinor, a.RefundedMinor);
            if (a.Status == PaymentAttemptStatus.PartiallyRefunded) Assert.InRange(a.RefundedMinor, 1, a.AmountMinor - 1);
            if (a.Status == PaymentAttemptStatus.Succeeded) Assert.Equal(0, a.RefundedMinor);
            Assert.Equal(a.Status == PaymentAttemptStatus.Creating, a.ProviderPaymentId is null);
        }
        return 0;
    });
}
