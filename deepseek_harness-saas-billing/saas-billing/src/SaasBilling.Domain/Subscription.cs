namespace SaasBilling.Domain;

public sealed class Subscription
{
    private Subscription()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Organization Organization { get; private set; } = null!;
    public string PlanCode { get; private set; } = string.Empty;
    public string PlanName { get; private set; } = string.Empty;
    public string ProductCode { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? PeriodStart { get; private set; }
    public DateTimeOffset? PeriodEnd { get; private set; }
    public DateTimeOffset? TerminatedAt { get; private set; }

    public static Subscription Create(Guid organizationId, Plan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PlanCode = plan.Code,
            PlanName = plan.Name,
            ProductCode = plan.ProductCode,
            Price = Money.Round(plan.Price),
            Currency = Money.NormalizeCurrency(plan.Currency),
            Status = SubscriptionStatus.PendingActivation,
            CreatedAt = now
        };
    }

    /// <summary>
    /// Activates a pending subscription for a single one-month period.
    /// Kept as a convenience for callers that do not manage renewals explicitly.
    /// </summary>
    public bool Activate(DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.Active or SubscriptionStatus.Refunded or SubscriptionStatus.Canceled)
        {
            return false;
        }

        return ActivateOrRenew(now, 1);
    }

    /// <summary>
    /// Activates a pending/past-due subscription or extends an active one by the plan's period.
    /// An early renewal is appended to the already paid period; an expired subscription starts
    /// a new period at <paramref name="now"/>. Terminal subscriptions are not reactivated.
    /// </summary>
    public bool ActivateOrRenew(DateTimeOffset now, int periodMonths)
    {
        if (periodMonths is < 1 or > 120)
        {
            throw new BillingValidationException("Subscription period must be between 1 and 120 months.");
        }

        if (Status is SubscriptionStatus.Refunded or SubscriptionStatus.Canceled)
        {
            return false;
        }

        var periodStart = PeriodEnd is { } currentEnd && currentEnd > now ? currentEnd : now;

        Status = SubscriptionStatus.Active;
        ActivatedAt ??= now;
        PeriodStart = periodStart;
        PeriodEnd = periodStart.AddMonths(periodMonths);
        TerminatedAt = null;
        return true;
    }

    public bool MarkPastDue()
    {
        if (Status != SubscriptionStatus.Active)
        {
            return false;
        }

        Status = SubscriptionStatus.PastDue;
        return true;
    }

    /// <summary>Full refund/cancellation. Terminal for this subscription.</summary>
    public bool MarkRefunded(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Refunded)
        {
            return false;
        }

        Status = SubscriptionStatus.Refunded;
        TerminatedAt = now;
        return true;
    }

    public bool Cancel(DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.Canceled or SubscriptionStatus.Refunded)
        {
            return false;
        }

        Status = SubscriptionStatus.Canceled;
        TerminatedAt = now;
        return true;
    }
}
