namespace SaasBilling.Domain;

public sealed class InvoiceLine
{
    private InvoiceLine()
    {
    }

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Invoice Invoice { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public string? ProductCode { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitAmount { get; private set; }
    public decimal Amount { get; private set; }

    public static InvoiceLine Create(string description, string? productCode, int quantity, decimal unitAmount)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 500)
        {
            throw new BillingValidationException("Invoice line description is required and must be at most 500 characters.");
        }

        if (quantity is < 1 or > 10_000)
        {
            throw new BillingValidationException("Invoice line quantity must be between 1 and 10000.");
        }

        if (unitAmount < 0)
        {
            throw new BillingValidationException("Invoice line unit amount cannot be negative.");
        }

        var normalizedProductCode = string.IsNullOrWhiteSpace(productCode)
            ? null
            : productCode.Trim().ToLowerInvariant();

        if (normalizedProductCode is { Length: > 100 })
        {
            throw new BillingValidationException("Product code must be at most 100 characters.");
        }

        var amount = Money.Round(unitAmount * quantity);
        if (amount <= 0)
        {
            throw new BillingValidationException("Invoice line amount must be positive.");
        }

        return new InvoiceLine
        {
            Id = Guid.NewGuid(),
            Description = description.Trim(),
            ProductCode = normalizedProductCode,
            Quantity = quantity,
            UnitAmount = Money.Round(unitAmount),
            Amount = amount
        };
    }

    internal void AttachTo(Guid invoiceId) => InvoiceId = invoiceId;
}
