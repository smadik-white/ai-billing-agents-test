using System.Text;
using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api;

public static class Endpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/plans", async (CreatePlanRequest r, BillingService s, CancellationToken ct) =>
        {
            var plan = await s.CreatePlanAsync(r, ct);
            return Results.Created($"/api/plans/{plan.Id}", plan);
        });

        api.MapPost("/organizations", async (CreateOrganizationRequest r, BillingService s, CancellationToken ct) =>
        {
            var org = await s.CreateOrganizationAsync(r, ct);
            return Results.Created($"/api/organizations/{org.Id}", org);
        });

        api.MapPost("/subscriptions", async (CreateSubscriptionRequest r, BillingService s, CancellationToken ct) =>
        {
            var sub = await s.CreateSubscriptionAsync(r, ct);
            return Results.Created($"/api/subscriptions/{sub.Id}", sub);
        });

        api.MapGet("/subscriptions/{id:guid}", async (Guid id, BillingService s, CancellationToken ct) =>
            Results.Ok(await s.GetSubscriptionAsync(id, ct)));

        api.MapPost("/invoices", async (CreateInvoiceRequest r, BillingService s, CancellationToken ct) =>
        {
            var invoice = await s.CreateInvoiceAsync(r, ct);
            return Results.Created($"/api/invoices/{invoice.Id}", invoice);
        });

        api.MapGet("/invoices/{id:guid}", async (Guid id, BillingService s, CancellationToken ct) =>
            Results.Ok(await s.GetInvoiceAsync(id, ct)));

        // Optional body {"amountMinor": n} enables partial payments; default = the remaining balance.
        // 201 = this request created the attempt, 200 = replay of an earlier request with the same Idempotency-Key.
        api.MapPost("/invoices/{id:guid}/payment-attempts",
            async (Guid id, CreatePaymentAttemptRequest? body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                BillingService s, CancellationToken ct) =>
            {
                var (attempt, created) = await s.CreatePaymentAttemptAsync(id, body?.AmountMinor, idempotencyKey, ct);
                return created ? Results.Created($"/api/invoices/{id}", attempt) : Results.Ok(attempt);
            });

        api.MapPost("/subscriptions/{id:guid}/cancel", async (Guid id, BillingService s, CancellationToken ct) =>
            Results.Ok(await s.CancelSubscriptionAsync(id, ct)));

        api.MapGet("/organizations/{id:guid}/access/{productCode}",
            async (Guid id, string productCode, BillingService s, CancellationToken ct) =>
                Results.Ok(await s.GetAccessAsync(id, productCode, ct)));

        // ---- provider webhook -------------------------------------------------------------------
        // 200 processed/duplicate/rejected (retrying would not help), 202 stored but deferred
        // (we own the retry), 401 bad signature, 400 malformed.
        api.MapPost("/webhooks/testpay", async (HttpRequest req, WebhookProcessor processor, CancellationToken ct) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var signature = req.Headers[TestPaySignature.HeaderName].FirstOrDefault();

            var result = await processor.ReceiveAsync(body, signature, ct);
            return result.Status switch
            {
                ReceiveStatus.Processed => Results.Ok(new { status = "processed" }),
                ReceiveStatus.Duplicate => Results.Ok(new { status = "duplicate" }),
                ReceiveStatus.Rejected => Results.Ok(new { status = "rejected", message = result.Message }),
                ReceiveStatus.Conflict => Results.Json(new { status = "conflict", message = result.Message }, statusCode: StatusCodes.Status409Conflict),
                ReceiveStatus.Deferred => Results.Json(new { status = "deferred", message = result.Message }, statusCode: StatusCodes.Status202Accepted),
                ReceiveStatus.InvalidSignature => Results.Json(new { error = "invalid_signature", message = result.Message }, statusCode: StatusCodes.Status401Unauthorized),
                _ => Results.BadRequest(new { error = "bad_request", message = result.Message })
            };
        });

        // ---- ops (no auth in this exercise!) ---------------------------------------------------
        var admin = api.MapGroup("/admin");

        admin.MapGet("/webhook-events", async (WebhookEventStatus? status, IDbContextFactory<BillingDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var q = db.WebhookEvents.AsNoTracking().AsQueryable();
            if (status is not null) q = q.Where(e => e.Status == status);
            return Results.Ok(await q.OrderByDescending(e => e.ReceivedAt).Take(100)
                .Select(e => new { e.ProviderEventId, e.Type, e.PaymentId, e.Status, e.Attempts, e.LastError, e.ReceivedAt, e.NextAttemptAt, e.ProcessedAt })
                .ToListAsync(ct));
        });

        admin.MapGet("/anomalies", async (string? kind, bool? unresolved, IDbContextFactory<BillingDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var q = db.Anomalies.AsNoTracking().AsQueryable();
            if (kind is not null) q = q.Where(a => a.Kind == kind);
            if (unresolved == true) q = q.Where(a => a.ResolvedAt == null);
            return Results.Ok(await q.OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct));
        });

        admin.MapGet("/audit", async (string? entityId, string? action, IDbContextFactory<BillingDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var q = db.AuditLogs.AsNoTracking().AsQueryable();
            if (entityId is not null) q = q.Where(a => a.EntityId == entityId);
            if (action is not null) q = q.Where(a => a.Action == action);
            return Results.Ok(await q.OrderByDescending(a => a.Id).Take(200).ToListAsync(ct));
        });

        // Manual recovery of one Failed/DeadLetter event: resets attempts and processes it immediately.
        admin.MapPost("/webhook-events/{providerEventId}/requeue", async (string providerEventId, WebhookProcessor p, CancellationToken ct) =>
        {
            var outcome = await p.RequeueAsync(providerEventId, ct);
            return outcome is null
                ? Results.Json(new { error = "not_requeueable", message = "Event does not exist or is not Failed/DeadLetter" }, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(new { outcome = outcome.ToString() });
        });

        // "Retry now": ignores the backoff schedule, still skips Processed/Rejected/DeadLetter events.
        admin.MapPost("/webhook-events/recover", async (WebhookProcessor p, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(new { attempted = await p.RecoverAsync(clock.GetUtcNow().AddYears(1), ct) }));
    }
}
