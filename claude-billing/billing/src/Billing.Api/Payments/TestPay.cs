using System.Security.Cryptography;
using System.Text;

namespace Billing.Api.Payments;

public sealed record ProviderPayment(string PaymentId, string CheckoutUrl);

public interface IPaymentProvider
{
    Task<ProviderPayment> CreatePaymentAsync(Guid invoiceId, long amountMinor, string currency, CancellationToken ct);
}

/// <summary>Stub provider: no network, just hands out an id and a fake checkout URL.</summary>
public sealed class TestPaymentProvider : IPaymentProvider
{
    public Task<ProviderPayment> CreatePaymentAsync(Guid invoiceId, long amountMinor, string currency, CancellationToken ct)
    {
        var id = "tp_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(new ProviderPayment(id, $"https://testpay.local/checkout/{id}"));
    }
}

/// <summary>
/// Webhook body (JSON, snake_case):
///   { "event_id": "...", "type": "payment.succeeded|payment.failed|refund.succeeded",
///     "payment_id": "...", "amount": 99000, "currency": "RUB", "refund_id": "...", "reason": "..." }
/// </summary>
public sealed record WebhookPayload(
    string EventId, string Type, string PaymentId, long Amount, string? Currency, string? RefundId, string? Reason);

/// <summary>
/// Header:  X-TestPay-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex(HMAC-SHA256(secret, "{t}.{rawBody}"))&gt;
/// </summary>
public static class TestPaySignature
{
    public const string HeaderName = "X-TestPay-Signature";

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static string BuildHeader(string secret, long timestamp, string body) =>
        $"t={timestamp},v1={Sign(secret, timestamp, body)}";

    public static bool TryVerify(string secret, string? header, string body, DateTimeOffset now, TimeSpan tolerance, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(header)) { error = "missing signature header"; return false; }

        long? ts = null;
        string? sig = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t" && long.TryParse(kv[1], out var t)) ts = t;
            else if (kv[0] == "v1") sig = kv[1];
        }

        if (ts is null or < 0 or > 253_402_300_799 || sig is null) { error = "malformed signature header"; return false; }

        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(ts.Value)).TotalSeconds) > tolerance.TotalSeconds)
        { error = "timestamp outside tolerance"; return false; }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts.Value, body));
        var actual = Encoding.ASCII.GetBytes(sig.ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) { error = "signature mismatch"; return false; }
        return true;
    }
}
