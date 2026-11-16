using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SaasBilling.Api.Contracts;
using SaasBilling.Api.Data;
using SaasBilling.Api.Infrastructure;
using SaasBilling.Api.Payments;
using SaasBilling.Domain;

namespace SaasBilling.Api.Services;

public sealed class WebhookOptions
{
    public const string SectionName = "Billing:Webhooks";
    public int MaxAttempts { get; set; } = 8;
    public int ProcessingLeaseSeconds { get; set; } = 300;
}

public sealed record WebhookProcessingResult(WebhookStatus Status, bool Retryable, string? Error)
{
    public bool Processed => Status == WebhookStatus.Processed;
    public bool InProgress => Status == WebhookStatus.Processing;
}

public sealed record WebhookReceipt(Guid EventId, bool Duplicate, WebhookProcessingResult Processing);

/// <summary>
/// Inbox + processor. The inbox insert is committed before any business side effect, so a crash
/// cannot lose an authenticated event. Processing itself is transactional and row-locked per invoice.
/// </summary>
public sealed class WebhookService(
    IDbContextFactory<BillingDbContext> dbFactory,
    IPaymentProvider paymentProvider,
    RequestContext requestContext,
    TimeProvider timeProvider,
    IOptions<WebhookOptions> options,
    ILogger<WebhookService> logger)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<WebhookReceipt> ReceiveAsync(
        string rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        var now = Now;
        var webhook = paymentProvider.VerifyAndParse(rawBody, signatureHeader, now);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

        Guid eventId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var inboxEvent = WebhookEvent.Received(
                paymentProvider.Name,
                webhook.Id,
                webhook.Type,
                rawBody,
                hash,
                now);

            db.WebhookEvents.Add(inboxEvent);
            AuditWriter.Add(db, requestContext, null, nameof(WebhookEvent), inboxEvent.Id,
                "WebhookReceived",
                new { inboxEvent.EventId, inboxEvent.EventType, inboxEvent.PayloadHash }, now);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                eventId = inboxEvent.Id;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var existing = await db.WebhookEvents
                    .AsNoTracking()
                    .SingleAsync(x => x.Provider == paymentProvider.Name && x.EventId == webhook.Id, cancellationToken);

                logger.LogInformation(
                    "Duplicate webhook {EventId} ignored; existing inbox id {InboxId}, status {Status}",
                    webhook.Id, existing.Id, existing.Status);

                return new WebhookReceipt(
                    existing.Id,
                    true,
                    new WebhookProcessingResult(
                        existing.Status,
                        existing.Status is WebhookStatus.Failed or WebhookStatus.Pending,
                        existing.LastError));
            }
        }

        var result = await ProcessAsync(eventId, cancellationToken);
        return new WebhookReceipt(eventId, false, result);
    }

    public async Task<WebhookProcessingResult> ProcessAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var claim = await TryClaimAsync(eventId, cancellationToken);
        if (!claim.Claimed)
        {
            return claim.Result!;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            var inboxEvent = await LockEventAsync(db, eventId, cancellationToken)
                ?? throw new BillingNotFoundException($"Webhook event '{eventId}' was not found.");

            if (inboxEvent.Status != WebhookStatus.Processing)
            {
                // Another worker won the race. Its committed result is authoritative.
                return new WebhookProcessingResult(inboxEvent.Status, false, inboxEvent.LastError);
            }

            var payload = paymentProvider.ParseUnverified(inboxEvent.Payload);
            await ApplyAsync(db, payload, Now, cancellationToken);

            inboxEvent.MarkProcessed(Now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Webhook {EventId} {InboxId} processed", inboxEvent.EventId, inboxEvent.Id);
            return new WebhookProcessingResult(WebhookStatus.Processed, false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var permanent = ex is PermanentWebhookException or BillingValidationException or BillingNotFoundException;
            var result = await MarkFailureAsync(eventId, ex, permanent, cancellationToken);

            if (permanent)
            {
                logger.LogWarning(ex, "Webhook {InboxId} dead-lettered", eventId);
            }
            else
            {
                logger.LogError(ex, "Webhook {InboxId} failed and will be retried", eventId);
            }

            return result;
        }
    }

    private async Task<(bool Claimed, WebhookProcessingResult? Result)> TryClaimAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var now = Now;
        var maxAttempts = Math.Max(1, options.Value.MaxAttempts);
        var lease = TimeSpan.FromSeconds(Math.Max(30, options.Value.ProcessingLeaseSeconds));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var inboxEvent = await LockEventAsync(db, eventId, cancellationToken);
        if (inboxEvent is null)
        {
            return (false, new WebhookProcessingResult(WebhookStatus.DeadLettered, false,
                $"Webhook event '{eventId}' was not found."));
        }

        if (inboxEvent.Status == WebhookStatus.Processed)
        {
            await transaction.CommitAsync(cancellationToken);
            return (false, new WebhookProcessingResult(WebhookStatus.Processed, false, null));
        }

        if (inboxEvent.Status == WebhookStatus.DeadLettered)
        {
            await transaction.CommitAsync(cancellationToken);
            return (false, new WebhookProcessingResult(WebhookStatus.DeadLettered, false, inboxEvent.LastError));
        }

        if (inboxEvent.Status == WebhookStatus.Processing &&
            inboxEvent.ProcessingStartedAt is { } startedAt &&
            startedAt > now - lease)
        {
            await transaction.CommitAsync(cancellationToken);
            return (false, new WebhookProcessingResult(WebhookStatus.Processing, false,
                "Webhook is already being processed by another worker."));
        }

        if (inboxEvent.Attempts >= maxAttempts)
        {
            inboxEvent.MarkDeadLettered($"Retry limit of {maxAttempts} attempts reached.");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (false, new WebhookProcessingResult(WebhookStatus.DeadLettered, false, inboxEvent.LastError));
        }

        inboxEvent.MarkProcessing(now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, null);
    }

    private async Task<WebhookProcessingResult> MarkFailureAsync(
        Guid eventId,
        Exception exception,
        bool permanent,
        CancellationToken cancellationToken)
    {
        var now = Now;
        var maxAttempts = Math.Max(1, options.Value.MaxAttempts);
        var message = $"{exception.GetType().Name}: {exception.Message}";

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            var inboxEvent = await LockEventAsync(db, eventId, cancellationToken);
            if (inboxEvent is null)
            {
                return new WebhookProcessingResult(WebhookStatus.DeadLettered, false, message);
            }

            if (inboxEvent.Status == WebhookStatus.Processed)
            {
                await transaction.CommitAsync(cancellationToken);
                return new WebhookProcessingResult(WebhookStatus.Processed, false, null);
            }

            if (permanent)
            {
                inboxEvent.MarkDeadLettered(message);
            }
            else if (inboxEvent.Attempts >= maxAttempts)
            {
                inboxEvent.MarkDeadLettered($"{message} (retry limit {maxAttempts} reached)");
            }
            else
            {
                inboxEvent.MarkFailed(message, now + Backoff(inboxEvent.Attempts));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new WebhookProcessingResult(
                inboxEvent.Status,
                !permanent && inboxEvent.Status == WebhookStatus.Failed,
                inboxEvent.LastError);
        }
        catch (Exception persistenceError)
        {
            // The original event is still recorded (status Processing) and will be reclaimed
            // by the recovery worker after its lease expires.
            logger.LogError(persistenceError,
                "Could not record failure for webhook {InboxId}; recovery will reclaim it", eventId);
            return new WebhookProcessingResult(WebhookStatus.Processing, true, message);
        }
    }

    private async Task ApplyAsync(
        BillingDbContext db,
        ProviderWebhook webhook,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (webhook.Type is not ("payment.succeeded" or "payment.failed" or "payment.refunded"))
        {
            throw new PermanentWebhookException($"Unsupported webhook type '{webhook.Type}'.");
        }

        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"Id\" = {webhook.Data.InvoiceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new PermanentWebhookException($"Invoice '{webhook.Data.InvoiceId}' was not found.");

        // Materialize children explicitly; the raw FOR UPDATE query is intentionally not composed with joins.
        await db.Entry(invoice).Collection(x => x.Lines).LoadAsync(cancellationToken);
        await db.Entry(invoice).Reference(x => x.Subscription).LoadAsync(cancellationToken);

        var normalizedCurrency = Money.NormalizeCurrency(webhook.Data.Currency);
        if (!string.Equals(invoice.Currency, normalizedCurrency, StringComparison.Ordinal))
        {
            throw new PermanentWebhookException(
                $"Webhook currency {normalizedCurrency} does not match invoice currency {invoice.Currency}.");
        }

        var attempt = await db.PaymentAttempts
            .SingleOrDefaultAsync(
                x => x.Provider == paymentProvider.Name && x.ProviderPaymentId == webhook.Data.PaymentId,
                cancellationToken)
            ?? throw new PermanentWebhookException($"Payment attempt '{webhook.Data.PaymentId}' was not found.");

        if (attempt.InvoiceId != invoice.Id)
        {
            throw new PermanentWebhookException("Payment attempt belongs to a different invoice.");
        }

        var amount = Money.Round(webhook.Data.Amount);
        if (webhook.Type == "payment.refunded")
        {
            // Refunds may be partial; the attempt itself enforces the cumulative limit.
            if (amount > attempt.Amount)
            {
                throw new PermanentWebhookException(
                    $"Refund amount {amount:0.00} exceeds payment attempt amount {attempt.Amount:0.00}.");
            }
        }
        else if (amount != attempt.Amount)
        {
            throw new PermanentWebhookException(
                $"Webhook amount {amount:0.00} does not match payment attempt amount {attempt.Amount:0.00}.");
        }

        switch (webhook.Type)
        {
            case "payment.succeeded":
                if (attempt.Status == PaymentStatus.Refunded)
                {
                    AddIgnoredAudit(db, invoice, now, webhook.Type, "Payment already refunded.");
                    return;
                }

                if (invoice.Status == InvoiceStatus.Refunded)
                {
                    throw new PermanentWebhookException("Cannot apply a successful payment to a refunded invoice.");
                }

                attempt.MarkSucceeded(now);
                var fullyPaid = invoice.RegisterPayment(amount, now);
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(PaymentAttempt), attempt.Id,
                    "PaymentSucceeded",
                    new { attempt.ProviderPaymentId, attempt.Amount, attempt.Currency, InvoiceFullyPaid = fullyPaid },
                    now);

                if (fullyPaid)
                {
                    await ActivateSubscriptionAndGrantAccessAsync(db, invoice, now, cancellationToken);
                }

                break;

            case "payment.failed":
                attempt.MarkFailed("Provider reported payment failure.", now);
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(PaymentAttempt), attempt.Id,
                    "PaymentFailed", new { attempt.ProviderPaymentId }, now);
                break;

            case "payment.refunded":
                if (attempt.Status == PaymentStatus.Pending || attempt.Status == PaymentStatus.Failed)
                {
                    // The event may have arrived before the success webhook. Retry with backoff.
                    throw new BillingConflictException("Cannot refund a payment that has not succeeded yet.");
                }

                if (attempt.Status == PaymentStatus.Refunded)
                {
                    AddIgnoredAudit(db, invoice, now, webhook.Type, "Payment is already fully refunded.");
                    return;
                }

                var paymentFullyRefunded = attempt.ApplyRefund(amount, now);
                var invoiceFullyRefunded = invoice.ApplyRefund(amount, now);

                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(PaymentAttempt), attempt.Id,
                    paymentFullyRefunded ? "PaymentRefunded" : "PaymentPartiallyRefunded",
                    new
                    {
                        attempt.ProviderPaymentId,
                        amount,
                        attempt.RefundedAmount,
                        InvoiceFullyRefunded = invoiceFullyRefunded
                    },
                    now);

                if (invoiceFullyRefunded)
                {
                    await ApplyFullRefundAsync(db, invoice, now, cancellationToken);
                }

                break;
        }
    }

    private async Task ActivateSubscriptionAndGrantAccessAsync(
        BillingDbContext db,
        Invoice invoice,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (invoice.Subscription is { } subscription)
        {
            if (subscription.Status is SubscriptionStatus.Refunded or SubscriptionStatus.Canceled)
            {
                throw new PermanentWebhookException("Subscription is terminated; cannot activate it.");
            }

            if (subscription.Activate(now))
            {
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(Subscription), subscription.Id,
                    "SubscriptionActivated",
                    new { subscription.PlanCode, subscription.PeriodStart, subscription.PeriodEnd }, now);
            }

            products.Add(subscription.ProductCode);
        }

        foreach (var product in invoice.Lines
                     .Select(x => x.ProductCode)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Cast<string>())
        {
            products.Add(product);
        }

        foreach (var productCode in products)
        {
            var access = await db.ProductAccesses.SingleOrDefaultAsync(
                x => x.OrganizationId == invoice.OrganizationId && x.ProductCode == productCode,
                cancellationToken);

            if (access is null)
            {
                access = ProductAccess.Grant(invoice.OrganizationId, productCode, invoice.Id, now);
                db.ProductAccesses.Add(access);
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(ProductAccess), access.Id,
                    "ProductAccessGranted", new { access.ProductCode, access.InvoiceId }, now);
            }
            else if (access.ReGrant(invoice.Id, now))
            {
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(ProductAccess), access.Id,
                    "ProductAccessGranted", new { access.ProductCode, access.InvoiceId }, now);
            }
        }
    }

    private async Task ApplyFullRefundAsync(
        BillingDbContext db,
        Invoice invoice,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (invoice.Subscription is { } subscription && subscription.MarkRefunded(now))
        {
            AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(Subscription), subscription.Id,
                "SubscriptionRefunded", new { subscription.PlanCode }, now);
        }

        var accesses = await db.ProductAccesses
            .Where(x => x.OrganizationId == invoice.OrganizationId &&
                        x.InvoiceId == invoice.Id &&
                        x.Status == AccessStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var access in accesses)
        {
            if (access.Revoke(now))
            {
                AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(ProductAccess), access.Id,
                    "ProductAccessRevoked", new { access.ProductCode, access.InvoiceId }, now);
            }
        }

        AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(Invoice), invoice.Id,
            "InvoiceFullyRefunded", new { invoice.AmountRefunded, invoice.Total }, now);
    }

    private void AddIgnoredAudit(
        BillingDbContext db,
        Invoice invoice,
        DateTimeOffset now,
        string type,
        string reason)
    {
        AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(WebhookEvent), null,
            "WebhookIgnored", new { type, reason, invoiceId = invoice.Id }, now);
    }

    private static async Task<WebhookEvent?> LockEventAsync(
        BillingDbContext db,
        Guid eventId,
        CancellationToken cancellationToken) =>
        await db.WebhookEvents
            .FromSqlInterpolated($"SELECT * FROM webhook_events WHERE \"Id\" = {eventId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempts, 1, 8))));

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
