using System.Text;
using Billing.Api.Domain;
using Billing.Api.Payments;

namespace Billing.Tests;

/// <summary>Pure logic, no database: money state machine, signature verification, provider idempotency.</summary>
public class InvoiceMathTests
{
    [Theory]
    [InlineData(InvoiceStatus.Open, 1000, 0, 0, InvoiceStatus.Open)]
    [InlineData(InvoiceStatus.Open, 1000, 500, 0, InvoiceStatus.PartiallyPaid)]
    [InlineData(InvoiceStatus.PartiallyPaid, 1000, 1000, 0, InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Paid, 1000, 1000, 200, InvoiceStatus.PartiallyRefunded)]
    [InlineData(InvoiceStatus.PartiallyRefunded, 1000, 1000, 999, InvoiceStatus.PartiallyRefunded)]
    [InlineData(InvoiceStatus.PartiallyRefunded, 1000, 1000, 1000, InvoiceStatus.Refunded)]
    [InlineData(InvoiceStatus.PartiallyPaid, 1000, 500, 500, InvoiceStatus.Refunded)]  // everything paid was refunded
    [InlineData(InvoiceStatus.PartiallyPaid, 1000, 500, 100, InvoiceStatus.PartiallyRefunded)]
    [InlineData(InvoiceStatus.Void, 1000, 0, 0, InvoiceStatus.Void)]
    public void Status_is_derived_from_amounts(InvoiceStatus current, int total, int paid, int refunded, InvoiceStatus expected) =>
        Assert.Equal(expected, InvoiceMath.StatusFor(current, total, paid, refunded));
}

public class TestPaySignatureTests
{
    private const string Secret = "s3cret";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private const string Body = """{"event_id":"e1","type":"payment.succeeded","payment_id":"p1","amount":100}""";

    private static bool Verify(string? header, string body = Body, DateTimeOffset? now = null) =>
        TestPaySignature.TryVerify(Secret, header, body, now ?? Now, Tolerance, out _);

    [Fact]
    public void Valid_signature_is_accepted() =>
        Assert.True(Verify(TestPaySignature.BuildHeader(Secret, Now.ToUnixTimeSeconds(), Body)));

    [Fact]
    public void Signature_matches_the_documented_format()
    {
        var ts = Now.ToUnixTimeSeconds();
        var mac = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{ts}.{Body}"));
        Assert.Equal($"t={ts},v1={Convert.ToHexString(mac).ToLowerInvariant()}", TestPaySignature.BuildHeader(Secret, ts, Body));
    }

    [Fact]
    public void Wrong_secret_or_modified_body_is_rejected()
    {
        var ts = Now.ToUnixTimeSeconds();
        Assert.False(Verify(TestPaySignature.BuildHeader("other", ts, Body)));
        Assert.False(Verify(TestPaySignature.BuildHeader(Secret, ts, Body), Body.Replace("100", "101")));
    }

    [Fact]
    public void Timestamp_outside_tolerance_is_rejected_in_both_directions()
    {
        Assert.False(Verify(TestPaySignature.BuildHeader(Secret, Now.AddMinutes(-6).ToUnixTimeSeconds(), Body)));
        Assert.False(Verify(TestPaySignature.BuildHeader(Secret, Now.AddMinutes(6).ToUnixTimeSeconds(), Body)));
        Assert.True(Verify(TestPaySignature.BuildHeader(Secret, Now.AddMinutes(-4).ToUnixTimeSeconds(), Body)));
    }

    [Fact]
    public void The_timestamp_is_part_of_the_signed_data()
    {
        var sig = TestPaySignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        Assert.False(Verify($"t={Now.AddSeconds(1).ToUnixTimeSeconds()},v1={sig}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1")]
    [InlineData("v1=00")]
    [InlineData("t=-5,v1=00")]
    [InlineData("t=99999999999999,v1=00")]
    public void Malformed_headers_are_rejected_without_throwing(string? header) => Assert.False(Verify(header));
}

public class ProviderAndHashTests
{
    [Fact]
    public async Task Test_provider_is_idempotent_per_key()
    {
        var p = new TestPaymentProvider();
        var a = await p.CreatePaymentAsync("key-1", Guid.NewGuid(), 100, "RUB", default);
        var again = await p.CreatePaymentAsync("key-1", Guid.NewGuid(), 100, "RUB", default);
        var other = await p.CreatePaymentAsync("key-2", Guid.NewGuid(), 100, "RUB", default);
        Assert.Equal(a.PaymentId, again.PaymentId);
        Assert.NotEqual(a.PaymentId, other.PaymentId);
    }

    [Fact]
    public void Payload_hash_is_stable_and_content_sensitive()
    {
        Assert.Equal(PayloadHash.Of("abc"), PayloadHash.Of("abc"));
        Assert.NotEqual(PayloadHash.Of("abc"), PayloadHash.Of("abd"));
        Assert.Equal(64, PayloadHash.Of("abc").Length);
    }
}
