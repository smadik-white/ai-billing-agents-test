namespace SaasBilling.Domain;

public static class Money
{
    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static string NormalizeCurrency(string? currency)
    {
        var normalized = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetter))
        {
            throw new BillingValidationException("Currency must be a three-letter ISO code, e.g. USD.");
        }

        return normalized;
    }
}

public sealed record Plan(
    string Code,
    string Name,
    string ProductCode,
    decimal Price,
    string Currency,
    int PeriodMonths);
