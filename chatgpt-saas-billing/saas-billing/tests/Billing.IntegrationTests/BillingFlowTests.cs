using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        Assert.Equal("Active", subscription.RootElement.GetProperty("status").GetString());
        Assert.True(subscription.RootElement.GetProperty("currentPeriodEndUtc").GetDateTime() > DateTime.UtcNow);

        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        var items = entitlements.RootElement.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.True(items[0].GetProperty("isActive").GetBoolean());

        using var opsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ops/webhooks");
        opsRequest.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var opsResponse = await fixture.Client.SendAsync(opsRequest);
        opsResponse.EnsureSuccessStatusCode();
        using var webhookRows = JsonDocument.Parse(await opsResponse.Content.ReadAsStringAsync());
        Assert.Single(webhookRows.RootElement.EnumerateArray());
        Assert.Equal("Processed", webhookRows.RootElement[0].GetProperty("status").GetString());
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
        Assert.Equal("Refunded", invoice.RootElement.GetProperty("status").GetString());
        Assert.Equal("Refunded", invoice.RootElement.GetProperty("paymentAttempts")[0].GetProperty("status").GetString());

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal("Suspended", subscription.RootElement.GetProperty("status").GetString());

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
        Assert.Equal("Failed", failedResult.RootElement.GetProperty("status").GetString());

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
        Assert.Equal("Failed", failedEvent.GetProperty("status").GetString());

        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/ops/webhooks/{failedId}/retry");
        retry.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var retryResponse = await fixture.Client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        using var retryBody = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        Assert.Equal("Processed", retryBody.RootElement.GetProperty("status").GetString());

        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal("Suspended", subscription.RootElement.GetProperty("status").GetString());
        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(entitlements.RootElement[0].GetProperty("isActive").GetBoolean());
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

        using var opsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ops/webhooks");
        opsRequest.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var opsResponse = await fixture.Client.SendAsync(opsRequest);
        opsResponse.EnsureSuccessStatusCode();
        using var rows = JsonDocument.Parse(await opsResponse.Content.ReadAsStringAsync());
        var refundEvent = rows.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("providerEventId").GetString() == "evt-race-refund");
        if (refundEvent.GetProperty("status").GetString() == "Failed")
        {
            var refundEventId = refundEvent.GetProperty("id").GetGuid();
            using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/ops/webhooks/{refundEventId}/retry");
            retry.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
            using var retryResponse = await fixture.Client.SendAsync(retry);
            Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        }

        using var invoice = await fixture.GetJsonAsync($"/api/invoices/{scenario.InvoiceId}");
        Assert.Equal("Refunded", invoice.RootElement.GetProperty("status").GetString());
        using var entitlement = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(entitlement.RootElement[0].GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Failed_payment_attempt_can_be_retried_with_a_new_attempt()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(29.00m);

        using var failedEvent = await fixture.SendWebhookAsync("evt-payment-failed", "payment.failed",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, failedEvent.StatusCode);

        using var oldAttempt = await fixture.GetJsonAsync($"/api/payment-attempts/{scenario.PaymentId}");
        Assert.Equal("Failed", oldAttempt.RootElement.GetProperty("status").GetString());

        using var newAttemptResponse = await fixture.Client.PostAsync(
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts", content: null);
        Assert.Equal(HttpStatusCode.Created, newAttemptResponse.StatusCode);
        using var newAttempt = JsonDocument.Parse(await newAttemptResponse.Content.ReadAsStringAsync());
        Assert.NotEqual(scenario.PaymentId, newAttempt.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Pending", newAttempt.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Late_capture_after_invoice_refund_is_flagged_without_restoring_access()
    {
        await fixture.ResetDatabaseAsync();
        var scenario = await fixture.CreateScenarioAsync(64.00m);

        using var firstFailed = await fixture.SendWebhookAsync("evt-first-attempt-failed", "payment.failed",
            scenario.PaymentId, scenario.Amount);
        Assert.Equal(HttpStatusCode.Accepted, firstFailed.StatusCode);

        using var secondAttemptResponse = await fixture.Client.PostAsync(
            $"/api/invoices/{scenario.InvoiceId}/payment-attempts", content: null);
        Assert.Equal(HttpStatusCode.Created, secondAttemptResponse.StatusCode);
        using var secondAttempt = JsonDocument.Parse(await secondAttemptResponse.Content.ReadAsStringAsync());
        var secondPaymentId = secondAttempt.RootElement.GetProperty("id").GetGuid();

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
        Assert.Equal("Succeeded", firstAttempt.RootElement.GetProperty("status").GetString());
        Assert.True(firstAttempt.RootElement.GetProperty("requiresManualRefund").GetBoolean());

        using var invoice = await fixture.GetJsonAsync($"/api/invoices/{scenario.InvoiceId}");
        Assert.Equal("Refunded", invoice.RootElement.GetProperty("status").GetString());
        using var subscription = await fixture.GetJsonAsync($"/api/subscriptions/{scenario.SubscriptionId}");
        Assert.Equal("Suspended", subscription.RootElement.GetProperty("status").GetString());
        using var entitlements = await fixture.GetJsonAsync($"/api/organizations/{scenario.OrganizationId}/entitlements");
        Assert.False(entitlements.RootElement[0].GetProperty("isActive").GetBoolean());
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
        Assert.Equal("PendingPayment", subscription.RootElement.GetProperty("status").GetString());

        using var opsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ops/webhooks");
        opsRequest.Headers.Add("X-Ops-Key", BillingApiFixture.OpsKey);
        using var opsResponse = await fixture.Client.SendAsync(opsRequest);
        opsResponse.EnsureSuccessStatusCode();
        using var rows = JsonDocument.Parse(await opsResponse.Content.ReadAsStringAsync());
        Assert.Empty(rows.RootElement.EnumerateArray());
    }
}
