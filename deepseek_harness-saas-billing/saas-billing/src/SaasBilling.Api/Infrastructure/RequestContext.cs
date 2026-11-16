namespace SaasBilling.Api.Infrastructure;

/// <summary>Scoped correlation id, filled by middleware and copied into the audit journal.</summary>
public sealed class RequestContext
{
    public string? CorrelationId { get; set; }
}
