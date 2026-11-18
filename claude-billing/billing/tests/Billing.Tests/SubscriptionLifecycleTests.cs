using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

[Collection(ApiCollection.Name)]
public class SubscriptionLifecycleTests
{
    private readonly ApiFixture _api;

    public SubscriptionLifecycleTests(ApiFixture api)
    {
        _api = api;
        api.ResetFaults();
    }

    /// <summary>Renewal invoice for the same subscription with its own full-amount attempt.</summary>
    private async Task<(InvoiceDto Inv, AttemptDto Attempt)> RenewalAsync(Seed s)
    {
        var inv = await _api.PostAsync<InvoiceDto>("/api/invoices", new { organizationId = s.OrgId, subscriptionId = s.SubId });
        return (inv, await _api.NewAttemptAsync(inv.Id));
    }

    private async Task<DateTimeOffset> PaidFirstPeriodAsync(Seed s)
    {
        await _api.PayAsync(s.PaymentId, s.Amount);
        return (await _api.SubDb(s.SubId)).CurrentPeriodEnd!.Value;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(365)]
    public async Task First_period_length_follows_the_plan(int periodDays)
    {
        var s = await _api.SeedAsync(periodDays: periodDays);
        await _api.PayAsync(s.PaymentId, s.Amount);

        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(sub.CurrentPeriodStart!.Value.AddDays(periodDays), sub.CurrentPeriodEnd);
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, periodDays - 0.1, periodDays + 0.1);
        Assert.Equal(sub.CurrentPeriodEnd, (await _api.AccessAsync(s)).ExpiresAt);
    }

    [Fact]
    public async Task Renewal_extends_the_period_from_the_current_end_by_exactly_the_plan_length()
    {
        var s = await _api.SeedAsync(periodDays: 30);
        var end1 = await PaidFirstPeriodAsync(s);

        var (inv2, att2) = await RenewalAsync(s);
        await _api.PayAsync(att2.ProviderPaymentId, att2.AmountMinor);

        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(end1, sub.CurrentPeriodStart);
        Assert.Equal(end1.AddDays(30), sub.CurrentPeriodEnd);
        Assert.Equal(sub.CurrentPeriodEnd, (await _api.AccessAsync(s)).ExpiresAt);
        Assert.Equal(2, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
        await _api.AssertInvoiceIntegrityAsync(inv2.Id);
    }

    [Fact]
    public async Task Redelivered_events_after_a_renewal_do_not_extend_the_subscription_again()
    {
        var s = await _api.SeedAsync();
        await PaidFirstPeriodAsync(s);
        var (_, att2) = await RenewalAsync(s);
        var evt = ApiFixture.NewEventId();
        await _api.SendWebhookAsync(evt, "payment.succeeded", att2.ProviderPaymentId, att2.AmountMinor);
        var endAfterRenewal = (await _api.SubDb(s.SubId)).CurrentPeriodEnd;

        await _api.SendWebhookAsync(evt, "payment.succeeded", att2.ProviderPaymentId, att2.AmountMinor);   // same event
        await _api.PayAsync(att2.ProviderPaymentId, att2.AmountMinor);                                      // other event id
        await _api.PayAsync(s.PaymentId, s.Amount);                                                         // first period again

        Assert.Equal(endAfterRenewal, (await _api.SubDb(s.SubId)).CurrentPeriodEnd);
        Assert.Equal(2, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
    }

    [Fact]
    public async Task Concurrent_payments_of_two_renewal_periods_lose_no_paid_time()
    {
        var s = await _api.SeedAsync(periodDays: 30);
        var end1 = await PaidFirstPeriodAsync(s);
        var (inv2, att2) = await RenewalAsync(s);
        var (inv3, att3) = await RenewalAsync(s);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).SelectMany(_ => new[]
        {
            _api.PayAsync(att2.ProviderPaymentId, att2.AmountMinor),
            _api.PayAsync(att3.ProviderPaymentId, att3.AmountMinor)
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(end1.AddDays(60), sub.CurrentPeriodEnd); // both periods are there, in either order
        Assert.Equal(sub.CurrentPeriodEnd, (await _api.AccessAsync(s)).ExpiresAt);
        Assert.Equal(3, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
        await _api.AssertInvoiceIntegrityAsync(inv2.Id);
        await _api.AssertInvoiceIntegrityAsync(inv3.Id);
    }

    [Fact]
    public async Task Two_first_period_invoices_paid_concurrently_give_two_periods()
    {
        var s = await _api.NewSubscriptionAsync(periodDays: 30);
        var i1 = await _api.NewInvoiceAsync(s);
        var i2 = await _api.NewInvoiceAsync(s);
        var a1 = await _api.NewAttemptAsync(i1.Id);
        var a2 = await _api.NewAttemptAsync(i2.Id);

        await Task.WhenAll(_api.PayAsync(a1.ProviderPaymentId, a1.AmountMinor), _api.PayAsync(a2.ProviderPaymentId, a2.AmountMinor));

        var sub = await _api.SubDb(s.SubId);
        Assert.InRange((sub.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 59.9, 60.1);
        Assert.Equal(2, await _api.CountAuditAsync("SubscriptionActivated", s.SubId.ToString()));
    }

    [Fact]
    public async Task Payment_after_the_previous_period_expired_starts_a_new_period_from_the_payment_time()
    {
        var s = await _api.SeedAsync(periodDays: 30);
        await PaidFirstPeriodAsync(s);

        var now = DateTimeOffset.UtcNow;
        await _api.Db(async db =>
        {
            var sub = await db.Subscriptions.SingleAsync(x => x.Id == s.SubId);
            sub.CurrentPeriodStart = now.AddDays(-40);
            sub.CurrentPeriodEnd = now.AddDays(-10);
            var access = await db.ProductAccesses.SingleAsync(a => a.OrganizationId == s.OrgId);
            access.ExpiresAt = now.AddDays(-10);
            return await db.SaveChangesAsync();
        });
        Assert.False((await _api.AccessAsync(s)).HasAccess);

        var (_, att2) = await RenewalAsync(s);
        await _api.PayAsync(att2.ProviderPaymentId, att2.AmountMinor);

        var renewed = await _api.SubDb(s.SubId);
        Assert.InRange((renewed.CurrentPeriodStart!.Value - DateTimeOffset.UtcNow).TotalSeconds, -60, 5); // starts now, not at the old end
        Assert.InRange((renewed.CurrentPeriodEnd!.Value - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        var access2 = await _api.AccessAsync(s);
        Assert.True(access2.HasAccess);
        Assert.Equal(renewed.CurrentPeriodEnd, access2.ExpiresAt);
    }

    // ---- cancellation ------------------------------------------------------------------------------

    [Fact]
    public async Task Payment_for_a_cancelled_unactivated_subscription_is_kept_but_never_activates_it()
    {
        var s = await _api.SeedAsync();
        var cancelled = await _api.PostAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}/cancel", new { });
        Assert.Equal("Cancelled", cancelled.Status);

        var res = await _api.PayAsync(s.PaymentId, s.Amount);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));

        var inv = await _api.InvoiceDb(s.InvoiceId);
        Assert.Equal(InvoiceStatus.Paid, inv.Status);       // the money is on the invoice ...
        Assert.Equal(s.Amount, inv.PaidMinor);
        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status); // ... but the subscription stays cancelled
        Assert.Null(sub.CurrentPeriodEnd);
        Assert.False((await _api.AccessAsync(s)).HasAccess);
        Assert.Equal(0, await _api.Db(db => db.ProductAccesses.CountAsync(a => a.OrganizationId == s.OrgId)));

        var anomaly = await _api.Db(db => db.Anomalies.SingleAsync(a => a.Kind == "PaymentForCancelledSubscription" && a.EntityId == s.InvoiceId.ToString()));
        Assert.Null(anomaly.ResolvedAt);
        await _api.AssertInvoiceIntegrityAsync(s.InvoiceId);
    }

    [Fact]
    public async Task Cancelled_active_subscription_keeps_paid_access_until_period_end_and_a_late_renewal_does_not_extend_it()
    {
        var s = await _api.SeedAsync();
        var end1 = await PaidFirstPeriodAsync(s);
        var (inv2, att2) = await RenewalAsync(s); // renewal invoice issued before the cancellation

        await _api.PostAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}/cancel", new { });
        Assert.True((await _api.AccessAsync(s)).HasAccess);                      // paid period is honored

        await _api.PayAsync(att2.ProviderPaymentId, att2.AmountMinor);           // webhook arrives after cancellation
        var sub = await _api.SubDb(s.SubId);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Equal(end1, sub.CurrentPeriodEnd);
        Assert.Equal(end1, (await _api.AccessAsync(s)).ExpiresAt);
        Assert.True(await _api.Db(db => db.Anomalies.AnyAsync(a => a.Kind == "PaymentForCancelledSubscription" && a.EntityId == inv2.Id.ToString())));
        await _api.AssertInvoiceIntegrityAsync(inv2.Id);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_new_invoices_for_a_cancelled_subscription_are_refused()
    {
        var s = await _api.SeedAsync();
        var first = await _api.PostAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}/cancel", new { });
        var cancelledAt = (await _api.SubDb(s.SubId)).CancelledAt;
        var second = await _api.PostAsync<SubscriptionDto>($"/api/subscriptions/{s.SubId}/cancel", new { });
        Assert.Equal("Cancelled", first.Status);
        Assert.Equal("Cancelled", second.Status);
        Assert.Equal(cancelledAt, (await _api.SubDb(s.SubId)).CancelledAt);
        Assert.Equal(1, await _api.CountAuditAsync("SubscriptionCancelledByUser", s.SubId.ToString()));

        var res = await _api.PostRawAsync("/api/invoices", new { organizationId = s.OrgId, subscriptionId = s.SubId });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
