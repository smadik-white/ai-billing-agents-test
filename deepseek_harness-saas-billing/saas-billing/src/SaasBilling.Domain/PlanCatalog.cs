namespace SaasBilling.Domain;

/// <summary>
/// Small, deliberately static plan catalogue. In a real product this would live in the database.
/// The plan owns the product code that is provisioned after successful payment.
/// </summary>
public static class PlanCatalog
{
    private static readonly IReadOnlyDictionary<string, Plan> Plans =
        new Dictionary<string, Plan>(StringComparer.OrdinalIgnoreCase)
        {
            ["starter"] = new("starter", "Starter", "starter-product", 29.00m, "USD", 1),
            ["pro"] = new("pro", "Pro", "pro-product", 99.00m, "USD", 1),
            ["team"] = new("team", "Team", "team-product", 299.00m, "USD", 1)
        };

    public static IReadOnlyCollection<Plan> All => Plans.Values.ToArray();

    public static Plan GetRequired(string code)
    {
        if (!Plans.TryGetValue(code?.Trim() ?? string.Empty, out var plan))
        {
            throw new BillingValidationException(
                $"Unknown plan '{code}'. Available plans: {string.Join(", ", Plans.Keys)}.");
        }

        return plan;
    }
}
