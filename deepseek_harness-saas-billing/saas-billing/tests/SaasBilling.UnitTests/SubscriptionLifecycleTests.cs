using SaasBilling.Domain;
using Xunit;

namespace SaasBilling.UnitTests;

public sealed class SubscriptionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly Plan ThreeMonthPlan = new("quarterly", "Quarterly", "quarterly-product", 75m, "USD", 3);

    [Fact]
    public void First_period_starts_now_and_uses_plan_duration()
    {
        var subscription = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);

        Assert.True(subscription.ActivateOrRenew(Now, ThreeMonthPlan.PeriodMonths));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Now, subscription.ActivatedAt);
        Assert.Equal(Now, subscription.PeriodStart);
        Assert.Equal(Now.AddMonths(3), subscription.PeriodEnd);
    }

    [Fact]
    public void Early_renewal_appends_to_the_paid_period()
    {
        var subscription = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);
        subscription.ActivateOrRenew(Now, ThreeMonthPlan.PeriodMonths);

        var renewalTime = Now.AddDays(10);
        Assert.True(subscription.ActivateOrRenew(renewalTime, ThreeMonthPlan.PeriodMonths));

        Assert.Equal(Now.AddMonths(3), subscription.PeriodStart);
        Assert.Equal(Now.AddMonths(6), subscription.PeriodEnd);
        Assert.Equal(Now, subscription.ActivatedAt);
    }

    [Fact]
    public void Expired_subscription_restarts_from_payment_time()
    {
        var subscription = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);
        subscription.ActivateOrRenew(Now, ThreeMonthPlan.PeriodMonths);

        var afterExpiry = Now.AddMonths(5);
        Assert.True(subscription.ActivateOrRenew(afterExpiry, ThreeMonthPlan.PeriodMonths));

        Assert.Equal(afterExpiry, subscription.PeriodStart);
        Assert.Equal(afterExpiry.AddMonths(3), subscription.PeriodEnd);
    }

    [Fact]
    public void Past_due_subscription_can_be_restored_and_extended()
    {
        var subscription = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);
        subscription.ActivateOrRenew(Now, ThreeMonthPlan.PeriodMonths);
        Assert.True(subscription.MarkPastDue());

        var paymentTime = Now.AddDays(5);
        Assert.True(subscription.ActivateOrRenew(paymentTime, ThreeMonthPlan.PeriodMonths));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Now.AddMonths(3), subscription.PeriodStart);
        Assert.Equal(Now.AddMonths(6), subscription.PeriodEnd);
    }

    [Fact]
    public void Canceled_and_refunded_subscriptions_are_terminal()
    {
        var canceled = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);
        Assert.True(canceled.Cancel(Now));
        Assert.False(canceled.ActivateOrRenew(Now.AddDays(1), 3));
        Assert.Equal(SubscriptionStatus.Canceled, canceled.Status);

        var refunded = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);
        Assert.True(refunded.MarkRefunded(Now));
        Assert.False(refunded.ActivateOrRenew(Now.AddDays(1), 3));
        Assert.Equal(SubscriptionStatus.Refunded, refunded.Status);
    }

    [Fact]
    public void Invalid_period_is_rejected()
    {
        var subscription = Subscription.Create(Guid.NewGuid(), ThreeMonthPlan, Now);

        Assert.Throws<BillingValidationException>(() => subscription.ActivateOrRenew(Now, 0));
        Assert.Throws<BillingValidationException>(() => subscription.ActivateOrRenew(Now, 121));
    }
}
