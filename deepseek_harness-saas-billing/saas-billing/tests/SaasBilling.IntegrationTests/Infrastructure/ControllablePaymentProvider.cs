using System.Collections.Concurrent;
using SaasBilling.Api.Payments;

namespace SaasBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Test double around the real test provider. It can hold the provider call to prove that
/// payment creation does not keep a database transaction open, and can fail after the provider
/// has returned an intent to model a crash before the local INSERT.
/// </summary>
public sealed class ControllablePaymentProvider(IPaymentProvider inner) : IPaymentProvider
{
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _createCalls;

    public string Name => inner.Name;
    public bool BlockCreate { get; set; }
    public bool FailAfterCreate { get; set; }
    public Task Entered => _entered.Task;
    public int CreateCalls => Volatile.Read(ref _createCalls);
    public ConcurrentQueue<string> CreatedPaymentIds { get; } = new();

    public PaymentIntent CreatePayment(Guid invoiceId, decimal amount, string currency, string idempotencyKey)
    {
        if (BlockCreate)
        {
            _entered.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }

        var intent = inner.CreatePayment(invoiceId, amount, currency, idempotencyKey);
        CreatedPaymentIds.Enqueue(intent.ProviderPaymentId);
        Interlocked.Increment(ref _createCalls);

        if (FailAfterCreate)
        {
            FailAfterCreate = false;
            throw new IOException("Injected provider-to-database failure.");
        }

        return intent;
    }

    public void Release() => _release.TrySetResult(true);

    public ProviderWebhook VerifyAndParse(string rawBody, string? signatureHeader, DateTimeOffset now) =>
        inner.VerifyAndParse(rawBody, signatureHeader, now);

    public ProviderWebhook ParseUnverified(string rawBody) => inner.ParseUnverified(rawBody);

    public string Sign(string rawBody, DateTimeOffset timestamp) => inner.Sign(rawBody, timestamp);
}
