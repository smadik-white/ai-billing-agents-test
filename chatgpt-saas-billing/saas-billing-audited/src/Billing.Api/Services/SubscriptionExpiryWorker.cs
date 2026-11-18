namespace Billing.Api.Services;

public sealed class SubscriptionExpiryWorker(
    SubscriptionExpiryService expiryService,
    IConfiguration configuration,
    ILogger<SubscriptionExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Math.Clamp(configuration.GetValue("SubscriptionExpiry:PollingIntervalSeconds", 30), 1, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try
            {
                await expiryService.ExpireDueSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subscription expiry polling failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
