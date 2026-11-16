using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaasBilling.Domain;

namespace SaasBilling.Api.Infrastructure;

public sealed class BillingExceptionHandler(ILogger<BillingExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            BillingException billing => (billing.StatusCode, TitleFor(billing), billing.Message),
            DbUpdateConcurrencyException => (409, "Concurrent update", "The resource was changed concurrently. Retry the operation."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (409, "Conflict", "The operation violates a uniqueness constraint."),
            BadHttpRequestException bad => (bad.StatusCode, "Bad request", bad.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed request", "The client closed the request."),
            _ => (500, "Internal server error", "An unexpected error occurred. Use the correlation id to find the server log entry.")
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled request failure. CorrelationId={CorrelationId}", httpContext.Items["CorrelationId"]);
        }
        else
        {
            logger.LogWarning(exception, "Request rejected with {StatusCode}.", status);
        }

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        problem.Extensions["correlationId"] = httpContext.Response.Headers["X-Correlation-Id"].FirstOrDefault();

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static string TitleFor(BillingException exception) => exception switch
    {
        BillingValidationException => "Validation failed",
        BillingNotFoundException => "Not found",
        BillingConflictException => "Conflict",
        InvalidWebhookSignatureException => "Invalid webhook signature",
        PermanentWebhookException => "Webhook cannot be applied",
        _ => "Request failed"
    };
}
