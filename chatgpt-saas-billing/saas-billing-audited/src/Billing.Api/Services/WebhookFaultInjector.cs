namespace Billing.Api.Services;

/// <summary>Fault-injection seam used to prove transaction rollback/recovery in integration tests.</summary>
public interface IWebhookFaultInjector
{
    Task AfterBusinessMutationAsync(Guid webhookEventId, CancellationToken cancellationToken);
}

public sealed class NoopWebhookFaultInjector : IWebhookFaultInjector
{
    public Task AfterBusinessMutationAsync(Guid webhookEventId, CancellationToken cancellationToken) => Task.CompletedTask;
}
