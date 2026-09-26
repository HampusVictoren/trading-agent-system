namespace Engine.Tests.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.Risk;
using Engine.Domain.ValueObjects;
using Shouldly;

/// <summary>
/// The two rules that sell without asking anyone. Pure functions of what the position already
/// knows, which is why they can be read here without a portfolio, a clock or an HTTP client.
/// </summary>
/// <remarks>
/// Throughout: bought at 100 with a 10 % stop, so the floor is 90, on a thesis of 15 days.
/// </remarks>
public class ExitRulesTests
{
    private static readonly Ticker Eric = new("ERIC-B.ST");
    private static readonly DateTimeOffset Bought = PortfolioTestExtensions.BoughtAt;

    private static readonly RiskPolicy Policy = new(
        maxPositionPct: 0.05m,
        cashBufferPct: 0.10m,
        maxQuoteAge: TimeSpan.FromMinutes(5),
        minHoldingPeriod: TimeSpan.FromDays(3),
        stopLossPct: 0.10m);

    private static Position Held(decimal cost = 100m, int horizonDays = 15) =>
        new(Eric, quantity: 10m, new Money(cost, Money.DefaultCurrency), Bought, horizonDays);

    private static OrderTrigger? At(decimal price, int daysLater = 1, Position? position = null) =>
        ExitRules.Triggered(
            position ?? Held(), new Money(price, Money.DefaultCurrency), Bought.AddDays(daysLater), Policy);

    [Fact]
    public void A_price_at_the_stop_is_a_stop_loss()
    {
        // The boundary is inclusive: a ten percent stop fires at a ten percent fall, not just
        // past it. Exactly at the limit is where an off-by-one hides.
        At(90m).ShouldBe(OrderTrigger.StopLoss);
    }

    [Fact]
    public void A_price_a_little_above_the_stop_is_left_alone()
    {
        At(90.01m).ShouldBeNull();
    }

    [Fact]
    public void A_price_well_below_the_stop_is_a_stop_loss()
    {
        At(70m).ShouldBe(OrderTrigger.StopLoss);
    }

    [Fact]
    public void A_holding_that_is_up_is_left_alone()
    {
        At(130m).ShouldBeNull();
    }

    [Fact]
    public void A_horizon_that_has_passed_is_a_time_limit()
    {
        At(100m, daysLater: 15).ShouldBe(OrderTrigger.TimeLimit);
    }

    [Fact]
    public void A_horizon_with_a_day_left_is_left_alone()
    {
        At(100m, daysLater: 14).ShouldBeNull();
    }

    [Fact]
    public void The_stop_loss_wins_when_both_apply()
    {
        // The trade is the same either way; the difference is what the ledger says happened,
        // and the ledger is what the comparison is made from.
        At(70m, daysLater: 30).ShouldBe(OrderTrigger.StopLoss);
    }

    [Fact]
    public void The_horizon_is_the_one_the_thesis_asked_for()
    {
        // Not a fixed period. A three day thesis has expired on day four; a thirty day one has
        // not, on the same day, at the same price.
        At(100m, daysLater: 4, position: Held(horizonDays: 3)).ShouldBe(OrderTrigger.TimeLimit);
        At(100m, daysLater: 4, position: Held(horizonDays: 30)).ShouldBeNull();
    }

    [Fact]
    public void Adding_to_a_holding_restarts_the_time_limit()
    {
        // The two types compose: the position moves its own clock on a purchase, so the rule
        // does not have to know that a top-up happened.
        var position = Held(horizonDays: 5);
        position.AddQuantity(5m, new Money(110m, Money.DefaultCurrency), Bought.AddDays(4), horizonDays: 5);

        ExitRules.Triggered(
                position, new Money(110m, Money.DefaultCurrency), Bought.AddDays(6), Policy)
            .ShouldBeNull();
    }

    [Fact]
    public void The_stop_is_measured_against_the_average_of_two_buys()
    {
        // 10 at 100 and 10 at 120 averages 110, so the floor moves to 99 - above the price
        // that was fine before the second purchase. Averaging up raises the stop, which is
        // what buying more at a higher price actually means.
        var position = Held();
        position.AddQuantity(10m, new Money(120m, Money.DefaultCurrency), Bought, horizonDays: 15);

        ExitRules.Triggered(position, new Money(95m, Money.DefaultCurrency), Bought.AddDays(1), Policy)
            .ShouldBe(OrderTrigger.StopLoss);
    }

    [Fact]
    public void A_price_in_another_currency_is_a_bug_rather_than_an_exit()
    {
        // Money refuses to combine currencies, which is what makes the comparison safe: the
        // alternative is reading a krona and a dollar as the same number and selling on it.
        Should.Throw<CurrencyMismatchException>(
            () => ExitRules.Triggered(Held(), new Money(90m, "EUR"), Bought.AddDays(1), Policy));
    }
}
