using System.Diagnostics;

namespace SaasBilling.Api.Infrastructure;

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        var fromHeader = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        var correlationId = !string.IsNullOrWhiteSpace(fromHeader)
            ? fromHeader.Trim()
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        if (correlationId.Length > 100)
        {
            correlationId = correlationId[..100];
        }

        requestContext.CorrelationId = correlationId;
        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId,
                   ["RequestPath"] = context.Request.Path.Value ?? string.Empty,
                   ["RequestMethod"] = context.Request.Method
               }))
        {
            await next(context);
        }
    }
}
