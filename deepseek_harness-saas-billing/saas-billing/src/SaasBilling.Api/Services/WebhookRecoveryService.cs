using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SaasBilling.Api.Data;
using SaasBilling.Domain;

namespace SaasBilling.Api.Services;

public sealed class RecoveryOptions
{
    public const string SectionName = "Billing:Recovery";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 20;
}

/// <summary>
/// Retries failed inbox events with exponential backoff and reclaims stale Processing rows
/// left behind by a crashed instance. Multiple API instances are safe: claiming is row-locked.
/// </summary>
public sealed class WebhookRecoveryService(
    IServiceScopeFactory scopeFactory,
    IOptions<RecoveryOptions> recoveryOptions,
    IOptions<WebhookOptions> webhookOptions,
    ILogger<WebhookRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = recoveryOptions.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("Webhook recovery worker is disabled by configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds));
        logger.LogInformation("Webhook recovery worker started; interval {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook recovery batch failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(RecoveryOptions options, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BillingDbContext>>();
        var processor = scope.ServiceProvider.GetRequiredService<WebhookService>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        var maxAttempts = Math.Max(1, webhookOptions.Value.MaxAttempts);
        var lease = TimeSpan.FromSeconds(Math.Max(30, webhookOptions.Value.ProcessingLeaseSeconds));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.WebhookEvents
            .AsNoTracking()
            .Where(x =>
                (((x.Status == WebhookStatus.Pending || x.Status == WebhookStatus.Failed) &&
                  x.Attempts < maxAttempts &&
                  (x.NextRetryAt == null || x.NextRetryAt <= now)) ||
                 (x.Status == WebhookStatus.Processing &&
                  x.ProcessingStartedAt != null &&
                  x.ProcessingStartedAt <= now - lease)))
            .OrderBy(x => x.ReceivedAt)
            .Take(Math.Clamp(options.BatchSize, 1, 500))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await processor.ProcessAsync(id, cancellationToken);
            if (!result.Processed)
            {
                logger.LogInformation("Recovery attempt for webhook {InboxId}: {Status}", id, result.Status);
            }
        }
    }
}
