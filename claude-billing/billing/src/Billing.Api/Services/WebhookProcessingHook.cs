namespace Billing.Api.Services;

/// <summary>
/// Observation point inside webhook processing (steps: after-lock, attempt-updated, invoice-updated,
/// subscription-updated, refund-recorded). No-op in production; integration tests use it to inject a crash
/// between two writes of the same transaction and prove that nothing is applied partially.
/// </summary>
public interface IWebhookProcessingHook
{
    Task OnStepAsync(string step, CancellationToken ct);
}

public sealed class NoopWebhookProcessingHook : IWebhookProcessingHook
{
    public Task OnStepAsync(string step, CancellationToken ct) => Task.CompletedTask;
}
