namespace SaasBilling.Domain;

public enum SubscriptionStatus
{
    PendingActivation = 0,
    Active = 1,
    PastDue = 2,
    Refunded = 3,
    Canceled = 4
}

public enum InvoiceStatus
{
    Open = 0,
    Paid = 1,
    Refunded = 2,
    Void = 3
}

public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3
}

public enum AccessStatus
{
    Active = 0,
    Revoked = 1
}

public enum WebhookStatus
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
    DeadLettered = 4
}
