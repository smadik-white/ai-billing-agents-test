namespace SaasBilling.Domain;

/// <summary>Base class for expected, mapped-to-HTTP errors.</summary>
public class BillingException(string message, int statusCode) : Exception(message)
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
