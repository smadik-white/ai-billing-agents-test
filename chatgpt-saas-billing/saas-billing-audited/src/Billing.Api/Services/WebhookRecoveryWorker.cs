using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

public sealed class WebhookRecoveryWorker(
    IDbContextFactory<BillingDbContext> dbFactory,
    WebhookProcessor processor,
    IConfiguration configuration,
    ILogger<WebhookRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Math.Clamp(configuration.GetValue("Recovery:PollingIntervalSeconds", 5), 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var now = DateTime.UtcNow;
                var pendingIds = await db.WebhookEvents.AsNoTracking()
                    .Where(x => (x.Status == WebhookStatus.Received || x.Status == WebhookStatus.Failed) &&
                                x.NextAttemptAtUtc <= now && x.AttemptCount < 8)
                    .OrderBy(x => x.ReceivedAtUtc)
                    .Select(x => x.Id)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var id in pendingIds)
                {
                    try
                    {
                        await processor.ProcessAsync(id, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unexpected recovery error for webhook {WebhookEventId}", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook recovery polling failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
