using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Double-crediting protection and diagnostics of conflicting events.</summary>
[Collection(ApiCollection.Name)]
public class WebhookIntegrityTests
{
    private readonly ApiFixture _api;

    public WebhookIntegrityTests(ApiFixture api)
    {
        _api = api;
        api.ResetFaults();
    }

    private static string Evt() => ApiFixture.NewEventId();

    [Fact]
    public async Task Same_payment_under_many_event_ids_credits_the_invoice_once()
    {
        var s = await _api.SeedAsync();
        for (var i = 0; i < 4; i++)
        {
            var res = await _api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(s.Amount, inv.PaidMinor);
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        var attemptId = (await _api.AttemptDb(s.PaymentId)).Id.ToString();
        Assert.Equal(3, await _api.CountAuditAsync("PaymentAlreadyApplied", attemptId)); // events 2..4 were harmless no-ops
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Concurrent_confirmations_of_one_attempt_under_different_event_ids_credit_once()
    {
        var s = await _api.SeedAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _api.PayAsync(s.PaymentId, s.Amount)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(s.Amount, inv.PaidMinor);
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(PaymentAttemptStatus.Succeeded, attempt.Status);
        Assert.True(attempt.AppliedToInvoice);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await _api.Db(db => db.ProductAccesses.CountAsync(a => a.OrganizationId == s.OrgId)));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Event_id_reused_with_different_content_is_not_applied_and_is_recorded_as_a_conflict()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        var failed = await _api.SendWebhookAsync(evt, "payment.failed", s.PaymentId, s.Amount);
        Assert.Equal("processed", await ApiFixture.StatusOf(failed));
        var originalHash = (await _api.EventDb(evt)).PayloadHash;

        // Same event id, but now claiming success: must not move any money.
        var forged = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Equal("conflict", await ApiFixture.StatusOf(forged));

        var stored = await _api.EventDb(evt);
        Assert.Equal("payment.failed", stored.Type);
        Assert.Equal(originalHash, stored.PayloadHash);
        Assert.Equal(WebhookEventStatus.Processed, stored.Status);

        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(PaymentAttemptStatus.Failed, attempt.Status);
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(0, inv.PaidMinor);
        Assert.Equal(InvoiceStatus.Open, inv.Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);

        // Diagnostics: the conflict is stored (once, even if the forged delivery is repeated) and exposed via the admin API.
        await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        var anomalies = await _api.Db(db => db.Anomalies.Where(a => a.Kind == "EventPayloadConflict" && a.EntityId == evt).ToListAsync());
        var anomaly = Assert.Single(anomalies);
        Assert.Contains(originalHash, anomaly.Details);
        var viaApi = await _api.Client.GetStringAsync("/api/admin/anomalies?kind=EventPayloadConflict");
        Assert.Contains(evt, viaApi);
    }

    [Fact]
    public async Task Identical_redelivery_is_not_reported_as_a_conflict()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        var again = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);

        Assert.Equal("duplicate", await ApiFixture.StatusOf(again));
        Assert.False(await _api.Db(db => db.Anomalies.AnyAsync(a => a.EntityId == evt)));
        Assert.Equal(1, await _api.Db(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == evt)));
    }

    [Fact]
    public async Task Success_after_full_refund_neither_recredits_nor_reactivates()
    {
        var s = await _api.SeedAsync();
        await _api.PayAsync(s.PaymentId, s.Amount);
        await _api.RefundAsync(s.PaymentId, s.Amount);
        var subBefore = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Cancelled, subBefore.Status);

        var late = await _api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);

        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Refunded, inv.Status);
        Assert.Equal(s.Amount, inv.PaidMinor);
        Assert.Equal(s.Amount, inv.RefundedMinor);
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(PaymentAttemptStatus.Refunded, attempt.Status);
        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Equal(subBefore.CurrentPeriodEnd, sub.CurrentPeriodEnd);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Late_failure_after_success_does_not_downgrade_the_payment()
    {
        var s = await _api.SeedAsync();
        await _api.PayAsync(s.PaymentId, s.Amount);
        var res = await _api.SendWebhookAsync(Evt(), "payment.failed", s.PaymentId, s.Amount);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));

        Assert.Equal(PaymentAttemptStatus.Succeeded, (await _api.AttemptDb(s.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.Paid, (await _api.InvoiceDb(s.InvoiceId)).Status);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Rejected_and_conflicting_events_are_available_for_diagnostics()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        var res = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount - 1);
        Assert.Equal("rejected", await ApiFixture.StatusOf(res));

        var anomaly = await _api.Db(db => db.Anomalies.SingleAsync(a => a.Kind == "WebhookRejected" && a.EntityId == evt));
        Assert.Contains("mismatch", anomaly.Details);
        Assert.Contains(evt, await _api.Client.GetStringAsync("/api/admin/anomalies?kind=WebhookRejected"));
        Assert.Contains(evt, await _api.Client.GetStringAsync("/api/admin/webhook-events?status=Rejected"));

        // Redelivering the rejected event is stable: still rejected, still one anomaly.
        var again = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount - 1);
        Assert.Equal("rejected", await ApiFixture.StatusOf(again));
        Assert.Equal(1, await _api.Db(db => db.Anomalies.CountAsync(a => a.Kind == "WebhookRejected" && a.EntityId == evt)));
        Assert.Equal(0, (await _api.InvoiceDb(s.InvoiceId)).PaidMinor);
    }
}
