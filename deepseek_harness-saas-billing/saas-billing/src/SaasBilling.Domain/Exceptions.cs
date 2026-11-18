namespace SaasBilling.Domain;

/// <summary>Base class for expected, mapped-to-HTTP errors.</summary>
public class BillingException(string message, int statusCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class BillingValidationException(string message) : BillingException(message, 400);

public sealed class BillingNotFoundException(string message) : BillingException(message, 404);

public sealed class BillingConflictException(string message) : BillingException(message, 409);

/// <summary>Signature/replay validation failed; mapped to 401.</summary>
public sealed class InvalidWebhookSignatureException(string message) : BillingException(message, 401);

/// <summary>The webhook is authentic, but its content can never be applied (bad amount, unknown object).</summary>
public sealed class PermanentWebhookException(string message) : BillingException(message, 422);

/// <summary>
/// The external payment provider could not be reached or refused the call. The local payment attempt
/// is left in a resumable state; the client may retry with the same Idempotency-Key.
/// </summary>
public sealed class PaymentProviderUnavailableException(string message, Exception? innerException = null)
    : BillingException(message, 502, innerException);
