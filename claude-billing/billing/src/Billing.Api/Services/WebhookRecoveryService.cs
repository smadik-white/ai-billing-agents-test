using Microsoft.Extensions.Options;

namespace Billing.Api.Services;

/// <summary>Periodically retries webhook events that were stored but not applied (crash, transient error, out-of-order).</summary>
public sealed class WebhookRecoveryService(
    WebhookProcessor processor, IOptions<BillingOptions> options, TimeProvider clock, ILogger<WebhookRecoveryService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.RecoveryEnabled) return;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.RecoveryIntervalSeconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var n = await processor.RecoverAsync(clock.GetUtcNow(), stoppingToken);
                    if (n > 0) log.LogInformation("Recovery attempted {Count} webhook event(s)", n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Webhook recovery iteration failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
