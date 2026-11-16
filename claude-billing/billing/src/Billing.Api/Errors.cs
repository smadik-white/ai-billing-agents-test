namespace Billing.Api;

public sealed class DomainException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Cannot be applied *now* (e.g. refund arrived before the payment). Retried by recovery.</summary>
public sealed class TransientWebhookException(string message) : Exception(message);

/// <summary>Can never be applied (amount mismatch, partial refund...). Event is marked Rejected, no retries.</summary>
public sealed class PermanentWebhookException(string message) : Exception(message);
