namespace Billing.Api.Domain;

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public required string ProductCode { get; set; }
    public decimal Price { get; set; }
    public required string Currency { get; set; }
    public int BillingPeriodDays { get; set; }
    public string Status { get; set; } = SubscriptionStatus.PendingPayment;
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<SubscriptionPeriodGrant> PeriodGrants { get; set; } = new List<SubscriptionPeriodGrant>();
}

public static class SubscriptionStatus
{
    public const string PendingPayment = "PendingPayment";
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Cancelled = "Cancelled";
}

public sealed class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public required string Number { get; set; }
    public required string Currency { get; set; }
    public decimal TotalAmount { get; set; }
    // Gross amount captured and gross amount refunded. Net paid is AmountPaid - AmountRefunded.
    public decimal AmountPaid { get; set; }
    public decimal AmountRefunded { get; set; }
    // Snapshot the tariff period so later tariff edits cannot change an already issued invoice.
    public int BillingPeriodDays { get; set; }
    public string Status { get; set; } = InvoiceStatus.Issued;
    public Guid? PaidPaymentAttemptId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
    public ICollection<PaymentAttempt> PaymentAttempts { get; set; } = new List<PaymentAttempt>();
    public decimal NetPaidAmount => AmountPaid - AmountRefunded;
    public decimal OutstandingAmount => Math.Max(0m, TotalAmount - NetPaidAmount);
}

public static class InvoiceStatus
{
    public const string Issued = "Issued";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Paid = "Paid";
    public const string PartiallyRefunded = "PartiallyRefunded";
    public const string Refunded = "Refunded";
}

public sealed class InvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public required string Description { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class PaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public required string Provider { get; set; }
    // Null while provider creation is being recovered. Provider idempotency key is Id.
    public string? ProviderPaymentId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public string Status { get; set; } = PaymentStatus.Creating;
    public decimal Amount { get; set; }
    public decimal RefundedAmount { get; set; }
    public required string Currency { get; set; }
    public string? CheckoutUrl { get; set; }
    public bool RequiresManualRefund { get; set; }
    public int ProviderCreateAttempts { get; set; }
    public DateTime? ProviderRetryAfterUtc { get; set; }
    public string? ProviderLastError { get; set; }
    public DateTime? CapturedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class PaymentStatus
{
    public const string Creating = "Creating";
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string PartiallyRefunded = "PartiallyRefunded";
    public const string Refunded = "Refunded";
    public const string Failed = "Failed";
}

public sealed class PaymentRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentAttemptId { get; set; }
    public PaymentAttempt? PaymentAttempt { get; set; }
    public required string Provider { get; set; }
    public required string ProviderRefundId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public Guid WebhookEventId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SubscriptionPeriodGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public Guid InvoiceId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Provider { get; set; }
    public required string ProviderEventId { get; set; }
    public required string PayloadHash { get; set; }
    public required string EventType { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public string? ProviderRefundId { get; set; }
    public required string RawPayload { get; set; }
    public string Status { get; set; } = WebhookStatus.Received;
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}

public sealed class WebhookConflict
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Provider { get; set; }
    public required string ProviderEventId { get; set; }
    public Guid? ExistingWebhookEventId { get; set; }
    public required string ExistingPayloadHash { get; set; }
    public required string IncomingPayloadHash { get; set; }
    public required string IncomingRawPayload { get; set; }
    public required string Reason { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class WebhookStatus
{
    public const string Received = "Received";
    public const string Failed = "Failed";
    public const string Processed = "Processed";
    public const string DeadLetter = "DeadLetter";
}

public sealed class ProductEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string ProductCode { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ValidUntilUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BillingAuditLog
{
    public long Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string EventType { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
