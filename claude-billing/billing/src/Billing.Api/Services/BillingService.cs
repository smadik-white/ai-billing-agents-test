using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billing.Api.Services;

public sealed record CreatePlanRequest(string Code, string Name, string ProductCode, long PriceMinor, string Currency, int PeriodDays);
public sealed record CreateOrganizationRequest(string Name, string Email);
public sealed record CreateSubscriptionRequest(Guid OrganizationId, string PlanCode);
public sealed record InvoiceItemRequest(string Description, int Quantity, long UnitPriceMinor);
public sealed record CreateInvoiceRequest(Guid OrganizationId, Guid? SubscriptionId, string? Currency, List<InvoiceItemRequest>? Items);
public sealed record AccessInfo(bool HasAccess, DateTimeOffset? ExpiresAt);
public sealed record CreatePaymentAttemptRequest(long? AmountMinor);

/// <summary>Synchronous (user-facing) part of billing: everything except webhook processing.</summary>
public sealed class BillingService(
    IDbContextFactory<BillingDbContext> factory,
    IPaymentProvider provider,
    IOptions<BillingOptions> options,
    TimeProvider clock,
    ILogger<BillingService> log)
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
    /// Creates a payment attempt (full remaining balance by default, or a partial amount) in three steps:
    ///   1. short transaction: reserve the attempt (Status=Creating) - the unique Idempotency-Key index makes
    ///      concurrent requests with the same key collapse into ONE row; no provider call happens yet;
    ///   2. provider call WITHOUT any DB transaction/lock, idempotent per attempt id, with a hard timeout;
    ///   3. short conditional update: Creating -> Pending with the provider payment id.
    /// If we die or time out after step 1 or 2, the row stays Creating; a retry with the same key (or the
    /// recovery sweep) repeats step 2 with the SAME provider key, so the provider returns the same payment.
    /// Same key + different parameters is rejected (422); same key + same parameters returns the original result.
    /// </summary>
    public async Task<(PaymentAttempt Attempt, bool Created)> CreatePaymentAttemptAsync(
        Guid invoiceId, long? requestedAmountMinor, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null)
        {
            idempotencyKey = idempotencyKey.Trim();
            if (idempotencyKey.Length is 0 or > 200)
                throw new DomainException("validation", "Idempotency-Key must be 1..200 characters");
        }
        // Raw request parameters (not the resolved amount), so a legit retry stays "the same request".
        var fingerprint = $"{invoiceId:N}|{(requestedAmountMinor?.ToString() ?? "full")}";

        await using var db = await factory.CreateDbContextAsync(ct);

        if (idempotencyKey is not null)
        {
            var existing = await db.PaymentAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return (await ReplayAsync(existing, fingerprint, ct), false);
        }

        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("not_found", "Invoice not found", 404);
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.PartiallyPaid))
            throw new DomainException("conflict", $"Invoice is {invoice.Status}, payment is not possible", 409);

        var remaining = invoice.TotalMinor - invoice.PaidMinor;
        var amount = requestedAmountMinor ?? remaining;
        if (amount <= 0 || amount > remaining)
            throw new DomainException("validation", $"amountMinor must be between 1 and the remaining balance {remaining}", 422);

        var attempt = new PaymentAttempt
        {
            InvoiceId = invoice.Id, AmountMinor = amount, Currency = invoice.Currency, Status = PaymentAttemptStatus.Creating,
            IdempotencyKey = idempotencyKey, RequestFingerprint = fingerprint, CreatedAt = clock.GetUtcNow()
        };
        db.PaymentAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() && idempotencyKey is not null)
        {
            // A concurrent request with the same key reserved the attempt first: follow it.
            db.ChangeTracker.Clear();
            var winner = await db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            return (await ReplayAsync(winner, fingerprint, ct), false);
        }

        return (await CompleteCreationAsync(attempt.Id, ct), true);
    }

    private async Task<PaymentAttempt> ReplayAsync(PaymentAttempt existing, string fingerprint, CancellationToken ct)
    {
        if (existing.RequestFingerprint != fingerprint)
            throw new DomainException("idempotency_key_reused", "This Idempotency-Key was already used with different parameters", 422);
        return existing.Status == PaymentAttemptStatus.Creating ? await CompleteCreationAsync(existing.Id, ct) : existing;
    }

    /// <summary>Steps 2+3. Safe to run any number of times, also concurrently (provider call is idempotent, update is conditional).</summary>
    private async Task<PaymentAttempt> CompleteCreationAsync(Guid attemptId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var a = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId, ct);
        if (a.Status != PaymentAttemptStatus.Creating) return a;

        ProviderPayment created;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
        try
        {
            created = await provider.CreatePaymentAsync(a.Id.ToString("N"), a.InvoiceId, a.AmountMinor, a.Currency, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client went away; the row stays Creating and is completed by retry/sweep
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Provider call failed for attempt {AttemptId}; it stays in Creating", a.Id);
            throw new DomainException("provider_unavailable",
                "Payment provider did not answer in time. Retry with the same Idempotency-Key; no duplicate payment will be created.", 502);
        }

        // Finalize even if the client disconnected meanwhile: the provider payment already exists.
        await db.PaymentAttempts
            .Where(x => x.Id == a.Id && x.Status == PaymentAttemptStatus.Creating)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.ProviderPaymentId, created.PaymentId)
                .SetProperty(x => x.CheckoutUrl, created.CheckoutUrl)
                .SetProperty(x => x.Status, PaymentAttemptStatus.Pending), CancellationToken.None);
        return await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == a.Id, CancellationToken.None);
    }

    /// <summary>Recovery sweep: finishes attempts that were reserved but never confirmed (crash/timeout between steps).</summary>
    public async Task<int> ResumeStaleCreationsAsync(DateTimeOffset createdBefore, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            ids = await db.PaymentAttempts
                .Where(a => a.Status == PaymentAttemptStatus.Creating && a.CreatedAt <= createdBefore)
                .OrderBy(a => a.CreatedAt).Select(a => a.Id).Take(options.Value.RecoveryBatchSize).ToListAsync(ct);
        }

        var done = 0;
        foreach (var id in ids)
        {
            try { await CompleteCreationAsync(id, ct); done++; }
            catch (DomainException) { /* provider still down; next sweep retries */ }
        }
        return done;
    }

    /// <summary>
    /// Cancels a subscription (idempotent). Rule: the already paid period stays usable until it ends;
    /// no further extension happens. A later payment for a cancelled subscription is kept on the invoice
    /// but never extends access - it is flagged as an anomaly for a manual decision.
    /// </summary>
    public async Task<Subscription> CancelSubscriptionAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockRowAsync($$"""SELECT 1 FROM "Subscriptions" WHERE "Id" = {{id}} FOR UPDATE""", ct);
        var sub = await db.Subscriptions.Include(s => s.Plan).SingleOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException("not_found", "Subscription not found", 404);

        if (sub.Status != SubscriptionStatus.Cancelled)
        {
            var now = clock.GetUtcNow();
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            db.AuditLogs.Add(new AuditLog
            {
                At = now, Action = "SubscriptionCancelledByUser", EntityType = "Subscription", EntityId = sub.Id.ToString(),
                Details = $"access remains until {sub.CurrentPeriodEnd:O}"
            });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return sub;
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
