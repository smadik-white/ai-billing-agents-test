using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaasBilling.Api.Contracts;
using SaasBilling.Api.Data;
using SaasBilling.Api.Infrastructure;
using SaasBilling.Api.Payments;
using SaasBilling.Domain;

namespace SaasBilling.Api.Services;

public sealed class BillingService(
    BillingDbContext db,
    IPaymentProvider paymentProvider,
    RequestContext requestContext,
    TimeProvider timeProvider,
    ILogger<BillingService> logger)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<Organization> CreateOrganizationAsync(string name, CancellationToken cancellationToken)
    {
        var now = Now;
        var organization = Organization.Create(name, now);
        db.Organizations.Add(organization);
        AuditWriter.Add(db, requestContext, organization.Id, nameof(Organization), organization.Id,
            "OrganizationCreated", new { organization.Name }, now);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Organization {OrganizationId} created", organization.Id);
        return organization;
    }

    public async Task<Subscription> CreateSubscriptionAsync(
        Guid organizationId,
        string planCode,
        CancellationToken cancellationToken)
    {
        var organization = await db.Organizations.FindAsync([organizationId], cancellationToken)
            ?? throw new BillingNotFoundException($"Organization '{organizationId}' was not found.");

        var plan = PlanCatalog.GetRequired(planCode);
        var now = Now;
        var subscription = Subscription.Create(organization.Id, plan, now);
        db.Subscriptions.Add(subscription);
        AuditWriter.Add(db, requestContext, organization.Id, nameof(Subscription), subscription.Id,
            "SubscriptionCreated", new { subscription.PlanCode, subscription.ProductCode, subscription.Price }, now);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Subscription {SubscriptionId} created for organization {OrganizationId}",
            subscription.Id, organization.Id);
        return subscription;
    }

    public async Task<Invoice> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new BillingValidationException("An invoice must contain at least one line.");
        }

        var organization = await db.Organizations.FindAsync([request.OrganizationId], cancellationToken)
            ?? throw new BillingNotFoundException($"Organization '{request.OrganizationId}' was not found.");

        Subscription? subscription = null;
        if (request.SubscriptionId is { } subscriptionId)
        {
            subscription = await db.Subscriptions.FindAsync([subscriptionId], cancellationToken)
                ?? throw new BillingNotFoundException($"Subscription '{subscriptionId}' was not found.");

            if (subscription.OrganizationId != organization.Id)
            {
                throw new BillingValidationException("Subscription does not belong to the specified organization.");
            }

            if (subscription.Status is SubscriptionStatus.Refunded or SubscriptionStatus.Canceled)
            {
                throw new BillingConflictException("Cannot create an invoice for a terminated subscription.");
            }
        }

        var lines = request.Lines
            .Select(line => InvoiceLine.Create(line.Description, line.ProductCode, line.Quantity, line.UnitAmount))
            .ToList();

        var now = Now;
        var invoice = Invoice.Create(
            organization.Id,
            subscription?.Id,
            BuildInvoiceNumber(now),
            request.Currency ?? subscription?.Currency ?? "USD",
            request.DueAt ?? now.AddDays(14),
            lines,
            now);

        db.Invoices.Add(invoice);
        AuditWriter.Add(db, requestContext, organization.Id, nameof(Invoice), invoice.Id,
            "InvoiceCreated", new { invoice.Number, invoice.Total, invoice.Currency, invoice.SubscriptionId }, now);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Invoice {InvoiceId} {InvoiceNumber} created for {OrganizationId}, total {Total} {Currency}",
            invoice.Id, invoice.Number, organization.Id, invoice.Total, invoice.Currency);
        return invoice;
    }

    public async Task<(PaymentAttempt Attempt, bool Created)> CreatePaymentAttemptAsync(
        Guid invoiceId,
        string? idempotencyKey,
        decimal? requestedAmount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new BillingValidationException("Idempotency-Key header is required.");
        }

        idempotencyKey = idempotencyKey.Trim();
        if (idempotencyKey.Length > 200)
        {
            throw new BillingValidationException("Idempotency-Key must be at most 200 characters.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Lock the invoice: payment creation and payment webhooks for the same invoice must serialize.
        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"Id\" = {invoiceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new BillingNotFoundException($"Invoice '{invoiceId}' was not found.");

        var existing = await db.PaymentAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvoiceId == invoice.Id && x.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (existing, false);
        }

        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new BillingConflictException($"Invoice is {invoice.Status}; only an open invoice can be paid.");
        }

        var amount = requestedAmount is null ? invoice.OutstandingAmount : Money.Round(requestedAmount.Value);
        var intent = paymentProvider.CreatePayment(invoice.Id, amount, invoice.Currency, idempotencyKey);
        var now = Now;
        var attempt = PaymentAttempt.Create(
            invoice,
            paymentProvider.Name,
            intent.ProviderPaymentId,
            idempotencyKey,
            amount,
            now);

        db.PaymentAttempts.Add(attempt);
        AuditWriter.Add(db, requestContext, invoice.OrganizationId, nameof(PaymentAttempt), attempt.Id,
            "PaymentAttemptCreated",
            new { attempt.InvoiceId, attempt.Provider, attempt.ProviderPaymentId, attempt.Amount, attempt.Currency },
            now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var duplicate = await db.PaymentAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey,
                    cancellationToken);

            if (duplicate is null)
            {
                throw;
            }

            return (duplicate, false);
        }

        logger.LogInformation("Payment attempt {PaymentAttemptId} {ProviderPaymentId} created for invoice {InvoiceId}",
            attempt.Id, attempt.ProviderPaymentId, invoice.Id);
        return (attempt, true);
    }

    public async Task<Organization?> GetOrganizationAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Organizations
            .AsNoTracking()
            .Include(x => x.Subscriptions)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<Subscription?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Invoices
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Lines)
            .Include(x => x.PaymentAttempts)
            .Include(x => x.Subscription)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ProductAccess>> GetAccessAsync(
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await db.ProductAccesses
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.ProductCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuditLog>> GetAuditAsync(
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

    private static string BuildInvoiceNumber(DateTimeOffset now) =>
        $"INV-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
