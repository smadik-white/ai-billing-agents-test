namespace Billing.Api.Domain;

public static class InvoiceFinancialRules
{
    public static decimal NetPaidAmount(Invoice invoice) => invoice.AmountPaid - invoice.AmountRefunded;

    public static decimal OutstandingAmount(Invoice invoice) =>
        Math.Max(0m, invoice.TotalAmount - NetPaidAmount(invoice));

    public static string DetermineStatus(Invoice invoice)
    {
        // A fully refunded invoice is terminal. A new late capture is reconciled manually and
        // must not silently reopen the original billing obligation.
        if (invoice.Status == InvoiceStatus.Refunded)
            return InvoiceStatus.Refunded;
        if (invoice.AmountPaid > 0 && invoice.AmountRefunded >= invoice.AmountPaid)
            return InvoiceStatus.Refunded;
        if (NetPaidAmount(invoice) >= invoice.TotalAmount)
            return InvoiceStatus.Paid;
        if (invoice.AmountRefunded > 0)
            return InvoiceStatus.PartiallyRefunded;
        if (invoice.AmountPaid > 0)
            return InvoiceStatus.PartiallyPaid;
        return InvoiceStatus.Issued;
    }
}

public static class SubscriptionPeriodRules
{
    public static DateTime StartNextPeriod(DateTime nowUtc, DateTime? previousPeriodEndUtc) =>
        previousPeriodEndUtc is { } previousEnd && previousEnd > nowUtc ? previousEnd : nowUtc;
}
