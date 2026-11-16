namespace Billing.Api.Contracts;

public sealed record CreateOrganizationRequest(string Name);
public sealed record CreateSubscriptionRequest(string ProductCode, decimal Price, string Currency, int BillingPeriodDays = 30);
public sealed record InvoiceLineRequest(string Description, int Quantity, decimal UnitPrice);
public sealed record CreateInvoiceRequest(IReadOnlyList<InvoiceLineRequest> Lines);

public sealed record ProviderWebhook(
    string EventId,
    string EventType,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt);
