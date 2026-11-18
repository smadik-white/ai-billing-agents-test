using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SaasBilling.Domain;

namespace SaasBilling.Api.Payments;

public sealed class TestPaymentOptions
{
    public const string SectionName = "Payments:Test";
    public string Secret { get; set; } = "dev_test_webhook_secret_change_me";
    public int ToleranceSeconds { get; set; } = 300;
}

public sealed record ProviderWebhookData(
    string PaymentId,
    Guid InvoiceId,
    decimal Amount,
    string Currency);

public sealed record ProviderWebhook(
    string Id,
    string Type,
    DateTimeOffset CreatedAt,
    ProviderWebhookData Data);

public sealed record PaymentIntent(
    string Provider,
    string ProviderPaymentId,
    decimal Amount,
    string Currency,
    string Status);

public interface IPaymentProvider
{
    string Name { get; }
    PaymentIntent CreatePayment(Guid invoiceId, decimal amount, string currency, string idempotencyKey);

    /// <summary>Verifies the HMAC and returns the authenticated, parsed event.</summary>
    ProviderWebhook VerifyAndParse(string rawBody, string? signatureHeader, DateTimeOffset now);

    /// <summary>Parses a previously authenticated payload from the inbox.</summary>
    ProviderWebhook ParseUnverified(string rawBody);

    /// <summary>Returns the signature header for a body. Used by tests and local curl examples.</summary>
    string Sign(string rawBody, DateTimeOffset timestamp);
}

/// <summary>
/// Deterministic test adapter. No network calls: creating a payment returns a synthetic id,
/// and the provider's webhook is authenticated with HMAC-SHA256.
///
/// Header: X-Billing-Signature: t=&lt;unix-seconds&gt;,v1=&lt;hex hmac_sha256(secret, "t.body")&gt;
/// </summary>
public sealed class TestPaymentProvider : IPaymentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TestPaymentOptions _options;
    private readonly TimeProvider _timeProvider;

    public TestPaymentProvider(IOptions<TestPaymentOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        if (string.IsNullOrWhiteSpace(_options.Secret) || _options.Secret.Length < 16)
        {
            throw new InvalidOperationException("Payments:Test:Secret must contain at least 16 characters.");
        }
    }

    public string Name => "test";

    public PaymentIntent CreatePayment(Guid invoiceId, decimal amount, string currency, string idempotencyKey)
    {
        // A real adapter would call the provider here. The deterministic id models the provider's
        // idempotency-key contract: retries with the same key return the same payment intent.
        var material = $"{Name}:{invoiceId:N}:{idempotencyKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return new PaymentIntent(Name, $"pay_{hash[..32]}", Money.Round(amount), currency, "pending");
    }

    public ProviderWebhook VerifyAndParse(string rawBody, string? signatureHeader, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            throw new InvalidWebhookSignatureException("X-Billing-Signature header is missing.");
        }

        if (!TryParseSignature(signatureHeader, out var timestamp, out var signatures))
        {
            throw new InvalidWebhookSignatureException("Signature header format is invalid.");
        }

        var age = now - timestamp;
        var tolerance = TimeSpan.FromSeconds(Math.Max(1, _options.ToleranceSeconds));
        if (age > tolerance || age < tolerance.Negate())
        {
            throw new InvalidWebhookSignatureException("Webhook timestamp is outside the allowed tolerance window.");
        }

        var expected = ComputeSignature(rawBody, timestamp);
        var valid = signatures.Any(signature =>
            signature.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature),
                Encoding.ASCII.GetBytes(expected)));

        if (!valid)
        {
            throw new InvalidWebhookSignatureException("Webhook signature is invalid.");
        }

        return ParseUnverified(rawBody);
    }

    public ProviderWebhook ParseUnverified(string rawBody)
    {
        if (rawBody.Length > 256 * 1024)
        {
            throw new PermanentWebhookException("Webhook payload is too large.");
        }

        ProviderWebhook? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<ProviderWebhook>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PermanentWebhookException($"Webhook payload is not valid JSON: {ex.Message}");
        }

        if (webhook is null || string.IsNullOrWhiteSpace(webhook.Id) || string.IsNullOrWhiteSpace(webhook.Type))
        {
            throw new PermanentWebhookException("Webhook must contain id and type.");
        }

        if (webhook.Data is null ||
            string.IsNullOrWhiteSpace(webhook.Data.PaymentId) ||
            webhook.Data.InvoiceId == Guid.Empty ||
            webhook.Data.Amount <= 0)
        {
            throw new PermanentWebhookException("Webhook data must contain paymentId, invoiceId and a positive amount.");
        }

        if (!string.Equals(webhook.Data.Currency?.Trim(), webhook.Data.Currency?.Trim().ToUpperInvariant(), StringComparison.Ordinal) ||
            webhook.Data.Currency?.Trim().Length != 3)
        {
            throw new PermanentWebhookException("Webhook data.currency must be a three-letter ISO code.");
        }

        return webhook;
    }

    public string Sign(string rawBody, DateTimeOffset timestamp)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        return $"t={unix},v1={ComputeSignature(rawBody, timestamp)}";
    }

    public string SignNow(string rawBody) => Sign(rawBody, _timeProvider.GetUtcNow());

    private string ComputeSignature(string rawBody, DateTimeOffset timestamp)
    {
        var signed = $"{timestamp.ToUnixTimeSeconds()}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
    }

    private static bool TryParseSignature(
        string header,
        out DateTimeOffset timestamp,
        out IReadOnlyList<string> signatures)
    {
        timestamp = default;
        var signatureList = new List<string>();
        long? unix = null;

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                signatures = [];
                return false;
            }

            if (pair[0] == "t" && long.TryParse(pair[1], out var parsedUnix))
            {
                unix = parsedUnix;
            }
            else if (pair[0] == "v1" && pair[1].Length > 0)
            {
                signatureList.Add(pair[1].ToLowerInvariant());
            }
        }

        signatures = signatureList;
        if (unix is null || signatureList.Count == 0)
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unix.Value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
