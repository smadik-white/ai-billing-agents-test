using System.Text.Json;
using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

/// <summary>
/// Moves subscriptions to Suspended once every paid period has expired. Access checks must still
/// compare ValidUntilUtc with the current time; this service persists the lifecycle transition.
/// </summary>
public sealed class SubscriptionExpiryService(
    IDbContextFactory<BillingDbContext> dbFactory,
    ILogger<SubscriptionExpiryService> logger)
{
    public async Task<int> ExpireDueSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var dueIds = await readDb.Subscriptions.AsNoTracking()
            .Where(x => x.Status == SubscriptionStatus.Active &&
                        x.CurrentPeriodEndUtc != null && x.CurrentPeriodEndUtc <= now)
            .OrderBy(x => x.CurrentPeriodEndUtc)
            .Select(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        foreach (var subscriptionId in dueIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {subscriptionId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var subscription = rows.SingleOrDefault();
            var processedAt = DateTime.UtcNow;
            if (subscription is null || subscription.Status != SubscriptionStatus.Active ||
                subscription.CurrentPeriodEndUtc is null || subscription.CurrentPeriodEndUtc > processedAt)
            {
                await tx.RollbackAsync(cancellationToken);
                continue;
            }

            var grants = await db.SubscriptionPeriodGrants
                .Where(x => x.SubscriptionId == subscription.Id && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var grant in grants.Where(x => x.PeriodEndUtc <= processedAt))
                grant.IsActive = false;

            var remaining = grants.Where(x => x.IsActive && x.PeriodEndUtc > processedAt).ToArray();
            var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
                x => x.OrganizationId == subscription.OrganizationId && x.ProductCode == subscription.ProductCode,
                cancellationToken);

            if (remaining.Length == 0)
            {
                subscription.Status = SubscriptionStatus.Suspended;
                subscription.CurrentPeriodStartUtc = null;
                // Keep the actual historical end timestamp; subsequent payment starts at now because it is expired.
                if (entitlement is not null)
                {
                    entitlement.IsActive = false;
                    entitlement.UpdatedAtUtc = processedAt;
                }
            }
            else
            {
                subscription.CurrentPeriodStartUtc = remaining.Min(x => x.PeriodStartUtc);
                subscription.CurrentPeriodEndUtc = remaining.Max(x => x.PeriodEndUtc);
                if (entitlement is not null)
                {
                    entitlement.IsActive = true;
                    entitlement.ValidUntilUtc = subscription.CurrentPeriodEndUtc;
                    entitlement.UpdatedAtUtc = processedAt;
                }
            }

            db.BillingAuditLogs.Add(new BillingAuditLog
            {
                OrganizationId = subscription.OrganizationId,
                EntityType = "Subscription",
                EntityId = subscription.Id.ToString("D"),
                EventType = remaining.Length == 0 ? "SubscriptionPeriodExpired" : "ExpiredGrantsPruned",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    processedAtUtc = processedAt,
                    expiredGrantCount = grants.Count(x => !x.IsActive),
                    remainingGrantCount = remaining.Length
                }),
                CreatedAtUtc = processedAt
            });

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            if (remaining.Length == 0)
            {
                expiredCount++;
                logger.LogInformation("Subscription {SubscriptionId} expired", subscription.Id);
            }
        }

        return expiredCount;
    }
}
