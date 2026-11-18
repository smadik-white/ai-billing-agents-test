using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Domain;
using Billing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Billing.IntegrationTests;

[Collection("billing")]
public sealed class BillingFlowTests(BillingApiFixture fixture)
{
    [Fact]
    public async Task Concurrent_subscription_creation_for_same_product_returns_one_success_and_one_conflict()
    {
        await fixture.ResetDatabaseAsync();
        using var organizationResponse = await fixture.Client.PostAsJsonAsync(
            "/api/organizations", new { name = "Concurrent Org" });
        organizationResponse.EnsureSuccessStatusCode();
        using var organization = JsonDocument.Parse(await organizationResponse.Content.ReadAsStringAsync());
        var organizationId = organization.RootElement.GetProperty("id").GetGuid();

        var first = fixture.Client.PostAsJsonAsync(
            $"/api/organizations/{organizationId}/subscriptions",
            new { productCode = "same-product", price = 49.00m, currency = "EUR", billingPeriodDays = 30 });
        var second = fixture.Client.PostAsJsonAsync(
            $"/api/organizations/{organizationId}/subscriptions",
            new { productCode = "same-product", price = 49.00m, currency = "EUR", billingPeriodDays = 30 });
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.Created));
        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Same_idempotency_key_concurrent_requests_create_one_payment_attempt_and_return_same_result()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(1000m, createInitialPayment: false);
        const string key = "concurrent-same-payment-key";

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/invoices/{scenario.InvoiceId}/payment-attempts");
            request.Headers.Add("Idempotency-Key", key);
            return await fixture.Client.SendAsync(request);
        }));

        Assert.All(responses, response => Assert.True((int)response.StatusCode is >= 200 and < 300));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var value = document.RootElement.TryGetProperty("paymentAttempt", out var nested)
                ? nested : document.RootElement;
            ids.Add(value.GetProperty("id").GetGuid());
            response.Dispose();
        }
        Assert.Single(ids.Distinct());

        var state = await fixture.WithDbAsync(async db => new
        {
            Attempts = await db.PaymentAttempts.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            ProviderPaymentCount = fixture.PaymentProvider.DistinctPaymentCount
        });
        Assert.Equal(1, state.Attempts);
        Assert.Equal(1, state.ProviderPaymentCount);
        Assert.Equal(1, fixture.PaymentProvider.CallCount);
    }

    [Fact]
    public async Task Reusing_idempotency_key_with_different_amount_is_rejected_without_creating_another_attempt()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(1000m, createInitialPayment: false);
        var paymentId = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "fixed-key", 500m);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts?amount=400.00");
        request.Headers.Add("Idempotency-Key", "fixed-key");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var state = await fixture.WithDbAsync(async db => new
        {
            Count = await db.PaymentAttempts.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == paymentId)
        });
        Assert.Equal(1, state.Count);
        Assert.Equal(500m, state.Payment.Amount);
    }

    [Fact]
    public async Task Concurrent_different_keys_reserve_distinct_partial_payment_amounts_without_overbooking_invoice()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(1000m, createInitialPayment: false);

        var ids = await Task.WhenAll(
            fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "partial-reserve-a", 500m),
            fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "partial-reserve-b", 500m));
        Assert.Equal(2, ids.Distinct().Count());

        using var overbookRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts?amount=0.01");
        overbookRequest.Headers.Add("Idempotency-Key", "partial-reserve-overbook");
        using var overbookResponse = await fixture.Client.SendAsync(overbookRequest);
        Assert.Equal(HttpStatusCode.Conflict, overbookResponse.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Attempts = await db.PaymentAttempts.Where(x => x.InvoiceId == scenario.InvoiceId).ToListAsync(),
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId)
        });
        Assert.Equal(2, state.Attempts.Count);
        Assert.Equal(1000m, state.Attempts.Sum(x => x.Amount));
        Assert.Equal(0m, state.Invoice.AmountPaid);
        Assert.Equal(1000m, state.Invoice.OutstandingAmount);
    }

    [Fact]
    public async Task Slow_provider_call_happens_after_payment_intent_is_committed_and_database_lock_is_released()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(200m, createInitialPayment: false);
        fixture.PaymentProvider.DelayNextFor(TimeSpan.FromSeconds(1));

        var requestTask = SendCreatePaymentRequestAsync(scenario.InvoiceId, "slow-provider-operation");
        (string Status, Guid Id)? visibleDuringProviderCall = null;
        for (var attempt = 0; attempt < 40 && visibleDuringProviderCall is null; attempt++)
        {
            visibleDuringProviderCall = await fixture.WithDbAsync(async db =>
            {
                var row = await db.PaymentAttempts.Where(x => x.InvoiceId == scenario.InvoiceId)
                    .Select(x => new { x.Status, x.Id }).SingleOrDefaultAsync();
                return row is null ? ((string Status, Guid Id)?)null : (row.Status, row.Id);
            });
            if (visibleDuringProviderCall is null)
                await Task.Delay(25);
        }
        Assert.NotNull(visibleDuringProviderCall);
        Assert.Equal(PaymentStatus.Creating, visibleDuringProviderCall.Value.Status);

        using var response = await requestTask;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var status = await fixture.WithDbAsync(async db => await db.PaymentAttempts
            .Where(x => x.Id == visibleDuringProviderCall.Value.Id).Select(x => x.Status).SingleAsync());
        Assert.Equal(PaymentStatus.Pending, status);
    }

    [Fact]
    public async Task Provider_response_loss_after_remote_create_is_recovered_with_same_provider_idempotency_key()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(500m, createInitialPayment: false);
        const string key = "provider-ambiguous-timeout";
        fixture.PaymentProvider.FailNextAfterCreate();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts?amount=500.00");
        firstRequest.Headers.Add("Idempotency-Key", key);
        using var firstResponse = await fixture.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        using var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstAttemptId = firstBody.RootElement.GetProperty("paymentAttempt").GetProperty("id").GetGuid();

        await fixture.WithDbAsync(async db =>
        {
            var attempt = await db.PaymentAttempts.SingleAsync(x => x.Id == firstAttemptId);
            attempt.ProviderRetryAfterUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            return true;
        });

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts?amount=500.00");
        retryRequest.Headers.Add("Idempotency-Key", key);
        using var retryResponse = await fixture.Client.SendAsync(retryRequest);
        Assert.True(retryResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
        using var retryBody = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        Assert.Equal(firstAttemptId, retryBody.RootElement.GetProperty("id").GetGuid());

        var persisted = await fixture.WithDbAsync(async db => await db.PaymentAttempts.SingleAsync(x => x.Id == firstAttemptId));
        Assert.Equal(PaymentStatus.Pending, persisted.Status);
        Assert.Equal(firstAttemptId.ToString("D"), persisted.ProviderPaymentId);
        Assert.Equal(1, fixture.PaymentProvider.DistinctPaymentCount);
        Assert.Equal(2, fixture.PaymentProvider.CallCount);
    }

    [Fact]
    public async Task Operator_manual_retry_recovers_ambiguous_provider_creation_without_creating_a_duplicate()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(375m, createInitialPayment: false);
        fixture.PaymentProvider.FailNextAfterCreate();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts?amount=375.00");
        firstRequest.Headers.Add("Idempotency-Key", "operator-retry-ambiguous-create");
        using var firstResponse = await fixture.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var attemptId = firstJson.RootElement.GetProperty("paymentAttempt").GetProperty("id").GetGuid();

        using var unauthorizedRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/ops/payment-attempts/{attemptId}/retry");
        unauthorizedRequest.Headers.Add("X-Ops-Key", "wrong-ops-key");
        using var unauthorizedResponse = await fixture.Client.SendAsync(unauthorizedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/ops/payment-attempts/{attemptId}/retry");
        retryRequest.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var retryResponse = await fixture.Client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Attempt = await db.PaymentAttempts.SingleAsync(x => x.Id == attemptId),
            AttemptCount = await db.PaymentAttempts.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            ManualRetryAudits = await db.BillingAuditLogs.CountAsync(x =>
                x.EntityType == "PaymentAttempt" && x.EntityId == attemptId.ToString("D") &&
                x.EventType == "ProviderPaymentManualRetryScheduled")
        });

        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(PaymentStatus.Pending, state.Attempt.Status);
        Assert.Equal(attemptId.ToString("D"), state.Attempt.ProviderPaymentId);
        Assert.Equal(2, fixture.PaymentProvider.CallCount);
        Assert.Equal(1, fixture.PaymentProvider.DistinctPaymentCount);
        Assert.Equal(0m, state.Invoice.AmountPaid);
        Assert.Equal(1, state.ManualRetryAudits);
    }

    [Fact]
    public async Task Successful_payment_activates_subscription_and_grants_access_once_even_when_delivered_concurrently()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync();

        var deliveries = Enumerable.Range(0, 12)
            .Select(_ => fixture.SendWebhookAsync("evt-payment-success-001", "payment.succeeded",
                scenario.PaymentId, scenario.Amount));
        var responses = await Task.WhenAll(deliveries);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal(SubscriptionStatus.Active, subscription.RootElement.GetProperty("status").GetString());
        Assert.True(subscription.RootElement.GetProperty("currentPeriodEndUtc").GetDateTime() > DateTime.UtcNow);

        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        var items = entitlements.RootElement.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.True(items[0].GetProperty("isActive").GetBoolean());

        var totals = await fixture.WithDbAsync(async db => new
        {
            Paid = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync(),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.SubscriptionId == scenario.SubscriptionId),
            Refunds = await db.PaymentRefunds.CountAsync()
        });
        Assert.Equal(scenario.Amount, totals.Paid);
        Assert.Equal(1, totals.Grants);
        Assert.Equal(0, totals.Refunds);
    }

    [Fact]
    public async Task Different_event_ids_for_same_payment_do_not_credit_or_extend_twice()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(99m, 30);
        var deliveries = await Task.WhenAll(
            fixture.SendWebhookAsync("evt-success-a", "payment.succeeded", scenario.PaymentId, scenario.Amount),
            fixture.SendWebhookAsync("evt-success-b", "payment.succeeded", scenario.PaymentId, scenario.Amount));
        Assert.All(deliveries, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in deliveries) response.Dispose();

        var state = await fixture.WithDbAsync(async db => new
        {
            Paid = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync(),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            Processed = await db.WebhookEvents.CountAsync(x => x.PaymentAttemptId == scenario.PaymentId && x.Status == WebhookStatus.Processed)
        });
        Assert.Equal(99m, state.Paid);
        Assert.Equal(1, state.Grants);
        Assert.Equal(2, state.Processed);
    }

    [Fact]
    public async Task Same_event_id_with_different_payload_is_persisted_as_a_diagnostic_conflict()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(150m);
        using var original = await fixture.SendWebhookAsync("evt-reused-id", "payment.succeeded", scenario.PaymentId, 150m);
        Assert.Equal(HttpStatusCode.Accepted, original.StatusCode);

        using var conflicting = await fixture.SendWebhookAsync("evt-reused-id", "payment.succeeded", scenario.PaymentId, 149m);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Conflicts = await db.WebhookConflicts.CountAsync(x => x.ProviderEventId == "evt-reused-id"),
            EventCount = await db.WebhookEvents.CountAsync(x => x.ProviderEventId == "evt-reused-id"),
            Paid = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync()
        });
        Assert.Equal(1, state.Conflicts);
        Assert.Equal(1, state.EventCount);
        Assert.Equal(150m, state.Paid);
    }

    [Fact]
    public async Task Full_refund_suspends_subscription_and_revokes_entitlement()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(119.00m);

        using var success = await fixture.SendWebhookAsync("evt-paid-001", "payment.succeeded", scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, success.StatusCode);
        using var refund = await fixture.SendWebhookAsync("evt-refund-001", "payment.refunded", scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, refund.StatusCode);

        using var invoice = await fixture.GetJsonAsync($"/api/invoices/{scenario.InvoiceId}");
        Assert.Equal(InvoiceStatus.Refunded, invoice.RootElement.GetProperty("status").GetString());
        Assert.Equal(119m, invoice.RootElement.GetProperty("amountPaid").GetDecimal());
        Assert.Equal(119m, invoice.RootElement.GetProperty("amountRefunded").GetDecimal());

        using var payment = await fixture.GetJsonAsync($"/api/payment-attempts/{scenario.PaymentId}");
        Assert.Equal(PaymentStatus.Refunded, payment.RootElement.GetProperty("status").GetString());
        Assert.Equal(119m, payment.RootElement.GetProperty("refundedAmount").GetDecimal());

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal(SubscriptionStatus.Suspended, subscription.RootElement.GetProperty("status").GetString());
        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(entitlements.RootElement[0].GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Early_refund_is_journaled_and_can_be_retried_after_payment_confirmation()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(75.00m);

        using var earlyRefund = await fixture.SendWebhookAsync("evt-refund-before-payment", "payment.refunded",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, earlyRefund.StatusCode);
        using var failedResult = JsonDocument.Parse(await earlyRefund.Content.ReadAsStringAsync());
        Assert.Equal(WebhookStatus.Failed, failedResult.RootElement.GetProperty("status").GetString());

        using var success = await fixture.SendWebhookAsync("evt-payment-arrived", "payment.succeeded",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, success.StatusCode);

        using var opsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ops/webhooks");
        opsRequest.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var opsResponse = await fixture.Client.SendAsync(opsRequest);
        opsResponse.EnsureSuccessStatusCode();
        using var rows = JsonDocument.Parse(await opsResponse.Content.ReadAsStringAsync());
        var failedEvent = rows.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("providerEventId").GetString() == "evt-refund-before-payment");
        var failedId = failedEvent.GetProperty("id").GetGuid();
        Assert.Equal(WebhookStatus.Failed, failedEvent.GetProperty("status").GetString());

        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/ops/webhooks/{failedId}/retry");
        retry.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var retryResponse = await fixture.Client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        using var retryBody = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        Assert.Equal(WebhookStatus.Processed, retryBody.RootElement.GetProperty("status").GetString());

        using var invoice = await fixture.GetJsonAsync($"/api/invoices/{scenario.InvoiceId}");
        Assert.Equal(InvoiceStatus.Refunded, invoice.RootElement.GetProperty("status").GetString());
        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal(SubscriptionStatus.Suspended, subscription.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Concurrent_success_and_refund_events_are_serialized_on_the_payment_attempt()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(88.00m);

        var successTask = fixture.SendWebhookAsync("evt-race-success", "payment.succeeded", scenario.PaymentId, scenario.Amount);
        var refundTask = fixture.SendWebhookAsync("evt-race-refund", "payment.refunded", scenario.PaymentId, scenario.Amount);
        var responses = await Task.WhenAll(successTask, refundTask);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        var refundEvent = await fixture.WithDbAsync(async db => await db.WebhookEvents
            .SingleAsync(x => x.ProviderEventId == "evt-race-refund"));
        if (refundEvent.Status == WebhookStatus.Failed)
        {
            using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/ops/webhooks/{refundEvent.Id}/retry");
            retry.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
            using var retryResponse = await fixture.Client.SendAsync(retry);
            Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        }

        var state = await fixture.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == scenario.PaymentId),
            Refunds = await db.PaymentRefunds.CountAsync(x => x.PaymentAttemptId == scenario.PaymentId)
        });
        Assert.Equal(88m, state.Invoice.AmountPaid);
        Assert.Equal(88m, state.Invoice.AmountRefunded);
        Assert.Equal(PaymentStatus.Refunded, state.Payment.Status);
        Assert.Equal(1, state.Refunds);
    }

    [Fact]
    public async Task Invoice_can_be_settled_by_two_partial_payments_and_duplicate_capture_is_credited_once()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(1000m, createInitialPayment: false);
        var firstPayment = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "part-1", 500m);
        var secondPayment = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "part-2", 500m);

        using var first = await fixture.SendWebhookAsync("evt-part-1", "payment.succeeded", firstPayment, 500m);
        using var duplicate = await fixture.SendWebhookAsync("evt-part-1-second-id", "payment.succeeded", firstPayment, 500m);
        using var second = await fixture.SendWebhookAsync("evt-part-2", "payment.succeeded", secondPayment, 500m);
        Assert.All(new[] { first, duplicate, second }, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));

        var state = await fixture.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            Attempts = await db.PaymentAttempts.Where(x => x.InvoiceId == scenario.InvoiceId).ToListAsync()
        });
        Assert.Equal(1000m, state.Invoice.AmountPaid);
        Assert.Equal(0m, state.Invoice.AmountRefunded);
        Assert.Equal(InvoiceStatus.Paid, state.Invoice.Status);
        Assert.Equal(1, state.Grants);
        Assert.Equal(2, state.Attempts.Count(x => x.Status == PaymentStatus.Succeeded));
    }

    [Fact]
    public async Task Partial_refund_is_counted_once_across_different_event_ids_and_revokes_underfunded_period()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(1000m, createInitialPayment: false);
        var firstPayment = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "part-refund-1", 500m);
        var secondPayment = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "part-refund-2", 500m);
        using var paid1 = await fixture.SendWebhookAsync("evt-pr-paid-1", "payment.succeeded", firstPayment, 500m);
        using var paid2 = await fixture.SendWebhookAsync("evt-pr-paid-2", "payment.succeeded", secondPayment, 500m);

        using var refund1 = await fixture.SendWebhookAsync("evt-pr-refund-a", "payment.refunded", firstPayment, 200m, refundId: "refund-stable-200");
        using var refundDuplicate = await fixture.SendWebhookAsync("evt-pr-refund-b", "payment.refunded", firstPayment, 200m, refundId: "refund-stable-200");
        Assert.Equal(HttpStatusCode.Accepted, refund1.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, refundDuplicate.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == firstPayment),
            RefundRows = await db.PaymentRefunds.CountAsync(x => x.ProviderRefundId == "refund-stable-200"),
            GrantActive = await db.SubscriptionPeriodGrants.Where(x => x.InvoiceId == scenario.InvoiceId).Select(x => x.IsActive).SingleAsync(),
            Subscription = await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId)
        });
        Assert.Equal(1000m, state.Invoice.AmountPaid);
        Assert.Equal(200m, state.Invoice.AmountRefunded);
        Assert.Equal(InvoiceStatus.PartiallyRefunded, state.Invoice.Status);
        Assert.Equal(200m, state.Payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, state.Payment.Status);
        Assert.Equal(1, state.RefundRows);
        Assert.False(state.GrantActive);
        Assert.Equal(SubscriptionStatus.Suspended, state.Subscription.Status);
    }

    [Fact]
    public async Task Concurrent_partial_refunds_cannot_exceed_captured_amount()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(500m);
        using var paid = await fixture.SendWebhookAsync("evt-refund-race-paid", "payment.succeeded", scenario.PaymentId, 500m);

        var responses = await Task.WhenAll(
            fixture.SendWebhookAsync("evt-refund-race-a", "payment.refunded", scenario.PaymentId, 300m, refundId: "refund-race-a"),
            fixture.SendWebhookAsync("evt-refund-race-b", "payment.refunded", scenario.PaymentId, 300m, refundId: "refund-race-b"));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        var state = await fixture.WithDbAsync(async db => new
        {
            InvoiceRefunded = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountRefunded).SingleAsync(),
            PaymentRefunded = await db.PaymentAttempts.Where(x => x.Id == scenario.PaymentId).Select(x => x.RefundedAmount).SingleAsync(),
            RefundCount = await db.PaymentRefunds.CountAsync(x => x.PaymentAttemptId == scenario.PaymentId),
            DeadLetters = await db.WebhookEvents.CountAsync(x => x.ProviderEventId.StartsWith("evt-refund-race-") && x.Status == WebhookStatus.DeadLetter)
        });
        Assert.Equal(300m, state.InvoiceRefunded);
        Assert.Equal(300m, state.PaymentRefunded);
        Assert.Equal(1, state.RefundCount);
        Assert.Equal(1, state.DeadLetters);
    }

    [Fact]
    public async Task Refund_larger_than_remaining_capture_is_dead_lettered_without_financial_mutation()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(500m);
        using var paid = await fixture.SendWebhookAsync("evt-overrefund-paid", "payment.succeeded", scenario.PaymentId, 500m);
        using var refund = await fixture.SendWebhookAsync("evt-overrefund", "payment.refunded", scenario.PaymentId, 501m, refundId: "refund-too-large");
        Assert.Equal(HttpStatusCode.Accepted, refund.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == scenario.PaymentId),
            Webhook = await db.WebhookEvents.SingleAsync(x => x.ProviderEventId == "evt-overrefund")
        });
        Assert.Equal(WebhookStatus.DeadLetter, state.Webhook.Status);
        Assert.Equal(500m, state.Invoice.AmountPaid);
        Assert.Equal(0m, state.Invoice.AmountRefunded);
        Assert.Equal(0m, state.Payment.RefundedAmount);
        Assert.Equal(PaymentStatus.Succeeded, state.Payment.Status);
    }

    [Fact]
    public async Task Renewal_for_next_invoice_extends_from_previous_period_and_duplicate_event_does_not_extend_again()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(80m, billingPeriodDays: 30);
        using var firstPayment = await fixture.SendWebhookAsync("evt-renewal-first", "payment.succeeded", scenario.PaymentId, 80m);
        using var firstPeriod = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        var firstEnd = firstPeriod.RootElement.GetProperty("currentPeriodEndUtc").GetDateTime();

        var invoice2 = await fixture.CreateInvoiceAsync(scenario.SubscriptionId, 80m);
        var payment2 = await fixture.CreatePaymentAttemptAsync(invoice2, "renewal-period-2");
        using var secondPayment = await fixture.SendWebhookAsync("evt-renewal-second", "payment.succeeded", payment2, 80m);
        using var duplicate = await fixture.SendWebhookAsync("evt-renewal-second-new-event-id", "payment.succeeded", payment2, 80m);
        using var current = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        var currentEnd = current.RootElement.GetProperty("currentPeriodEndUtc").GetDateTime();

        Assert.True(currentEnd - firstEnd > TimeSpan.FromDays(29));
        Assert.Equal(2, await fixture.WithDbAsync(db => db.SubscriptionPeriodGrants.CountAsync(x => x.SubscriptionId == scenario.SubscriptionId)));
    }

    [Fact]
    public async Task Concurrent_successes_for_different_subscription_periods_do_not_lose_a_period()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(25m, billingPeriodDays: 30, createInitialPayment: false);
        var invoice2 = await fixture.CreateInvoiceAsync(scenario.SubscriptionId, 25m);
        var invoice3 = await fixture.CreateInvoiceAsync(scenario.SubscriptionId, 25m);
        var payment1 = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "period-concurrent-1");
        var payment2 = await fixture.CreatePaymentAttemptAsync(invoice2, "period-concurrent-2");
        var payment3 = await fixture.CreatePaymentAttemptAsync(invoice3, "period-concurrent-3");

        var responses = await Task.WhenAll(
            fixture.SendWebhookAsync("evt-period-one", "payment.succeeded", payment1, 25m),
            fixture.SendWebhookAsync("evt-period-two", "payment.succeeded", payment2, 25m),
            fixture.SendWebhookAsync("evt-period-three", "payment.succeeded", payment3, 25m));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        var state = await fixture.WithDbAsync(async db => new
        {
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.SubscriptionId == scenario.SubscriptionId && x.IsActive),
            Paid = await db.Invoices.Where(x => x.SubscriptionId == scenario.SubscriptionId).SumAsync(x => x.AmountPaid),
            End = await db.Subscriptions.Where(x => x.Id == scenario.SubscriptionId).Select(x => x.CurrentPeriodEndUtc).SingleAsync()
        });
        Assert.Equal(3, state.Grants);
        Assert.Equal(75m, state.Paid);
        Assert.True(state.End > DateTime.UtcNow.AddDays(89));
    }

    [Fact]
    public async Task Payment_after_previous_period_expiry_starts_a_new_period_from_now()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(40m, billingPeriodDays: 30);
        using var firstPayment = await fixture.SendWebhookAsync("evt-expiry-first", "payment.succeeded", scenario.PaymentId, 40m);
        var invoice2 = await fixture.CreateInvoiceAsync(scenario.SubscriptionId, 40m);
        var payment2 = await fixture.CreatePaymentAttemptAsync(invoice2, "period-after-expiry");

        await fixture.WithDbAsync(async db =>
        {
            var subscription = await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId);
            subscription.CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(-2);
            var grant = await db.SubscriptionPeriodGrants.SingleAsync(x => x.InvoiceId == scenario.InvoiceId);
            grant.PeriodEndUtc = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
            return true;
        });

        var before = DateTime.UtcNow;
        using var secondPayment = await fixture.SendWebhookAsync("evt-expiry-second", "payment.succeeded", payment2, 40m);
        var subscriptionAfter = await fixture.WithDbAsync(async db => await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId));
        Assert.True(subscriptionAfter.CurrentPeriodStartUtc >= before.AddSeconds(-2));
        Assert.True(subscriptionAfter.CurrentPeriodEndUtc > before.AddDays(29));
    }

    [Fact]
    public async Task Expired_paid_period_revokes_effective_access_and_expiry_worker_persists_suspension()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(45m, billingPeriodDays: 30);
        using var paid = await fixture.SendWebhookAsync("evt-expiry-worker-paid", "payment.succeeded", scenario.PaymentId, 45m);

        await fixture.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow.AddMinutes(-1);
            var subscription = await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId);
            subscription.CurrentPeriodEndUtc = now;
            var grant = await db.SubscriptionPeriodGrants.SingleAsync(x => x.InvoiceId == scenario.InvoiceId);
            grant.PeriodEndUtc = now;
            var entitlement = await db.ProductEntitlements.SingleAsync(x => x.OrganizationId == scenario.OrganizationId);
            entitlement.ValidUntilUtc = now;
            // Simulate the short interval before the polling worker persists the expiry transition.
            entitlement.IsActive = true;
            await db.SaveChangesAsync();
            return true;
        });

        using var effectiveAccess = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(effectiveAccess.RootElement[0].GetProperty("isActive").GetBoolean());

        var expiryService = fixture.Services.GetRequiredService<SubscriptionExpiryService>();
        Assert.Equal(1, await expiryService.ExpireDueSubscriptionsAsync());
        var state = await fixture.WithDbAsync(async db => new
        {
            Subscription = await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId),
            GrantActive = await db.SubscriptionPeriodGrants.Where(x => x.InvoiceId == scenario.InvoiceId)
                .Select(x => x.IsActive).SingleAsync(),
            Entitlement = await db.ProductEntitlements.SingleAsync(x => x.OrganizationId == scenario.OrganizationId)
        });
        Assert.Equal(SubscriptionStatus.Suspended, state.Subscription.Status);
        Assert.False(state.GrantActive);
        Assert.False(state.Entitlement.IsActive);
    }

    [Fact]
    public async Task Cancellation_is_immediate_and_late_success_is_recorded_for_manual_refund_without_reactivation()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(70m);
        using var cancel = await fixture.Client.PostAsync($"/api/subscriptions/{scenario.SubscriptionId}/cancel", content: null);
        cancel.EnsureSuccessStatusCode();
        using var success = await fixture.SendWebhookAsync("evt-after-cancel", "payment.succeeded", scenario.PaymentId, 70m);
        Assert.Equal(HttpStatusCode.Accepted, success.StatusCode);

        var state = await fixture.WithDbAsync(async db => new
        {
            Subscription = await db.Subscriptions.SingleAsync(x => x.Id == scenario.SubscriptionId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == scenario.PaymentId),
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.SubscriptionId == scenario.SubscriptionId)
        });
        Assert.Equal(SubscriptionStatus.Cancelled, state.Subscription.Status);
        Assert.True(state.Payment.RequiresManualRefund);
        Assert.Equal(70m, state.Invoice.AmountPaid);
        Assert.Equal(InvoiceStatus.Paid, state.Invoice.Status);
        Assert.Equal(0, state.Grants);
    }

    [Fact]
    public async Task Webhook_failure_after_mutation_rolls_back_financial_state_and_manual_retry_recovers_atomically()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(90m);
        fixture.WebhookFaultInjector.FailNext();

        using var failed = await fixture.SendWebhookAsync("evt-fault-injected", "payment.succeeded", scenario.PaymentId, 90m);
        Assert.Equal(HttpStatusCode.Accepted, failed.StatusCode);
        using var failedBody = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        Assert.Equal(WebhookStatus.Failed, failedBody.RootElement.GetProperty("status").GetString());

        var beforeRetry = await fixture.WithDbAsync(async db => new
        {
            Paid = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync(),
            PaymentStatus = await db.PaymentAttempts.Where(x => x.Id == scenario.PaymentId).Select(x => x.Status).SingleAsync(),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.InvoiceId == scenario.InvoiceId)
        });
        Assert.Equal(0m, beforeRetry.Paid);
        Assert.Equal(PaymentStatus.Pending, beforeRetry.PaymentStatus);
        Assert.Equal(0, beforeRetry.Grants);

        var webhookId = await fixture.WithDbAsync(async db => await db.WebhookEvents
            .Where(x => x.ProviderEventId == "evt-fault-injected").Select(x => x.Id).SingleAsync());
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/ops/webhooks/{webhookId}/retry");
        retry.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var retryResponse = await fixture.Client.SendAsync(retry);
        retryResponse.EnsureSuccessStatusCode();

        var afterRetry = await fixture.WithDbAsync(async db => new
        {
            Paid = await db.Invoices.Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync(),
            PaymentStatus = await db.PaymentAttempts.Where(x => x.Id == scenario.PaymentId).Select(x => x.Status).SingleAsync(),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.InvoiceId == scenario.InvoiceId),
            EventStatus = await db.WebhookEvents.Where(x => x.Id == webhookId).Select(x => x.Status).SingleAsync()
        });
        Assert.Equal(90m, afterRetry.Paid);
        Assert.Equal(PaymentStatus.Succeeded, afterRetry.PaymentStatus);
        Assert.Equal(1, afterRetry.Grants);
        Assert.Equal(WebhookStatus.Processed, afterRetry.EventStatus);
    }

    [Fact]
    public async Task Database_outage_does_not_lose_provider_event_when_provider_redelivers_after_recovery()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(55m);
        await fixture.StopDatabaseAsync();
        HttpResponseMessage? outageResponse = null;
        try
        {
            try
            {
                outageResponse = await fixture.SendWebhookAsync("evt-db-outage", "payment.succeeded", scenario.PaymentId, 55m);
                Assert.Equal(HttpStatusCode.InternalServerError, outageResponse.StatusCode);
            }
            catch (NpgsqlException)
            {
                // TestServer may propagate an unhandled database connectivity exception instead of
                // translating it into HTTP 500; the assertion of durability is the successful retry below.
            }
        }
        finally
        {
            outageResponse?.Dispose();
            await fixture.StartDatabaseAsync();
        }

        using var retry = await fixture.SendWebhookAsync("evt-db-outage", "payment.succeeded", scenario.PaymentId, 55m);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var paid = await fixture.WithDbAsync(async db => await db.Invoices
            .Where(x => x.Id == scenario.InvoiceId).Select(x => x.AmountPaid).SingleAsync());
        Assert.Equal(55m, paid);
    }

    [Fact]
    public async Task Failed_payment_attempt_can_be_retried_with_a_new_idempotency_key()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(29.00m);

        using var failedEvent = await fixture.SendWebhookAsync("evt-payment-failed", "payment.failed",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, failedEvent.StatusCode);

        using var oldAttempt = await fixture.GetJsonAsync($"/api/payment-attempts/{scenario.PaymentId}");
        Assert.Equal(PaymentStatus.Failed, oldAttempt.RootElement.GetProperty("status").GetString());

        var newPaymentId = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "retry-after-failed-attempt");
        Assert.NotEqual(scenario.PaymentId, newPaymentId);
        using var newAttempt = await fixture.GetJsonAsync($"/api/payment-attempts/{newPaymentId}");
        Assert.Equal(PaymentStatus.Pending, newAttempt.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Late_capture_after_invoice_refund_is_flagged_without_restoring_access()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(64.00m);

        using var firstFailed = await fixture.SendWebhookAsync("evt-first-attempt-failed", "payment.failed",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, firstFailed.StatusCode);

        var secondPaymentId = await fixture.CreatePaymentAttemptAsync(scenario.InvoiceId, "second-after-failure");
        using var secondSuccess = await fixture.SendWebhookAsync("evt-second-attempt-success", "payment.succeeded",
            secondPaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, secondSuccess.StatusCode);
        using var refund = await fixture.SendWebhookAsync("evt-second-attempt-refund", "payment.refunded",
            secondPaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, refund.StatusCode);

        using var lateSuccess = await fixture.SendWebhookAsync("evt-first-attempt-late-success", "payment.succeeded",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, lateSuccess.StatusCode);

        using var firstAttempt = await fixture.GetJsonAsync($"/api/payment-attempts/{scenario.PaymentId}");
        Assert.Equal(PaymentStatus.Succeeded, firstAttempt.RootElement.GetProperty("status").GetString());
        Assert.True(firstAttempt.RootElement.GetProperty("requiresManualRefund").GetBoolean());

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal(SubscriptionStatus.Suspended, subscription.RootElement.GetProperty("status").GetString());
        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(entitlements.RootElement[0].GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Success_webhook_after_payment_was_returned_is_a_financial_no_op()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(110m);
        using var success = await fixture.SendWebhookAsync("evt-before-refund", "payment.succeeded", scenario.PaymentId, 110m);
        using var refund = await fixture.SendWebhookAsync("evt-full-return", "payment.refunded", scenario.PaymentId, 110m);
        using var lateSuccess = await fixture.SendWebhookAsync("evt-after-refund-success", "payment.succeeded", scenario.PaymentId, 110m);

        var state = await fixture.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.SingleAsync(x => x.Id == scenario.InvoiceId),
            Payment = await db.PaymentAttempts.SingleAsync(x => x.Id == scenario.PaymentId),
            Grants = await db.SubscriptionPeriodGrants.CountAsync(x => x.InvoiceId == scenario.InvoiceId)
        });
        Assert.Equal(110m, state.Invoice.AmountPaid);
        Assert.Equal(110m, state.Invoice.AmountRefunded);
        Assert.Equal(PaymentStatus.Refunded, state.Payment.Status);
        Assert.Equal(1, state.Grants);
    }

    [Fact]
    public async Task Invalid_hmac_signature_is_rejected_and_not_journaled()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/test-provider")
        {
            Content = JsonContent.Create(new
            {
                eventId = "evt-invalid-signature",
                eventType = "payment.succeeded",
                paymentId = scenario.PaymentId,
                amount = scenario.Amount,
                currency = "EUR",
                occurredAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            })
        };
        request.Headers.Add("X-Test-Signature", "sha256=deadbeef");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal(SubscriptionStatus.PendingPayment, subscription.RootElement.GetProperty("status").GetString());
        var events = await fixture.WithDbAsync(db => db.WebhookEvents.CountAsync());
        Assert.Equal(0, events);
    }

    private async Task<HttpResponseMessage> SendCreatePaymentRequestAsync(Guid invoiceId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invoices/{invoiceId}/payment-attempts");
        request.Headers.Add("Idempotency-Key", key);
        return await fixture.Client.SendAsync(request);
    }
}

public sealed class BillingRulesUnitTests
{
    [Fact]
    public void Payment_fingerprint_is_stable_for_same_request_and_changes_when_amount_changes()
    {
        var invoiceId = Guid.Parse("5f9c42b9-4a50-4ac1-a8a2-5a4c6c0b6211");
        var first = PaymentAttemptService.ComputeFingerprint(invoiceId, 500m, "eur");
        var retry = PaymentAttemptService.ComputeFingerprint(invoiceId, 500.00m, "EUR");
        var changed = PaymentAttemptService.ComputeFingerprint(invoiceId, 400m, "EUR");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Invoice_outstanding_balance_uses_net_paid_amount_after_partial_refund()
    {
        var invoice = new Invoice
        {
            OrganizationId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            Number = "INV-UNIT-1",
            Currency = "EUR",
            TotalAmount = 1000m,
            AmountPaid = 500m,
            AmountRefunded = 200m,
            BillingPeriodDays = 30
        };

        Assert.Equal(300m, InvoiceFinancialRules.NetPaidAmount(invoice));
        Assert.Equal(700m, InvoiceFinancialRules.OutstandingAmount(invoice));
        Assert.Equal(InvoiceStatus.PartiallyRefunded, InvoiceFinancialRules.DetermineStatus(invoice));
    }

    [Fact]
    public void Period_start_uses_future_period_end_and_uses_now_after_expiry()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var previous = now.AddDays(10);
        Assert.Equal(previous, SubscriptionPeriodRules.StartNextPeriod(now, previous));
        Assert.Equal(now, SubscriptionPeriodRules.StartNextPeriod(now, now.AddTicks(-1)));
        Assert.Equal(now, SubscriptionPeriodRules.StartNextPeriod(now, null));
    }
}
