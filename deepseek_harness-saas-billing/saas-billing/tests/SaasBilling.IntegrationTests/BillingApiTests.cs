using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SaasBilling.Api.Contracts;
using SaasBilling.Api.Data;
using SaasBilling.Api.Payments;
using SaasBilling.Domain;
using SaasBilling.IntegrationTests.Infrastructure;
using Xunit;

namespace SaasBilling.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BillingApiTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TestPaymentProvider Signer = new(
        Options.Create(new TestPaymentOptions
        {
            Secret = BillingApiFactory.WebhookSecret,
            ToleranceSeconds = 300
        }),
        TimeProvider.System);

    private HttpClient Client => fixture.Client!;

    [IntegrationFact]
    public async Task Full_payment_activates_subscription_and_grants_product_access()
    {
        var flow = await CreateFlowAsync();

        Assert.Equal(InvoiceStatus.Open, flow.Invoice.Status);
        Assert.Equal(SubscriptionStatus.PendingActivation, flow.Subscription.Status);

        var evt = BuildWebhook("payment.succeeded", flow);
        var response = await SendWebhookAsync(evt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var receipt = await ReadAsync<WebhookReceiptResponse>(response);
        Assert.False(receipt.Duplicate);
        Assert.Equal(WebhookStatus.Processed, receipt.Status);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var invoice = await db.Invoices.AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Lines)
            .Include(x => x.PaymentAttempts)
            .SingleAsync(x => x.Id == flow.Invoice.Id);
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == flow.Subscription.Id);
        var accesses = await db.ProductAccesses.AsNoTracking()
            .Where(x => x.OrganizationId == flow.Organization.Id)
            .ToListAsync();

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(invoice.Total, invoice.AmountPaid);
        Assert.Equal(0m, invoice.OutstandingAmount);
        Assert.NotNull(invoice.PaidAt);
        Assert.Equal(PaymentStatus.Succeeded, invoice.PaymentAttempts.Single().Status);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.NotNull(subscription.ActivatedAt);
        Assert.NotNull(subscription.PeriodStart);
        Assert.NotNull(subscription.PeriodEnd);

        var access = Assert.Single(accesses);
        Assert.Equal("starter-product", access.ProductCode);
        Assert.Equal(AccessStatus.Active, access.Status);
        Assert.Equal(invoice.Id, access.InvoiceId);

        var webhook = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.EventId == evt.EventId);
        Assert.Equal(WebhookStatus.Processed, webhook.Status);
        Assert.Equal(1, webhook.Attempts);
        Assert.NotNull(webhook.ProcessedAt);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(x => x.OrganizationId == flow.Organization.Id)
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Contains("OrganizationCreated", audits);
        Assert.Contains("SubscriptionCreated", audits);
        Assert.Contains("InvoiceCreated", audits);
        Assert.Contains("PaymentAttemptCreated", audits);
        Assert.Contains("PaymentSucceeded", audits);
        Assert.Contains("SubscriptionActivated", audits);
        Assert.Contains("ProductAccessGranted", audits);
    }

    [IntegrationFact]
    public async Task Duplicate_webhook_is_idempotent()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.succeeded", flow);

        var first = await SendWebhookAsync(evt);
        var second = await SendWebhookAsync(evt);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var receipt = await ReadAsync<WebhookReceiptResponse>(second);
        Assert.True(receipt.Duplicate);
        Assert.Equal(WebhookStatus.Processed, receipt.Status);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        Assert.Equal(1, await db.WebhookEvents.CountAsync(x => x.EventId == evt.EventId));
        Assert.Equal(1, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id && x.Action == "SubscriptionActivated"));
    }

    [IntegrationFact]
    public async Task Concurrent_webhook_deliveries_are_processed_exactly_once()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.succeeded", flow);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => SendWebhookAsync(evt))
            .ToArray();
        var responses = await Task.WhenAll(requests);

        Assert.All(responses, response =>
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
                $"Unexpected status {response.StatusCode}"));

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.EventId == evt.EventId);
        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(1, inbox.Attempts);

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(1, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id && x.Action == "SubscriptionActivated"));
    }

    [IntegrationFact]
    public async Task Invalid_signature_is_rejected_and_not_written_to_inbox()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.succeeded", flow);
        var tampered = evt.Raw + " ";

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/test")
        {
            Content = new StringContent(tampered, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Billing-Signature", evt.Signature);

        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.Equal(0, await db.WebhookEvents.CountAsync(x => x.EventId == evt.EventId));

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
    }

    [IntegrationFact]
    public async Task Full_refund_revokes_access_and_marks_subscription_refunded()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);

        var refund = BuildWebhook("payment.refunded", flow);
        var refundResponse = await SendWebhookAsync(refund);
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);

        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var invoice = await db.Invoices.AsNoTracking().Include(x => x.PaymentAttempts)
                .SingleAsync(x => x.Id == flow.Invoice.Id);
            var subscription = await db.Subscriptions.AsNoTracking()
                .SingleAsync(x => x.Id == flow.Subscription.Id);
            var access = await db.ProductAccesses.AsNoTracking()
                .SingleAsync(x => x.OrganizationId == flow.Organization.Id);

            Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
            Assert.Equal(invoice.Total, invoice.AmountRefunded);
            Assert.NotNull(invoice.RefundedAt);
            Assert.Equal(PaymentStatus.Refunded, invoice.PaymentAttempts.Single().Status);
            Assert.Equal(SubscriptionStatus.Refunded, subscription.Status);
            Assert.Equal(AccessStatus.Revoked, access.Status);
            Assert.NotNull(access.RevokedAt);

            Assert.Equal(1, await db.AuditLogs.CountAsync(
                x => x.OrganizationId == flow.Organization.Id && x.Action == "InvoiceFullyRefunded"));
        }

        // Exact redelivery is a duplicate and must not double-refund.
        var duplicate = await SendWebhookAsync(refund);
        var receipt = await ReadAsync<WebhookReceiptResponse>(duplicate);
        Assert.True(receipt.Duplicate);
        Assert.Equal(WebhookStatus.Processed, receipt.Status);

        // A new success event after a full refund must not resurrect the subscription.
        var lateSuccess = BuildWebhook("payment.succeeded", flow);
        var lateResponse = await SendWebhookAsync(lateSuccess);
        Assert.Equal(HttpStatusCode.OK, lateResponse.StatusCode);

        await using var scope2 = fixture.Factory!.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<BillingDbContext>();
        var after = await db2.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var subAfter = await db2.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == flow.Subscription.Id);
        Assert.Equal(InvoiceStatus.Refunded, after.Status);
        Assert.Equal(flow.Invoice.Total, after.AmountRefunded);
        Assert.Equal(SubscriptionStatus.Refunded, subAfter.Status);
    }

    [IntegrationFact]
    public async Task Payment_attempt_creation_is_idempotent_by_header()
    {
        var flow = await CreateUnpaidFlowAsync();
        var key = "same-key-" + Guid.NewGuid().ToString("N");

        var first = await CreatePaymentAttemptAsync(flow.Invoice.Id, key);
        var second = await CreatePaymentAttemptAsync(flow.Invoice.Id, key);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ProviderPaymentId, second.ProviderPaymentId);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.Equal(1, await db.PaymentAttempts.CountAsync(x => x.InvoiceId == flow.Invoice.Id));
    }

    [IntegrationFact]
    public async Task Invalid_amount_is_dead_lettered_and_retry_does_not_change_state()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.succeeded", flow, amount: 1.00m);

        var response = await SendWebhookAsync(evt);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var receipt = await ReadAsync<WebhookReceiptResponse>(response);
        Assert.Equal(WebhookStatus.DeadLettered, receipt.Status);
        Assert.False(receipt.Retryable);
        Assert.NotNull(receipt.Error);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.EventId == evt.EventId);
        Assert.Equal(WebhookStatus.DeadLettered, inbox.Status);
        Assert.Contains("does not match", inbox.LastError);

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(0, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));

        var retry = await Client.PostAsync($"/api/webhooks/{inbox.Id}/retry", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
    }

    [IntegrationFact]
    public async Task Partial_refund_keeps_access_until_the_remaining_amount_is_refunded()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);

        var partial = await SendWebhookAsync(BuildWebhook("payment.refunded", flow, amount: 10.00m));
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);

        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
            var access = await db.ProductAccesses.AsNoTracking()
                .SingleAsync(x => x.OrganizationId == flow.Organization.Id);

            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(10.00m, invoice.AmountRefunded);
            Assert.Equal(AccessStatus.Active, access.Status);
        }

        var remaining = await SendWebhookAsync(
            BuildWebhook("payment.refunded", flow, amount: flow.Invoice.Total - 10.00m));
        Assert.Equal(HttpStatusCode.OK, remaining.StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var finalInvoice = await finalDb.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var finalSubscription = await finalDb.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == flow.Subscription.Id);
        var finalAccess = await finalDb.ProductAccesses.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == flow.Organization.Id);

        Assert.Equal(InvoiceStatus.Refunded, finalInvoice.Status);
        Assert.Equal(flow.Invoice.Total, finalInvoice.AmountRefunded);
        Assert.Equal(SubscriptionStatus.Refunded, finalSubscription.Status);
        Assert.Equal(AccessStatus.Revoked, finalAccess.Status);
    }

    [IntegrationFact]
    public async Task Out_of_order_refund_is_retried_after_payment_succeeds()
    {
        var flow = await CreateFlowAsync();
        var refund = BuildWebhook("payment.refunded", flow);

        // Provider delivery order is not guaranteed: refund arrives before success.
        var earlyRefund = await SendWebhookAsync(refund);
        Assert.Equal(HttpStatusCode.InternalServerError, earlyRefund.StatusCode);

        var earlyReceipt = await ReadAsync<WebhookReceiptResponse>(earlyRefund);
        Assert.Equal(WebhookStatus.Failed, earlyReceipt.Status);
        Assert.True(earlyReceipt.Retryable);

        await SendSuccessfulPaymentAsync(flow);

        var retry = await Client.PostAsync($"/api/webhooks/{earlyReceipt.EventId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == earlyReceipt.EventId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);

        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(2, inbox.Attempts);
        Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
    }

    [IntegrationFact]
    public async Task Failed_payment_keeps_invoice_open_and_does_not_grant_access()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.failed", flow);

        var response = await SendWebhookAsync(evt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.PaymentAttempts)
            .SingleAsync(x => x.Id == flow.Invoice.Id);

        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(PaymentStatus.Failed, invoice.PaymentAttempts.Single().Status);
        Assert.Equal(0, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
    }

    private async Task<Flow> CreateFlowAsync()
    {
        var flow = await CreateUnpaidFlowAsync();
        var attempt = await CreatePaymentAttemptAsync(flow.Invoice.Id, "pay-" + Guid.NewGuid().ToString("N"));
        return flow with { Attempt = attempt };
    }

    private async Task<Flow> CreateUnpaidFlowAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var organization = await PostAsync<OrganizationResponse>(
            "/api/organizations", new { name = $"Org {suffix}" });

        var subscription = await PostAsync<SubscriptionResponse>(
            $"/api/organizations/{organization.Id}/subscriptions",
            new { planCode = "starter" });

        var invoice = await PostAsync<InvoiceResponse>("/api/invoices", new
        {
            organizationId = organization.Id,
            subscriptionId = subscription.Id,
            currency = "USD",
            lines = new[]
            {
                new
                {
                    description = "Starter plan",
                    productCode = subscription.ProductCode,
                    quantity = 1,
                    unitAmount = subscription.Price
                }
            }
        });

        return new Flow(organization, subscription, invoice, null);
    }

    private async Task<PaymentAttemptResponse> CreatePaymentAttemptAsync(Guid invoiceId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invoices/{invoiceId}/payment-attempts")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Idempotency-Key", key);

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<PaymentAttemptResponse>(response);
    }

    private async Task SendSuccessfulPaymentAsync(Flow flow)
    {
        var response = await SendWebhookAsync(BuildWebhook("payment.succeeded", flow));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var response = await Client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        Assert.NotNull(result);
        return result!;
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(SignedEvent evt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/test")
        {
            Content = new StringContent(evt.Raw, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Billing-Signature", evt.Signature);
        return await Client.SendAsync(request);
    }

    private static SignedEvent BuildWebhook(
        string type,
        Flow flow,
        decimal? amount = null,
        string? eventId = null,
        DateTimeOffset? timestamp = null)
    {
        eventId ??= $"evt_{Guid.NewGuid():N}";
        var now = timestamp ?? DateTimeOffset.UtcNow;
        var payload = new
        {
            id = eventId,
            type,
            createdAt = now,
            data = new
            {
                paymentId = flow.Attempt!.ProviderPaymentId,
                invoiceId = flow.Invoice.Id,
                amount = amount ?? flow.Invoice.Total,
                currency = flow.Invoice.Currency
            }
        };

        var raw = JsonSerializer.Serialize(payload, JsonOptions);
        var signature = Signer.Sign(raw, now);
        return new SignedEvent(eventId, raw, signature);
    }

    private sealed record Flow(
        OrganizationResponse Organization,
        SubscriptionResponse Subscription,
        InvoiceResponse Invoice,
        PaymentAttemptResponse? Attempt);

    private sealed record SignedEvent(string EventId, string Raw, string Signature);
}

