using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Sweeps the whole database (everything every other test left behind) for violated financial invariants.</summary>
[Collection(ApiCollection.Name)]
public class GlobalIntegrityTests(ApiFixture api)
{
    [Fact]
    public async Task Every_invoice_in_the_database_satisfies_the_money_invariants()
    {
        var ids = await api.Db(db => db.Invoices.AsNoTracking().Select(i => i.Id).ToListAsync());
        foreach (var id in ids) await api.AssertInvoiceIntegrityAsync(id);
    }

    [Fact]
    public async Task Subscriptions_and_access_grants_are_consistent()
    {
        await api.Db(async db =>
        {
            var subs = await db.Subscriptions.AsNoTracking().ToListAsync();
            foreach (var s in subs)
            {
                if (s.Status == SubscriptionStatus.Active)
                {
                    Assert.NotNull(s.CurrentPeriodStart);
                    Assert.NotNull(s.CurrentPeriodEnd);
                    Assert.True(s.CurrentPeriodEnd > s.CurrentPeriodStart);
                }
                if (s.Status == SubscriptionStatus.Pending) Assert.Null(s.ActivatedAt);
                if (s.Status == SubscriptionStatus.Cancelled) Assert.NotNull(s.CancelledAt);
            }

            // A live grant always mirrors the paid period of its subscription; it never outlives it.
            var live = await db.ProductAccesses.AsNoTracking().Where(a => a.RevokedAt == null).ToListAsync();
            foreach (var a in live)
            {
                var sub = subs.Single(s => s.Id == a.SubscriptionId);
                Assert.Equal(sub.CurrentPeriodEnd, a.ExpiresAt);
            }

            // A fully paid invoice with a subscription has exactly one activation per such invoice (never twice).
            var paid = await db.Invoices.AsNoTracking().Where(i => i.PaidAt != null && i.SubscriptionId != null).Select(i => i.Id).ToListAsync();
            foreach (var invoiceId in paid)
            {
                var key = invoiceId.ToString();
                var paidAudits = await db.AuditLogs.CountAsync(a => a.Action == "InvoicePaid" && a.EntityId == key);
                Assert.Equal(1, paidAudits);
            }

            // Each provider refund and each provider event exists once.
            Assert.Equal(await db.Refunds.CountAsync(), await db.Refunds.Select(r => r.ProviderRefundId).Distinct().CountAsync());
            Assert.Equal(await db.WebhookEvents.CountAsync(), await db.WebhookEvents.Select(e => e.ProviderEventId).Distinct().CountAsync());
            return 0;
        });
    }
}
