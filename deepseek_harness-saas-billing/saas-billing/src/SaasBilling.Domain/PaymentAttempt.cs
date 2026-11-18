namespace SaasBilling.Domain;

public sealed class PaymentAttempt
{
    private PaymentAttempt()
    {
    }

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Invoice Invoice { get; private set; } = null!;
    public string Provider { get; private set; } = string.Empty;
    public string ProviderPaymentId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the normalized client request. The same idempotency key with a different
    /// fingerprint is a conflict and must never return or create another payment operation.
    /// </summary>
    public string RequestFingerprint { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SucceededAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public string? FailureReason { get; private set; }

    public static PaymentAttempt Create(
        Invoice invoice,
        string provider,
        string providerPaymentId,
        string idempotencyKey,
        string requestFingerprint,
        decimal amount,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 50)
        {
            throw new BillingValidationException("Payment provider is required.");
        }

        if (string.IsNullOrWhiteSpace(providerPaymentId) || providerPaymentId.Length > 200)
        {
            throw new BillingValidationException("Provider payment id is required.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new BillingValidationException("Idempotency-Key header is required and must be at most 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(requestFingerprint) || requestFingerprint.Length > 64)
        {
            throw new BillingValidationException("Payment request fingerprint is required.");
        }

        amount = Money.Round(amount);
        if (amount <= 0 || amount > invoice.OutstandingAmount)
        {
            throw new BillingValidationException(
                $"Payment amount must be positive and no greater than the outstanding amount ({invoice.OutstandingAmount:0.00} {invoice.Currency}).");
        }

        return new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Provider = provider.Trim().ToLowerInvariant(),
            ProviderPaymentId = providerPaymentId.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            RequestFingerprint = requestFingerprint,
            Amount = amount,
            Currency = invoice.Currency,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public bool MarkSucceeded(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return false;
        }

        if (Status == PaymentStatus.Refunded)
        {
            return false;
        }

        Status = PaymentStatus.Succeeded;
        SucceededAt = now;
        UpdatedAt = now;
        FailureReason = null;
        return true;
    }

    public bool MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.Refunded)
        {
            return false;
        }

        Status = PaymentStatus.Failed;
        FailureReason = reason.Length > 1000 ? reason[..1000] : reason;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Applies a refund to this attempt. Returns true when the whole attempt is refunded.</summary>
    public bool ApplyRefund(decimal amount, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Refunded)
        {
            return true;
        }

        if (Status != PaymentStatus.Succeeded)
        {
            throw new PermanentWebhookException("Only a succeeded payment can be refunded.");
        }

        amount = Money.Round(amount);
        if (amount <= 0 || RefundedAmount + amount > Amount)
        {
            throw new PermanentWebhookException(
                $"Refund amount is invalid for payment {ProviderPaymentId}. Payment amount: {Amount:0.00}, already refunded: {RefundedAmount:0.00}.");
        }

        RefundedAmount = Money.Round(RefundedAmount + amount);
        UpdatedAt = now;
        if (RefundedAmount == Amount)
        {
            Status = PaymentStatus.Refunded;
            RefundedAt = now;
            return true;
        }

        return false;
    }
}
