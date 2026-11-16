using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

[Collection(ApiCollection.Name)]
public class PaymentFlowTests(ApiFixture api)
{
    private static string Evt() => ApiFixture.NewEventId();

    [Fact]
    public async Task Invoice_is_built_from_items_and_plan_price()
    {
        var s = await api.SeedAsync(price: 50_000);
        var inv = await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}");
        Assert.Equal("Open", inv.Status);
        Assert.Equal(50_000, inv.TotalMinor);

        var custom = await api.PostAsync<InvoiceDto>("/api/invoices", new
        {
            organizationId = s.OrgId, currency = "RUB",
            items = new[] { new { description = "Setup", quantity = 2, unitPriceMinor = 1_500L }, new { description = "Seats", quantity = 3, unitPriceMinor = 1_000L } }
        });
        Assert.Equal(6_000, custom.TotalMinor);
        Assert.Equal(2, custom.Items.Count);
    }

    [Fact]
    public async Task Payment_attempt_is_idempotent_by_key()
    {
        var s = await api.SeedAsync();
        var key = Guid.NewGuid().ToString();
        var a1 = await api.NewAttemptAsync(s.InvoiceId, key);
        var a2 = await api.NewAttemptAsync(s.InvoiceId, key);
        Assert.Equal(a1.Id, a2.Id);
        Assert.Equal(a1.ProviderPaymentId, a2.ProviderPaymentId);
    }

    [Fact]
    public async Task Confirmed_payment_activates_subscription_and_grants_access()
    {
        var s = await api.SeedAsync(periodDays: 30);
        Assert.False((await api.AccessAsync(s)).HasAccess);

        var res = await api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));

        Assert.Equal("Paid", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        var sub = await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}");
        Assert.Equal("Active", sub.Status);
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);

        var access = await api.AccessAsync(s);
        Assert.True(access.HasAccess);
        Assert.Equal(sub.CurrentPeriodEnd, access.ExpiresAt);
    }

    [Fact]
    public async Task Failed_payment_keeps_invoice_open_and_a_new_attempt_can_succeed()
    {
        var s = await api.SeedAsync();
        var res = await api.SendWebhookAsync(Evt(), "payment.failed", s.PaymentId, s.Amount);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));
        Assert.Equal("Open", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        Assert.False((await api.AccessAsync(s)).HasAccess);

        var second = await api.NewAttemptAsync(s.InvoiceId);
        await api.SendWebhookAsync(Evt(), "payment.succeeded", second.ProviderPaymentId, s.Amount);
        Assert.Equal("Paid", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        Assert.True((await api.AccessAsync(s)).HasAccess);
    }

    // ---- signature ----------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_signatures_are_rejected_and_nothing_is_stored()
    {
        var s = await api.SeedAsync();

        var wrongSecret = Evt();
        var r1 = await api.SendWebhookAsync(wrongSecret, "payment.succeeded", s.PaymentId, s.Amount, secret: "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);

        var stale = Evt();
        var r2 = await api.SendWebhookAsync(stale, "payment.succeeded", s.PaymentId, s.Amount, at: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);

        var tampered = Evt();
        var signed = ApiFixture.BuildBody(tampered, "payment.succeeded", s.PaymentId, 1);
        var r3 = await api.SendRawAsync(ApiFixture.BuildBody(tampered, "payment.succeeded", s.PaymentId, s.Amount), signedBody: signed);
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);

        var stored = await api.Db(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == wrongSecret || e.ProviderEventId == stale || e.ProviderEventId == tampered));
        Assert.Equal(0, stored);
        Assert.False((await api.AccessAsync(s)).HasAccess);
    }

    // ---- idempotency / concurrency ------------------------------------------------------------------

    [Fact]
    public async Task Redelivered_event_is_a_noop()
    {
        var s = await api.SeedAsync();
        var evt = Evt();
        var first = await api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);
        var endAfterFirst = (await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}")).CurrentPeriodEnd;
        var second = await api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount);

        Assert.Equal("processed", await ApiFixture.StatusOf(first));
        Assert.Equal("duplicate", await ApiFixture.StatusOf(second));
        Assert.Equal(endAfterFirst, (await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}")).CurrentPeriodEnd);
        Assert.Equal(1, await api.Db(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == evt)));
    }

    [Fact]
    public async Task Concurrent_deliveries_of_the_same_event_apply_exactly_once()
    {
        var s = await api.SeedAsync();
        var evt = Evt();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => api.SendWebhookAsync(evt, "payment.succeeded", s.PaymentId, s.Amount)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var statuses = await Task.WhenAll(responses.Select(ApiFixture.StatusOf));
        Assert.Equal(1, statuses.Count(x => x == "processed"));
        Assert.Equal(19, statuses.Count(x => x == "duplicate"));

        Assert.Equal(1, await api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        var sub = await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}");
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        Assert.Equal(1, await api.Db(db => db.ProductAccesses.CountAsync(a => a.OrganizationId == s.OrgId)));
    }

    [Fact]
    public async Task Concurrent_distinct_events_for_the_same_payment_apply_once()
    {
        var s = await api.SeedAsync();
        var ids = Enumerable.Range(0, 10).Select(_ => Evt()).ToList();

        var responses = await Task.WhenAll(ids.Select(id => api.SendWebhookAsync(id, "payment.succeeded", s.PaymentId, s.Amount)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(10, await api.Db(db => db.WebhookEvents.CountAsync(e => ids.Contains(e.ProviderEventId) && e.Status == WebhookEventStatus.Processed)));
        var sub = await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}");
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public async Task Two_payments_for_one_invoice_confirmed_concurrently_activate_once()
    {
        var s = await api.SeedAsync();
        var second = await api.NewAttemptAsync(s.InvoiceId);

        var responses = await Task.WhenAll(
            api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount),
            api.SendWebhookAsync(Evt(), "payment.succeeded", second.ProviderPaymentId, s.Amount));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await api.CountAuditAsync("InvoicePaid", s.InvoiceId.ToString()));
        Assert.Equal(1, await api.CountAuditAsync("DuplicatePayment", s.InvoiceId.ToString())); // flagged for manual refund
        var sub = await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}");
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1); // not 60
    }

    // ---- refunds ------------------------------------------------------------------------------------

    [Fact]
    public async Task Full_refund_cancels_subscription_and_revokes_access()
    {
        var s = await api.SeedAsync();
        await api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);
        Assert.True((await api.AccessAsync(s)).HasAccess);

        var refundId = "re_" + Guid.NewGuid().ToString("N");
        var res = await api.SendWebhookAsync(Evt(), "refund.succeeded", s.PaymentId, s.Amount, refundId);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));

        Assert.Equal("Refunded", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        Assert.Equal("Cancelled", (await api.GetAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}")).Status);
        Assert.False((await api.AccessAsync(s)).HasAccess);

        // Same refund re-sent under a new event id: harmless.
        var again = await api.SendWebhookAsync(Evt(), "refund.succeeded", s.PaymentId, s.Amount, refundId);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
    }

    [Fact]
    public async Task Partial_refund_is_rejected_without_touching_state()
    {
        var s = await api.SeedAsync();
        await api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount);

        var evt = Evt();
        var res = await api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, s.Amount / 2, "re_" + Guid.NewGuid().ToString("N"));
        Assert.Equal("rejected", await ApiFixture.StatusOf(res));

        Assert.True((await api.AccessAsync(s)).HasAccess);
        Assert.Equal("Paid", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        var ev = await api.Db(db => db.WebhookEvents.SingleAsync(e => e.ProviderEventId == evt));
        Assert.Equal(WebhookEventStatus.Rejected, ev.Status);
        Assert.Contains("full refunds", ev.LastError);
    }

    [Fact]
    public async Task Amount_mismatch_is_rejected_and_does_not_activate()
    {
        var s = await api.SeedAsync();
        var res = await api.SendWebhookAsync(Evt(), "payment.succeeded", s.PaymentId, s.Amount - 1);
        Assert.Equal("rejected", await ApiFixture.StatusOf(res));
        Assert.Equal("Open", (await api.GetAsync<InvoiceDto>($"/api/invoices/{s.InvoiceId}")).Status);
        Assert.False((await api.AccessAsync(s)).HasAccess);
    }
}
