namespace SaasBilling.Domain;

public sealed class WebhookEvent
{
    private WebhookEvent()
    {
    }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public WebhookStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public string? LastError { get; private set; }

    public static WebhookEvent Received(
        string provider,
        string eventId,
        string eventType,
        string payload,
        string payloadHash,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 50)
        {
            throw new BillingValidationException("Webhook provider is required.");
        }

        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 200)
        {
            throw new BillingValidationException("Webhook event id is required.");
        }

        if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 100)
        {
            throw new BillingValidationException("Webhook event type is required.");
        }

        return new WebhookEvent
        {
            Id = Guid.NewGuid(),
            Provider = provider.Trim().ToLowerInvariant(),
            EventId = eventId.Trim(),
            EventType = eventType.Trim(),
            Payload = payload,
            PayloadHash = payloadHash,
            Status = WebhookStatus.Pending,
            ReceivedAt = now
        };
    }

    public void MarkProcessing(DateTimeOffset now)
    {
        if (Status == WebhookStatus.Processed)
        {
            throw new BillingConflictException("Webhook event is already processed.");
        }

        Status = WebhookStatus.Processing;
        Attempts++;
        ProcessingStartedAt = now;
        NextRetryAt = null;
        LastError = null;
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = WebhookStatus.Processed;
        ProcessedAt = now;
        ProcessingStartedAt = null;
        NextRetryAt = null;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset? nextRetryAt)
    {
        Status = WebhookStatus.Failed;
        LastError = Truncate(error);
        NextRetryAt = nextRetryAt;
        ProcessingStartedAt = null;
    }

    public void MarkDeadLettered(string error)
    {
        Status = WebhookStatus.DeadLettered;
        LastError = Truncate(error);
        NextRetryAt = null;
        ProcessingStartedAt = null;
    }

    public bool CanRetry(DateTimeOffset now, int maxAttempts) =>
        Status is WebhookStatus.Pending or WebhookStatus.Failed &&
        Attempts < maxAttempts &&
        (NextRetryAt is null || NextRetryAt <= now);

    private static string Truncate(string error) => error.Length > 4000 ? error[..4000] : error;
}
