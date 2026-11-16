namespace SaasBilling.Domain;

public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Organization Organization { get; private set; } = null!;
    public Guid? SubscriptionId { get; private set; }
    public Subscription? Subscription { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;
    public InvoiceStatus Status { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal Total { get; private set; }
    public decimal AmountPaid { get; private set; }
    public decimal AmountRefunded { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    public ICollection<InvoiceLine> Lines => _lines;
    public ICollection<PaymentAttempt> PaymentAttempts { get; private set; } = new List<PaymentAttempt>();

    public decimal OutstandingAmount => Money.Round(Total - AmountPaid);

    public static Invoice Create(
        Guid organizationId,
        Guid? subscriptionId,
        string number,
        string currency,
        DateTimeOffset dueAt,
        IEnumerable<InvoiceLine> lines,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new BillingValidationException("OrganizationId is required.");
        }

        if (string.IsNullOrWhiteSpace(number) || number.Length > 40)
        {
            throw new BillingValidationException("Invoice number is required and must be at most 40 characters.");
        }

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            SubscriptionId = subscriptionId,
            Number = number.Trim(),
            Currency = Money.NormalizeCurrency(currency),
            Status = InvoiceStatus.Open,
            CreatedAt = now,
            DueAt = dueAt
        };

        foreach (var line in lines)
        {
            line.AttachTo(invoice.Id);
            invoice._lines.Add(line);
        }

        if (invoice._lines.Count == 0)
        {
            throw new BillingValidationException("An invoice must contain at least one line.");
        }

        invoice.Subtotal = Money.Round(invoice._lines.Sum(x => x.Amount));
        invoice.Total = invoice.Subtotal;

        if (invoice.Total <= 0)
        {
            throw new BillingValidationException("Invoice total must be positive.");
        }

        return invoice;
    }

    /// <summary>
    /// Applies a confirmed payment. Returns true when the invoice becomes fully paid.
    /// A duplicate application on an already paid invoice is a safe no-op.
    /// </summary>
    public bool RegisterPayment(decimal amount, DateTimeOffset now)
    {
        if (Status == InvoiceStatus.Refunded)
        {
            throw new PermanentWebhookException("Cannot apply a payment to a refunded invoice.");
        }

        if (Status == InvoiceStatus.Void)
        {
            throw new PermanentWebhookException("Cannot apply a payment to a void invoice.");
        }

        if (Status == InvoiceStatus.Paid)
        {
            return true;
        }

        amount = Money.Round(amount);
        if (amount <= 0)
        {
            throw new PermanentWebhookException("Payment amount must be positive.");
        }

        if (AmountPaid + amount > Total)
        {
            throw new PermanentWebhookException(
                $"Payment exceeds invoice outstanding amount ({OutstandingAmount:0.00} {Currency}).");
        }

        AmountPaid = Money.Round(AmountPaid + amount);
        if (AmountPaid == Total)
        {
            Status = InvoiceStatus.Paid;
            PaidAt = now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a refund. Partial refunds keep the invoice paid; a full refund moves it to Refunded.
    /// Returns true when the invoice becomes fully refunded.
    /// </summary>
    public bool ApplyRefund(decimal amount, DateTimeOffset now)
    {
        if (Status == InvoiceStatus.Refunded)
        {
            return true;
        }

        if (Status != InvoiceStatus.Paid)
        {
            throw new PermanentWebhookException("Only a paid invoice can be refunded.");
        }

        amount = Money.Round(amount);
        if (amount <= 0)
        {
            throw new PermanentWebhookException("Refund amount must be positive.");
        }

        if (AmountRefunded + amount > Total)
        {
            throw new PermanentWebhookException(
                $"Refund exceeds invoice total ({Total:0.00} {Currency}); already refunded {AmountRefunded:0.00}.");
        }

        AmountRefunded = Money.Round(AmountRefunded + amount);
        if (AmountRefunded == Total)
        {
            Status = InvoiceStatus.Refunded;
            RefundedAt = now;
            return true;
        }

        return false;
    }

    public void Void()
    {
        if (Status != InvoiceStatus.Open)
        {
            throw new BillingConflictException("Only an open invoice can be voided.");
        }

        Status = InvoiceStatus.Void;
    }
}
