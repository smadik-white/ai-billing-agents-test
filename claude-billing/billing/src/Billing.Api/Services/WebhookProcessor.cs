using System.Text.Json;
using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billing.Api.Services;

public enum ReceiveStatus { Processed, Duplicate, Deferred, Rejected, InvalidSignature, BadRequest }
public sealed record ReceiveResult(ReceiveStatus Status, string? Message = null);

public enum ProcessOutcome { Processed, AlreadyProcessed, Rejected, Deferred, DeadLetter }

/// <summary>
/// Webhook pipeline:
///   1. verify HMAC signature (nothing unauthenticated is ever stored);
///   2. persist the event in an inbox table (unique ProviderEventId = idempotency anchor);
///   3. process it in ONE transaction that holds the event row lock, so duplicate or concurrent
///      deliveries wait for each other and the second one sees "Processed";
///   4. on failure roll back business changes and journal the error; recovery retries later.
/// </summary>
public sealed class WebhookProcessor(
    IDbContextFactory<BillingDbContext> factory,
    IOptions<BillingOptions> options,
    TimeProvider clock,
    ILogger<WebhookProcessor> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<ReceiveResult> ReceiveAsync(string body, string? signatureHeader, CancellationToken ct)
    {
        var o = options.Value;
        if (!TestPaySignature.TryVerify(o.WebhookSecret, signatureHeader, body, clock.GetUtcNow(),
                TimeSpan.FromSeconds(o.SignatureToleranceSeconds), out var error))
        {
            log.LogWarning("Webhook rejected: invalid signature ({Reason})", error);
            return new ReceiveResult(ReceiveStatus.InvalidSignature, error);
        }

        WebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookPayload>(body, Json); }
        catch (JsonException) { payload = null; }
        if (payload is null || string.IsNullOrWhiteSpace(payload.EventId)
            || string.IsNullOrWhiteSpace(payload.Type) || string.IsNullOrWhiteSpace(payload.PaymentId))
            return new ReceiveResult(ReceiveStatus.BadRequest, "event_id, type and payment_id are required");

        var now = clock.GetUtcNow();
        Guid eventId;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var ev = new WebhookEvent
            {
                ProviderEventId = payload.EventId, Type = payload.Type, PaymentId = payload.PaymentId, Payload = body,
                Status = WebhookEventStatus.Received, ReceivedAt = now,
                // If we crash right after this insert, recovery picks the event up after the delay.
                NextAttemptAt = now.AddSeconds(o.RecoveryDelaySeconds)
            };
            db.WebhookEvents.Add(ev);
            try
            {
                await db.SaveChangesAsync(ct);
                eventId = ev.Id;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                db.ChangeTracker.Clear();
                eventId = await db.WebhookEvents.Where(e => e.ProviderEventId == payload.EventId).Select(e => e.Id).SingleAsync(ct);
                log.LogInformation("Duplicate delivery of webhook event {ProviderEventId}", payload.EventId);
            }
        }

        // The event is durable now; do not let a client disconnect abort processing half-way.
        var outcome = await ProcessAsync(eventId, CancellationToken.None);
        return outcome switch
        {
            ProcessOutcome.Processed => new ReceiveResult(ReceiveStatus.Processed),
            ProcessOutcome.AlreadyProcessed => new ReceiveResult(ReceiveStatus.Duplicate),
            ProcessOutcome.Rejected => new ReceiveResult(ReceiveStatus.Rejected, "event cannot be applied"),
            _ => new ReceiveResult(ReceiveStatus.Deferred, "stored; will be retried")
        };
    }

    /// <summary>Re-processes stored events that are Received/Failed and due. Returns the number of events attempted.</summary>
    public async Task<int> RecoverAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            ids = await db.WebhookEvents
                .Where(e => (e.Status == WebhookEventStatus.Received || e.Status == WebhookEventStatus.Failed) && e.NextAttemptAt <= asOf)
                .OrderBy(e => e.NextAttemptAt)
                .Select(e => e.Id)
                .Take(options.Value.RecoveryBatchSize)
                .ToListAsync(ct);
        }
        foreach (var id in ids) await ProcessAsync(id, ct);
        return ids.Count;
    }

    private async Task<ProcessOutcome> ProcessAsync(Guid eventId, CancellationToken ct)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["WebhookEventId"] = eventId });
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LockRowAsync($$"""SELECT 1 FROM "WebhookEvents" WHERE "Id" = {{eventId}} FOR UPDATE""", ct);

            var ev = await db.WebhookEvents.SingleAsync(e => e.Id == eventId, ct);
            if (ev.Status == WebhookEventStatus.Processed) return ProcessOutcome.AlreadyProcessed;
            if (ev.Status == WebhookEventStatus.Rejected) return ProcessOutcome.Rejected;

            var payload = JsonSerializer.Deserialize<WebhookPayload>(ev.Payload, Json)!;
            var now = clock.GetUtcNow();
            await WebhookEventApplier.ApplyAsync(db, payload, now, ct);

            ev.Attempts++;
            ev.Status = WebhookEventStatus.Processed;
            ev.ProcessedAt = now;
            ev.NextAttemptAt = null;
            ev.LastError = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation("Webhook {ProviderEventId} ({Type}) processed", ev.ProviderEventId, ev.Type);
            return ProcessOutcome.Processed;
        }
        catch (PermanentWebhookException ex)
        {
            // tx has been rolled back by disposal: no partial business changes survive.
            log.LogWarning("Webhook rejected permanently: {Reason}", ex.Message);
            await MarkFailedAsync(eventId, ex.Message, permanent: true);
            return ProcessOutcome.Rejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Webhook processing failed");
            var status = await MarkFailedAsync(eventId, ex.Message, permanent: false);
            return status == WebhookEventStatus.DeadLetter ? ProcessOutcome.DeadLetter : ProcessOutcome.Deferred;
        }
    }

    private async Task<WebhookEventStatus> MarkFailedAsync(Guid eventId, string error, bool permanent)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.LockRowAsync($$"""SELECT 1 FROM "WebhookEvents" WHERE "Id" = {{eventId}} FOR UPDATE""", CancellationToken.None);

        var ev = await db.WebhookEvents.SingleAsync(e => e.Id == eventId);
        // Another worker may have finished it between our rollback and this lock.
        if (ev.Status == WebhookEventStatus.Processed) return ev.Status;

        ev.Attempts++;
        ev.LastError = error.Length > 2000 ? error[..2000] : error;
        if (permanent)
        {
            ev.Status = WebhookEventStatus.Rejected;
            ev.NextAttemptAt = null;
        }
        else if (ev.Attempts >= o.MaxWebhookAttempts)
        {
            ev.Status = WebhookEventStatus.DeadLetter;
            ev.NextAttemptAt = null;
        }
        else
        {
            ev.Status = WebhookEventStatus.Failed;
            ev.NextAttemptAt = now.AddSeconds(Math.Min(5 * Math.Pow(2, ev.Attempts - 1), 600)); // 5s,10s,20s... max 10min
        }

        db.AuditLogs.Add(new AuditLog
        {
            At = now, Action = ev.Status == WebhookEventStatus.DeadLetter ? "WebhookDeadLettered" : "WebhookFailed",
            EntityType = "WebhookEvent", EntityId = ev.ProviderEventId,
            Details = $"status={ev.Status}, attempt={ev.Attempts}: {ev.LastError}"
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ev.Status;
    }
}
