namespace Billing.Api;

public sealed class BillingOptions
{
    public string WebhookSecret { get; set; } = "";
    public int SignatureToleranceSeconds { get; set; } = 300;
    public int MaxWebhookAttempts { get; set; } = 5;
    /// <summary>Delay before the recovery job picks up an event that was stored but never finished.</summary>
    public int RecoveryDelaySeconds { get; set; } = 30;
    public bool RecoveryEnabled { get; set; } = true;
    public int RecoveryIntervalSeconds { get; set; } = 15;
    public int RecoveryBatchSize { get; set; } = 50;
    /// <summary>Hard limit for one provider call; no DB transaction is open while waiting.</summary>
    public int ProviderTimeoutSeconds { get; set; } = 15;
    /// <summary>Attempts stuck in Creating for longer than this are completed by the recovery job.</summary>
    public int StaleCreationSeconds { get; set; } = 60;
}
