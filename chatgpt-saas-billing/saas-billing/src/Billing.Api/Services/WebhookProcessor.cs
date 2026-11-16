using System.Data;
using System.Text.Json;
using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

public sealed class WebhookProcessor(
    IDbContextFactory<BillingDbContext> dbFactory,
    ILogger<WebhookProcessor> logger)
{
    private const int MaxAutomaticAttempts = 8;

    public async Task<string> ProcessAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // The event lock serializes duplicate deliveries. The payment lock serializes
            // different provider events (e.g. succeeded and refunded) for the same payment.
            var lockedWebhookRows = await db.WebhookEvents
                .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var webhook = lockedWebhookRows.SingleOrDefault();

            if (webhook is null)
                return "NotFound";
            if (webhook.Status == WebhookStatus.Processed)
                return WebhookStatus.Processed;
            if (webhook.Status == WebhookStatus.DeadLetter)
                return WebhookStatus.DeadLetter;
            if (webhook.Status == WebhookStatus.Failed && webhook.NextAttemptAtUtc > DateTime.UtcNow)
                return WebhookStatus.Failed; // Honor backoff when multiple workers picked the same candidate.

            var lockedPaymentRows = await db.PaymentAttempts
                .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {webhook.PaymentAttemptId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var payment = lockedPaymentRows.SingleOrDefault();

            if (payment is null)
                throw new WebhookProcessingException("Payment attempt was not found.", permanent: true);

            if (payment.Provider != webhook.Provider ||
                payment.Amount != webhook.Amount ||
                !string.Equals(payment.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new WebhookProcessingException("Webhook payment, amount or currency does not match the payment attempt.", permanent: true);
            }

            var lockedInvoiceRows = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM \"Invoices\" WHERE \"Id\" = {payment.InvoiceId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var invoice = lockedInvoiceRows.Single();

            switch (webhook.EventType)
            {
                case "payment.succeeded":
                    await HandlePaymentSucceededAsync(db, webhook, payment, invoice, cancellationToken);
                    break;
                case "payment.refunded":
                    await HandlePaymentRefundedAsync(db, webhook, payment, invoice, cancellationToken);
                    break;
                case "payment.failed":
                    HandlePaymentFailed(webhook, payment);
                    break;
                default:
                    throw new WebhookProcessingException($"Unsupported event type '{webhook.EventType}'.", permanent: true);
            }

            webhook.Status = WebhookStatus.Processed;
            webhook.ProcessedAtUtc = DateTime.UtcNow;
            webhook.LastError = null;
            AddAudit(db, invoice.OrganizationId, "WebhookEvent", webhook.Id.ToString("D"), "WebhookProcessed",
                new { webhook.ProviderEventId, webhook.EventType, payment.Id });

            await db.SaveChangesAsync(cancellationToken);
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
        if (payment.Status == PaymentStatus.Refunded)
            return; // A delayed success for an already-refunded payment cannot undo its refund.

        if (invoice.Status == InvoiceStatus.Refunded)
        {
            // A distinct attempt can still capture late, after another attempt already refunded
            // this invoice. Preserve the capture for operator reconciliation without restoring access.
            payment.Status = PaymentStatus.Succeeded;
            payment.RequiresManualRefund = true;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"), "LateCaptureAfterInvoiceRefund",
                new { invoice.Id, payment.ProviderPaymentId });
            return;
        }

        payment.Status = PaymentStatus.Succeeded;
        payment.UpdatedAtUtc = DateTime.UtcNow;

        if (invoice.Status == InvoiceStatus.Issued)
        {
            var lockedSubscriptionRows = await db.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {invoice.SubscriptionId} FOR UPDATE")
                .ToListAsync(ct);
            var subscription = lockedSubscriptionRows.Single();

            var now = DateTime.UtcNow;
            var periodStart = subscription.Status == SubscriptionStatus.Active &&
                              subscription.CurrentPeriodEndUtc is { } previousEnd && previousEnd > now
                ? previousEnd
                : now;
            var periodEnd = periodStart.AddDays(subscription.BillingPeriodDays);
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidPaymentAttemptId = payment.Id;
            subscription.Status = SubscriptionStatus.Active;
            subscription.CurrentPeriodStartUtc = periodStart;
            subscription.CurrentPeriodEndUtc = periodEnd;

            var lockKey = $"{invoice.OrganizationId:N}:{subscription.ProductCode}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

            var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
                x => x.OrganizationId == invoice.OrganizationId && x.ProductCode == subscription.ProductCode, ct);
            if (entitlement is null)
            {
                entitlement = new ProductEntitlement
                {
                    OrganizationId = invoice.OrganizationId,
                    ProductCode = subscription.ProductCode,
                    IsActive = true,
                    ValidUntilUtc = periodEnd,
                    UpdatedAtUtc = now
                };
                db.ProductEntitlements.Add(entitlement);
            }
            else
            {
                entitlement.IsActive = true;
                entitlement.ValidUntilUtc = periodEnd;
                entitlement.UpdatedAtUtc = now;
            }

            AddAudit(db, invoice.OrganizationId, "Subscription", subscription.Id.ToString("D"), "SubscriptionActivated",
                new { invoice.Id, payment.Id, entitlement.ProductCode, periodEnd });
        }
        else if (invoice.Status == InvoiceStatus.Paid && invoice.PaidPaymentAttemptId != payment.Id)
        {
            // A second captured attempt must not extend access twice. It is explicitly flagged
            // for an operator to refund rather than silently being treated as another renewal.
            payment.RequiresManualRefund = true;
            AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"), "DuplicateCaptureDetected",
                new { invoice.Id, payment.ProviderPaymentId });
        }
    }

    private static void HandlePaymentFailed(WebhookEvent webhook, PaymentAttempt payment)
    {
        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.Refunded)
            return; // A delayed failure notification must not overwrite a confirmed capture.

        payment.Status = PaymentStatus.Failed;
        payment.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task HandlePaymentRefundedAsync(
        BillingDbContext db,
        WebhookEvent webhook,
        PaymentAttempt payment,
        Invoice invoice,
        CancellationToken ct)
    {
        if (webhook.Amount != invoice.TotalAmount || webhook.Amount != payment.Amount)
            throw new WebhookProcessingException("Only a full invoice refund is supported by this adapter.", permanent: true);

        if (payment.Status == PaymentStatus.Refunded)
            return;
        if (payment.Status != PaymentStatus.Succeeded)
            throw new InvalidOperationException("Refund arrived before payment confirmation; it will be retried.");

        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedAtUtc = DateTime.UtcNow;

        // If this was a late second capture, refunding it must not revoke access paid by the first attempt.
        if (invoice.PaidPaymentAttemptId == payment.Id || invoice.Status == InvoiceStatus.Paid && invoice.PaidPaymentAttemptId is null)
        {
            var lockedSubscriptionRows = await db.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {invoice.SubscriptionId} FOR UPDATE")
                .ToListAsync(ct);
            var subscription = lockedSubscriptionRows.Single();
            invoice.Status = InvoiceStatus.Refunded;
            subscription.Status = SubscriptionStatus.Suspended;
            subscription.CurrentPeriodEndUtc = DateTime.UtcNow;
            var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
                x => x.OrganizationId == invoice.OrganizationId && x.ProductCode == subscription.ProductCode, ct);
            if (entitlement is not null)
            {
                entitlement.IsActive = false;
                entitlement.ValidUntilUtc = DateTime.UtcNow;
                entitlement.UpdatedAtUtc = DateTime.UtcNow;
            }

            AddAudit(db, invoice.OrganizationId, "Subscription", subscription.Id.ToString("D"), "SubscriptionSuspendedAfterRefund",
                new { invoice.Id, payment.Id, productCode = subscription.ProductCode });
        }

        AddAudit(db, invoice.OrganizationId, "PaymentAttempt", payment.Id.ToString("D"), "PaymentRefunded",
            new { invoice.Id, webhook.Amount, webhook.Currency });
    }

    private async Task MarkFailedAsync(Guid eventId, string error, bool permanent, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockedWebhookRows = await db.WebhookEvents
            .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
            .ToListAsync(ct);
        var webhook = lockedWebhookRows.SingleOrDefault();
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
        var lockedWebhookRows = await db.WebhookEvents
            .FromSqlInterpolated($"SELECT * FROM \"WebhookEvents\" WHERE \"Id\" = {eventId} FOR UPDATE")
            .ToListAsync(ct);
        var webhook = lockedWebhookRows.SingleOrDefault();
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
