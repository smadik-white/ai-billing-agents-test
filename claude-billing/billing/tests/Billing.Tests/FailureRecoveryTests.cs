using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Crashes, DB outage, repeated queueing: no half-applied money operations, safe re-processing.</summary>
[Collection(ApiCollection.Name)]
public class FailureRecoveryTests
{
    private readonly ApiFixture _api;

    public FailureRecoveryTests(ApiFixture api)
    {
        _api = api;
        api.ResetFaults();
    }

    private static string Evt() => ApiFixture.NewEventId();
    private static DateTimeOffset Future() => DateTimeOffset.UtcNow.AddDays(1);

    [Theory]
    [InlineData("after-lock")]
    [InlineData("attempt-updated")]
    [InlineData("invoice-updated")]
    [InlineData("subscription-updated")]
    public async Task Crash_in_the_middle_of_a_payment_leaves_nothing_applied_and_recovery_applies_it_once(string step)
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        _api.Hook.FailOnce(step);

        var res = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        // Atomicity: whatever the handler wrote before dying was rolled back as a whole.
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(PaymentAttemptStatus.Pending, attempt.Status);
        Assert.False(attempt.AppliedToInvoice);
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Open, inv.Status);
        Assert.Equal(0, inv.PaidMinor);
        Assert.Null(inv.PaidAt);
        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Pending, sub.Status);
        Assert.Null(sub.CurrentPeriodEnd);
        Assert.Equal(0, await _api.Db(db => db.ProductAccesses.CountAsync(a => a.OrganizationId == s.OrgId)));
        Assert.Equal(0, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));

        // Data for recovery is kept: payload, error, attempt counter, next attempt time.
        var ev = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.Failed, ev.Status);
        Assert.Equal(1, ev.Attempts);
        Assert.Contains("Injected crash", ev.LastError);
        Assert.NotNull(ev.NextAttemptAt);
        Assert.Contains(s.PaymentId, ev.Payload);

        // Recovery applies it exactly once; running it again changes nothing.
        Assert.True(await _api.RecoverAsync(Future()) >= 1);
        await _api.RecoverAsync(Future());
        var again = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal("duplicate", await ApiFixture.StatusOf(again));

        Assert.Equal(WebhookEventStatus.Processed, (await _api.EventDb(evt)).Status);
        inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
        Assert.Equal(s.Amount, inv.PaidMinor);
        sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Crash_after_a_refund_was_recorded_leaves_no_partial_refund_and_recovery_applies_it_once()
    {
        var s = await _api.SeedAsync();
        await _api.PayAsync(s.PaymentId, s.Amount);
        var evt = Evt();
        var refundId = "re_" + Guid.NewGuid().ToString("N");
        _api.Hook.FailOnce("refund-recorded");

        var res = await _api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, s.Amount, refundId);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        Assert.Equal(0, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(0, attempt.RefundedMinor);
        Assert.Equal(PaymentAttemptStatus.Succeeded, attempt.Status);
        Assert.Equal(0, (await _api.InvoiceDb(s.InvoiceId)).RefundedMinor);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);

        await _api.RecoverAsync(Future());
        await _api.RecoverAsync(Future());

        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        Assert.Equal(InvoiceStatus.Refunded, (await _api.InvoiceDb(s.InvoiceId)).Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Database_outage_during_processing_keeps_the_stored_event_for_recovery()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        // The process loses the database in the middle of processing, so even the failure cannot be journaled.
        _api.Hook.FailOnce("after-lock", () => _api.DbFault.Down = true);

        var res = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode); // stored, will be retried
        _api.DbFault.Down = false;

        var ev = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.Received, ev.Status); // still queued
        Assert.Equal(0, ev.Attempts);
        Assert.Equal(InvoiceStatus.Open, (await _api.InvoiceDb(s.InvoiceId)).Status);

        Assert.True(await _api.RecoverAsync(Future()) >= 1);
        Assert.Equal(WebhookEventStatus.Processed, (await _api.EventDb(evt)).Status);
        Assert.Equal(InvoiceStatus.Paid, (await _api.InvoiceDb(s.InvoiceId)).Status);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Database_outage_before_the_event_is_stored_returns_500_and_the_redelivery_succeeds()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        _api.DbFault.Down = true;

        var failed = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); // the provider will retry
        _api.DbFault.Down = false;
        Assert.Equal(0, await _api.Db(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == evt)));

        var redelivered = await _api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal("processed", await ApiFixture.StatusOf(redelivered));
        Assert.Equal(s.Amount, (await _api.InvoiceDb(s.InvoiceId)).PaidMinor);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Event_queued_for_retry_many_times_concurrently_is_applied_once()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        var refundId = "re_" + Guid.NewGuid().ToString("N");

        // Refund arrives before the payment is confirmed: stored as Failed (retryable).
        var early = await _api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, s.Amount, refundId);
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        await _api.PayAsync(s.PaymentId, s.Amount);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _api.RecoverAsync(Future())));

        var ev = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.Processed, ev.Status);
        Assert.Equal(2, ev.Attempts); // one failed try + exactly one successful processing
        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(s.Amount, attempt.RefundedMinor);
        Assert.Equal(InvoiceStatus.Refunded, (await _api.InvoiceDb(s.InvoiceId)).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, (await _api.SubDb(s.SubId)).Status);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Dead_lettered_event_is_flagged_and_can_be_requeued_manually()
    {
        var s = await _api.SeedAsync();
        var evt = Evt();
        var refundId = "re_" + Guid.NewGuid().ToString("N");
        await _api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, s.Amount, refundId); // payment never confirmed yet
        for (var i = 0; i < 10; i++) await _api.RecoverAsync(Future());

        var dead = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.DeadLetter, dead.Status);
        Assert.Null(dead.NextAttemptAt);
        Assert.True(await _api.Db(db => db.Anomalies.AnyAsync(a => a.Kind == "WebhookDeadLettered" && a.EntityId == evt)));
        await _api.RecoverAsync(Future()); // automatic recovery no longer touches a dead-lettered event
        dead = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.DeadLetter, dead.Status);
        Assert.Equal(5, dead.Attempts);

        // Operator fixes the cause (the payment arrives) and requeues the event by hand.
        await _api.PayAsync(s.PaymentId, s.Amount);
        var requeue = await _api.Client.PostAsync($"/api/admin/webhook-events/{evt}/requeue", null);
        Assert.Equal(HttpStatusCode.OK, requeue.StatusCode);

        var ev = await _api.EventDb(evt);
        Assert.Equal(WebhookEventStatus.Processed, ev.Status);
        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        Assert.Equal(InvoiceStatus.Refunded, (await _api.InvoiceDb(s.InvoiceId)).Status);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);

        // A processed event cannot be requeued (and thus cannot be applied twice).
        var again = await _api.Client.PostAsync($"/api/admin/webhook-events/{evt}/requeue", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
    }

    [Fact]
    public async Task Stored_but_unprocessed_events_are_recovered_in_order_without_double_effects()
    {
        // Process died after storing two events of the same payment (success + refund): both must apply once.
        var s = await _api.SeedAsync();
        var payEvt = Evt();
        var refundEvt = Evt();
        var refundId = "re_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _api.Db(async db =>
        {
            foreach (var (id, type, rid, at) in new[] { (payEvt, "payment.succeeded", (string?)null, now.AddMinutes(-20)), (refundEvt, "refund.succeeded", refundId, now.AddMinutes(-10)) })
            {
                var body = ApiFixture.BuildBody(id, type, s.PaymentId, s.Amount, rid);
                db.WebhookEvents.Add(new WebhookEvent
                {
                    ProviderEventId = id, Type = type, PaymentId = s.PaymentId, Payload = body, PayloadHash = Billing.Api.Payments.PayloadHash.Of(body),
                    Status = WebhookEventStatus.Received, ReceivedAt = at, NextAttemptAt = at.AddSeconds(30)
                });
            }
            return await db.SaveChangesAsync();
        });

        await _api.RecoverAsync(DateTimeOffset.UtcNow);
        await _api.RecoverAsync(DateTimeOffset.UtcNow);

        Assert.Equal(WebhookEventStatus.Processed, (await _api.EventDb(payEvt)).Status);
        Assert.Equal(WebhookEventStatus.Processed, (await _api.EventDb(refundEvt)).Status);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(InvoiceStatus.Refunded, (await _api.InvoiceDb(s.InvoiceId)).Status);
        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }
}
