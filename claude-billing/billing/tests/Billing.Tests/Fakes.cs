using System.Collections.Concurrent;
using System.Data.Common;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Billing.Tests;

/// <summary>
/// Controllable payment provider honoring the idempotency-key contract (same key => same payment),
/// with fault injection: slow responses, failures before/after the payment is really created ("response lost").
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, (Guid InvoiceId, ProviderPayment Payment)> _byKey = new();
    private int _failBefore, _failAfter, _calls;

    public TimeSpan Delay { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource? Entered { get; set; }

    public int Calls => _calls;
    public void FailBeforeCreate(int times) => Interlocked.Exchange(ref _failBefore, times);
    public void FailAfterCreate(int times) => Interlocked.Exchange(ref _failAfter, times);

    /// <summary>Number of DISTINCT payments the provider holds for an invoice (what the customer could be charged for).</summary>
    public int PaymentsFor(Guid invoiceId) => _byKey.Values.Count(v => v.InvoiceId == invoiceId);

    public void Reset()
    {
        Delay = TimeSpan.Zero; Gate = null; Entered = null;
        Interlocked.Exchange(ref _failBefore, 0);
        Interlocked.Exchange(ref _failAfter, 0);
    }

    public async Task<ProviderPayment> CreatePaymentAsync(string idempotencyKey, Guid invoiceId, long amountMinor, string currency, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Entered?.TrySetResult();
        if (Gate is { } gate) await gate.Task.WaitAsync(ct);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Interlocked.Decrement(ref _failBefore) >= 0) throw new HttpRequestException("provider unavailable");

        var entry = _byKey.GetOrAdd(idempotencyKey, k =>
            (invoiceId, new ProviderPayment("fp_" + Guid.NewGuid().ToString("N"), $"https://fake.pay/{k}")));

        if (Interlocked.Decrement(ref _failAfter) >= 0) throw new HttpRequestException("connection reset after the payment was created");
        return entry.Payment;
    }
}

/// <summary>Simulates "database unavailable": every connection open fails while Down is set.</summary>
public sealed class DbFaultInterceptor : DbConnectionInterceptor
{
    public volatile bool Down;

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (Down) throw new InvalidOperationException("Simulated database outage");
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Down) throw new InvalidOperationException("Simulated database outage");
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}

/// <summary>Throws once at a chosen processing step ("process crashes between two writes of one transaction").</summary>
public sealed class ScriptedProcessingHook : IWebhookProcessingHook
{
    private readonly object _lock = new();
    private string? _failAt;
    private Action? _sideEffect;

    public void FailOnce(string step, Action? sideEffect = null)
    {
        lock (_lock) { _failAt = step; _sideEffect = sideEffect; }
    }

    public void Reset()
    {
        lock (_lock) { _failAt = null; _sideEffect = null; }
    }

    public Task OnStepAsync(string step, CancellationToken ct)
    {
        Action? effect;
        lock (_lock)
        {
            if (_failAt != step) return Task.CompletedTask;
            _failAt = null;
            effect = _sideEffect;
        }
        effect?.Invoke();
        throw new InvalidOperationException($"Injected crash at step '{step}'");
    }
}
