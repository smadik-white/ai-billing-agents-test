using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Billing.Api.Contracts;
using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("BillingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:BillingDb is required.");

builder.Services.AddDbContextFactory<BillingDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
builder.Services.AddSingleton<WebhookSignatureValidator>();
builder.Services.AddSingleton<IWebhookFaultInjector, NoopWebhookFaultInjector>();
builder.Services.AddSingleton<WebhookProcessor>();
builder.Services.AddSingleton<PaymentAttemptService>();
builder.Services.AddSingleton<SubscriptionExpiryService>();
builder.Services.AddHostedService<SubscriptionExpiryWorker>();
builder.Services.AddHostedService<WebhookRecoveryWorker>();
builder.Services.AddHostedService<PaymentAttemptRecoveryWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

// For this runnable sample we create the schema on startup. Replace this with reviewed EF
// migrations before production deployment (see README.md).
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BillingDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/organizations", async (
    CreateOrganizationRequest request,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        return ValidationError("name", "Name is required and must be at most 200 characters.");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var organization = new Organization { Name = request.Name.Trim() };
    db.Organizations.Add(organization);
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        OrganizationId = organization.Id,
        EntityType = "Organization",
        EntityId = organization.Id.ToString("D"),
        EventType = "OrganizationCreated"
    });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organization.Id}", new
    {
        organization.Id,
        organization.Name,
        organization.CreatedAtUtc
    });
});

app.MapGet("/api/organizations/{organizationId:guid}", async (
    Guid organizationId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var organization = await db.Organizations.AsNoTracking()
        .Where(x => x.Id == organizationId)
        .Select(x => new
        {
            x.Id,
            x.Name,
            x.CreatedAtUtc,
            Subscriptions = x.Subscriptions.Select(s => new
            {
                s.Id, s.ProductCode, s.Price, s.Currency, s.BillingPeriodDays, s.Status,
                s.CurrentPeriodStartUtc, s.CurrentPeriodEndUtc, s.CancelledAtUtc, s.CreatedAtUtc
            }).ToList()
        })
        .SingleOrDefaultAsync(ct);
    return organization is null ? Results.NotFound() : Results.Ok(organization);
});

app.MapPost("/api/organizations/{organizationId:guid}/subscriptions", async (
    Guid organizationId,
    CreateSubscriptionRequest request,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    var currency = NormalizeCurrency(request.Currency);
    if (string.IsNullOrWhiteSpace(request.ProductCode) || request.ProductCode.Trim().Length > 100)
        return ValidationError("productCode", "Product code is required and must be at most 100 characters.");
    if (request.Price <= 0 || request.Price != decimal.Round(request.Price, 2))
        return ValidationError("price", "Price must be positive and have no more than 2 decimal places.");
    if (currency is null)
        return ValidationError("currency", "Currency must be a 3-letter ISO-style code.");
    if (request.BillingPeriodDays is < 1 or > 366)
        return ValidationError("billingPeriodDays", "Billing period must be between 1 and 366 days.");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    // Serialize creation of the same org/product subscription, matching the unique DB index.
    var lockedOrganizationRows = await db.Organizations
        .FromSqlInterpolated($"SELECT * FROM \"Organizations\" WHERE \"Id\" = {organizationId} FOR UPDATE")
        .ToListAsync(ct);
    var organization = lockedOrganizationRows.SingleOrDefault();
    if (organization is null)
        return Results.NotFound(new { error = "Organization was not found." });

    var productCode = request.ProductCode.Trim();
    var existing = await db.Subscriptions.AnyAsync(
        x => x.OrganizationId == organizationId && x.ProductCode == productCode, ct);
    if (existing)
        return Results.Conflict(new { error = "This organization already has a subscription for the product." });

    var subscription = new Subscription
    {
        OrganizationId = organizationId,
        ProductCode = productCode,
        Price = request.Price,
        Currency = currency,
        BillingPeriodDays = request.BillingPeriodDays
    };
    db.Subscriptions.Add(subscription);
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        OrganizationId = organizationId,
        EntityType = "Subscription",
        EntityId = subscription.Id.ToString("D"),
        EventType = "SubscriptionCreated"
    });
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Created($"/api/subscriptions/{subscription.Id}", new
    {
        subscription.Id,
        subscription.OrganizationId,
        subscription.ProductCode,
        subscription.Price,
        subscription.Currency,
        subscription.BillingPeriodDays,
        subscription.Status,
        subscription.CreatedAtUtc
    });
});

app.MapPost("/api/subscriptions/{subscriptionId:guid}/cancel", async (
    Guid subscriptionId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var rows = await db.Subscriptions
        .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {subscriptionId} FOR UPDATE")
        .ToListAsync(ct);
    var subscription = rows.SingleOrDefault();
    if (subscription is null)
        return Results.NotFound(new { error = "Subscription was not found." });
    if (subscription.Status == SubscriptionStatus.Cancelled)
    {
        await tx.CommitAsync(ct);
        return Results.Ok(new { subscription.Id, subscription.Status, subscription.CancelledAtUtc, alreadyCancelled = true });
    }

    var now = DateTime.UtcNow;
    subscription.Status = SubscriptionStatus.Cancelled;
    subscription.CancelledAtUtc = now;
    subscription.CurrentPeriodEndUtc = now;
    var grants = await db.SubscriptionPeriodGrants.Where(x => x.SubscriptionId == subscription.Id && x.IsActive).ToListAsync(ct);
    foreach (var grant in grants) grant.IsActive = false;
    var entitlement = await db.ProductEntitlements.SingleOrDefaultAsync(
        x => x.OrganizationId == subscription.OrganizationId && x.ProductCode == subscription.ProductCode, ct);
    if (entitlement is not null)
    {
        entitlement.IsActive = false;
        entitlement.ValidUntilUtc = now;
        entitlement.UpdatedAtUtc = now;
    }
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        OrganizationId = subscription.OrganizationId,
        EntityType = "Subscription",
        EntityId = subscription.Id.ToString("D"),
        EventType = "SubscriptionCancelled",
        DetailsJson = JsonSerializer.Serialize(new { cancelledAtUtc = now, policy = "immediate" })
    });
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { subscription.Id, subscription.Status, subscription.CancelledAtUtc, accessRevokedImmediately = true });
});

app.MapGet("/api/subscriptions/{subscriptionId:guid}", async (
    Guid subscriptionId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var subscription = await db.Subscriptions.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == subscriptionId, ct);
    return subscription is null ? Results.NotFound() : Results.Ok(subscription);
});

app.MapPost("/api/subscriptions/{subscriptionId:guid}/invoices", async (
    Guid subscriptionId,
    CreateInvoiceRequest request,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 100)
        return ValidationError("lines", "At least one and at most 100 invoice lines are required.");

    foreach (var line in request.Lines)
    {
        if (string.IsNullOrWhiteSpace(line.Description) || line.Description.Trim().Length > 500)
            return ValidationError("lines", "Each line needs a description of at most 500 characters.");
        if (line.Quantity is < 1 or > 10000)
            return ValidationError("lines", "Line quantity must be between 1 and 10000.");
        if (line.UnitPrice < 0 || line.UnitPrice != decimal.Round(line.UnitPrice, 2))
            return ValidationError("lines", "Unit price must be non-negative and have no more than 2 decimal places.");
    }

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var subscriptionRows = await db.Subscriptions
        .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"Id\" = {subscriptionId} FOR UPDATE")
        .ToListAsync(ct);
    var subscription = subscriptionRows.SingleOrDefault();
    if (subscription is null)
        return Results.NotFound(new { error = "Subscription was not found." });
    if (subscription.Status == SubscriptionStatus.Cancelled)
        return Results.Conflict(new { error = "Cannot issue a new invoice for a cancelled subscription." });

    var total = request.Lines.Sum(x => decimal.Round(x.UnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero));
    if (total <= 0 || total != subscription.Price)
        return ValidationError("lines", $"Invoice total must equal the subscription price ({subscription.Price:0.00} {subscription.Currency}).");

    var invoice = new Invoice
    {
        OrganizationId = subscription.OrganizationId,
        SubscriptionId = subscription.Id,
        Number = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}".ToUpperInvariant(),
        Currency = subscription.Currency,
        TotalAmount = total,
        BillingPeriodDays = subscription.BillingPeriodDays,
        Status = InvoiceStatus.Issued,
        Lines = request.Lines.Select(x => new InvoiceLine
        {
            Description = x.Description.Trim(),
            Quantity = x.Quantity,
            UnitPrice = x.UnitPrice,
            LineTotal = decimal.Round(x.UnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero)
        }).ToList()
    };
    db.Invoices.Add(invoice);
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        OrganizationId = subscription.OrganizationId,
        EntityType = "Invoice",
        EntityId = invoice.Id.ToString("D"),
        EventType = "InvoiceIssued",
        DetailsJson = JsonSerializer.Serialize(new { invoice.Number, invoice.TotalAmount, invoice.Currency })
    });
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Created($"/api/invoices/{invoice.Id}", new
    {
        invoice.Id,
        invoice.OrganizationId,
        invoice.SubscriptionId,
        invoice.Number,
        invoice.Currency,
        invoice.TotalAmount,
        invoice.Status,
        invoice.CreatedAtUtc,
        Lines = invoice.Lines.Select(x => new { x.Id, x.Description, x.Quantity, x.UnitPrice, x.LineTotal })
    });
});

app.MapGet("/api/invoices/{invoiceId:guid}", async (
    Guid invoiceId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var invoice = await db.Invoices.AsNoTracking()
        .Where(x => x.Id == invoiceId)
        .Select(x => new
        {
            x.Id, x.OrganizationId, x.SubscriptionId, x.Number, x.Currency,
            x.TotalAmount, x.AmountPaid, x.AmountRefunded, OutstandingAmount = x.TotalAmount - x.AmountPaid + x.AmountRefunded,
            x.BillingPeriodDays, x.Status, x.PaidPaymentAttemptId, x.CreatedAtUtc,
            Lines = x.Lines.Select(line => new
            {
                line.Id, line.Description, line.Quantity, line.UnitPrice, line.LineTotal
            }).ToList(),
            PaymentAttempts = x.PaymentAttempts.Select(payment => new
            {
                payment.Id, payment.Provider, payment.ProviderPaymentId, payment.IdempotencyKey, payment.Status,
                payment.Amount, payment.RefundedAmount, payment.Currency, payment.CheckoutUrl, payment.RequiresManualRefund,
                payment.ProviderCreateAttempts, payment.ProviderRetryAfterUtc, payment.ProviderLastError,
                payment.CreatedAtUtc, payment.UpdatedAtUtc
            }).ToList()
        })
        .SingleOrDefaultAsync(ct);
    return invoice is null ? Results.NotFound() : Results.Ok(invoice);
});

app.MapPost("/api/invoices/{invoiceId:guid}/payment-attempts", async (
    Guid invoiceId,
    HttpRequest request,
    PaymentAttemptService paymentService,
    CancellationToken ct) =>
{
    var idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        return Results.BadRequest(new { error = "Idempotency-Key header is required and must contain 1 to 200 characters." });

    decimal? requestedAmount = null;
    if (request.Query.TryGetValue("amount", out var amountValue))
    {
        if (!decimal.TryParse(amountValue.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAmount))
            return ValidationError("amount", "Amount must be a valid decimal using invariant format.");
        requestedAmount = parsedAmount;
    }

    var result = await paymentService.CreateOrGetAsync(invoiceId, idempotencyKey, requestedAmount, ct);
    if (result.Outcome == "NotFound")
        return Results.NotFound(new { error = result.Error });
    if (result.Outcome is "Conflict")
        return Results.Conflict(new { error = result.Error, id = result.PaymentAttempt?.Id });
    if (result.Outcome is "InvalidKey" or "InvalidAmount")
        return ValidationError("payment", result.Error ?? "Invalid payment request.");
    if (result.PaymentAttempt is null)
        return Results.Problem("Payment attempt could not be created.", statusCode: 500);

    var attempt = result.PaymentAttempt;
    var body = new
    {
        attempt.Id,
        attempt.InvoiceId,
        attempt.Provider,
        attempt.ProviderPaymentId,
        attempt.IdempotencyKey,
        attempt.Status,
        attempt.Amount,
        attempt.RefundedAmount,
        attempt.Currency,
        attempt.CheckoutUrl,
        attempt.RequiresManualRefund,
        attempt.CreatedAtUtc
    };
    if (result.Outcome == "ProviderUnavailable")
        return Results.Json(new { error = result.Error, paymentAttempt = body }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (attempt.Status == PaymentStatus.Creating)
        return Results.Accepted($"/api/payment-attempts/{attempt.Id}", body);
    return result.Reused
        ? Results.Ok(body)
        : Results.Created($"/api/payment-attempts/{attempt.Id}", body);
});

app.MapGet("/api/payment-attempts/{paymentAttemptId:guid}", async (
    Guid paymentAttemptId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var attempt = await db.PaymentAttempts.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == paymentAttemptId, ct);
    return attempt is null ? Results.NotFound() : Results.Ok(attempt);
});

app.MapGet("/api/organizations/{organizationId:guid}/entitlements", async (
    Guid organizationId,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var exists = await db.Organizations.AnyAsync(x => x.Id == organizationId, ct);
    if (!exists)
        return Results.NotFound();
    var now = DateTime.UtcNow;
    var items = await db.ProductEntitlements.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId)
        .Select(x => new
        {
            x.Id,
            x.OrganizationId,
            x.ProductCode,
            IsActive = x.IsActive && x.ValidUntilUtc != null && x.ValidUntilUtc > now,
            x.ValidUntilUtc,
            x.UpdatedAtUtc
        }).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/webhooks/test-provider", async (
    HttpRequest request,
    IDbContextFactory<BillingDbContext> dbFactory,
    WebhookSignatureValidator signatureValidator,
    WebhookProcessor processor,
    CancellationToken ct) =>
{
    if (!request.Headers.TryGetValue("X-Test-Signature", out var signature))
        return Results.Unauthorized();

    await using var bodyStream = new MemoryStream();
    var buffer = new byte[8 * 1024];
    while (true)
    {
        var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
        if (read == 0)
            break;
        if (bodyStream.Length + read > 64 * 1024)
            return Results.BadRequest(new { error = "Webhook body must be between 1 byte and 64 KiB." });
        bodyStream.Write(buffer, 0, read);
    }
    var payload = bodyStream.ToArray();
    if (payload.Length == 0)
        return Results.BadRequest(new { error = "Webhook body must be between 1 byte and 64 KiB." });
    if (!signatureValidator.IsValid(payload, signature.ToString()))
        return Results.Unauthorized();

    ProviderWebhook? message;
    try
    {
        message = JsonSerializer.Deserialize<ProviderWebhook>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Malformed webhook JSON." });
    }

    if (message is null || string.IsNullOrWhiteSpace(message.EventId) || message.EventId.Length > 200 ||
        string.IsNullOrWhiteSpace(message.EventType) || message.EventType.Length > 100 ||
        message.PaymentId == Guid.Empty || message.Amount <= 0 || message.Amount != decimal.Round(message.Amount, 2) ||
        message.OccurredAt == default || NormalizeCurrency(message.Currency) is null ||
        (message.EventType == "payment.refunded" && (string.IsNullOrWhiteSpace(message.RefundId) || message.RefundId.Length > 200)))
        return Results.BadRequest(new { error = "Webhook is missing required fields or contains invalid values. Refund events require a stable refundId." });

    var raw = Encoding.UTF8.GetString(payload);
    var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var existing = await db.WebhookEvents.AsNoTracking().SingleOrDefaultAsync(
        x => x.Provider == "test-provider" && x.ProviderEventId == message.EventId, ct);
    if (existing is not null)
    {
        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            await RecordWebhookConflictAsync(dbFactory, existing, payloadHash, raw,
                "Provider event id was replayed with a different payload.", ct);
            return Results.Conflict(new { error = "Provider event id was already used with a different payload.", conflictRecorded = true });
        }
        var duplicateStatus = await processor.ProcessAsync(existing.Id, ct);
        return Results.Accepted(value: new { existing.Id, status = duplicateStatus, duplicate = true });
    }

    var webhook = new WebhookEvent
    {
        Provider = "test-provider",
        ProviderEventId = message.EventId,
        PayloadHash = payloadHash,
        EventType = message.EventType,
        PaymentAttemptId = message.PaymentId,
        Amount = message.Amount,
        Currency = NormalizeCurrency(message.Currency)!,
        ProviderRefundId = message.RefundId,
        RawPayload = raw,
        Status = WebhookStatus.Received,
        NextAttemptAtUtc = DateTime.UtcNow
    };
    db.WebhookEvents.Add(webhook);
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        EntityType = "WebhookEvent",
        EntityId = webhook.Id.ToString("D"),
        EventType = "WebhookReceived",
        DetailsJson = JsonSerializer.Serialize(new { webhook.Provider, webhook.ProviderEventId, webhook.EventType })
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        db.ChangeTracker.Clear();
        var raced = await db.WebhookEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.Provider == "test-provider" && x.ProviderEventId == message.EventId, ct);
        if (raced is null)
            return Results.Conflict(new { error = "Conflicting duplicate provider event." });
        if (!string.Equals(raced.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            await RecordWebhookConflictAsync(dbFactory, raced, payloadHash, raw,
                "Concurrent provider event id collision with different payload.", ct);
            return Results.Conflict(new { error = "Conflicting duplicate provider event.", conflictRecorded = true });
        }
        return Results.Accepted(value: new { raced.Id, raced.Status, duplicate = true });
    }

    await processor.ProcessAsync(webhook.Id, ct);
    await using var readDb = await dbFactory.CreateDbContextAsync(ct);
    var saved = await readDb.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == webhook.Id, ct);
    return Results.Accepted(value: new { saved.Id, saved.Status, saved.AttemptCount, saved.LastError, duplicate = false });
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));

app.MapGet("/api/ops/payment-attempts", async (
    HttpRequest request,
    IConfiguration configuration,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.PaymentAttempts.AsNoTracking()
        .Where(x => x.Status == PaymentStatus.Creating)
        .OrderBy(x => x.ProviderRetryAfterUtc)
        .Take(100)
        .Select(x => new
        {
            x.Id, x.InvoiceId, x.Provider, x.ProviderPaymentId, x.Status, x.Amount, x.Currency,
            x.ProviderCreateAttempts, x.ProviderRetryAfterUtc, x.ProviderLastError, x.CreatedAtUtc, x.UpdatedAtUtc
        }).ToListAsync(ct);
    return Results.Ok(rows);
});

app.MapPost("/api/ops/payment-attempts/{paymentAttemptId:guid}/retry", async (
    Guid paymentAttemptId,
    HttpRequest request,
    IConfiguration configuration,
    PaymentAttemptService paymentService,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();
    var scheduled = await paymentService.ScheduleManualRetryAsync(paymentAttemptId, ct);
    if (!scheduled)
        return Results.Conflict(new { error = "Payment attempt is not in a recoverable Creating state or its provider lease is still active." });

    await paymentService.RetryProviderCreationAsync(paymentAttemptId, ct);
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == paymentAttemptId, ct);
    return attempt.Status == PaymentStatus.Creating
        ? Results.Accepted($"/api/payment-attempts/{attempt.Id}", new { attempt.Id, attempt.Status, attempt.ProviderCreateAttempts, attempt.ProviderRetryAfterUtc, attempt.ProviderLastError })
        : Results.Ok(new { attempt.Id, attempt.Status, attempt.ProviderPaymentId, attempt.CheckoutUrl });
});

app.MapGet("/api/ops/webhooks", async (
    HttpRequest request,
    IConfiguration configuration,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.WebhookEvents.AsNoTracking()
        .OrderByDescending(x => x.ReceivedAtUtc).Take(100)
        .Select(x => new
        {
            x.Id, x.ProviderEventId, x.PayloadHash, x.EventType, x.PaymentAttemptId, x.Amount,
            x.ProviderRefundId, x.Status, x.AttemptCount, x.NextAttemptAtUtc, x.LastError,
            x.ReceivedAtUtc, x.ProcessedAtUtc
        }).ToListAsync(ct);
    return Results.Ok(rows);
});

app.MapGet("/api/ops/webhook-conflicts", async (
    HttpRequest request,
    IConfiguration configuration,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.WebhookConflicts.AsNoTracking()
        .OrderByDescending(x => x.ReceivedAtUtc).Take(100)
        .Select(x => new
        {
            x.Id, x.Provider, x.ProviderEventId, x.ExistingWebhookEventId, x.ExistingPayloadHash,
            x.IncomingPayloadHash, x.IncomingRawPayload, x.Reason, x.ReceivedAtUtc
        }).ToListAsync(ct);
    return Results.Ok(rows);
});

app.MapGet("/api/ops/audit", async (
    HttpRequest request,
    IConfiguration configuration,
    IDbContextFactory<BillingDbContext> dbFactory,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();

    var take = int.TryParse(request.Query["take"], out var parsedTake) ? Math.Clamp(parsedTake, 1, 500) : 100;
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.BillingAuditLogs.AsNoTracking()
        .OrderByDescending(x => x.CreatedAtUtc).Take(take)
        .Select(x => new
        {
            x.Id, x.OrganizationId, x.EntityType, x.EntityId, x.EventType, x.DetailsJson, x.CreatedAtUtc
        }).ToListAsync(ct);
    return Results.Ok(rows);
});

app.MapPost("/api/ops/webhooks/{eventId:guid}/retry", async (
    Guid eventId,
    HttpRequest request,
    IConfiguration configuration,
    WebhookProcessor processor,
    CancellationToken ct) =>
{
    if (!IsOpsAuthorized(request, configuration))
        return Results.Unauthorized();
    var scheduled = await processor.ScheduleManualRetryAsync(eventId, ct);
    if (!scheduled)
        return Results.NotFound(new { error = "Webhook event was not found or is already processed." });
    var status = await processor.ProcessAsync(eventId, ct);
    return Results.Accepted(value: new { id = eventId, status });
});

app.Run();

static async Task RecordWebhookConflictAsync(
    IDbContextFactory<BillingDbContext> dbFactory,
    WebhookEvent existing,
    string incomingHash,
    string incomingPayload,
    string reason,
    CancellationToken ct)
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    db.WebhookConflicts.Add(new WebhookConflict
    {
        Provider = existing.Provider,
        ProviderEventId = existing.ProviderEventId,
        ExistingWebhookEventId = existing.Id,
        ExistingPayloadHash = existing.PayloadHash,
        IncomingPayloadHash = incomingHash,
        IncomingRawPayload = incomingPayload,
        Reason = reason
    });
    db.BillingAuditLogs.Add(new BillingAuditLog
    {
        EntityType = "WebhookEvent",
        EntityId = existing.Id.ToString("D"),
        EventType = "WebhookEventIdentityConflict",
        DetailsJson = JsonSerializer.Serialize(new { existing.ProviderEventId, existing.PayloadHash, incomingHash, reason })
    });
    await db.SaveChangesAsync(ct);
}

static IResult ValidationError(string key, string message) =>
    Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });

static string? NormalizeCurrency(string? currency)
{
    if (string.IsNullOrWhiteSpace(currency)) return null;
    var value = currency.Trim().ToUpperInvariant();
    return value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z') ? value : null;
}

static bool IsOpsAuthorized(HttpRequest request, IConfiguration configuration)
{
    var expected = configuration["Ops:ApiKey"];
    if (string.IsNullOrWhiteSpace(expected) || !request.Headers.TryGetValue("X-Ops-Key", out var supplied))
        return false;
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
    return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

public partial class Program { }
