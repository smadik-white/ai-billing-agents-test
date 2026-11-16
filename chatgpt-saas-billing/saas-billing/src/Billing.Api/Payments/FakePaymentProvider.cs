namespace Billing.Api.Payments;

public interface IPaymentProvider
{
    string Name { get; }
    (string ProviderPaymentId, string CheckoutUrl) CreatePayment(Guid paymentAttemptId, decimal amount, string currency);
}

/// <summary>Deterministic local adapter. It does not contact a payment network.</summary>
public sealed class FakePaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    public string Name => "test-provider";

    public (string ProviderPaymentId, string CheckoutUrl) CreatePayment(
        Guid paymentAttemptId, decimal amount, string currency)
    {
        var baseUrl = configuration["Payments:CheckoutBaseUrl"] ?? "https://checkout.test/pay";
        return (paymentAttemptId.ToString("D"), $"{baseUrl.TrimEnd('/')}/{paymentAttemptId:D}");
    }
}
