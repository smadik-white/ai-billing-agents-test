using System.Data;
using System.Text.Json;
using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

public sealed class WebhookProcessor(
    IDbContextFactory<BillingDbContext> dbFactory,
    IWebhookFaultInjector faultInjector,
    ILogger<WebhookProcessor> logger)
{
    private const int MaxAutomaticAttempts = 8;

    public async Task<string> ProcessAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Event -> payment -> invoice -> subscription is the global lock order used by all
            // financial transitions. It serializes duplicate events and concurrent mutations.
            var webhookRows = await db.WebhookEvents
                .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var webhook = webhookRows.SingleOrDefault();
            if (webhook is null)
                return "NotFound";
            if (webhook.Status == WebhookStatus.Processed)
                return WebhookStatus.Processed;
            if (webhook.Status == WebhookStatus.DeadLetter)
                return WebhookStatus.DeadLetter;
            if (webhook.Status == WebhookStatus.Failed && webhook.NextAttemptAtUtc > DateTime.UtcNow)
                return WebhookStatus.Failed;

            var paymentRows = await db.PaymentAttempts
                .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {webhook.PaymentAttemptId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var payment = paymentRows.SingleOrDefault();
            if (payment is null)
                throw new WebhookProcessingException("Payment attempt was not found.", permanent: true);

            if (!string.Equals(payment.Provider, webhook.Provider, StringComparison.Ordinal) ||
                !string.Equals(payment.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase))
                throw new WebhookProcessingException("Webhook provider or currency does not match the payment attempt.", permanent: true);

            if (webhook.EventType is "payment.succeeded" or "payment.failed")
            {
                if (webhook.Amount != payment.Amount)
                    throw new WebhookProcessingException("Webhook amount does not match the payment attempt.", permanent: true);
            }
            else if (webhook.EventType == "payment.refunded")
            {
                if (webhook.Amount <= 0 || string.IsNullOrWhiteSpace(webhook.ProviderRefundId))
                    throw new WebhookProcessingException("A refund requires a positive amount and stable RefundId.", permanent: true);
            }

            var invoiceRows = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM \"Invoices\" WHERE \"Id\" = {payment.InvoiceId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var invoice = invoiceRows.Single();

            switch (webhook.EventType)
            {
                case "payment.succeeded":
                    await HandlePaymentSucceededAsync(db, webhook, payment, invoice, cancellationToken);
                    break;
                case "payment.refunded":
                    await HandlePaymentRefundedAsync(db, webhook, payment, invoice, cancellationToken);
                    break;
                case "payment.failed":
                    HandlePaymentFailed(db, webhook, payment, invoice);
                    break;
                default:
                    throw new WebhookProcessingException($"Unsupported event type '{webhook.EventType}'.", permanent: true);
            }

            webhook.Status = WebhookStatus.Processed;
            webhook.ProcessedAtUtc = DateTime.UtcNow;
            webhook.LastError = null;
            AddAudit(db, invoice.OrganizationId, "WebhookEvent", webhook.Id.ToString("D"), "WebhookProcessed",
                new { webhook.ProviderEventId, webhook.EventType, payment.Id, webhook.ProviderRefundId });

            await db.SaveChangesAsync(cancellationToken);
            // Inject after SQL writes but before commit to prove the transaction rolls back durable
            // entity changes as well as the in-memory state when the process fails at this point.
            await faultInjector.AfterBusinessMutationAsync(webhook.Id, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return WebhookStatus.Processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook event {WebhookEventId} processing failed", eventId);
            await MarkFailedAsync(eventId, ex.Message, ex is WebhookProcessingException { Permanent: true }, cancellationToken);
            return ex is WebhookProcessingException { Permanent: true } ? WebhookStatus.DeadLetter : WebhookStatus.Failed;
        }
    }

    private static async Task HandlePaymentSucceededAsync(
        BillingDbContext db,
        WebhookEvent webhook,
        PaymentAttempt payment,
        Invoice invoice,
        CancellationToken ct)
    {
        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
                "DuplicateCaptureEventIgnored", new { webhook.ProviderEventId, payment.Status });
            return;
        }

        // A capture after a terminal invoice refund is real money, but is not allowed to reopen
        // service. It is accounted for and explicitly escalated for a compensating refund.
        payment.Status = PaymentStatus.Succeeded;
        payment.CapturedAtUtc = DateTime.UtcNow;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        invoice.AmountPaid += payment.Amount;

        if (invoice.Status == InvoiceStatus.Refunded)
        {
            payment.RequiresManualRefund = true;
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
                "LateCaptureAfterInvoiceRefund", new { invoice.Id, payment.Amount, webhook.ProviderEventId });
            return;
        }

        RefreshInvoiceStatus(invoice);
        if (invoice.NetPaidAmount > invoice.TotalAmount)
        {
            payment.RequiresManualRefund = true;
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
                "OverCollectionRequiresManualRefund", new { invoice.Id, invoice.NetPaidAmount, invoice.TotalAmount });
        }

        var subscription = await LockSubscriptionAsync(db, invoice.SubscriptionId, ct);
        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            payment.RequiresManualRefund = true;
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
                "CaptureAfterSubscriptionCancellation", new { invoice.Id, subscription.Id, payment.Amount });
            return;
        }

        if (invoice.NetPaidAmount >= invoice.TotalAmount && !payment.RequiresManualRefund)
        {
            invoice.PaidPaymentAttemptId ??= payment.Id;
            await ActivateInvoicePeriodAsync(db, invoice, subscription, ct);
        }
    }

    private static void HandlePaymentFailed(
        BillingDbContext db,
        WebhookEvent webhook,
        PaymentAttempt payment,
        Invoice invoice)
    {
        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
                "StalePaymentFailureIgnored", new { webhook.ProviderEventId, payment.Status });
            return;
        }

        payment.Status = PaymentStatus.Failed;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"),
            "PaymentAttemptFailed", new { webhook.ProviderEventId });
    }

    private static async Task HandlePaymentRefundedAsync(
        BillingDbContext db,
        WebhookEvent webhook,
        PaymentAttempt payment,
        Invoice invoice,
        CancellationToken ct)
    {
        var refundId = webhook.ProviderRefundId!;
        var existingRefund = await db.PaymentRefunds.SingleOrDefaultAsync(
            x => x.Provider == webhook.Provider && x.ProviderRefundId == refundId, ct);
        if (existingRefund is not null)
        {
            if (existingRefund.PaymentAttemptId != payment.Id || existingRefund.Amount != webhook.Amount ||
                !string.Equals(existingRefund.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase))
                throw new WebhookProcessingException("RefundId was reused with different payment or amount data.", permanent: true);

            AddAudit(db, invoice.OrganizationId, "PaymentRefund", existingRefund.Id.ToString("D"),
                "DuplicateRefundEventIgnored", new { webhook.ProviderEventId, refundId });
            return;
        }

        if (payment.Status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
            throw new InvalidOperationException("Refund arrived before the payment capture; it will be retried.");

        var refundable = payment.Amount - payment.RefundedAmount;
        if (webhook.Amount > refundable)
            throw new WebhookProcessingException(
                $"Refund amount {webhook.Amount:0.00} exceeds remaining captured amount {refundable:0.00}.", permanent: true);

        var refund = new PaymentRefund
        {
            PaymentAttemptId = payment.Id,
            Provider = webhook.Provider,
            ProviderRefundId = refundId,
            Amount = webhook.Amount,
            Currency = webhook.Currency,
            WebhookEventId = webhook.Id
        };
        db.PaymentRefunds.Add(refund);
        payment.RefundedAmount += webhook.Amount;
        payment.Status = payment.RefundedAmount == payment.Amount
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        invoice.AmountRefunded += webhook.Amount;
        RefreshInvoiceStatus(invoice);

        // If refunds make this invoice underfunded, revoke only the period granted by this invoice.
        // Later separately-paid periods remain valid.
        if (invoice.NetPaidAmount < invoice.TotalAmount)
        {
            var grant = await db.SubscriptionPeriodGrants.SingleOrDefaultAsync(x => x.InvoiceId == invoice.Id, ct);
            if (grant is not null)
                grant.IsActive = false;

            var subscription = await LockSubscriptionAsync(db, invoice.SubscriptionId, ct);
            await RecomputeSubscriptionPeriodAndEntitlementAsync(db, subscription, ct);
        }

        AddAudit(db, invoice.OrganizationId, "PaymentRefund", refund.Id.ToString("D"), "PaymentRefundRecorded",
            new { invoice.Id, payment.Id, refund.ProviderRefundId, refund.Amount, payment.RefundedAmount, payment.Amount });
    }

    private static void RefreshInvoiceStatus(Invoice invoice) =>
        invoice.Status = InvoiceFinancialRules.DetermineStatus(invoice);

    private static async Task ActivateInvoicePeriodAsync(
        BillingDbContext db,
        Invoice invoice,
        Subscription subscription,
        CancellationToken ct)
    {
        if (subscription.Status == SubscriptionStatus.Cancelled)
            return;

        var grant = await db.SubscriptionPeriodGrants.SingleOrDefaultAsync(x => x.InvoiceId == invoice.Id, ct);
        if (grant is { IsActive: true })
            return;

        var now = DateTime.UtcNow;
        var priorEnd = await db.SubscriptionPeriodGrants
            .Where(x => x.SubscriptionId == subscription.Id && x.IsActive)
            .Select(x => (DateTime?)x.PeriodEndUtc)
            .MaxAsync(ct);
        var start = SubscriptionPeriodRules.StartNextPeriod(now, priorEnd);
        var end = start.AddDays(invoice.BillingPeriodDays);

        if (grant is null)
        {
            grant = new SubscriptionPeriodGrant
            {
                SubscriptionId = subscription.Id,
                InvoiceId = invoice.Id,
                PeriodStartUtc = start,
                PeriodEndUtc = end,
                IsActive = true
            };
            db.SubscriptionPeriodGrants.Add(grant);
        }
        else
        {
            grant.PeriodStartUtc = start;
            grant.PeriodEndUtc = end;
            grant.IsActive = true;
        }

        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStartUtc = start;
        subscription.CurrentPeriodEndUtc = end;
        var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
            x => x.OrganizationId == invoice.OrganizationId && x.ProductCode == subscription.ProductCode, ct);
        if (entitlement is null)
        {
            db.ProductEntitlements.Add(new ProductEntitlement
            {
                OrganizationId = invoice.OrganizationId,
                ProductCode = subscription.ProductCode,
                IsActive = true,
                ValidUntilUtc = end
            });
        }
        else
        {
            entitlement.IsActive = true;
            entitlement.ValidUntilUtc = end;
            entitlement.UpdatedAtUtc = now;
        }

        AddAudit(db, invoice.OrganizationId, "SubscriptionPeriodGrant", grant.Id.ToString("D"),
            "SubscriptionPeriodGranted", new { invoice.Id, subscription.Id, start, end, invoice.BillingPeriodDays });
    }

    private static async Task<Subscription> LockSubscriptionAsync(BillingDbContext db, Guid subscriptionId, CancellationToken ct)
    {
        var rows = await db.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {subscriptionId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Single();
    }

    private static async Task RecomputeSubscriptionPeriodAndEntitlementAsync(
        BillingDbContext db,
        Subscription subscription,
        CancellationToken ct)
    {
        var grants = await db.SubscriptionPeriodGrants
            .Where(x => x.SubscriptionId == subscription.Id && x.IsActive)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var validGrants = grants.Where(x => x.PeriodEndUtc > now).ToArray();
        var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
            x => x.OrganizationId == subscription.OrganizationId && x.ProductCode == subscription.ProductCode, ct);

        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            if (entitlement is not null)
            {
                entitlement.IsActive = false;
                entitlement.ValidUntilUtc = now;
                entitlement.UpdatedAtUtc = now;
            }
            return;
        }

        if (validGrants.Length == 0)
        {
            subscription.Status = SubscriptionStatus.Suspended;
            subscription.CurrentPeriodStartUtc = null;
            subscription.CurrentPeriodEndUtc = now;
            if (entitlement is not null)
            {
                entitlement.IsActive = false;
                entitlement.ValidUntilUtc = now;
                entitlement.UpdatedAtUtc = now;
            }
            return;
        }

        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStartUtc = validGrants.Min(x => x.PeriodStartUtc);
        subscription.CurrentPeriodEndUtc = validGrants.Max(x => x.PeriodEndUtc);
        if (entitlement is null)
        {
            db.ProductEntitlements.Add(new ProductEntitlement
            {
                OrganizationId = subscription.OrganizationId,
                ProductCode = subscription.ProductCode,
                IsActive = true,
                ValidUntilUtc = subscription.CurrentPeriodEndUtc,
                UpdatedAtUtc = now
            });
        }
        else
        {
            entitlement.IsActive = true;
            entitlement.ValidUntilUtc = subscription.CurrentPeriodEndUtc;
            entitlement.UpdatedAtUtc = now;
        }
    }

    private async Task MarkFailedAsync(Guid eventId, string error, bool permanent, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.WebhookEvents
            .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
            .ToListAsync(ct);
        var webhook = rows.SingleOrDefault();
        if (webhook is null || webhook.Status == WebhookStatus.Processed)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        webhook.AttemptCount++;
        webhook.LastError = error.Length > 1900 ? error[..1900] : error;
        webhook.Status = permanent || webhook.AttemptCount >= MaxAutomaticAttempts
            ? WebhookStatus.DeadLetter
            : WebhookStatus.Failed;
        var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(webhook.AttemptCount, 8)));
        webhook.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        AddAudit(db, null, "WebhookEvent", webhook.Id.ToString("D"),
            webhook.Status == WebhookStatus.DeadLetter ? "WebhookDeadLettered" : "WebhookProcessingFailed",
            new { webhook.ProviderEventId, webhook.AttemptCount, error = webhook.LastError });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> ScheduleManualRetryAsync(Guid eventId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.WebhookEvents
            .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
            .ToListAsync(ct);
        var webhook = rows.SingleOrDefault();
        if (webhook is null || webhook.Status == WebhookStatus.Processed)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        webhook.Status = WebhookStatus.Received;
        webhook.AttemptCount = 0;
        webhook.NextAttemptAtUtc = DateTime.UtcNow;
        webhook.LastError = null;
        AddAudit(db, null, "WebhookEvent", webhook.Id.ToString("D"), "WebhookManualRetryScheduled", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static void AddAudit(
        BillingDbContext db, Guid? organizationId, string entityType, string entityId, string eventType, object? details)
    {
        db.BillingAuditLogs.Add(new BillingAuditLog
        {
            OrganizationId = organizationId,
            EntityType = entityType,
            EntityId = entityId,
            EventType = eventType,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}

public sealed class WebhookProcessingException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
