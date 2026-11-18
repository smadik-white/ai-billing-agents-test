namespace SaasBilling.Domain;

/// <summary>Append-only business audit journal. Written in the same transaction as state changes.</summary>
public sealed class AuditLog
{
    private AuditLog()
    {
    }

    public Guid Id { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public Guid? EntityId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Details { get; private set; } = "{}";
    public string? TraceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Write(
        Guid? organizationId,
        string entityType,
        Guid? entityId,
        string action,
        string details,
        string? traceId,
        DateTimeOffset now)
    {
        return new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Details = string.IsNullOrWhiteSpace(details) ? "{}" : details,
            TraceId = traceId,
            CreatedAt = now
        };
    }
}
