using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

[Collection(ApiCollection.Name)]
public class RecoveryTests(ApiFixture api)
{
    private readonly bool _reset = Reset(api);
    private static bool Reset(ApiFixture a) { a.ResetFaults(); return true; }

    private static string Evt() => ApiFixture.NewEventId();

    [Fact]
    public async Task Refund_that_arrives_before_the_payment_is_deferred_and_applied_by_recovery()
    {
        var s = await api.SeedAsync();
        var refundEvt = Evt();
        var refundId = "re_" + Guid.NewGuid().ToString("N");

        // 1. Out-of-order: refund first. Stored, 202, nothing applied.
        var r1 = await api.SendWebhookAsync(refundEvt, "refund.succeeded", s.PaymentId, s.Amount, refundId);
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == refundEvt));
        Assert.Equal(WebhookEventStatus.Failed, ev.Status);
        Assert.Equal(1, ev.Attempts);
        Assert.NotNull(ev.NextAttemptAt);

        // 2. Recovery while the payment is still unconfirmed: still deferred, attempts grow.
        await api.RecoverAsync(DateTimeOffset.UtcNow.AddHours(1));
        ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == refundEvt));
        Assert.Equal(WebhookEventStatus.Failed, ev.Status);
        Assert.Equal(2, ev.Attempts);

        // 3. The payment is confirmed, then recovery finally applies the refund.
        await api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);
        Assert.True((await api.AccessAsync(s)).HasAccess);
        await api.RecoverAsync(DateTimeOffset.UtcNow.AddHours(1));

        ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == refundEvt));
        Assert.Equal(WebhookEventStatus.Processed, ev.Status);
        Assert.Equal("Refunded", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        Assert.False((await api.AccessAsync(s)).HasAccess);
    }

    [Fact]
    public async Task Event_stored_but_never_processed_is_picked_up_by_recovery()
    {
        // Simulates a crash between "event persisted" and "event processed".
        var s = await api.SeedAsync();
        var evt = Evt();
        var now = DateTimeOffset.UtcNow;
        await api.Db(async db =>
        {
            db.WebhookEvents.Add(new WebhookEvent
            {
                ProviderEventId = evt, Type = "payment.succeeded", PaymentId = s.PaymentId,
                Payload = ApiFixture.BuildBody(evt, "payment.succeeded", s.PaymentId, s.Amount),
                Status = WebhookEventStatus.Received, ReceivedAt = now.AddMinutes(-10), NextAttemptAt = now.AddMinutes(-9)
            });
            return await db.SaveChangesAsync();
        });
        Assert.False((await api.AccessAsync(s)).HasAccess);

        var attempted = await api.RecoverAsync(DateTimeOffset.UtcNow);

        Assert.True(attempted >= 1);
        Assert.True((await api.AccessAsync(s)).HasAccess);
        var ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == evt));
        Assert.Equal(WebhookEventStatus.Processed, ev.Status);
        Assert.NotNull(ev.ProcessedAt);
    }

    [Fact]
    public async Task Event_for_an_unknown_payment_ends_up_in_dead_letter_after_max_attempts()
    {
        var evt = Evt();
        var r = await api.SendWebhookAsync(evt, "payment.succeeded", "tp_does_not_exist", 1000);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        for (var i = 0; i < 10; i++) await api.RecoverAsync(DateTimeOffset.UtcNow.AddDays(1));

        var ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == evt));
        Assert.Equal(WebhookEventStatus.DeadLetter, ev.Status);
        Assert.Equal(5, ev.Attempts); // Billing:MaxWebhookAttempts default
        Assert.Null(ev.NextAttemptAt);
        Assert.True(await api.Db(db => db.AuditLogs.AnyAsync(a => a.Action == "WebhookDeadLettered" && a.EntityId == evt)));
    }
}
