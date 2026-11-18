using System.Text.Json;
using SaasBilling.Api.Data;
using SaasBilling.Domain;

namespace SaasBilling.Api.Infrastructure;

public static class AuditWriter
{
    public static void Add(
        BillingDbContext db,
        RequestContext requestContext,
        Guid? organizationId,
        string entityType,
        Guid? entityId,
        string action,
        object details,
        DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.AuditLogs.Add(AuditLog.Write(
            organizationId,
            entityType,
            entityId,
            action,
            json,
            requestContext.CorrelationId,
            now));
    }
}
