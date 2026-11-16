using SaasBilling.Domain;

namespace SaasBilling.Api.Contracts;

public sealed record CreateOrganizationRequest(string Name);

public sealed record CreateSubscriptionRequest(string PlanCode);

public sealed record CreateInvoiceLineRequest(string Description, string? ProductCode, int Quantity, decimal UnitAmount);

public sealed record CreateInvoiceRequest(
    Guid OrganizationId,
    Guid? SubscriptionId,
    string? Currency,
    DateTimeOffset? DueAt,
    IReadOnlyList<CreateInvoiceLineRequest>? Lines);

public sealed record CreatePaymentAttemptRequest(decimal? Amount);

public sealed record PlanResponse(
    string Code,
    string Name,
    string ProductCode,
    decimal Price,
    string Currency,
    int PeriodMonths);

public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SubscriptionResponse> Subscriptions);

public sealed record SubscriptionResponse(
    Guid Id,
    Guid OrganizationId,
    string PlanCode,
    string PlanName,
    string ProductCode,
    decimal Price,
    string Currency,
    SubscriptionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? TerminatedAt);

public sealed record InvoiceLineResponse(
    Guid Id,
    string Description,
    string? ProductCode,
    int Quantity,
    decimal UnitAmount,
    decimal Amount);

public sealed record InvoiceResponse(
    Guid Id,
    Guid OrganizationId,
    Guid? SubscriptionId,
    string Number,
    string Currency,
    InvoiceStatus Status,
    decimal Subtotal,
    decimal Total,
    decimal AmountPaid,
    decimal AmountRefunded,
    decimal OutstandingAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? RefundedAt,
    IReadOnlyList<InvoiceLineResponse> Lines,
    IReadOnlyList<PaymentAttemptResponse> PaymentAttempts);

public sealed record PaymentAttemptResponse(
    Guid Id,
    Guid InvoiceId,
    string Provider,
    string ProviderPaymentId,
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    decimal RefundedAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? RefundedAt,
    string? FailureReason);

public sealed record ProductAccessResponse(
    Guid Id,
    Guid OrganizationId,
    string ProductCode,
    Guid InvoiceId,
    AccessStatus Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt);

public sealed record WebhookEventResponse(
    Guid Id,
    string Provider,
    string EventId,
    string EventType,
    WebhookStatus Status,
    int Attempts,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessingStartedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? NextRetryAt,
    string? LastError,
    string PayloadHash);

public sealed record WebhookReceiptResponse(
    Guid EventId,
    bool Duplicate,
    WebhookStatus Status,
    bool Retryable,
    string? Error);

public sealed record AuditLogResponse(
    Guid Id,
    Guid? OrganizationId,
    string EntityType,
    Guid? EntityId,
    string Action,
    string Details,
    string? TraceId,
    DateTimeOffset CreatedAt);

public static class ContractMappings
{
    public static OrganizationResponse ToResponse(this Organization organization) => new(
        organization.Id,
        organization.Name,
        organization.CreatedAt,
        organization.Subscriptions
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ToResponse())
            .ToList());

    public static SubscriptionResponse ToResponse(this Subscription subscription) => new(
        subscription.Id,
        subscription.OrganizationId,
        subscription.PlanCode,
        subscription.PlanName,
        subscription.ProductCode,
        subscription.Price,
        subscription.Currency,
        subscription.Status,
        subscription.CreatedAt,
        subscription.ActivatedAt,
        subscription.PeriodStart,
        subscription.PeriodEnd,
        subscription.TerminatedAt);

    public static InvoiceResponse ToResponse(this Invoice invoice) => new(
        invoice.Id,
        invoice.OrganizationId,
        invoice.SubscriptionId,
        invoice.Number,
        invoice.Currency,
        invoice.Status,
        invoice.Subtotal,
        invoice.Total,
        invoice.AmountPaid,
        invoice.AmountRefunded,
        invoice.OutstandingAmount,
        invoice.CreatedAt,
        invoice.DueAt,
        invoice.PaidAt,
        invoice.RefundedAt,
        invoice.Lines.OrderBy(x => x.Description).Select(x => x.ToResponse()).ToList(),
        invoice.PaymentAttempts.OrderByDescending(x => x.CreatedAt).Select(x => x.ToResponse()).ToList());

    public static InvoiceLineResponse ToResponse(this InvoiceLine line) => new(
        line.Id,
        line.Description,
        line.ProductCode,
        line.Quantity,
        line.UnitAmount,
        line.Amount);

    public static PaymentAttemptResponse ToResponse(this PaymentAttempt attempt) => new(
        attempt.Id,
        attempt.InvoiceId,
        attempt.Provider,
        attempt.ProviderPaymentId,
        attempt.IdempotencyKey,
        attempt.Amount,
        attempt.Currency,
        attempt.Status,
        attempt.RefundedAmount,
        attempt.CreatedAt,
        attempt.UpdatedAt,
        attempt.SucceededAt,
        attempt.RefundedAt,
        attempt.FailureReason);

    public static ProductAccessResponse ToResponse(this ProductAccess access) => new(
        access.Id,
        access.OrganizationId,
        access.ProductCode,
        access.InvoiceId,
        access.Status,
        access.GrantedAt,
        access.RevokedAt);

    public static WebhookEventResponse ToResponse(this WebhookEvent webhook) => new(
        webhook.Id,
        webhook.Provider,
        webhook.EventId,
        webhook.EventType,
        webhook.Status,
        webhook.Attempts,
        webhook.ReceivedAt,
        webhook.ProcessingStartedAt,
        webhook.ProcessedAt,
        webhook.NextRetryAt,
        webhook.LastError,
        webhook.PayloadHash);

    public static AuditLogResponse ToResponse(this AuditLog audit) => new(
        audit.Id,
        audit.OrganizationId,
        audit.EntityType,
        audit.EntityId,
        audit.Action,
        audit.Details,
        audit.TraceId,
        audit.CreatedAt);
}
