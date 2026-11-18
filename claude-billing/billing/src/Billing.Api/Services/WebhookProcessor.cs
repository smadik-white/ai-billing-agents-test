using System.Text.Json;
using Billing.Api.Data;
using Billing.Api.Domain;
using Billing.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billing.Api.Services;

public enum ReceiveStatus { Processed, Duplicate, Deferred, Rejected, Conflict, InvalidSignature, BadRequest }
public sealed record ReceiveResult(ReceiveStatus Status, string? Message = null);

public enum ProcessOutcome { Processed, AlreadyProcessed, Rejected, Deferred, DeadLetter }

/// <summary>
/// Webhook pipeline:
///   1. verify HMAC signature (nothing unauthenticated is ever stored);
///   2. persist the event in an inbox table (unique ProviderEventId = idempotency anchor). A re-delivery with a
///      different body is NOT applied: it is recorded as an anomaly and answered with 409;
///   3. process it in ONE transaction that holds the event row lock, so duplicate or concurrent
///      deliveries wait for each other and the later one sees "Processed";
///   4. on failure roll back business changes and journal the error (best effort); recovery retries later.
/// No network call to the payment provider is ever made inside this transaction.
/// </summary>
public sealed class WebhookProcessor(
    IDbContextFactory<BillingDbContext> factory,
    IOptions<BillingOptions> options,
    TimeProvider clock,
    IWebhookProcessingHook hook,
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
        var hash = PayloadHash.Of(body);
        Guid eventId;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var ev = new WebhookEvent
            {
                ProviderEventId = payload.EventId, Type = payload.Type, PaymentId = payload.PaymentId, Payload = body,
                PayloadHash = hash, Status = WebhookEventStatus.Received, ReceivedAt = now,
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
                var existing = await db.WebhookEvents.AsNoTracking()
                    .Where(e => e.ProviderEventId == payload.EventId)
                    .Select(e => new { e.Id, e.PayloadHash })
                    .SingleAsync(ct);

                if (existing.PayloadHash != hash)
                {
                    log.LogError("Event {ProviderEventId} re-delivered with different content; ignored", payload.EventId);
                    await RecordConflictAsync(db, payload.EventId, existing.PayloadHash, hash, body, now, ct);
                    return new ReceiveResult(ReceiveStatus.Conflict, "event id was already used with different content");
                }
                eventId = existing.Id;
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

    private static async Task RecordConflictAsync(BillingDbContext db, string eventId, string originalHash, string receivedHash,
        string body, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.Anomalies.Add(new BillingAnomaly
        {
            Key = $"event-conflict:{eventId}:{receivedHash}", Kind = "EventPayloadConflict", EntityType = "WebhookEvent", EntityId = eventId,
            Details = $"Event id re-delivered with different content (original sha256 {originalHash}, received {receivedHash}); delivery ignored. Received body: {Truncate(body, 1500)}",
            CreatedAt = now
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation()) { /* same conflicting payload already recorded */ }
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

    /// <summary>Manual recovery: puts a Failed/DeadLetter event back into the queue (attempts reset) and processes it now. Null = not requeueable.</summary>
    public async Task<ProcessOutcome?> RequeueAsync(string providerEventId, CancellationToken ct)
    {
        Guid id;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var now = clock.GetUtcNow();
            var ev = await db.WebhookEvents.AsNoTracking().SingleOrDefaultAsync(e => e.ProviderEventId == providerEventId, ct);
            if (ev is null) return null;

            // Atomic compare-and-set: if a worker finished the event in the meantime, nothing is touched.
            var changed = await db.WebhookEvents
                .Where(e => e.Id == ev.Id && (e.Status == WebhookEventStatus.DeadLetter || e.Status == WebhookEventStatus.Failed))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, WebhookEventStatus.Failed)
                    .SetProperty(e => e.Attempts, 0)
                    .SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)now), ct);
            if (changed == 0) return null;

            db.AuditLogs.Add(new AuditLog
            {
                At = now, Action = "WebhookRequeued", EntityType = "WebhookEvent", EntityId = providerEventId,
                Details = $"manually requeued from {ev.Status}; previous error: {ev.LastError}"
            });
            await db.SaveChangesAsync(ct);
            id = ev.Id;
        }
        return await ProcessAsync(id, ct);
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
            await hook.OnStepAsync("after-lock", ct);

            var payload = JsonSerializer.Deserialize<WebhookPayload>(ev.Payload, Json)!;
            var now = clock.GetUtcNow();
            await WebhookEventApplier.ApplyAsync(db, payload, now, hook, ct);

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
            return await TryMarkFailedAsync(eventId, ex.Message, permanent: true) == WebhookEventStatus.Rejected
                ? ProcessOutcome.Rejected : ProcessOutcome.Deferred;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Webhook processing failed");
            var status = await TryMarkFailedAsync(eventId, ex.Message, permanent: false);
            return status == WebhookEventStatus.DeadLetter ? ProcessOutcome.DeadLetter : ProcessOutcome.Deferred;
        }
    }

    /// <summary>Journaling is best effort: if the DB is down the event simply stays Received/Failed and recovery retries it.</summary>
    private async Task<WebhookEventStatus> TryMarkFailedAsync(Guid eventId, string error, bool permanent)
    {
        try { return await MarkFailedAsync(eventId, error, permanent); }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not journal webhook failure; event stays queued for recovery");
            return WebhookEventStatus.Failed;
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
        ev.LastError = Truncate(error, 2000);
        if (permanent)
        {
            ev.Status = WebhookEventStatus.Rejected;
            ev.NextAttemptAt = null;
            db.Anomalies.Add(new BillingAnomaly
            {
                Key = $"rejected:{ev.ProviderEventId}", Kind = "WebhookRejected", EntityType = "WebhookEvent", EntityId = ev.ProviderEventId,
                Details = $"{ev.Type} for payment {ev.PaymentId} rejected: {ev.LastError}", CreatedAt = now
            });
        }
        else if (ev.Attempts >= o.MaxWebhookAttempts)
        {
            ev.Status = WebhookEventStatus.DeadLetter;
            ev.NextAttemptAt = null;
            db.Anomalies.Add(new BillingAnomaly
            {
                Key = $"deadletter:{ev.Id}:{Guid.NewGuid():N}", Kind = "WebhookDeadLettered", EntityType = "WebhookEvent", EntityId = ev.ProviderEventId,
                Details = $"{ev.Type} for payment {ev.PaymentId} gave up after {ev.Attempts} attempts: {ev.LastError}", CreatedAt = now
            });
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

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
