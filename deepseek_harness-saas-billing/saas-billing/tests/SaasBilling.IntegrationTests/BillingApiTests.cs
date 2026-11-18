using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task Second_period_payment_renews_subscription_and_duplicate_does_not_extend_twice()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);

        var (firstStart, firstEnd) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        Assert.NotNull(firstStart);
        Assert.NotNull(firstEnd);

        var renewalInvoice = await CreateInvoiceAsync(
            flow.Organization.Id,
            flow.Subscription.Id,
            flow.Subscription.Price,
            flow.Subscription.ProductCode,
            "Starter plan renewal");
        var renewalAttempt = await CreatePaymentAttemptAsync(
            renewalInvoice.Id, "renewal-" + Guid.NewGuid().ToString("N"));

        var renewal = BuildWebhookFor(
            "payment.succeeded", renewalInvoice.Id, renewalAttempt.ProviderPaymentId, renewalInvoice.Total);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(renewal)).StatusCode);

        var (renewedStart, renewedEnd) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        Assert.Equal(firstEnd, renewedStart);
        Assert.Equal(firstEnd!.Value.AddMonths(1), renewedEnd);

        var secondInvoice = await GetInvoiceRowAsync(renewalInvoice.Id);
        var access = await GetAccessAsync(flow.Organization.Id);
        Assert.Equal(InvoiceStatus.Paid, secondInvoice.Status);
        Assert.Equal(renewalInvoice.Total, secondInvoice.AmountPaid);
        Assert.Equal(AccessStatus.Active, Assert.Single(access).Status);
        Assert.Equal(renewalInvoice.Id, Assert.Single(access).InvoiceId);
        Assert.Equal(1, await CountAuditAsync(flow.Organization.Id, "SubscriptionRenewed"));

        // Exact redelivery of the renewal event must not extend a third month.
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(renewal)).StatusCode);
        var (afterDuplicateStart, afterDuplicateEnd) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        Assert.Equal(renewedStart, afterDuplicateStart);
        Assert.Equal(renewedEnd, afterDuplicateEnd);
    }

    [IntegrationFact]
    public async Task Quarterly_plan_activates_for_three_months()
    {
        var flow = await CreateUnpaidFlowAsync(75m, "quarterly");
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "quarterly-" + Guid.NewGuid().ToString("N"));
        var evt = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, flow.Invoice.Total);

        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(evt)).StatusCode);

        var (start, end) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        var startValue = start ?? throw new InvalidOperationException("Subscription period start is missing.");
        var endValue = end ?? throw new InvalidOperationException("Subscription period end is missing.");
        Assert.Equal(startValue.AddMonths(3), endValue);
        Assert.Equal(1, await CountAuditAsync(flow.Organization.Id, "SubscriptionActivated"));
    }

    [IntegrationFact]
    public async Task Concurrent_payments_for_different_periods_do_not_lose_a_period()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);
        var (_, firstEnd) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);

        var invoice2 = await CreateInvoiceAsync(
            flow.Organization.Id, flow.Subscription.Id, flow.Subscription.Price,
            flow.Subscription.ProductCode, "Period 2");
        var invoice3 = await CreateInvoiceAsync(
            flow.Organization.Id, flow.Subscription.Id, flow.Subscription.Price,
            flow.Subscription.ProductCode, "Period 3");

        var attempt2 = await CreatePaymentAttemptAsync(invoice2.Id, "period-2-" + Guid.NewGuid().ToString("N"));
        var attempt3 = await CreatePaymentAttemptAsync(invoice3.Id, "period-3-" + Guid.NewGuid().ToString("N"));

        var events = new[]
        {
            BuildWebhookFor("payment.succeeded", invoice2.Id, attempt2.ProviderPaymentId, invoice2.Total),
            BuildWebhookFor("payment.succeeded", invoice3.Id, attempt3.ProviderPaymentId, invoice3.Total)
        };

        var responses = await Task.WhenAll(events.Select(e => SendWebhookAsync(e)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var (start, end) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        Assert.Equal(firstEnd!.Value.AddMonths(1), start);
        Assert.Equal(firstEnd.Value.AddMonths(2), end);
        Assert.Equal(InvoiceStatus.Paid, (await GetInvoiceRowAsync(invoice2.Id)).Status);
        Assert.Equal(InvoiceStatus.Paid, (await GetInvoiceRowAsync(invoice3.Id)).Status);
        Assert.Single(await GetAccessAsync(flow.Organization.Id));
    }

    [IntegrationFact]
    public async Task Expired_subscription_renewal_starts_from_payment_time()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);

        var pastEnd = DateTimeOffset.UtcNow.AddDays(-2);
        var pastStart = pastEnd.AddMonths(-1);
        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE subscriptions SET \"PeriodStart\" = {pastStart}, \"PeriodEnd\" = {pastEnd} WHERE \"Id\" = {flow.Subscription.Id}");
        }

        var renewalInvoice = await CreateInvoiceAsync(
            flow.Organization.Id, flow.Subscription.Id, flow.Subscription.Price,
            flow.Subscription.ProductCode, "Expired period renewal");
        var attempt = await CreatePaymentAttemptAsync(
            renewalInvoice.Id, "expired-" + Guid.NewGuid().ToString("N"));

        var beforePayment = DateTimeOffset.UtcNow.AddSeconds(-1);
        var evt = BuildWebhookFor(
            "payment.succeeded", renewalInvoice.Id, attempt.ProviderPaymentId, renewalInvoice.Total);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(evt)).StatusCode);

        var (start, end) = await GetSubscriptionPeriodAsync(flow.Subscription.Id);
        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.True(start >= beforePayment, $"PeriodStart {start} was not reset after expiry");
        Assert.Equal(start.Value.AddMonths(1), end.Value);
    }

    [IntegrationFact]
    public async Task Payment_after_cancellation_is_recorded_but_subscription_stays_canceled()
    {
        var flow = await CreateUnpaidFlowAsync();
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "canceled-" + Guid.NewGuid().ToString("N"));

        var cancelResponse = await Client.PostAsync($"/api/subscriptions/{flow.Subscription.Id}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();
        var canceled = await ReadAsync<SubscriptionResponse>(cancelResponse);
        Assert.Equal(SubscriptionStatus.Canceled, canceled.Status);

        var evt = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, flow.Invoice.Total);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(evt)).StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var subscription = await db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == flow.Subscription.Id);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Equal(0, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id &&
                 x.Action == "PaymentReceivedForTerminalSubscription"));
    }

    [IntegrationFact]
    public async Task Cancellation_revokes_access_and_late_payment_does_not_restore_it()
    {
        var flow = await CreateFlowAsync();
        await SendSuccessfulPaymentAsync(flow);

        var renewalInvoice = await CreateInvoiceAsync(
            flow.Organization.Id, flow.Subscription.Id, flow.Subscription.Price,
            flow.Subscription.ProductCode, "Pre-cancel invoice");
        var attempt = await CreatePaymentAttemptAsync(
            renewalInvoice.Id, "cancel-access-" + Guid.NewGuid().ToString("N"));

        var cancel = await Client.PostAsync($"/api/subscriptions/{flow.Subscription.Id}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        var accessAfterCancel = await GetAccessAsync(flow.Organization.Id);
        Assert.Equal(AccessStatus.Revoked, Assert.Single(accessAfterCancel).Status);

        var evt = BuildWebhookFor(
            "payment.succeeded", renewalInvoice.Id, attempt.ProviderPaymentId, renewalInvoice.Total);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(evt)).StatusCode);

        var lateInvoice = await GetInvoiceRowAsync(renewalInvoice.Id);
        var subscription = await GetSubscriptionRowAsync(flow.Subscription.Id);
        var finalAccess = await GetAccessAsync(flow.Organization.Id);

        Assert.Equal(InvoiceStatus.Paid, lateInvoice.Status);
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Equal(AccessStatus.Revoked, Assert.Single(finalAccess).Status);
        Assert.Equal(1, await CountAuditAsync(flow.Organization.Id, "SubscriptionCanceled"));
        Assert.Equal(1, await CountAuditAsync(flow.Organization.Id, "PaymentReceivedForTerminalSubscription"));
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
    public async Task Partial_payments_credit_once_even_with_distinct_success_event_ids()
    {
        var flow = await CreateUnpaidFlowAsync(1000m);
        var firstAttempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "partial-1-" + Guid.NewGuid().ToString("N"), 500m);

        var firstSuccess = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, firstAttempt.ProviderPaymentId, 500m);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(firstSuccess)).StatusCode);

        // Same payment, different provider event id: must not credit another 500.
        var duplicateSuccess = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, firstAttempt.ProviderPaymentId, 500m);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(duplicateSuccess)).StatusCode);

        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == firstAttempt.Id);
            Assert.Equal(500m, invoice.AmountPaid);
            Assert.Equal(InvoiceStatus.Open, invoice.Status);
            Assert.Equal(PaymentStatus.Succeeded, attempt.Status);
            Assert.Equal(1, await db.AuditLogs.CountAsync(
                x => x.OrganizationId == flow.Organization.Id && x.Action == "PaymentSucceeded"));
            Assert.Equal(1, await db.AuditLogs.CountAsync(
                x => x.OrganizationId == flow.Organization.Id && x.Action == "WebhookIgnored"));
        }

        var secondAttempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "partial-2-" + Guid.NewGuid().ToString("N"), 500m);
        var secondSuccess = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, secondAttempt.ProviderPaymentId, 500m);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(secondSuccess)).StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var paidInvoice = await finalDb.Invoices.AsNoTracking()
            .Include(x => x.PaymentAttempts)
            .SingleAsync(x => x.Id == flow.Invoice.Id);
        var subscription = await finalDb.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == flow.Subscription.Id);
        var access = await finalDb.ProductAccesses.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == flow.Organization.Id);

        Assert.Equal(InvoiceStatus.Paid, paidInvoice.Status);
        Assert.Equal(1000m, paidInvoice.AmountPaid);
        Assert.Equal(0m, paidInvoice.OutstandingAmount);
        Assert.Equal(2, paidInvoice.PaymentAttempts.Count);
        Assert.All(paidInvoice.PaymentAttempts, x => Assert.Equal(PaymentStatus.Succeeded, x.Status));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(AccessStatus.Active, access.Status);
    }

    [IntegrationFact]
    public async Task Concurrent_distinct_success_events_for_same_attempt_credit_once()
    {
        var flow = await CreateUnpaidFlowAsync(1000m);
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "concurrent-success-" + Guid.NewGuid().ToString("N"), 500m);

        var events = Enumerable.Range(0, 6)
            .Select(_ => BuildWebhookFor(
                "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, 500m))
            .ToArray();

        var responses = await Task.WhenAll(events.Select(e => SendWebhookAsync(e)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var storedAttempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);

        Assert.Equal(500m, invoice.AmountPaid);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(PaymentStatus.Succeeded, storedAttempt.Status);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id && x.Action == "PaymentSucceeded"));
        Assert.Equal(5, await db.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id && x.Action == "WebhookIgnored"));
        Assert.Equal(0, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
    }

    [IntegrationFact]
    public async Task Second_full_payment_for_same_invoice_is_rejected_and_audited()
    {
        var flow = await CreateUnpaidFlowAsync(1000m);
        var first = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "overpay-1-" + Guid.NewGuid().ToString("N"), 1000m);
        var second = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "overpay-2-" + Guid.NewGuid().ToString("N"), 1000m);

        var firstEvent = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, first.ProviderPaymentId, 1000m);
        var secondEvent = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, second.ProviderPaymentId, 1000m);

        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(firstEvent)).StatusCode);
        var overpayment = await SendWebhookAsync(secondEvent);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overpayment.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(x => x.InvoiceId == flow.Invoice.Id)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        var inbox = await db.WebhookEvents.AsNoTracking()
            .SingleAsync(x => x.EventId == secondEvent.EventId);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(1000m, invoice.AmountPaid);
        Assert.Equal(PaymentStatus.Succeeded, attempts[0].Status);
        Assert.Equal(PaymentStatus.Pending, attempts[1].Status);
        Assert.Equal(WebhookStatus.DeadLettered, inbox.Status);
        Assert.Contains("already fully paid", inbox.LastError);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.Action == "WebhookDeadLettered" && x.EntityId == inbox.Id));
    }

    [IntegrationFact]
    public async Task Same_event_id_with_different_payload_is_a_conflict_and_is_audited()
    {
        var flow = await CreateFlowAsync();
        var original = BuildWebhook("payment.succeeded", flow);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(original)).StatusCode);

        var conflicting = BuildWebhookFor(
            "payment.succeeded",
            flow.Invoice.Id,
            flow.Attempt!.ProviderPaymentId,
            1.00m,
            eventId: original.EventId);

        var response = await SendWebhookAsync(conflicting);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.EventId == original.EventId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);

        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.Action == "WebhookPayloadConflict" && x.EntityId == inbox.Id));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(flow.Invoice.Total, invoice.AmountPaid);
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

        // A distinct refund event id must not refund the already refunded payment again.
        var lateRefund = BuildWebhook("payment.refunded", flow);
        var lateRefundResponse = await SendWebhookAsync(lateRefund);
        Assert.Equal(HttpStatusCode.OK, lateRefundResponse.StatusCode);

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
    public async Task Concurrent_payment_creation_with_same_key_creates_one_attempt()
    {
        var flow = await CreateUnpaidFlowAsync();
        var key = "concurrent-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => SendPaymentAttemptAsync(flow.Invoice.Id, key, 10.00m)));

        Assert.All(responses, response =>
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                $"Unexpected status {response.StatusCode}"));

        var attempts = await Task.WhenAll(responses.Select(ReadAsync<PaymentAttemptResponse>));
        var attempt = attempts[0];
        Assert.Single(attempts.Select(x => x.Id).Distinct());
        Assert.Single(attempts.Select(x => x.ProviderPaymentId).Distinct());
        Assert.Equal(10.00m, attempt.Amount);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var stored = await db.PaymentAttempts.AsNoTracking()
            .SingleAsync(x => x.InvoiceId == flow.Invoice.Id);
        Assert.Equal(10.00m, stored.Amount);
        Assert.False(string.IsNullOrWhiteSpace(stored.RequestFingerprint));
        Assert.Equal(1, await db.PaymentAttempts.CountAsync(x => x.InvoiceId == flow.Invoice.Id));
    }

    [IntegrationFact]
    public async Task Same_idempotency_key_with_different_amount_is_rejected()
    {
        var flow = await CreateUnpaidFlowAsync();
        var key = "different-params-" + Guid.NewGuid().ToString("N");

        var first = await SendPaymentAttemptAsync(flow.Invoice.Id, key, 10.00m);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await SendPaymentAttemptAsync(flow.Invoice.Id, key, 20.00m);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(x => x.InvoiceId == flow.Invoice.Id)
            .ToListAsync();

        var attempt = Assert.Single(attempts);
        Assert.Equal(10.00m, attempt.Amount);
    }

    [IntegrationFact]
    public async Task Provider_failure_after_intent_creation_is_retryable_without_duplicate()
    {
        var controllable = new ControllablePaymentProvider(Signer) { FailAfterCreate = true };
        using var factory = CreateFactoryWithProvider(controllable);
        using var client = factory.CreateClient();

        var flow = await CreateUnpaidFlowAsync();
        var key = "provider-crash-" + Guid.NewGuid().ToString("N");

        var first = await SendPaymentAttemptAsync(flow.Invoice.Id, key, 10.00m, client);
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(flow.Invoice.Id));

        var second = await SendPaymentAttemptAsync(flow.Invoice.Id, key, 10.00m, client);
        Assert.True(second.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        var attempt = await ReadAsync<PaymentAttemptResponse>(second);

        var providerIds = controllable.CreatedPaymentIds.Distinct().ToList();
        Assert.Single(providerIds);
        Assert.Equal(providerIds[0], attempt.ProviderPaymentId);

        var stored = await GetAttemptsAsync(flow.Invoice.Id);
        Assert.Single(stored);
        Assert.Equal(10.00m, stored[0].Amount);
    }

    [IntegrationFact]
    public async Task Delayed_provider_does_not_hold_an_invoice_row_lock()
    {
        var controllable = new ControllablePaymentProvider(Signer) { BlockCreate = true };
        using var factory = CreateFactoryWithProvider(controllable);
        using var client = factory.CreateClient();

        var flow = await CreateUnpaidFlowAsync();
        var key = "provider-delay-" + Guid.NewGuid().ToString("N");

        var paymentTask = SendPaymentAttemptAsync(flow.Invoice.Id, key, 10.00m, client);
        try
        {
            await controllable.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            await using var scope = fixture.Factory!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var changed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE invoices SET \"DueAt\" = {DateTimeOffset.UtcNow.AddDays(30)} WHERE \"Id\" = {flow.Invoice.Id}",
                timeout.Token);
            Assert.Equal(1, changed);
        }
        finally
        {
            controllable.BlockCreate = false;
            controllable.Release();
        }

        var response = await paymentTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        Assert.Single(await GetAttemptsAsync(flow.Invoice.Id));
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
    public async Task Partial_refunds_are_counted_once_and_cannot_exceed_paid_amount()
    {
        var flow = await CreateFlowAsync(1000m);
        await SendSuccessfulPaymentAsync(flow);

        var refund = BuildWebhook("payment.refunded", flow, amount: 200m);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(refund)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(refund)).StatusCode); // duplicate

        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == flow.Attempt!.Id);
            Assert.Equal(200m, invoice.AmountRefunded);
            Assert.Equal(200m, attempt.RefundedAmount);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(PaymentStatus.Succeeded, attempt.Status);
        }

        var concurrent = new[]
        {
            BuildWebhook("payment.refunded", flow, amount: 200m),
            BuildWebhook("payment.refunded", flow, amount: 200m)
        };
        var responses = await Task.WhenAll(concurrent.Select(e => SendWebhookAsync(e)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == flow.Attempt!.Id);
            Assert.Equal(600m, invoice.AmountRefunded);
            Assert.Equal(600m, attempt.RefundedAmount);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        }

        var overRefund = BuildWebhook("payment.refunded", flow, amount: 401m);
        var overRefundResponse = await SendWebhookAsync(overRefund);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overRefundResponse.StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var finalInvoice = await finalDb.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var finalAttempt = await finalDb.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == flow.Attempt!.Id);
        var overRefundEvent = await finalDb.WebhookEvents.AsNoTracking()
            .SingleAsync(x => x.EventId == overRefund.EventId);
        Assert.Equal(600m, finalInvoice.AmountRefunded);
        Assert.Equal(600m, finalAttempt.RefundedAmount);
        Assert.Equal(WebhookStatus.DeadLettered, overRefundEvent.Status);
        Assert.Equal(3, await finalDb.AuditLogs.CountAsync(
            x => x.OrganizationId == flow.Organization.Id && x.Action == "PaymentPartiallyRefunded"));
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
    public async Task Audit_failure_rolls_back_the_whole_payment_and_retry_recovers()
    {
        var flow = await CreateFlowAsync();
        var evt = BuildWebhook("payment.succeeded", flow);
        var failedInboxId = Guid.Empty;

        try
        {
            await ExecuteSqlAsync("""
                CREATE OR REPLACE FUNCTION fail_billing_audit() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Action" = 'PaymentSucceeded' THEN
                        RAISE EXCEPTION 'injected audit failure';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
            await ExecuteSqlAsync("""
                DROP TRIGGER IF EXISTS fail_billing_audit_trigger ON audit_logs;
                CREATE TRIGGER fail_billing_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_billing_audit();
                """);

            var response = await SendWebhookAsync(evt);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var receipt = await ReadAsync<WebhookReceiptResponse>(response);
            failedInboxId = receipt.EventId;
            Assert.Equal(WebhookStatus.Failed, receipt.Status);
            Assert.True(receipt.Retryable);

            await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
                var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
                var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == flow.Attempt!.Id);
                var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == receipt.EventId);

                Assert.Equal(InvoiceStatus.Open, invoice.Status);
                Assert.Equal(0m, invoice.AmountPaid);
                Assert.Equal(PaymentStatus.Pending, attempt.Status);
                Assert.Equal(WebhookStatus.Failed, inbox.Status);
                Assert.Equal(0, await db.ProductAccesses.CountAsync(x => x.OrganizationId == flow.Organization.Id));
            }
        }
        finally
        {
            await ExecuteSqlAsync("DROP TRIGGER IF EXISTS fail_billing_audit_trigger ON audit_logs;");
            await ExecuteSqlAsync("DROP FUNCTION IF EXISTS fail_billing_audit();");
        }

        Assert.NotEqual(Guid.Empty, failedInboxId);
        var retry = await Client.PostAsync($"/api/webhooks/{failedInboxId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var finalInvoice = await finalDb.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var finalEvent = await finalDb.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == failedInboxId);
        Assert.Equal(InvoiceStatus.Paid, finalInvoice.Status);
        Assert.Equal(WebhookStatus.Processed, finalEvent.Status);
        Assert.Equal(2, finalEvent.Attempts);
        Assert.Single(await GetAccessAsync(flow.Organization.Id));
    }

    [IntegrationFact]
    public async Task Stale_processing_event_is_recovered_after_a_crash()
    {
        var flow = await CreateUnpaidFlowAsync();
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "crash-" + Guid.NewGuid().ToString("N"));
        var evt = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, flow.Invoice.Total);

        var inboxId = Guid.NewGuid();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(evt.Raw))).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO webhook_events
                    ("Id", "Provider", "EventId", "EventType", "Payload", "PayloadHash",
                     "Status", "Attempts", "ReceivedAt", "ProcessingStartedAt")
                VALUES
                    ({inboxId}, 'test', {evt.EventId}, 'payment.succeeded', CAST({evt.Raw} AS jsonb), {hash},
                     'Processing', 1, {now}, {now.AddMinutes(-10)})
                """);
        }

        var retry = await Client.PostAsync($"/api/webhooks/{inboxId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await finalDb.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == inboxId);
        var invoice = await finalDb.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var storedAttempt = await finalDb.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);

        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(2, inbox.Attempts);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(PaymentStatus.Succeeded, storedAttempt.Status);
        Assert.Single(await GetAccessAsync(flow.Organization.Id));
    }

    [IntegrationFact]
    public async Task Dead_lettered_event_can_be_recovered_manually_with_force()
    {
        var flow = await CreateUnpaidFlowAsync();
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "manual-recovery-" + Guid.NewGuid().ToString("N"));
        var evt = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, flow.Invoice.Total);

        var inboxId = Guid.NewGuid();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(evt.Raw))).ToLowerInvariant();
        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO webhook_events
                    ("Id", "Provider", "EventId", "EventType", "Payload", "PayloadHash",
                     "Status", "Attempts", "ReceivedAt", "LastError")
                VALUES
                    ({inboxId}, 'test', {evt.EventId}, 'payment.succeeded', CAST({evt.Raw} AS jsonb), {hash},
                     'DeadLettered', 8, {DateTimeOffset.UtcNow}, 'manual investigation required')
                """);
        }

        var withoutForce = await Client.PostAsync($"/api/webhooks/{inboxId}/retry", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutForce.StatusCode);

        var forced = await Client.PostAsync($"/api/webhooks/{inboxId}/retry?force=true", null);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        await using var finalScope = fixture.Factory!.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await finalDb.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == inboxId);
        var invoice = await finalDb.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(9, inbox.Attempts);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [IntegrationFact]
    public async Task Background_recovery_worker_processes_a_pending_event()
    {
        using var recoveryFactory = fixture.Factory!.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Billing:Recovery:Enabled"] = "true",
                    ["Billing:Recovery:IntervalSeconds"] = "1",
                    ["Billing:Recovery:BatchSize"] = "20"
                });
            });
        });
        using var recoveryClient = recoveryFactory.CreateClient();

        var flow = await CreateUnpaidFlowAsync();
        var attempt = await CreatePaymentAttemptAsync(
            flow.Invoice.Id, "worker-" + Guid.NewGuid().ToString("N"));
        var evt = BuildWebhookFor(
            "payment.succeeded", flow.Invoice.Id, attempt.ProviderPaymentId, flow.Invoice.Total);

        var inboxId = Guid.NewGuid();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(evt.Raw))).ToLowerInvariant();
        await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO webhook_events
                    ("Id", "Provider", "EventId", "EventType", "Payload", "PayloadHash",
                     "Status", "Attempts", "ReceivedAt")
                VALUES
                    ({inboxId}, 'test', {evt.EventId}, 'payment.succeeded', CAST({evt.Raw} AS jsonb), {hash},
                     'Pending', 0, {DateTimeOffset.UtcNow})
                """);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        WebhookStatus status;
        do
        {
            await Task.Delay(250);
            await using var scope = fixture.Factory!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            status = await db.WebhookEvents.AsNoTracking()
                .Where(x => x.Id == inboxId)
                .Select(x => x.Status)
                .SingleAsync();
        } while (status != WebhookStatus.Processed && DateTimeOffset.UtcNow < deadline);

        Assert.Equal(WebhookStatus.Processed, status);

        var invoice = await GetInvoiceRowAsync(flow.Invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Single(await GetAccessAsync(flow.Organization.Id));
    }

    [IntegrationFact]
    public async Task Repeated_retries_of_a_failed_event_are_processed_once()
    {
        var flow = await CreateFlowAsync();
        var refund = BuildWebhook("payment.refunded", flow);

        var early = await SendWebhookAsync(refund);
        Assert.Equal(HttpStatusCode.InternalServerError, early.StatusCode);
        var earlyReceipt = await ReadAsync<WebhookReceiptResponse>(early);

        await SendSuccessfulPaymentAsync(flow);

        var retries = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Client.PostAsync($"/api/webhooks/{earlyReceipt.EventId}/retry", null)));
        Assert.All(retries, response =>
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
                $"Unexpected retry status {response.StatusCode}"));

        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inbox = await db.WebhookEvents.AsNoTracking().SingleAsync(x => x.Id == earlyReceipt.EventId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == flow.Invoice.Id);
        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == flow.Attempt!.Id);

        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Equal(2, inbox.Attempts);
        Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
        Assert.Equal(flow.Invoice.Total, invoice.AmountRefunded);
        Assert.Equal(PaymentStatus.Refunded, attempt.Status);
        Assert.Equal(1, await CountAuditAsync(flow.Organization.Id, "PaymentRefunded"));
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

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<int> CountAttemptsAsync(Guid invoiceId)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.PaymentAttempts.CountAsync(x => x.InvoiceId == invoiceId);
    }

    private async Task<(DateTimeOffset? Start, DateTimeOffset? End)> GetSubscriptionPeriodAsync(Guid id)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == id);
        return (subscription.PeriodStart, subscription.PeriodEnd);
    }

    private async Task<Invoice> GetInvoiceRowAsync(Guid id)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<Subscription> GetSubscriptionRowAsync(Guid id)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<List<ProductAccess>> GetAccessAsync(Guid organizationId)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.ProductAccesses.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync();
    }

    private async Task<int> CountAuditAsync(Guid organizationId, string action)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.AuditLogs.CountAsync(x => x.OrganizationId == organizationId && x.Action == action);
    }

    private async Task<List<PaymentAttempt>> GetAttemptsAsync(Guid invoiceId)
    {
        await using var scope = fixture.Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.PaymentAttempts.AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
    }

    private WebApplicationFactory<Program> CreateFactoryWithProvider(IPaymentProvider provider)
    {
        return fixture.Factory!.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPaymentProvider>();
                services.AddSingleton(provider);
            });
        });
    }

    private async Task<Flow> CreateFlowAsync(decimal? invoiceAmount = null)
    {
        var flow = await CreateUnpaidFlowAsync(invoiceAmount);
        var attempt = await CreatePaymentAttemptAsync(flow.Invoice.Id, "pay-" + Guid.NewGuid().ToString("N"));
        return flow with { Attempt = attempt };
    }

    private async Task<Flow> CreateUnpaidFlowAsync(decimal? invoiceAmount = null, string planCode = "starter")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var organization = await PostAsync<OrganizationResponse>(
            "/api/organizations", new { name = $"Org {suffix}" });

        var subscription = await PostAsync<SubscriptionResponse>(
            $"/api/organizations/{organization.Id}/subscriptions",
            new { planCode });

        var invoice = await CreateInvoiceAsync(
            organization.Id,
            subscription.Id,
            invoiceAmount ?? subscription.Price,
            subscription.ProductCode,
            $"{subscription.PlanName} plan");

        return new Flow(organization, subscription, invoice, null);
    }

    private async Task<InvoiceResponse> CreateInvoiceAsync(
        Guid organizationId,
        Guid subscriptionId,
        decimal amount,
        string productCode,
        string description)
    {
        return await PostAsync<InvoiceResponse>("/api/invoices", new
        {
            organizationId,
            subscriptionId,
            currency = "USD",
            lines = new[]
            {
                new
                {
                    description,
                    productCode,
                    quantity = 1,
                    unitAmount = amount
                }
            }
        });
    }

    private Task<HttpResponseMessage> SendPaymentAttemptAsync(
        Guid invoiceId,
        string key,
        decimal? amount = null,
        HttpClient? client = null)
    {
        var body = amount is null
            ? "{}"
            : JsonSerializer.Serialize(new { amount }, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invoices/{invoiceId}/payment-attempts")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return (client ?? Client).SendAsync(request);
    }

    private async Task<PaymentAttemptResponse> CreatePaymentAttemptAsync(
        Guid invoiceId,
        string key,
        decimal? amount = null,
        HttpClient? client = null)
    {
        var response = await SendPaymentAttemptAsync(invoiceId, key, amount, client);
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

    private Task<HttpResponseMessage> SendWebhookAsync(SignedEvent evt, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/test")
        {
            Content = new StringContent(evt.Raw, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Billing-Signature", evt.Signature);
        return (client ?? Client).SendAsync(request);
    }

    private static SignedEvent BuildWebhook(
        string type,
        Flow flow,
        decimal? amount = null,
        string? eventId = null,
        DateTimeOffset? timestamp = null)
    {
        return BuildWebhookFor(
            type,
            flow.Invoice.Id,
            flow.Attempt!.ProviderPaymentId,
            amount ?? flow.Invoice.Total,
            flow.Invoice.Currency,
            eventId,
            timestamp);
    }

    private static SignedEvent BuildWebhookFor(
        string type,
        Guid invoiceId,
        string paymentId,
        decimal amount,
        string currency = "USD",
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
                paymentId,
                invoiceId,
                amount,
                currency
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



