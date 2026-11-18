using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

public sealed record CreatePaymentResult(
    string Outcome,
    PaymentAttempt? PaymentAttempt,
    bool Reused,
    string? Error = null);

public sealed class PaymentAttemptService(
    IDbContextFactory<BillingDbContext> dbFactory,
    IPaymentProvider provider,
    ILogger<PaymentAttemptService> logger)
{
    public async Task<CreatePaymentResult> CreateOrGetAsync(
        Guid invoiceId,
        string idempotencyKey,
        decimal? requestedAmount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return new CreatePaymentResult("InvalidKey", null, false, "Idempotency-Key must contain 1 to 200 characters.");
        if (requestedAmount is { } requested && (requested <= 0 || requested != decimal.Round(requested, 2)))
            return new CreatePaymentResult("InvalidAmount", null, false, "Amount must be positive and have at most two decimal places.");

        PaymentAttempt attempt;
        var reused = false;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var lockedInvoiceRows = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM \"Invoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var invoice = lockedInvoiceRows.SingleOrDefault();
            if (invoice is null)
                return new CreatePaymentResult("NotFound", null, false, "Invoice was not found.");

            var fingerprint = ComputeFingerprint(invoice.Id, requestedAmount, invoice.Currency);
            var existing = await db.PaymentAttempts.SingleOrDefaultAsync(
                x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(existing.RequestFingerprint), Convert.FromHexString(fingerprint)))
                    return new CreatePaymentResult("Conflict", existing, true,
                        "Idempotency-Key has already been used with different request parameters.");

                attempt = existing;
                reused = true;
            }
            else
            {
                if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Refunded)
                    return new CreatePaymentResult("Conflict", null, false, "Invoice is already settled or refunded.");

                var subscriptionRows = await db.Subscriptions
                    .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {invoice.SubscriptionId} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var subscription = subscriptionRows.Single();
                if (subscription.Status == SubscriptionStatus.Cancelled)
                    return new CreatePaymentResult("Conflict", null, false, "Subscription is cancelled; a new payment cannot be initiated.");

                var reserved = await db.PaymentAttempts
                    .Where(x => x.InvoiceId == invoiceId &&
                                (x.Status == PaymentStatus.Creating || x.Status == PaymentStatus.Pending))
                    .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
                var available = InvoiceFinancialRules.OutstandingAmount(invoice) - reserved;
                var amount = requestedAmount ?? available;
                if (amount <= 0 || amount > available)
                    return new CreatePaymentResult("Conflict", null, false,
                        $"Requested amount exceeds the remaining unreserved balance ({Math.Max(0m, available):0.00} {invoice.Currency}).");

                attempt = new PaymentAttempt
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    Provider = provider.Name,
                    IdempotencyKey = idempotencyKey,
                    RequestFingerprint = fingerprint,
                    Status = PaymentStatus.Creating,
                    Amount = amount,
                    Currency = invoice.Currency,
                    ProviderRetryAfterUtc = DateTime.UtcNow
                };
                db.PaymentAttempts.Add(attempt);
                AddAudit(db, invoice.OrganizationId, "PaymentAttempt", attempt.Id.ToString("D"),
                    "PaymentAttemptReserved", new { attempt.IdempotencyKey, attempt.Amount, attempt.Currency });
                await db.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        if (attempt.Status == PaymentStatus.Creating)
        {
            var providerResult = await TryCreateWithProviderAsync(attempt.Id, cancellationToken);
            if (!providerResult)
            {
                await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
                var pendingAttempt = await readDb.PaymentAttempts.AsNoTracking()
                    .SingleAsync(x => x.Id == attempt.Id, cancellationToken);
                var outcome = pendingAttempt.ProviderLastError is null ? "InProgress" : "ProviderUnavailable";
                return new CreatePaymentResult(outcome, pendingAttempt, reused,
                    "Payment attempt is durably reserved. Retry the same Idempotency-Key; the provider call is idempotent.");
            }
        }

        await using var finalDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await finalDb.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id, cancellationToken);
        return new CreatePaymentResult("Ok", result, reused);
    }

    public async Task<bool> RetryProviderCreationAsync(Guid paymentAttemptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var isCreating = await db.PaymentAttempts.AsNoTracking()
            .AnyAsync(x => x.Id == paymentAttemptId && x.Status == PaymentStatus.Creating &&
                           (x.ProviderRetryAfterUtc == null || x.ProviderRetryAfterUtc <= DateTime.UtcNow), cancellationToken);
        return isCreating && await TryCreateWithProviderAsync(paymentAttemptId, cancellationToken);
    }

    public async Task<bool> ScheduleManualRetryAsync(Guid paymentAttemptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {paymentAttemptId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var attempt = rows.SingleOrDefault();
        var now = DateTime.UtcNow;
        if (attempt is null || attempt.Status != PaymentStatus.Creating ||
            (attempt.ProviderLastError is null && attempt.ProviderRetryAfterUtc is { } leaseUntil && leaseUntil > now))
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        attempt.ProviderRetryAfterUtc = now;
        attempt.ProviderLastError = null;
        attempt.UpdatedAtUtc = now;
        AddAudit(db, null, "PaymentAttempt", attempt.Id.ToString("D"), "ProviderPaymentManualRetryScheduled",
            new { attempt.ProviderCreateAttempts, attempt.ProviderPaymentId });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TryCreateWithProviderAsync(Guid paymentAttemptId, CancellationToken cancellationToken)
    {
        PaymentAttempt attempt;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var rows = await db.PaymentAttempts
                .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {paymentAttemptId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            attempt = rows.SingleOrDefault()!;
            if (attempt is null || attempt.Status != PaymentStatus.Creating)
            {
                await tx.RollbackAsync(cancellationToken);
                return attempt is not null;
            }

            var now = DateTime.UtcNow;
            if (attempt.ProviderRetryAfterUtc is { } retryAt && retryAt > now)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Lease is committed before the network call. A crash expires the lease and lets the
            // recovery worker retry the same provider idempotency key later.
            attempt.ProviderCreateAttempts++;
            attempt.ProviderRetryAfterUtc = now.AddSeconds(30);
            attempt.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        ProviderPayment providerPayment;
        try
        {
            providerPayment = await provider.CreatePaymentAsync(
                attempt.Id, attempt.Amount, attempt.Currency, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Provider payment creation failed for attempt {PaymentAttemptId}", paymentAttemptId);
            await RecordProviderFailureAsync(paymentAttemptId, ex.Message, cancellationToken);
            return false;
        }

        await using var finalDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var finalTx = await finalDb.Database.BeginTransactionAsync(cancellationToken);
        var finalRows = await finalDb.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {paymentAttemptId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var finalAttempt = finalRows.Single();
        if (finalAttempt.ProviderPaymentId is not null && finalAttempt.ProviderPaymentId != providerPayment.ProviderPaymentId)
            throw new InvalidOperationException("Provider returned a different payment id for the same idempotency key.");

        finalAttempt.ProviderPaymentId = providerPayment.ProviderPaymentId;
        finalAttempt.CheckoutUrl = providerPayment.CheckoutUrl;
        finalAttempt.ProviderLastError = null;
        finalAttempt.ProviderRetryAfterUtc = null;
        if (finalAttempt.Status == PaymentStatus.Creating)
            finalAttempt.Status = PaymentStatus.Pending;
        finalAttempt.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(finalDb, null, "PaymentAttempt", finalAttempt.Id.ToString("D"),
            "ProviderPaymentCreated", new { finalAttempt.ProviderPaymentId, finalAttempt.ProviderCreateAttempts });
        await finalDb.SaveChangesAsync(cancellationToken);
        await finalTx.CommitAsync(cancellationToken);
        return true;
    }

    private async Task RecordProviderFailureAsync(Guid paymentAttemptId, string error, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {paymentAttemptId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var attempt = rows.SingleOrDefault();
        if (attempt is null || attempt.Status != PaymentStatus.Creating)
        {
            await tx.RollbackAsync(cancellationToken);
            return;
        }
        attempt.ProviderLastError = error.Length > 1900 ? error[..1900] : error;
        var delay = Math.Min(900, Math.Pow(2, Math.Min(attempt.ProviderCreateAttempts, 9)));
        attempt.ProviderRetryAfterUtc = DateTime.UtcNow.AddSeconds(delay);
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public static string ComputeFingerprint(Guid invoiceId, decimal? requestedAmount, string currency)
    {
        var amount = requestedAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "AUTO";
        var canonical = $"v1|{invoiceId:D}|{amount}|{currency.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void AddAudit(BillingDbContext db, Guid? organizationId, string entityType,
        string entityId, string eventType, object details)
    {
        db.BillingAuditLogs.Add(new BillingAuditLog
        {
            OrganizationId = organizationId,
            EntityType = entityType,
            EntityId = entityId,
            EventType = eventType,
            DetailsJson = JsonSerializer.Serialize(details)
        });
    }
}
