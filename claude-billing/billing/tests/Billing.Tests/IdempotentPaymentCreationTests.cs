using System.Net;
using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

[Collection(ApiCollection.Name)]
public class IdempotentPaymentCreationTests
{
    private readonly ApiFixture _api;

    public IdempotentPaymentCreationTests(ApiFixture api)
    {
        _api = api;
        api.ResetFaults();
    }

    private static string Key() => Guid.NewGuid().ToString();
    private static string Url(Guid invoiceId) => $"/api/invoices/{invoiceId}/payment-attempts";

    private async Task<InvoiceDto> NewInvoiceAsync(long price = 100_000) =>
        await _api.NewInvoiceAsync(await _api.NewSubscriptionAsync(price));

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_one_logical_payment()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _api.PostRawAsync(Url(inv.Id), new { }, key)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"unexpected {(int)r.StatusCode}"));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var attempts = await Task.WhenAll(responses.Select(r => ApiFixture.ReadAsync<AttemptDto>(r)));
        Assert.Single(attempts.Select(a => a.Id).Distinct());
        Assert.Single(attempts.Select(a => a.ProviderPaymentId).Distinct());

        var rows = await _api.AttemptsDb(inv.Id);
        var row = Assert.Single(rows);
        Assert.Equal(PaymentAttemptStatus.Pending, row.Status);
        Assert.Equal(inv.TotalMinor, row.AmountMinor);
        Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id)); // exactly one payment exists at the provider
    }

    [Fact]
    public async Task Replay_with_same_key_and_parameters_returns_the_original_result_even_after_the_invoice_moved_on()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        var first = await _api.PostRawAsync(Url(inv.Id), new { }, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ApiFixture.ReadAsync<AttemptDto>(first);

        await _api.PayAsync(original.ProviderPaymentId, original.AmountMinor); // invoice is Paid now

        var replay = await _api.PostRawAsync(Url(inv.Id), new { }, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var again = await ApiFixture.ReadAsync<AttemptDto>(replay);
        Assert.Equal(original.Id, again.Id);
        Assert.Equal(original.ProviderPaymentId, again.ProviderPaymentId);
        Assert.Single(await _api.AttemptsDb(inv.Id));
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task Same_key_with_a_different_amount_is_rejected_and_creates_nothing()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        Assert.Equal(HttpStatusCode.Created, (await _api.PostRawAsync(Url(inv.Id), new { amountMinor = 40_000 }, key)).StatusCode);

        var res = await _api.PostRawAsync(Url(inv.Id), new { amountMinor = 50_000 }, key);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Contains("idempotency_key_reused", await res.Content.ReadAsStringAsync());

        // "full" vs explicit amount are different parameters too
        Assert.Equal((HttpStatusCode)422, (await _api.PostRawAsync(Url(inv.Id), new { }, key)).StatusCode);

        var row = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(40_000, row.AmountMinor);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));
    }

    [Fact]
    public async Task Same_key_for_a_different_invoice_is_rejected()
    {
        var a = await NewInvoiceAsync();
        var b = await NewInvoiceAsync();
        var key = Key();
        Assert.Equal(HttpStatusCode.Created, (await _api.PostRawAsync(Url(a.Id), new { }, key)).StatusCode);

        var res = await _api.PostRawAsync(Url(b.Id), new { }, key);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Empty(await _api.AttemptsDb(b.Id));
        Assert.Equal(0, _api.Provider.PaymentsFor(b.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_001)]
    public async Task Invalid_amounts_are_rejected(int amount)
    {
        var inv = await NewInvoiceAsync(100_000);
        var res = await _api.PostRawAsync(Url(inv.Id), new { amountMinor = amount }, Key());
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Empty(await _api.AttemptsDb(inv.Id));
    }

    // ---- failures ----------------------------------------------------------------------------------

    [Fact]
    public async Task Provider_response_lost_after_creation_does_not_create_a_second_payment_on_retry()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        _api.Provider.FailAfterCreate(1); // provider really creates the payment, but we never see the answer

        var first = await _api.PostRawAsync(Url(inv.Id), new { }, key);
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);

        var reserved = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(PaymentAttemptStatus.Creating, reserved.Status); // our reservation survived
        Assert.Null(reserved.ProviderPaymentId);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));

        var retry = await _api.PostRawAsync(Url(inv.Id), new { }, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var attempt = await ApiFixture.ReadAsync<AttemptDto>(retry);
        Assert.Equal("Pending", attempt.Status);

        var row = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(attempt.ProviderPaymentId, row.ProviderPaymentId);
        Assert.Equal(PaymentAttemptStatus.Pending, row.Status);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id)); // still ONE payment at the provider
    }

    [Fact]
    public async Task Provider_failure_before_creation_can_be_retried_with_the_same_key()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        _api.Provider.FailBeforeCreate(1);

        Assert.Equal(HttpStatusCode.BadGateway, (await _api.PostRawAsync(Url(inv.Id), new { }, key)).StatusCode);
        Assert.Equal(0, _api.Provider.PaymentsFor(inv.Id));

        Assert.Equal(HttpStatusCode.OK, (await _api.PostRawAsync(Url(inv.Id), new { }, key)).StatusCode);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, Assert.Single(await _api.AttemptsDb(inv.Id)).Status);
    }

    [Fact]
    public async Task Slow_provider_times_out_without_holding_state_and_the_retry_succeeds()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        _api.Provider.Delay = TimeSpan.FromSeconds(10); // Billing:ProviderTimeoutSeconds is 2 in tests

        var started = DateTimeOffset.UtcNow;
        var first = await _api.PostRawAsync(Url(inv.Id), new { }, key);
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.InRange((DateTimeOffset.UtcNow - started).TotalSeconds, 1.5, 8);
        Assert.Equal(0, _api.Provider.PaymentsFor(inv.Id));

        _api.Provider.Delay = TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, (await _api.PostRawAsync(Url(inv.Id), new { }, key)).StatusCode);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));
        Assert.Single(await _api.AttemptsDb(inv.Id));
    }

    [Fact]
    public async Task Crash_between_provider_call_and_db_save_is_healed_by_the_recovery_sweep_without_a_client_retry()
    {
        var inv = await NewInvoiceAsync(100_000);
        _api.Provider.FailAfterCreate(1);
        Assert.Equal(HttpStatusCode.BadGateway, (await _api.PostRawAsync(Url(inv.Id), new { }, Key())).StatusCode);
        Assert.Equal(PaymentAttemptStatus.Creating, Assert.Single(await _api.AttemptsDb(inv.Id)).Status);

        // The sweep ignores attempts younger than the threshold ...
        Assert.Equal(0, await _api.ResumeStaleCreationsAsync(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(PaymentAttemptStatus.Creating, Assert.Single(await _api.AttemptsDb(inv.Id)).Status);

        // ... and completes older ones using the same provider key => same payment, no duplicate.
        Assert.True(await _api.ResumeStaleCreationsAsync(DateTimeOffset.UtcNow.AddMinutes(5)) >= 1);
        var row = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, row.Status);
        Assert.NotNull(row.ProviderPaymentId);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));

        // The recovered payment is fully usable: its webhook now finds the attempt.
        var res = await _api.PayAsync(row.ProviderPaymentId!, row.AmountMinor);
        Assert.Equal("processed", await ApiFixture.StatusOf(res));
        Assert.Equal("Paid", (await _api.GetAsync<InvoiceDto>($"/api/invoices/{inv.Id}")).Status);
        await _api.AssertInvoiceIntegrityAsync(inv.Id);
    }

    [Fact]
    public async Task Concurrent_requests_with_provider_failures_still_end_with_exactly_one_payment()
    {
        var inv = await NewInvoiceAsync();
        var key = Key();
        _api.Provider.FailAfterCreate(3); // first three provider calls "lose" their response

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _api.PostRawAsync(Url(inv.Id), new { }, key)));
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.BadGateway, $"unexpected {(int)r.StatusCode}"));
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.BadGateway) >= 1);

        HttpResponseMessage final;
        var tries = 0;
        do { final = await _api.PostRawAsync(Url(inv.Id), new { }, key); } // the client keeps retrying with the same key
        while (final.StatusCode == HttpStatusCode.BadGateway && ++tries < 5);
        Assert.True(final.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"unexpected {(int)final.StatusCode}");

        var row = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, row.Status);
        Assert.Equal(1, _api.Provider.PaymentsFor(inv.Id));
    }

    [Fact]
    public async Task No_database_transaction_or_lock_is_held_while_waiting_for_the_provider()
    {
        var inv = await NewInvoiceAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Provider.Gate = gate;
        _api.Provider.Entered = entered;

        var request = _api.PostRawAsync(Url(inv.Id), new { }, Key());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the request is now "on the network"

        // The reservation is already committed and visible to everybody ...
        var row = Assert.Single(await _api.AttemptsDb(inv.Id));
        Assert.Equal(PaymentAttemptStatus.Creating, row.Status);

        // ... and nothing is locked: NOWAIT would throw immediately if a transaction still held the rows.
        await _api.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($$"""SELECT 1 FROM "Invoices" WHERE "Id" = {{inv.Id}} FOR UPDATE NOWAIT""");
            await db.Database.ExecuteSqlAsync($$"""SELECT 1 FROM "PaymentAttempts" WHERE "Id" = {{row.Id}} FOR UPDATE NOWAIT""");
            await tx.RollbackAsync();
            return 0;
        });

        gate.SetResult();
        Assert.Equal(HttpStatusCode.Created, (await request).StatusCode);
        Assert.Equal(PaymentAttemptStatus.Pending, Assert.Single(await _api.AttemptsDb(inv.Id)).Status);
    }
}
