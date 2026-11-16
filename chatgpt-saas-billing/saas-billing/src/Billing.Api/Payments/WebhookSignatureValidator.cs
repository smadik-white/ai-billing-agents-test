using System.Security.Cryptography;
using System.Text;

namespace Billing.Api.Payments;

public sealed class WebhookSignatureValidator(IConfiguration configuration)
{
    public bool IsValid(ReadOnlySpan<byte> payload, string? signature)
    {
        var secret = configuration["Payments:HmacSecret"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
            return false;

        const string prefix = "sha256=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
