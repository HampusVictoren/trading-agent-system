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
        Should.Throw<ArgumentOutOfRangeException>(() => new RiskPolicy(maxPositionPct, 0.10m));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void A_cash_buffer_outside_a_share_is_refused(decimal cashBufferPct)
    {
        // A buffer of 1 would reserve the whole portfolio and never let anything be bought.
        Should.Throw<ArgumentOutOfRangeException>(() => new RiskPolicy(0.05m, cashBufferPct));
    }

    [Fact]
    public void No_buffer_at_all_is_allowed()
    {
        new RiskPolicy(0.05m, 0m).CashBufferPct.ShouldBe(0m);
    }

    [Fact]
    public void A_whole_portfolio_in_one_position_is_allowed()
    {
        // Unwise, but it is a policy decision rather than a domain rule.
        new RiskPolicy(1m, 0m).MaxPositionPct.ShouldBe(1m);
    }
}
