using Engine.Domain.Risk;
using Shouldly;

namespace Engine.Tests.Domain.Risk;

/// <summary>
/// The domain does not trust that configuration was validated. RiskPolicyOptions checks the
/// same ranges at startup, but a policy built anywhere else has to be impossible to get wrong.
/// </summary>
public class RiskPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_position_limit_outside_a_share_is_refused(decimal maxPositionPct)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RiskPolicy(maxPositionPct, 0.10m, TimeSpan.FromMinutes(5), TimeSpan.FromDays(3)));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void A_cash_buffer_outside_a_share_is_refused(decimal cashBufferPct)
    {
        // A buffer of 1 would reserve the whole portfolio and never let anything be bought.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RiskPolicy(0.05m, cashBufferPct, TimeSpan.FromMinutes(5), TimeSpan.FromDays(3)));
    }

    [Fact]
    public void No_buffer_at_all_is_allowed()
    {
        new RiskPolicy(0.05m, 0m, TimeSpan.FromMinutes(5), TimeSpan.FromDays(3)).CashBufferPct.ShouldBe(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_quote_age_limit_that_is_not_a_period_is_refused(int seconds)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RiskPolicy(0.05m, 0.10m, TimeSpan.FromSeconds(seconds), TimeSpan.FromDays(3)));
    }

    [Fact]
    public void A_whole_portfolio_in_one_position_is_allowed()
    {
        // Unwise, but it is a policy decision rather than a domain rule.
        new RiskPolicy(1m, 0m, TimeSpan.FromMinutes(5), TimeSpan.FromDays(3)).MaxPositionPct.ShouldBe(1m);
    }

    [Fact]
    public void A_negative_minimum_holding_period_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RiskPolicy(0.05m, 0.10m, TimeSpan.FromMinutes(5), TimeSpan.FromDays(-1)));
    }

    [Fact]
    public void No_minimum_holding_period_at_all_is_allowed()
    {
        // It means the agents may reverse a purchase at once. Unwise given how fast the same
        // fact sheet has produced two different answers, but it is a policy decision.
        new RiskPolicy(0.05m, 0.10m, TimeSpan.FromMinutes(5), TimeSpan.Zero)
            .MinHoldingPeriod.ShouldBe(TimeSpan.Zero);
    }
}
