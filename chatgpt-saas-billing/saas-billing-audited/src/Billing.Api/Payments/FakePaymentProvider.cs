namespace Billing.Api.Payments;

public sealed record ProviderPayment(string ProviderPaymentId, string CheckoutUrl);

public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>
    /// Must be idempotent for the supplied idempotency key. Repeating the same call after a timeout
    /// or process crash must return the same provider-side payment, never create a second charge.
    /// </summary>
    Task<ProviderPayment> CreatePaymentAsync(
        Guid idempotencyKey,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministic local adapter. It does not contact a payment network.</summary>
public sealed class FakePaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    public string Name => "test-provider";

    public Task<ProviderPayment> CreatePaymentAsync(
        Guid idempotencyKey,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseUrl = configuration["Payments:CheckoutBaseUrl"] ?? "https://checkout.test/pay";
        var id = idempotencyKey.ToString("D");
        return Task.FromResult(new ProviderPayment(id, $"{baseUrl.TrimEnd('/')}/{id}"));
    }
}
