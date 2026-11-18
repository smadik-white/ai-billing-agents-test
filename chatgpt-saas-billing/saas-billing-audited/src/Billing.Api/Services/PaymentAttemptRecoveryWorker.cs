using Billing.Api.Domain;
using Billing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Services;

/// <summary>Retries durable provider-creation intents after process restarts or ambiguous timeouts.</summary>
public sealed class PaymentAttemptRecoveryWorker(
    IDbContextFactory<BillingDbContext> dbFactory,
    PaymentAttemptService paymentService,
    IConfiguration configuration,
    ILogger<PaymentAttemptRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Math.Clamp(configuration.GetValue("PaymentRecovery:PollingIntervalSeconds", 5), 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var now = DateTime.UtcNow;
                var ids = await db.PaymentAttempts.AsNoTracking()
                    .Where(x => x.Status == PaymentStatus.Creating &&
                                (x.ProviderRetryAfterUtc == null || x.ProviderRetryAfterUtc <= now))
                    .OrderBy(x => x.CreatedAtUtc)
                    .Select(x => x.Id)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var id in ids)
                {
                    try
                    {
                        await paymentService.RetryProviderCreationAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Provider payment recovery failed for attempt {PaymentAttemptId}", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment attempt recovery polling failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
