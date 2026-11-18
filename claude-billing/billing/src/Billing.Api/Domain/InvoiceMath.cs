namespace Billing.Api.Domain;

public static class InvoiceMath
{
    /// <summary>Single source of truth for the invoice status derived from its amounts.</summary>
    public static InvoiceStatus StatusFor(InvoiceStatus current, long total, long paid, long refunded)
    {
        if (current == InvoiceStatus.Void) return current;
        if (paid > 0 && refunded >= paid) return InvoiceStatus.Refunded;
        if (refunded > 0) return InvoiceStatus.PartiallyRefunded;
        if (paid >= total) return InvoiceStatus.Paid;
        if (paid > 0) return InvoiceStatus.PartiallyPaid;
        return InvoiceStatus.Open;
    }
}
