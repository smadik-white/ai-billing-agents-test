using System.Text;
using Microsoft.EntityFrameworkCore;
using SaasBilling.Api.Contracts;
using SaasBilling.Api.Data;
using SaasBilling.Api.Services;
using SaasBilling.Domain;

namespace SaasBilling.Api.Endpoints;

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            service = "SaaS Billing",
            docs = "See README.md",
            health = "/health"
        }));

        app.MapGet("/health", async (IDbContextFactory<BillingDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? Results.Ok(new { status = "healthy", database = "up" })
                : Results.Json(new { status = "unhealthy", database = "down" }, statusCode: 503);
        });

        app.MapGet("/api/plans", () => Results.Ok(PlanCatalog.All
            .OrderBy(x => x.Price)
            .Select(x => new PlanResponse(x.Code, x.Name, x.ProductCode, x.Price, x.Currency, x.PeriodMonths))));

        app.MapPost("/api/organizations", async (
            CreateOrganizationRequest request,
            BillingService billing,
            CancellationToken ct) =>
        {
            var organization = await billing.CreateOrganizationAsync(request.Name, ct);
            return Results.Created($"/api/organizations/{organization.Id}", organization.ToResponse());
        });

        app.MapGet("/api/organizations/{id:guid}", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var organization = await billing.GetOrganizationAsync(id, ct)
                ?? throw new BillingNotFoundException($"Organization '{id}' was not found.");
            return Results.Ok(organization.ToResponse());
        });

        app.MapPost("/api/organizations/{id:guid}/subscriptions", async (
            Guid id,
            CreateSubscriptionRequest request,
            BillingService billing,
            CancellationToken ct) =>
        {
            var subscription = await billing.CreateSubscriptionAsync(id, request.PlanCode, ct);
            return Results.Created($"/api/subscriptions/{subscription.Id}", subscription.ToResponse());
        });

        app.MapGet("/api/organizations/{id:guid}/access", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var accesses = await billing.GetAccessAsync(id, ct);
            return Results.Ok(accesses.Select(x => x.ToResponse()));
        });

        app.MapGet("/api/organizations/{id:guid}/audit", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var audit = await billing.GetAuditAsync(id, ct);
            return Results.Ok(audit.Select(x => x.ToResponse()));
        });

        app.MapGet("/api/subscriptions/{id:guid}", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var subscription = await billing.GetSubscriptionAsync(id, ct)
                ?? throw new BillingNotFoundException($"Subscription '{id}' was not found.");
            return Results.Ok(subscription.ToResponse());
        });

        app.MapPost("/api/subscriptions/{id:guid}/cancel", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var subscription = await billing.CancelSubscriptionAsync(id, ct);
            return Results.Ok(subscription.ToResponse());
        });

        app.MapPost("/api/invoices", async (
            CreateInvoiceRequest request,
            BillingService billing,
            CancellationToken ct) =>
        {
            var invoice = await billing.CreateInvoiceAsync(request, ct);
            return Results.Created($"/api/invoices/{invoice.Id}", invoice.ToResponse());
        });

        app.MapGet("/api/invoices/{id:guid}", async (
            Guid id,
            BillingService billing,
            CancellationToken ct) =>
        {
            var invoice = await billing.GetInvoiceAsync(id, ct)
                ?? throw new BillingNotFoundException($"Invoice '{id}' was not found.");
            return Results.Ok(invoice.ToResponse());
        });

        app.MapPost("/api/invoices/{invoiceId:guid}/payment-attempts", async (
            Guid invoiceId,
            CreatePaymentAttemptRequest request,
            HttpRequest httpRequest,
            BillingService billing,
            CancellationToken ct) =>
        {
            var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
            var (attempt, created) = await billing.CreatePaymentAttemptAsync(
                invoiceId, idempotencyKey, request.Amount, ct);

            var response = attempt.ToResponse();
            return created
                ? Results.Created($"/api/payment-attempts/{attempt.Id}", response)
                : Results.Ok(response);
        });

        app.MapPost("/api/webhooks/test", async (
            HttpRequest httpRequest,
            WebhookService webhooks,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(httpRequest.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var signature = httpRequest.Headers["X-Billing-Signature"].FirstOrDefault();

            var receipt = await webhooks.ReceiveAsync(body, signature, ct);
            var response = new WebhookReceiptResponse(
                receipt.EventId,
                receipt.Duplicate,
                receipt.Processing.Status,
                receipt.Processing.Retryable,
                receipt.Processing.Error);

            return Results.Json(response, statusCode: StatusFor(receipt));
        });

        app.MapGet("/api/webhooks/{id:guid}", async (
            Guid id,
            IDbContextFactory<BillingDbContext> factory,
            CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var webhook = await db.WebhookEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new BillingNotFoundException($"Webhook event '{id}' was not found.");
            return Results.Ok(webhook.ToResponse());
        });

        app.MapPost("/api/webhooks/{id:guid}/retry", async (
            Guid id,
            HttpRequest httpRequest,
            WebhookService webhooks,
            CancellationToken ct) =>
        {
            var force = bool.TryParse(httpRequest.Query["force"], out var parsedForce) && parsedForce;
            var result = await webhooks.ProcessAsync(id, ct, force);
            var status = result.Status switch
            {
                WebhookStatus.Processed => StatusCodes.Status200OK,
                WebhookStatus.Processing => StatusCodes.Status202Accepted,
                WebhookStatus.DeadLettered => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status500InternalServerError
            };

            return Results.Json(new { eventId = id, status = result.Status, retryable = result.Retryable, error = result.Error },
                statusCode: status);
        });

        return app;
    }

    private static int StatusFor(WebhookReceipt receipt)
    {
        if (receipt.Duplicate)
        {
            return receipt.Processing.Status switch
            {
                WebhookStatus.Processed => StatusCodes.Status200OK,
                WebhookStatus.Pending or WebhookStatus.Processing => StatusCodes.Status202Accepted,
                WebhookStatus.Failed => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status200OK
            };
        }

        return receipt.Processing.Status switch
        {
            WebhookStatus.Processed => StatusCodes.Status200OK,
            WebhookStatus.Processing => StatusCodes.Status202Accepted,
            WebhookStatus.DeadLettered => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError
        };
    }
}
