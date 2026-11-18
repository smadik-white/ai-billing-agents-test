using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Invoice of 1 000 RUB (100 000 kopecks) paid and refunded in parts.</summary>
[Collection(ApiCollection.Name)]
public class PartialPaymentsAndRefundsTests
{
    private const long Total = 100_000;
    private const long Half = 50_000;
    private readonly ApiFixture _api;

    public PartialPaymentsAndRefundsTests(ApiFixture api)
    {
        _api = api;
        api.ResetFaults();
    }

    private async Task<(SubSeed S, InvoiceDto Inv, AttemptDto A, AttemptDto B)> TwoHalvesAsync()
    {
        var s = await _api.NewSubscriptionAsync(Total);
        var inv = await _api.NewInvoiceAsync(s);
        var a = await _api.NewAttemptAsync(inv.Id, amountMinor: Half);
        var b = await _api.NewAttemptAsync(inv.Id, amountMinor: Half);
        return (s, inv, a, b);
    }

    /// <summary>Invoice of 1 000 RUB paid in full by a single payment (second tuple member is unused).</summary>
    private async Task<(Seed S, int Unused)> FullyPaidAsync()
    {
        var s = await _api.SeedAsync(price: Total);
        await _api.PayAsync(s.PaymentId, s.Amount);
        return (s, 0);
    }

    // ---- partial payments --------------------------------------------------------------------------

    [Fact]
    public async Task Two_payments_of_500_complete_an_invoice_of_1000_and_activate_only_at_the_end()
    {
        var (s, inv, a, b) = await TwoHalvesAsync();
        Assert.Equal(Half, a.AmountMinor);

        await _api.PayAsync(a.ProviderPaymentId, a.AmountMinor);
        var afterFirst = await _api.InvoiceDb(inv.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, afterFirst.Status);
        Assert.Equal(Half, afterFirst.PaidMinor);
        Assert.Null(afterFirst.PaidAt);
        Assert.Equal(SubscriptionStatus.Pending, (await _api.SubDb(s.SubId)).Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);

        await _api.PayAsync(b.ProviderPaymentId, b.AmountMinor);
        var done = await _api.InvoiceDb(inv.Id);
        Assert.Equal(InvoiceStatus.Paid, done.Status);
        Assert.Equal(Total, done.PaidMinor);
        Assert.Equal(SubscriptionStatus.Active, (await _api.SubDb(s.SubId)).Status);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", inv.Id.ToString()));
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task Repeated_confirmation_of_a_partial_payment_does_not_increase_the_paid_amount()
    {
        var (_, inv, a, _) = await TwoHalvesAsync();
        var evt = ApiFixture.NewEventId();
        await _api.SendWebhookAsync(evt, "payment.succeeded", a.ProviderPaymentId, a.AmountMinor);
        await _api.SendWebhookAsync(evt, "payment.succeeded", a.ProviderPaymentId, a.AmountMinor);      // same event
        await _api.PayAsync(a.ProviderPaymentId, a.AmountMinor);                                         // new event id
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _api.PayAsync(a.ProviderPaymentId, a.AmountMinor)));

        var row = await _api.InvoiceDb(inv.Id);
        Assert.Equal(Half, row.PaidMinor);
        Assert.Equal(InvoiceStatus.PartiallyPaid, row.Status);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task Concurrent_confirmation_of_both_halves_with_duplicates_is_counted_once_each()
    {
        var (s, inv, a, b) = await TwoHalvesAsync();
        var tasks = Enumerable.Range(0, 6).SelectMany(_ => new[]
        {
            _api.PayAsync(a.ProviderPaymentId, a.AmountMinor),
            _api.PayAsync(b.ProviderPaymentId, b.AmountMinor)
        });
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var row = await _api.InvoiceDb(inv.Id);
        Assert.Equal(Total, row.PaidMinor);
        Assert.Equal(InvoiceStatus.Paid, row.Status);
        Assert.Equal(1, await _api.CountAuditAsync("InvoicePaid", inv.Id.ToString()));
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        var sub = await _api.SubDb(s.SubId);
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task A_new_attempt_cannot_exceed_the_remaining_balance_and_defaults_to_it()
    {
        var (_, inv, a, _) = await TwoHalvesAsync();
        await _api.PayAsync(a.ProviderPaymentId, a.AmountMinor);

        var tooMuch = await _api.PostRawAsync($"/api/invoices/{inv.Id}/payment-attempts", new { amountMinor = Half + 1 });
        Assert.Equal((HttpStatusCode)422, tooMuch.StatusCode);

        var rest = await _api.NewAttemptAsync(inv.Id);
        Assert.Equal(Half, rest.AmountMinor);
    }

    [Fact]
    public async Task Surplus_payment_is_not_credited_is_flagged_and_its_refund_leaves_the_invoice_untouched()
    {
        var s = await _api.SeedAsync(price: Total);
        var second = await _api.NewAttemptAsync(s.InvoiceId); // a second full payment for the same open invoice
        await _api.PayAsync(s.PaymentId, Total);
        await _api.PayAsync(second.ProviderPaymentId, Total);

        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(Total, inv.PaidMinor); // never more than the invoice total
        var surplus = await _api.AttemptDb(second.ProviderPaymentId);
        Assert.Equal(PaymentAttemptStatus.Succeeded, surplus.Status);
        Assert.False(surplus.AppliedToInvoice);
        var surplusKey = $"surplus:{surplus.Id}";
        var anomaly = await _api.Db(db => db.Anomalies.SingleAsync(a => a.Key == surplusKey));
        Assert.Null(anomaly.ResolvedAt);
        Assert.Equal(1, await _api.CountAuditAsync("DuplicatePayment", s.InvoiceId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);

        await _api.RefundAsync(second.ProviderPaymentId, Total);
        inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
        Assert.Equal(0, inv.RefundedMinor);
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        Assert.NotNull((await _api.Db(db => db.Anomalies.SingleAsync(a => a.Id == anomaly.Id))).ResolvedAt);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    // ---- partial refunds ---------------------------------------------------------------------------

    [Fact]
    public async Task Partial_refund_delivered_twice_is_counted_once_and_keeps_the_subscription()
    {
        var (s, _) = await FullyPaidAsync();
        var evt = ApiFixture.NewEventId();
        var refundId = "re_" + Guid.NewGuid().ToString("N");

        var first = await _api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, 20_000, refundId);
        var sameEvent = await _api.SendWebhookAsync(evt, "refund.succeeded", s.PaymentId, 20_000, refundId);
        var otherEvent = await _api.SendWebhookAsync(ApiFixture.NewEventId(), "refund.succeeded", s.PaymentId, 20_000, refundId);

        Assert.Equal("processed", await ApiFixture.StatusOf(first));
        Assert.Equal("duplicate", await ApiFixture.StatusOf(sameEvent));
        Assert.Equal("processed", await ApiFixture.StatusOf(otherEvent)); // no-op, same refund id

        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(20_000, attempt.RefundedMinor);
        Assert.Equal(PaymentAttemptStatus.PartiallyRefunded, attempt.Status);
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(20_000, inv.RefundedMinor);
        Assert.Equal(Total, inv.PaidMinor);
        Assert.Equal(InvoiceStatus.PartiallyRefunded, inv.Status);
        Assert.Equal(1, await _api.Db(db => db.Refunds.CountAsync(r => r.ProviderRefundId == refundId)));
        Assert.Equal(SubscriptionStatus.Active, (await _api.SubDb(s.SubId)).Status); // rule: partial refund keeps access
        Assert.True((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Two_partial_refunds_arriving_concurrently_are_both_counted_exactly_once()
    {
        var (s, _) = await FullyPaidAsync();
        var r1 = "re_" + Guid.NewGuid().ToString("N");
        var r2 = "re_" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).SelectMany(_ => new[]
        {
            _api.RefundAsync(s.PaymentId, 30_000, r1),
            _api.RefundAsync(s.PaymentId, 40_000, r2)
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(70_000, attempt.RefundedMinor);
        Assert.Equal(2, await _api.Db(db => db.Refunds.CountAsync(r => r.PaymentAttemptId == attempt.Id)));
        Assert.Equal(70_000, (await _api.InvoiceDb(s.InvoiceId)).RefundedMinor);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Concurrent_refunds_can_never_exceed_the_paid_amount()
    {
        var (s, _) = await FullyPaidAsync();

        // 4 distinct refunds of 30 000 against 100 000 paid: exactly three can be applied.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _api.RefundAsync(s.PaymentId, 30_000)));
        var statuses = await Task.WhenAll(responses.Select(r => ApiFixture.StatusOf(r)));

        Assert.Equal(3, statuses.Count(x => x == "processed"));
        Assert.Equal(1, statuses.Count(x => x == "rejected"));
        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(90_000, attempt.RefundedMinor);
        Assert.Equal(3, await _api.Db(db => db.Refunds.CountAsync(r => r.PaymentAttemptId == attempt.Id)));
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(90_000, inv.RefundedMinor);
        Assert.True(inv.RefundedMinor <= inv.PaidMinor);
        Assert.Equal(1, await _api.Db(db => db.WebhookEvents.CountAsync(e => e.PaymentId == s.PaymentId && e.Status == WebhookEventStatus.Rejected)));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Refund_larger_than_the_paid_amount_is_rejected_atomically()
    {
        var (s, _) = await FullyPaidAsync();
        var res = await _api.RefundAsync(s.PaymentId, Total + 1);
        Assert.Equal("rejected", await ApiFixture.StatusOf(res));

        var attempt = await _api.AttemptDb(s.PaymentId);
        Assert.Equal(0, attempt.RefundedMinor);
        Assert.Equal(PaymentAttemptStatus.Succeeded, attempt.Status);
        Assert.Equal(0, (await _api.InvoiceDb(s.InvoiceId)).RefundedMinor);
        Assert.Equal(0, await _api.Db(db => db.Refunds.CountAsync(r => r.PaymentAttemptId == attempt.Id)));
        Assert.True((await _api.AccessAsync(s)).HasAccess);
    }

    [Fact]
    public async Task Cumulative_partial_refunds_up_to_the_full_amount_cancel_the_subscription_and_revoke_access()
    {
        var (s, _) = await FullyPaidAsync();
        await _api.RefundAsync(s.PaymentId, 60_000);
        Assert.True((await _api.AccessAsync(s)).HasAccess);

        await _api.RefundAsync(s.PaymentId, 40_000);
        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Refunded, inv.Status);
        Assert.Equal(Total, inv.RefundedMinor);
        Assert.Equal(PaymentAttemptStatus.Refunded, (await _api.AttemptDb(s.PaymentId)).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, (await _api.SubDb(s.SubId)).Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);

        var extra = await _api.RefundAsync(s.PaymentId, 1); // nothing left to refund
        Assert.Equal("rejected", await ApiFixture.StatusOf(extra));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Refunding_one_of_two_partial_payments_only_affects_that_payment()
    {
        var (s, inv, a, b) = await TwoHalvesAsync();
        await _api.PayAsync(a.ProviderPaymentId, a.AmountMinor);
        await _api.PayAsync(b.ProviderPaymentId, b.AmountMinor);

        await _api.RefundAsync(a.ProviderPaymentId, Half);
        var row = await _api.InvoiceDb(inv.Id);
        Assert.Equal(InvoiceStatus.PartiallyRefunded, row.Status);
        Assert.Equal(Total, row.PaidMinor);
        Assert.Equal(Half, row.RefundedMinor);
        Assert.Equal(PaymentAttemptStatus.Refunded, (await _api.AttemptDb(a.ProviderPaymentId)).Status);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await _api.AttemptDb(b.ProviderPaymentId)).Status);
        Assert.Equal(SubscriptionStatus.Active, (await _api.SubDb(s.SubId)).Status);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);

        await _api.RefundAsync(b.ProviderPaymentId, Half);
        Assert.Equal(InvoiceStatus.Refunded, (await _api.InvoiceDb(inv.Id)).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, (await _api.SubDb(s.SubId)).Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task Refunding_a_partially_paid_invoice_does_not_cancel_a_subscription_that_was_never_activated()
    {
        var (s, inv, a, _) = await TwoHalvesAsync();
        await _api.PayAsync(a.ProviderPaymentId, a.AmountMinor);
        await _api.RefundAsync(a.ProviderPaymentId, Half);

        var row = await _api.InvoiceDb(inv.Id);
        Assert.Equal(InvoiceStatus.Refunded, row.Status);
        Assert.Equal(SubscriptionStatus.Pending, (await _api.SubDb(s.SubId)).Status);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }
}
