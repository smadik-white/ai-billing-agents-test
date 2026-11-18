using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

/// <summary>
/// Business effects of a webhook. Runs inside the caller's transaction, which already holds the event row lock.
/// Row locks taken here follow the global order: attempt -> invoice -> subscription, so concurrent events for the
/// same payment/invoice/subscription serialize and never deadlock. Every handler is idempotent.
///
/// Money invariants maintained here:
///   invoice.PaidMinor     = sum(amount of attempts with AppliedToInvoice and a confirmed status) &lt;= invoice.TotalMinor
///   invoice.RefundedMinor = sum(refunds of applied attempts)                                    &lt;= invoice.PaidMinor
///   attempt.RefundedMinor = sum(its refunds)                                                    &lt;= attempt.AmountMinor
/// </summary>
internal static class WebhookEventApplier
{
    public static Task ApplyAsync(BillingDbContext db, WebhookPayload p, DateTimeOffset now, IWebhookProcessingHook hook, CancellationToken ct) =>
        p.Type switch
        {
            "payment.succeeded" => PaymentSucceeded(db, p, now, hook, ct),
            "payment.failed" => PaymentFailed(db, p, now, ct),
            "refund.succeeded" => RefundSucceeded(db, p, now, hook, ct),
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

    private static bool SameCurrency(PaymentAttempt a, WebhookPayload p) =>
        string.Equals(p.Currency ?? a.Currency, a.Currency, StringComparison.OrdinalIgnoreCase);

    // ---- payment.succeeded -------------------------------------------------------------------------

    private static async Task PaymentSucceeded(BillingDbContext db, WebhookPayload p, DateTimeOffset now, IWebhookProcessingHook hook, CancellationToken ct)
    {
        var attempt = await LockAttemptAsync(db, p.PaymentId, ct);

        if (p.Amount != attempt.AmountMinor || !SameCurrency(attempt, p))
            throw new PermanentWebhookException(
                $"Amount/currency mismatch: expected {attempt.AmountMinor} {attempt.Currency}, got {p.Amount} {p.Currency}");

        // One attempt can credit money only once; a late success after (partial) refund must not re-credit.
        if (attempt.Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.PartiallyRefunded or PaymentAttemptStatus.Refunded)
        {
            Audit(db, now, "PaymentAlreadyApplied", "PaymentAttempt", attempt.Id.ToString(), $"event {p.EventId}: attempt is {attempt.Status}");
            return;
        }
        if (attempt.Status == PaymentAttemptStatus.Creating)
            throw new TransientWebhookException("Payment attempt creation is not finalized yet");

        var invoice = await LockInvoiceAsync(db, attempt.InvoiceId, ct);
        attempt.Status = PaymentAttemptStatus.Succeeded;
        attempt.CompletedAt = now;
        attempt.FailureReason = null;
        await hook.OnStepAsync("attempt-updated", ct);

        if (invoice.Status == InvoiceStatus.Void || invoice.RefundedMinor > 0 || invoice.PaidMinor + attempt.AmountMinor > invoice.TotalMinor)
        {
            // Money was received but crediting it would break the invoice invariants: keep it as surplus, flag for a refund.
            attempt.AppliedToInvoice = false;
            Audit(db, now, "DuplicatePayment", "Invoice", invoice.Id.ToString(),
                $"attempt {attempt.Id} ({attempt.AmountMinor}) not credited: invoice is {invoice.Status}, paid {invoice.PaidMinor}/{invoice.TotalMinor}, refunded {invoice.RefundedMinor}; needs manual refund");
            Anomaly(db, now, $"surplus:{attempt.Id}", "SurplusPayment", "PaymentAttempt", attempt.Id.ToString(),
                $"Confirmed payment {attempt.ProviderPaymentId} of {attempt.AmountMinor} {attempt.Currency} was not credited to invoice {invoice.Id} ({invoice.Status}); refund it manually");
            return;
        }

        attempt.AppliedToInvoice = true;
        invoice.PaidMinor += attempt.AmountMinor;
        invoice.Status = InvoiceMath.StatusFor(invoice.Status, invoice.TotalMinor, invoice.PaidMinor, invoice.RefundedMinor);
        Audit(db, now, "InvoicePaymentApplied", "Invoice", invoice.Id.ToString(),
            $"attempt {attempt.Id} +{attempt.AmountMinor}; paid {invoice.PaidMinor}/{invoice.TotalMinor}, event {p.EventId}");
        await hook.OnStepAsync("invoice-updated", ct);

        if (invoice.PaidMinor < invoice.TotalMinor) return; // partial payment: no activation yet

        invoice.PaidAt = now;
        Audit(db, now, "InvoicePaid", "Invoice", invoice.Id.ToString(), $"fully paid, last attempt {attempt.Id}, event {p.EventId}");
        if (invoice.SubscriptionId is { } subId)
            await ActivateSubscription(db, subId, invoice, now, hook, ct);
    }

    /// <summary>
    /// Business rules:
    ///  - Active (or expired) subscription: the paid period is added after the current period end
    ///    (or from now when that end is already in the past) - paid time is never lost.
    ///  - Cancelled subscription: money is kept on the invoice but the subscription is NOT reactivated or extended;
    ///    an anomaly is raised so that support refunds or reinstates it consciously.
    /// </summary>
    private static async Task ActivateSubscription(BillingDbContext db, Guid subId, Invoice invoice, DateTimeOffset now, IWebhookProcessingHook hook, CancellationToken ct)
    {
        var sub = await LockSubscriptionAsync(db, subId, ct);
        if (sub.Status == SubscriptionStatus.Cancelled)
        {
            Audit(db, now, "PaymentForCancelledSubscription", "Subscription", sub.Id.ToString(), $"invoice {invoice.Id} paid, subscription not reactivated");
            Anomaly(db, now, $"cancelled-sub-payment:{invoice.Id}", "PaymentForCancelledSubscription", "Invoice", invoice.Id.ToString(),
                $"Invoice {invoice.Id} ({invoice.PaidMinor} {invoice.Currency}) was paid after subscription {sub.Id} was cancelled; not extended, decide: refund or reinstate");
            return;
        }

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
        await hook.OnStepAsync("subscription-updated", ct);
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

    private static async Task RefundSucceeded(BillingDbContext db, WebhookPayload p, DateTimeOffset now, IWebhookProcessingHook hook, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.RefundId))
            throw new PermanentWebhookException("refund_id is required for refund events");
        if (p.Amount <= 0)
            throw new PermanentWebhookException("refund amount must be positive");

        var attempt = await LockAttemptAsync(db, p.PaymentId, ct);

        // Each refund id is counted exactly once, whatever event id carried it.
        var known = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.ProviderRefundId == p.RefundId, ct);
        if (known is not null)
        {
            if (known.PaymentAttemptId == attempt.Id && known.AmountMinor == p.Amount)
            {
                Audit(db, now, "RefundAlreadyApplied", "PaymentAttempt", attempt.Id.ToString(), $"refund {p.RefundId}, event {p.EventId}");
                return;
            }
            throw new PermanentWebhookException($"Refund id '{p.RefundId}' was already used with different parameters");
        }

        if (attempt.Status is PaymentAttemptStatus.Pending or PaymentAttemptStatus.Creating)
            throw new TransientWebhookException("Refund arrived before payment confirmation; will be retried");
        if (attempt.Status == PaymentAttemptStatus.Failed)
            throw new PermanentWebhookException("Refund for a payment that never succeeded");
        if (!SameCurrency(attempt, p))
            throw new PermanentWebhookException($"Refund currency '{p.Currency}' differs from payment currency '{attempt.Currency}'");
        if (attempt.RefundedMinor + p.Amount > attempt.AmountMinor)
            throw new PermanentWebhookException(
                $"Refund exceeds paid amount: paid {attempt.AmountMinor}, already refunded {attempt.RefundedMinor}, requested {p.Amount}");

        db.Refunds.Add(new Refund { PaymentAttemptId = attempt.Id, ProviderRefundId = p.RefundId, AmountMinor = p.Amount, CreatedAt = now });
        attempt.RefundedMinor += p.Amount;
        attempt.Status = attempt.RefundedMinor == attempt.AmountMinor ? PaymentAttemptStatus.Refunded : PaymentAttemptStatus.PartiallyRefunded;
        await hook.OnStepAsync("refund-recorded", ct);

        if (!attempt.AppliedToInvoice)
        {
            // Refund of surplus money: the invoice itself is untouched.
            Audit(db, now, "SurplusPaymentRefunded", "PaymentAttempt", attempt.Id.ToString(), $"refund {p.RefundId} {p.Amount}; invoice {attempt.InvoiceId} unchanged");
            if (attempt.Status == PaymentAttemptStatus.Refunded)
            {
                var key = $"surplus:{attempt.Id}";
                var anomaly = await db.Anomalies.SingleOrDefaultAsync(a => a.Key == key, ct);
                if (anomaly is not null) anomaly.ResolvedAt = now;
            }
            return;
        }

        var invoice = await LockInvoiceAsync(db, attempt.InvoiceId, ct);
        invoice.RefundedMinor += p.Amount;
        invoice.Status = InvoiceMath.StatusFor(invoice.Status, invoice.TotalMinor, invoice.PaidMinor, invoice.RefundedMinor);
        Audit(db, now, "InvoiceRefunded", "Invoice", invoice.Id.ToString(),
            $"refund {p.RefundId} {p.Amount}; refunded {invoice.RefundedMinor}/{invoice.PaidMinor}, event {p.EventId}");

        // Business rule: partial refunds keep the subscription (price adjustment); access is revoked only
        // when everything that was paid has been refunded.
        if (invoice.PaidAt is null || invoice.RefundedMinor < invoice.PaidMinor || invoice.SubscriptionId is not { } subId)
            return;

        var sub = await LockSubscriptionAsync(db, subId, ct);
        if (sub.Status != SubscriptionStatus.Cancelled)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
        }
        var accesses = await db.ProductAccesses.Where(a => a.SubscriptionId == sub.Id && a.RevokedAt == null).ToListAsync(ct);
        foreach (var a in accesses)
        {
            a.RevokedAt = now;
            a.ExpiresAt = now;
        }
        Audit(db, now, "SubscriptionCancelled", "Subscription", sub.Id.ToString(), $"full refund; {accesses.Count} access grant(s) revoked");
    }

    private static void Audit(BillingDbContext db, DateTimeOffset now, string action, string entityType, string entityId, string details) =>
        db.AuditLogs.Add(new AuditLog { At = now, Action = action, EntityType = entityType, EntityId = entityId, Details = details });

    private static void Anomaly(BillingDbContext db, DateTimeOffset now, string key, string kind, string entityType, string entityId, string details) =>
        db.Anomalies.Add(new BillingAnomaly { Key = key, Kind = kind, EntityType = entityType, EntityId = entityId, Details = details, CreatedAt = now });
}
