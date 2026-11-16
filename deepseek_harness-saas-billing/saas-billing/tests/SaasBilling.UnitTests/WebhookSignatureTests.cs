using Microsoft.Extensions.Options;
using SaasBilling.Api.Payments;
using SaasBilling.Domain;
using Xunit;

namespace SaasBilling.UnitTests;

public sealed class WebhookSignatureTests
{
    private const string Secret = "unit_test_webhook_secret_123456789";
    private readonly TestPaymentProvider _provider = new(
        Options.Create(new TestPaymentOptions { Secret = Secret, ToleranceSeconds = 300 }),
        TimeProvider.System);

    private const string Payload = """
        {
          "id": "evt_unit_1",
          "type": "payment.succeeded",
          "createdAt": "2026-11-16T12:00:00Z",
          "data": {
            "paymentId": "pay_unit_1",
            "invoiceId": "11111111-1111-1111-1111-111111111111",
            "amount": 29.00,
            "currency": "USD"
          }
        }
        """;

    [Fact]
    public void Valid_signature_is_accepted_and_payload_is_parsed()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = _provider.Sign(Payload, now);

        var parsed = _provider.VerifyAndParse(Payload, signature, now);

        Assert.Equal("evt_unit_1", parsed.Id);
        Assert.Equal("payment.succeeded", parsed.Type);
        Assert.Equal("pay_unit_1", parsed.Data.PaymentId);
        Assert.Equal(29.00m, parsed.Data.Amount);
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = _provider.Sign(Payload, now);
        var tampered = Payload.Replace("29.00", "1.00");

        var exception = Assert.Throws<InvalidWebhookSignatureException>(
            () => _provider.VerifyAndParse(tampered, signature, now));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void Expired_timestamp_is_rejected()
    {
        var signedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var signature = _provider.Sign(Payload, signedAt);

        Assert.Throws<InvalidWebhookSignatureException>(
            () => _provider.VerifyAndParse(Payload, signature, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=not-a-number,v1=abcdef")]
    [InlineData("t=123")]
    public void Malformed_signature_header_is_rejected(string header)
    {
        Assert.Throws<InvalidWebhookSignatureException>(() =>
            _provider.VerifyAndParse(Payload, header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Signature_covers_event_id_and_amount()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = _provider.Sign(Payload, now);

        var changedId = Payload.Replace("evt_unit_1", "evt_unit_2");
        var changedAmount = Payload.Replace("29.00", "99.00");

        Assert.Throws<InvalidWebhookSignatureException>(
            () => _provider.VerifyAndParse(changedId, signature, now));
        Assert.Throws<InvalidWebhookSignatureException>(
            () => _provider.VerifyAndParse(changedAmount, signature, now));
    }

    [Fact]
    public void Signed_but_structurally_invalid_payload_is_permanent()
    {
        const string bad = """{"id":"evt","type":"payment.succeeded","data":{"amount":0}}""";
        var now = DateTimeOffset.UtcNow;
        var signature = _provider.Sign(bad, now);

        Assert.Throws<PermanentWebhookException>(
            () => _provider.VerifyAndParse(bad, signature, now));
    }
}
