namespace Billing.Api.Domain;

public enum SubscriptionStatus { Pending, Active, Cancelled }
public enum InvoiceStatus { Open, PartiallyPaid, Paid, PartiallyRefunded, Refunded, Void }
/// <summary>Creating = reserved in our DB, provider call not confirmed yet (see BillingService.CreatePaymentAttemptAsync).</summary>
public enum PaymentAttemptStatus { Creating, Pending, Succeeded, Failed, PartiallyRefunded, Refunded }

/// <summary>Received -> Processed | Failed (retry) -> DeadLetter; Rejected = permanent business error.</summary>
public enum WebhookEventStatus { Received, Processed, Failed, Rejected, DeadLetter }

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public long PriceMinor { get; set; }
    public string Currency { get; set; } = "RUB";
    public int PeriodDays { get; set; }
}

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    public SubscriptionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Currency { get; set; } = "RUB";
    public long TotalMinor { get; set; }
    public InvoiceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set when the invoice became fully paid (== subscription activation moment).</summary>
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>Sum of confirmed payments credited to this invoice. Invariant: PaidMinor &lt;= TotalMinor.</summary>
    public long PaidMinor { get; set; }
    /// <summary>Sum of refunds of credited payments. Invariant: RefundedMinor &lt;= PaidMinor.</summary>
    public long RefundedMinor { get; set; }
    public List<InvoiceItem> Items { get; set; } = [];
}

public class InvoiceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public long UnitPriceMinor { get; set; }
    public long TotalMinor { get; set; }
}

public class PaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    /// <summary>Null while Status == Creating.</summary>
    public string? ProviderPaymentId { get; set; }
    public string? CheckoutUrl { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "RUB";
    public PaymentAttemptStatus Status { get; set; }
    public string? IdempotencyKey { get; set; }
    /// <summary>Canonical form of the request parameters; same key + other fingerprint = rejected.</summary>
    public string RequestFingerprint { get; set; } = "";
    public long RefundedMinor { get; set; }
    /// <summary>
    /// True if the confirmed amount was credited to the invoice. False for surplus money
    /// (invoice already paid/refunded/void or overpayment) that needs a manual refund.
    /// </summary>
    public bool AppliedToInvoice { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class Refund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentAttemptId { get; set; }
    public string ProviderRefundId { get; set; } = "";
    public long AmountMinor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ProductAccess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string ProductCode { get; set; } = "";
    public Guid SubscriptionId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Inbox / journal of every verified webhook delivery.</summary>
public class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderEventId { get; set; } = "";
    public string Type { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public WebhookEventStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Details { get; set; } = "";
}

/// <summary>Things that need a human: conflicting events, surplus payments, rejected/dead-lettered webhooks.</summary>
public class BillingAnomaly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique, makes recording the same anomaly twice a no-op.</summary>
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
