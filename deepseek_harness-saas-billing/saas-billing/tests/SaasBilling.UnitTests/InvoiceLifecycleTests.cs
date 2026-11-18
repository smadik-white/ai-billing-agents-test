using SaasBilling.Domain;
using Xunit;

namespace SaasBilling.UnitTests;

public sealed class InvoiceLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invoice_totals_lines_and_pays_once()
    {
        var invoice = CreateInvoice();

        Assert.Equal(58.00m, invoice.Total);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);

        Assert.False(invoice.RegisterPayment(29.00m, Now));
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(29.00m, invoice.OutstandingAmount);

        Assert.True(invoice.RegisterPayment(29.00m, Now));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, invoice.OutstandingAmount);

        // A redelivered success event is a no-op.
        Assert.True(invoice.RegisterPayment(29.00m, Now));
        Assert.Equal(58.00m, invoice.AmountPaid);
    }

    [Fact]
    public void Overpayment_is_rejected()
    {
        var invoice = CreateInvoice();

        var exception = Assert.Throws<PermanentWebhookException>(
            () => invoice.RegisterPayment(59.00m, Now));

        Assert.Contains("exceeds", exception.Message);
        Assert.Equal(0m, invoice.AmountPaid);
    }

    [Fact]
    public void Full_refund_moves_invoice_to_terminal_state_and_is_idempotent()
    {
        var invoice = CreateInvoice();
        invoice.RegisterPayment(58.00m, Now);

        Assert.True(invoice.ApplyRefund(58.00m, Now));
        Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
        Assert.Equal(58.00m, invoice.AmountRefunded);

        Assert.True(invoice.ApplyRefund(58.00m, Now));
        Assert.Equal(58.00m, invoice.AmountRefunded);
    }

    [Fact]
    public void Partial_refund_keeps_invoice_paid()
    {
        var invoice = CreateInvoice();
        invoice.RegisterPayment(58.00m, Now);

        Assert.False(invoice.ApplyRefund(10.00m, Now));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(10.00m, invoice.AmountRefunded);
    }

    [Fact]
    public void Payment_attempt_can_be_refunded_only_when_succeeded()
    {
        var invoice = CreateInvoice();
        var attempt = PaymentAttempt.Create(invoice, "test", "pay_1", "idem-1", new string('a', 64), 58.00m, Now);

        Assert.Throws<PermanentWebhookException>(() => attempt.ApplyRefund(58.00m, Now));

        Assert.True(attempt.MarkSucceeded(Now));
        Assert.True(attempt.ApplyRefund(58.00m, Now));
        Assert.Equal(PaymentStatus.Refunded, attempt.Status);
        Assert.Equal(58.00m, attempt.RefundedAmount);
    }

    [Fact]
    public void Partial_refunds_are_cumulative_and_cannot_exceed_the_attempt()
    {
        var invoice = CreateInvoice();
        var attempt = PaymentAttempt.Create(invoice, "test", "pay_2", "idem-2", new string('b', 64), 58.00m, Now);
        attempt.MarkSucceeded(Now);

        Assert.False(attempt.ApplyRefund(20.00m, Now));
        Assert.False(attempt.ApplyRefund(20.00m, Now));
        Assert.Equal(40.00m, attempt.RefundedAmount);
        Assert.Equal(PaymentStatus.Succeeded, attempt.Status);

        Assert.Throws<PermanentWebhookException>(() => attempt.ApplyRefund(19.00m, Now));
        Assert.Equal(40.00m, attempt.RefundedAmount);

        Assert.True(attempt.ApplyRefund(18.00m, Now));
        Assert.Equal(58.00m, attempt.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, attempt.Status);
    }

    [Fact]
    public void Invoice_refund_cannot_exceed_total_and_partial_refunds_accumulate()
    {
        var invoice = CreateInvoice();
        invoice.RegisterPayment(58.00m, Now);

        Assert.False(invoice.ApplyRefund(20.00m, Now));
        Assert.False(invoice.ApplyRefund(20.00m, Now));
        Assert.Equal(40.00m, invoice.AmountRefunded);

        Assert.Throws<PermanentWebhookException>(() => invoice.ApplyRefund(19.00m, Now));
        Assert.Equal(40.00m, invoice.AmountRefunded);

        Assert.True(invoice.ApplyRefund(18.00m, Now));
        Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
        Assert.Equal(58.00m, invoice.AmountRefunded);
    }

    [Fact]
    public void Subscription_activation_is_idempotent_and_refund_is_terminal()
    {
        var plan = PlanCatalog.GetRequired("starter");
        var subscription = Subscription.Create(Guid.NewGuid(), plan, Now);

        Assert.True(subscription.Activate(Now));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.False(subscription.Activate(Now.AddDays(1)));
        Assert.Equal(Now, subscription.ActivatedAt);

        Assert.True(subscription.MarkRefunded(Now.AddDays(2)));
        Assert.Equal(SubscriptionStatus.Refunded, subscription.Status);
        Assert.False(subscription.Activate(Now.AddDays(3)));
    }

    [Fact]
    public void Product_access_revokes_and_regrants()
    {
        var organizationId = Guid.NewGuid();
        var access = ProductAccess.Grant(organizationId, "starter-product", Guid.NewGuid(), Now);

        Assert.False(access.ReGrant(access.InvoiceId, Now.AddMinutes(1)));
        Assert.True(access.Revoke(Now.AddMinutes(2)));
        Assert.False(access.Revoke(Now.AddMinutes(3)));
        Assert.Equal(AccessStatus.Revoked, access.Status);

        var newInvoice = Guid.NewGuid();
        Assert.True(access.ReGrant(newInvoice, Now.AddMinutes(4)));
        Assert.Equal(AccessStatus.Active, access.Status);
        Assert.Null(access.RevokedAt);
        Assert.Equal(newInvoice, access.InvoiceId);
    }

    [Fact]
    public void Line_amounts_are_rounded_to_cents()
    {
        var line = InvoiceLine.Create("Usage", "api", 3, 0.335m);
        Assert.Equal(1.01m, line.Amount);
        Assert.Equal(0.34m, line.UnitAmount);
    }

    private static Invoice CreateInvoice()
    {
        var organizationId = Guid.NewGuid();
        var invoice = Invoice.Create(
            organizationId,
            null,
            "INV-TEST-1",
            "USD",
            Now.AddDays(14),
            [
                InvoiceLine.Create("Starter plan", "starter-product", 1, 29.00m),
                InvoiceLine.Create("Extra seats", null, 2, 14.50m)
            ],
            Now);

        return invoice;
    }
}
