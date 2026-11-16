using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

/// <summary>
/// Business effects of a webhook. Runs inside the caller's transaction, which already holds the
/// event row lock. Row locks taken here follow the global order: attempt -> invoice -> subscription,
/// so concurrent events for the same payment/invoice/subscription serialize and never deadlock.
/// Every handler is idempotent: re-applying an already applied event is a no-op.
/// </summary>
internal static class WebhookEventApplier
{
    public static Task ApplyAsync(BillingDbContext db, WebhookPayload p, DateTimeOffset now, CancellationToken ct) =>
        p.Type switch
        {
            "payment.succeeded" => PaymentSucceeded(db, p, now, ct),
            "payment.failed" => PaymentFailed(db, p, now, ct),
            "refund.succeeded" => RefundSucceeded(db, p, now, ct),
            _ => Ignore(db, p, now)
        };

    private static Task Ignore(BillingDbContext db, WebhookPayload p, DateTimeOffset now)
    {
        Audit(db, now, "WebhookIgnored", "WebhookEvent", p.EventId, $"Unsupported event type '{p.Type}'");
        return Task.CompletedTask;
    }

    private static async Task<PaymentAttempt> LockAttemptAsync(BillingDbContext db, string paymentId, CancellationToken ct)
    {
        await db.LockRowAsync($$"""SELECT 1 FROM "PaymentAttempts" WHERE "ProviderPaymentId" = {{paymentId}} FOR UPDATE""", ct);
        return await db.PaymentAttempts.SingleOrDefaultAsync(a => a.ProviderPaymentId == paymentId, ct)
               ?? throw new TransientWebhookException($"Payment '{paymentId}' is not known (yet)");
    }

    private static async Task<Invoice> LockInvoiceAsync(BillingDbContext db, Guid id, CancellationToken ct)
    {
        await db.LockRowAsync($$"""SELECT 1 FROM "Invoices" WHERE "Id" = {{id}} FOR UPDATE""", ct);
        return await db.Invoices.SingleAsync(i => i.Id == id, ct);
    }

    private static async Task<Subscription> LockSubscriptionAsync(BillingDbContext db, Guid id, CancellationToken ct)
    {
        await db.LockRowAsync($$"""SELECT 1 FROM "Subscriptions" WHERE "Id" = {{id}} FOR UPDATE""", ct);
        return await db.Subscriptions.Include(s => s.Plan).SingleAsync(s => s.Id == id, ct);
    }

    private static bool SameMoney(PaymentAttempt a, WebhookPayload p) =>
        p.Amount == a.AmountMinor && string.Equals(p.Currency ?? a.Currency, a.Currency, StringComparison.OrdinalIgnoreCase);

    // ---- payment.succeeded -------------------------------------------------------------------------

    private static async Task PaymentSucceeded(BillingDbContext db, WebhookPayload p, DateTimeOffset now, CancellationToken ct)
    {
        var attempt = await LockAttemptAsync(db, p.PaymentId, ct);

        if (!SameMoney(attempt, p))
            throw new PermanentWebhookException(
                $"Amount/currency mismatch: expected {attempt.AmountMinor} {attempt.Currency}, got {p.Amount} {p.Currency}");

        if (attempt.Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.Refunded)
        {
            Audit(db, now, "PaymentAlreadyApplied", "PaymentAttempt", attempt.Id.ToString(), $"event {p.EventId}: attempt is {attempt.Status}");
            return;
        }

        var invoice = await LockInvoiceAsync(db, attempt.InvoiceId, ct);
        attempt.Status = PaymentAttemptStatus.Succeeded;
        attempt.CompletedAt = now;
        attempt.FailureReason = null;

        if (invoice.Status != InvoiceStatus.Open)
        {
            // Money was received for an invoice that is already paid/refunded/void: do not activate twice.
            Audit(db, now, "DuplicatePayment", "Invoice", invoice.Id.ToString(),
                $"attempt {attempt.Id} succeeded but invoice is {invoice.Status}; needs manual refund");
            return;
        }

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = now;
        invoice.PaidByAttemptId = attempt.Id;
        Audit(db, now, "InvoicePaid", "Invoice", invoice.Id.ToString(), $"paid by attempt {attempt.Id}, event {p.EventId}");

        if (invoice.SubscriptionId is { } subId)
            await ActivateSubscription(db, subId, invoice, now, ct);
    }

    private static async Task ActivateSubscription(BillingDbContext db, Guid subId, Invoice invoice, DateTimeOffset now, CancellationToken ct)
    {
        var sub = await LockSubscriptionAsync(db, subId, ct);
        if (sub.Status == SubscriptionStatus.Cancelled)
        {
            Audit(db, now, "PaymentForCancelledSubscription", "Subscription", sub.Id.ToString(), $"invoice {invoice.Id} paid, subscription not reactivated");
            return;
        }

        // Renewal while still active extends from the current end, otherwise the period starts now.
        var start = sub.Status == SubscriptionStatus.Active && sub.CurrentPeriodEnd > now ? sub.CurrentPeriodEnd!.Value : now;
        sub.Status = SubscriptionStatus.Active;
        sub.ActivatedAt ??= now;
        sub.CurrentPeriodStart = start;
        sub.CurrentPeriodEnd = start.AddDays(sub.Plan.PeriodDays);

        var access = await db.ProductAccesses.SingleOrDefaultAsync(
            a => a.OrganizationId == sub.OrganizationId && a.ProductCode == sub.Plan.ProductCode, ct);
        if (access is null)
        {
            db.ProductAccesses.Add(new ProductAccess
            {
                OrganizationId = sub.OrganizationId, ProductCode = sub.Plan.ProductCode, SubscriptionId = sub.Id,
                GrantedAt = now, ExpiresAt = sub.CurrentPeriodEnd.Value
            });
        }
        else
        {
            access.SubscriptionId = sub.Id;
            access.ExpiresAt = sub.CurrentPeriodEnd.Value;
            access.RevokedAt = null;
        }

        Audit(db, now, "SubscriptionActivated", "Subscription", sub.Id.ToString(),
            $"period {sub.CurrentPeriodStart:O} .. {sub.CurrentPeriodEnd:O}, access to '{sub.Plan.ProductCode}' granted");
    }

    // ---- payment.failed ----------------------------------------------------------------------------

    private static async Task PaymentFailed(BillingDbContext db, WebhookPayload p, DateTimeOffset now, CancellationToken ct)
    {
        var attempt = await LockAttemptAsync(db, p.PaymentId, ct);
        if (attempt.Status != PaymentAttemptStatus.Pending)
        {
            // Late/duplicate "failed" after success or refund must never downgrade the state.
            Audit(db, now, "PaymentFailureIgnored", "PaymentAttempt", attempt.Id.ToString(), $"attempt is already {attempt.Status}");
            return;
        }
        attempt.Status = PaymentAttemptStatus.Failed;
        attempt.CompletedAt = now;
        attempt.FailureReason = p.Reason;
        Audit(db, now, "PaymentFailed", "PaymentAttempt", attempt.Id.ToString(), p.Reason ?? "no reason given");
    }

    // ---- refund.succeeded --------------------------------------------------------------------------

    private static async Task RefundSucceeded(BillingDbContext db, WebhookPayload p, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.RefundId))
            throw new PermanentWebhookException("refund_id is required for refund events");

        var attempt = await LockAttemptAsync(db, p.PaymentId, ct);

        if (attempt.Status == PaymentAttemptStatus.Refunded)
        {
            Audit(db, now, "RefundAlreadyApplied", "PaymentAttempt", attempt.Id.ToString(), $"refund {p.RefundId}");
            return;
        }
        if (attempt.Status == PaymentAttemptStatus.Pending)
            throw new TransientWebhookException("Refund arrived before payment confirmation; will be retried");
        if (attempt.Status == PaymentAttemptStatus.Failed)
            throw new PermanentWebhookException("Refund for a payment that never succeeded");
        if (!SameMoney(attempt, p))
            throw new PermanentWebhookException($"Only full refunds are supported: payment {attempt.AmountMinor}, refund {p.Amount}");
        if (await db.Refunds.AnyAsync(r => r.ProviderRefundId == p.RefundId, ct))
            throw new PermanentWebhookException($"Refund id '{p.RefundId}' was already used for another payment");

        db.Refunds.Add(new Refund { PaymentAttemptId = attempt.Id, ProviderRefundId = p.RefundId, AmountMinor = p.Amount, CreatedAt = now });
        attempt.Status = PaymentAttemptStatus.Refunded;

        var invoice = await LockInvoiceAsync(db, attempt.InvoiceId, ct);
        if (invoice.PaidByAttemptId != attempt.Id || invoice.Status != InvoiceStatus.Paid)
        {
            // Refund of a duplicate payment: the invoice itself stays valid.
            Audit(db, now, "DuplicatePaymentRefunded", "PaymentAttempt", attempt.Id.ToString(), $"refund {p.RefundId}; invoice {invoice.Id} unchanged");
            return;
        }

        invoice.Status = InvoiceStatus.Refunded;
        Audit(db, now, "InvoiceRefunded", "Invoice", invoice.Id.ToString(), $"refund {p.RefundId}, event {p.EventId}");

        if (invoice.SubscriptionId is { } subId)
        {
            var sub = await LockSubscriptionAsync(db, subId, ct);
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;

            var accesses = await db.ProductAccesses.Where(a => a.SubscriptionId == sub.Id && a.RevokedAt == null).ToListAsync(ct);
            foreach (var a in accesses)
            {
                a.RevokedAt = now;
                a.ExpiresAt = now;
            }
            Audit(db, now, "SubscriptionCancelled", "Subscription", sub.Id.ToString(), $"full refund; {accesses.Count} access grant(s) revoked");
        }
    }

    private static void Audit(BillingDbContext db, DateTimeOffset now, string action, string entityType, string entityId, string details) =>
        db.AuditLogs.Add(new AuditLog { At = now, Action = action, EntityType = entityType, EntityId = entityId, Details = details });
}
