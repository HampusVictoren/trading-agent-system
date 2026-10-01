using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Risk;

/// <summary>
/// How much the engine may spend on purchases in one trading day.
/// </summary>
/// <remarks>
/// <para>
/// It is not <see cref="RiskPolicy.MaxPositionPct"/> again. That rule bounds any one position and
/// holds whatever this one says; this one bounds a <i>day</i>, because since stage 5 a day's buying
/// is up to ten decisions from one model on one screen, taken within a few minutes of each other.
/// They share whatever that day's bias is, and a momentum ranking in a rising market hands the
/// agents ten names that move together - so ten positions is less diversification than it looks.
/// </para>
/// <para>
/// Both halves are tested, because both exist: the sizer shrinks an order against what is left of
/// the day, and the gate re-derives the limit and can still refuse. That is the same arrangement
/// every other limit in this engine has, and the reason is that the sizer's arithmetic is not
/// evidence about the sizer's arithmetic.
/// </para>
/// </remarks>
public class DailyDeploymentLimitTests
{
    private static readonly Ticker Aapl = new("AAPL");

    private static readonly PositionSizer Sizer = new();
    private static readonly RiskEngine Gate = new();

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 5, 0, TimeSpan.Zero);

    /// <summary>
    /// A fifth of the portfolio a day, against a twentieth in any one position - so four full
    /// positions fit in a day and the fifth does not.
    /// </summary>
    private static readonly RiskPolicy Policy = new(
        maxPositionPct: 0.05m,
        cashBufferPct: 0m,
        maxQuoteAge: TimeSpan.FromMinutes(5),
        minHoldingPeriod: TimeSpan.FromDays(3),
        stopLossPct: 0.10m,
        maxDailyDeploymentPct: 0.20m);

    private static Portfolio WithCash(decimal cash = 10_000m) => new(new Money(cash, Money.DefaultCurrency));

    private static Money Kronor(decimal amount) => new(amount, Money.DefaultCurrency);

    private static TradeSignal Signal(Stance stance = Stance.Buy, double conviction = 0.8, decimal price = 100m) =>
        new(
            new Instrument.Equity(Aapl),
            stance,
            new Conviction(conviction),
            "thesis",
            ["a risk"],
            HorizonDays: 5,
            new Money(price, Money.DefaultCurrency),
            Now,
            new RunMetadata("default", "v1", Revisions: 0));

    [Fact]
    public void A_day_with_nothing_spent_is_bounded_by_the_position_limit_as_before()
    {
        // The limit that binds first is the one that applies, and on an untouched day that is
        // still the position limit: 5 % of 10 000 is 500, which buys five shares at 100.
        var intent = Sizer.Size(Signal(), WithCash(), PriceSnapshot.Empty, Policy, Kronor(0m));

        intent.ShouldBeOfType<OrderIntent.Buy>().Quantity.ShouldBe(5m);
    }

    [Fact]
    public void An_order_shrinks_against_what_is_left_of_the_day()
    {
        // 20 % of 10 000 is 2 000 and 1 800 is spent, so 200 is left - two shares at 100, where
        // the position limit alone would have allowed five. It shrinks rather than being refused,
        // exactly the way an order already shrinks against the cash buffer.
        var intent = Sizer.Size(Signal(), WithCash(), PriceSnapshot.Empty, Policy, Kronor(1_800m));

        intent.ShouldBeOfType<OrderIntent.Buy>().Quantity.ShouldBe(2m);
    }

    [Fact]
    public void A_day_that_is_spent_sizes_to_nothing()
    {
        var intent = Sizer.Size(Signal(), WithCash(), PriceSnapshot.Empty, Policy, Kronor(2_000m));

        intent.ShouldBeOfType<OrderIntent.None>().Reason.ShouldContain("does not reach one share");
    }

    [Fact]
    public void A_day_spent_past_its_limit_sizes_to_nothing_rather_than_to_a_negative()
    {
        // Reachable by lowering the setting between cycles, which is configuration and allowed.
        // Floor over a negative budget would otherwise produce a negative quantity, and a negative
        // buy is a sale nobody asked for.
        var intent = Sizer.Size(Signal(), WithCash(), PriceSnapshot.Empty, Policy, Kronor(5_000m));

        intent.ShouldBeOfType<OrderIntent.None>();
    }

    [Fact]
    public void A_sale_ignores_the_day_entirely()
    {
        // Selling frees capital rather than committing it, so a daily purchase budget has nothing
        // to say about it - and a cap that could stop an exit would be the opposite of a risk
        // control. The sizer takes the figure and must not consult it.
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 10m, new Money(100m), Now, horizonDays: 5);

        var intent = Sizer.Size(
            Signal(stance: Stance.Sell), portfolio, PriceSnapshot.Empty, Policy, Kronor(1_000_000m));

        intent.ShouldBeOfType<OrderIntent.Sell>().Quantity.ShouldBe(10m);
    }

    [Fact]
    public void The_gate_refuses_a_buy_that_would_pass_the_days_limit()
    {
        // The sizer is not trusted. An order built by hand - or by a sizer with a different idea
        // of the day - still meets the limit on the way out.
        var order = new OrderIntent.Buy(new Instrument.Equity(Aapl), Quantity: 3m, new Money(100m));

        var decision = Gate.Evaluate(
            order, Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now, Kronor(1_800m));

        decision.ShouldBeOfType<RiskDecision.Rejected>().Reason
            .ShouldBe("the day's purchases would reach 2100, past the limit of 2000.00");
    }

    [Fact]
    public void The_gate_lets_an_order_that_exactly_fills_the_day_through()
    {
        // The boundary is inclusive on the allowed side, like the position limit next to it.
        var order = new OrderIntent.Buy(new Instrument.Equity(Aapl), Quantity: 2m, new Money(100m));

        Gate.Evaluate(order, Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now, Kronor(1_800m))
            .ShouldBeOfType<RiskDecision.Approved>();
    }

    [Fact]
    public void The_position_limit_is_reported_before_the_day_when_both_are_breached()
    {
        // The order of the two checks, stated as a test. "This position is too big" tells an
        // operator more than "the day is spent", and only one of the two can be fixed by waiting.
        var order = new OrderIntent.Buy(new Instrument.Equity(Aapl), Quantity: 50m, new Money(100m));

        Gate.Evaluate(order, Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now, Kronor(1_900m))
            .ShouldBeOfType<RiskDecision.Rejected>().Reason.ShouldContain("the position would reach");
    }

    [Fact]
    public void A_sale_is_never_asked_about_the_day()
    {
        // The sell gate takes no deployment figure at all, which is what makes it impossible for
        // a daily purchase budget to trap a position. That the overload has no such parameter is
        // the assertion; this test exists so removing it would not compile.
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 10m, new Money(100m), Now.AddDays(-10), horizonDays: 5);

        var order = new OrderIntent.Sell(
            new Instrument.Equity(Aapl), Quantity: 10m, new Money(100m), Now, OrderTrigger.Signal);

        Gate.Evaluate(order, portfolio, Policy, Now).ShouldBeOfType<RiskDecision.Approved>();
    }
}
