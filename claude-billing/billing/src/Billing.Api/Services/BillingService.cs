using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

public sealed record CreatePlanRequest(string Code, string Name, string ProductCode, long PriceMinor, string Currency, int PeriodDays);
public sealed record CreateOrganizationRequest(string Name, string Email);
public sealed record CreateSubscriptionRequest(Guid OrganizationId, string PlanCode);
public sealed record InvoiceItemRequest(string Description, int Quantity, long UnitPriceMinor);
public sealed record CreateInvoiceRequest(Guid OrganizationId, Guid? SubscriptionId, string? Currency, List<InvoiceItemRequest>? Items);
public sealed record AccessInfo(bool HasAccess, DateTimeOffset? ExpiresAt);

/// <summary>Synchronous (user-facing) part of billing: everything except webhook processing.</summary>
public sealed class BillingService(IDbContextFactory<BillingDbContext> factory, IPaymentProvider provider, TimeProvider clock)
{
    public async Task<Plan> CreatePlanAsync(CreatePlanRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.ProductCode))
            throw new DomainException("validation", "code, name and productCode are required");
        if (r.PriceMinor <= 0 || r.PeriodDays <= 0)
            throw new DomainException("validation", "priceMinor and periodDays must be positive");
        if (r.Currency is not { Length: 3 })
            throw new DomainException("validation", "currency must be a 3-letter code");

        var plan = new Plan
        {
            Code = r.Code.Trim(), Name = r.Name.Trim(), ProductCode = r.ProductCode.Trim(),
            PriceMinor = r.PriceMinor, Currency = r.Currency.ToUpperInvariant(), PeriodDays = r.PeriodDays
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Plans.Add(plan);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new DomainException("conflict", $"Plan '{plan.Code}' already exists", 409);
        }
        return plan;
    }

    public async Task<Organization> CreateOrganizationAsync(CreateOrganizationRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@'))
            throw new DomainException("validation", "name and a valid email are required");

        var org = new Organization { Name = r.Name.Trim(), Email = r.Email.Trim(), CreatedAt = clock.GetUtcNow() };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Organizations.Add(org);
        await db.SaveChangesAsync(ct);
        return org;
    }

    /// <summary>Creates a Pending subscription. It becomes Active only after a confirmed payment of its invoice.</summary>
    public async Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest r, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Organizations.AnyAsync(o => o.Id == r.OrganizationId, ct))
            throw new DomainException("not_found", "Organization not found", 404);
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Code == r.PlanCode, ct)
                   ?? throw new DomainException("not_found", $"Plan '{r.PlanCode}' not found", 404);

        var sub = new Subscription
        {
            OrganizationId = r.OrganizationId, PlanId = plan.Id, Plan = plan,
            Status = SubscriptionStatus.Pending, CreatedAt = clock.GetUtcNow()
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> GetSubscriptionAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking().Include(s => s.Plan).SingleOrDefaultAsync(s => s.Id == id, ct)
               ?? throw new DomainException("not_found", "Subscription not found", 404);
    }

    /// <summary>
    /// Creates an Open invoice. If items are omitted and a subscription is given,
    /// the invoice gets one line with the plan price (initial purchase or renewal).
    /// </summary>
    public async Task<Invoice> CreateInvoiceAsync(CreateInvoiceRequest r, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Organizations.AnyAsync(o => o.Id == r.OrganizationId, ct))
            throw new DomainException("not_found", "Organization not found", 404);

        Subscription? sub = null;
        if (r.SubscriptionId is { } sid)
        {
            sub = await db.Subscriptions.Include(s => s.Plan)
                      .SingleOrDefaultAsync(s => s.Id == sid && s.OrganizationId == r.OrganizationId, ct)
                  ?? throw new DomainException("not_found", "Subscription not found for this organization", 404);
            if (sub.Status == SubscriptionStatus.Cancelled)
                throw new DomainException("conflict", "Subscription is cancelled", 409);
        }

        List<InvoiceItemRequest> requested;
        if (r.Items is { Count: > 0 }) requested = r.Items;
        else if (sub is not null) requested = new List<InvoiceItemRequest> { new(sub.Plan.Name, 1, sub.Plan.PriceMinor) };
        else throw new DomainException("validation", "items are required when there is no subscription");

        var currency = (sub?.Plan.Currency ?? r.Currency ?? "RUB").ToUpperInvariant();
        if (currency.Length != 3) throw new DomainException("validation", "currency must be a 3-letter code");

        var now = clock.GetUtcNow();
        var invoice = new Invoice
        {
            Number = $"INV-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            OrganizationId = r.OrganizationId, SubscriptionId = sub?.Id, Currency = currency,
            Status = InvoiceStatus.Open, CreatedAt = now
        };

        try
        {
            foreach (var i in requested)
            {
                if (string.IsNullOrWhiteSpace(i.Description) || i.Quantity <= 0 || i.UnitPriceMinor < 0)
                    throw new DomainException("validation", "each item needs a description, quantity > 0 and unitPriceMinor >= 0");
                invoice.Items.Add(new InvoiceItem
                {
                    Description = i.Description.Trim(), Quantity = i.Quantity, UnitPriceMinor = i.UnitPriceMinor,
                    TotalMinor = checked(i.Quantity * i.UnitPriceMinor)
                });
            }
            invoice.TotalMinor = invoice.Items.Sum(i => i.TotalMinor);
        }
        catch (OverflowException)
        {
            throw new DomainException("validation", "invoice total is too large");
        }
        if (invoice.TotalMinor <= 0) throw new DomainException("validation", "invoice total must be positive");

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<Invoice> GetInvoiceAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Invoices.AsNoTracking().Include(i => i.Items).SingleOrDefaultAsync(i => i.Id == id, ct)
               ?? throw new DomainException("not_found", "Invoice not found", 404);
    }

    /// <summary>
    /// Creates a payment attempt for an Open invoice. With an Idempotency-Key a retried request
    /// returns the same attempt instead of creating a second payment at the provider.
    /// </summary>
    public async Task<(PaymentAttempt Attempt, bool Created)> CreatePaymentAttemptAsync(
        Guid invoiceId, string? idempotencyKey, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("not_found", "Invoice not found", 404);

        if (idempotencyKey is not null)
        {
            var existing = await db.PaymentAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.InvoiceId == invoiceId && a.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return (existing, false);
        }

        if (invoice.Status != InvoiceStatus.Open)
            throw new DomainException("conflict", $"Invoice is {invoice.Status}, payment is not possible", 409);

        var created = await provider.CreatePaymentAsync(invoice.Id, invoice.TotalMinor, invoice.Currency, ct);
        var attempt = new PaymentAttempt
        {
            InvoiceId = invoice.Id, ProviderPaymentId = created.PaymentId, CheckoutUrl = created.CheckoutUrl,
            AmountMinor = invoice.TotalMinor, Currency = invoice.Currency, Status = PaymentAttemptStatus.Pending,
            IdempotencyKey = idempotencyKey, CreatedAt = clock.GetUtcNow()
        };
        db.PaymentAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
            return (attempt, true);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() && idempotencyKey is not null)
        {
            // Concurrent request with the same key won the race.
            db.ChangeTracker.Clear();
            var winner = await db.PaymentAttempts.AsNoTracking()
                .SingleAsync(a => a.InvoiceId == invoiceId && a.IdempotencyKey == idempotencyKey, ct);
            return (winner, false);
        }
    }

    public async Task<AccessInfo> GetAccessAsync(Guid organizationId, string productCode, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var a = await db.ProductAccesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.ProductCode == productCode, ct);
        var has = a is { RevokedAt: null } && a.ExpiresAt > clock.GetUtcNow();
        return new AccessInfo(has, a?.ExpiresAt);
    }
}
