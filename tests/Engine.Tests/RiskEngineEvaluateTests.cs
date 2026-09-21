using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Risk;

/// <summary>
/// The last gate before an order is placed. It re-derives the limits PositionSizer already
/// enforced rather than trusting them - this is the only code that moves money, and a sizing
/// bug should have to be repeated here to get past both - and it checks the one thing sizing
/// cannot see: how old the quote is.
/// </summary>
public class RiskEngineEvaluateTests
{
    private static readonly Ticker Aapl = new("AAPL");
    private static readonly Ticker Msft = new("MSFT");
    private static readonly RiskEngine Engine = new();
    private static readonly PositionSizer Sizer = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 15, 0, 0, TimeSpan.Zero);

    private static readonly RiskPolicy Policy =
        new(maxPositionPct: 0.05m, cashBufferPct: 0.10m, maxQuoteAge: TimeSpan.FromMinutes(5));

    private static Portfolio WithCash(decimal cash = 10_000m) => new(new Money(cash, "USD"));

    private static TradeSignal Signal(DateTimeOffset? quoteAsOf = null, decimal price = 100m) =>
        new(
            new Instrument.Equity(Aapl),
            Stance.Buy,
            new Conviction(0.8),
            "thesis",
            ["a risk"],
            HorizonDays: 5,
            new Money(price, "USD"),
            quoteAsOf ?? Now.AddSeconds(-13),
            new RunMetadata("default", "v1", Revisions: 0));

    private static OrderIntent.Buy Order(decimal quantity, decimal price = 100m) =>
        new(new Instrument.Equity(Aapl), quantity, new Money(price, "USD"));

    private static string RejectionOf(RiskDecision decision) =>
        decision.ShouldBeOfType<RiskDecision.Rejected>().Reason;

    [Fact]
    public void A_fresh_order_within_every_limit_is_approved()
    {
        Engine.Evaluate(Order(5m), Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now)
            .ShouldBeOfType<RiskDecision.Approved>();
    }

    [Fact]
    public void A_quote_exactly_at_the_age_limit_is_still_good()
    {
        Engine.Evaluate(Order(5m), Signal(Now.AddMinutes(-5)), WithCash(), PriceSnapshot.Empty, Policy, Now)
            .ShouldBeOfType<RiskDecision.Approved>();
    }

    [Fact]
    public void A_quote_past_the_age_limit_is_refused()
    {
        RejectionOf(Engine.Evaluate(
                Order(5m), Signal(Now.AddMinutes(-5).AddSeconds(-1)), WithCash(), PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("old");
    }

    [Fact]
    public void A_quote_dated_in_the_future_is_refused()
    {
        // quote_as_of comes from the agent service. Without this a future date would sail
        // past a plain age check and make every quote look fresh for ever.
        RejectionOf(Engine.Evaluate(
                Order(5m), Signal(Now.AddHours(1)), WithCash(), PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("future");
    }

    [Fact]
    public void An_order_past_the_position_limit_is_refused()
    {
        // Six shares at 100 is 600 against a limit of 500. PositionSizer would never produce
        // this; that is the point of checking again.
        RejectionOf(Engine.Evaluate(Order(6m), Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("past the limit");
    }

    [Fact]
    public void An_order_that_would_take_an_existing_position_past_the_limit_is_refused()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 3m, new Money(100m));

        RejectionOf(Engine.Evaluate(Order(3m), Signal(), portfolio, PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("past the limit");
    }

    [Fact]
    public void Topping_a_position_up_to_the_limit_exactly_is_allowed()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 3m, new Money(100m));

        Engine.Evaluate(Order(2m), Signal(), portfolio, PriceSnapshot.Empty, Policy, Now)
            .ShouldBeOfType<RiskDecision.Approved>();
    }

    [Fact]
    public void An_order_that_costs_more_than_the_cash_on_hand_is_refused()
    {
        // Within the position limit, because net asset value is mostly someone else's shares.
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Msft, quantity: 99m, new Money(100m));

        RejectionOf(Engine.Evaluate(
                Order(5m), Signal(), portfolio, PriceSnapshot.Of(Msft, new Money(100m)), Policy, Now))
            .ShouldContain("available");
    }

    [Fact]
    public void A_portfolio_that_cannot_be_valued_is_not_traded_against()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Msft, quantity: 1m, new Money(400m));

        RejectionOf(Engine.Evaluate(Order(1m), Signal(), portfolio, PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("MSFT");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_order_for_less_than_a_share_is_refused(decimal quantity)
    {
        RejectionOf(Engine.Evaluate(Order(quantity), Signal(), WithCash(), PriceSnapshot.Empty, Policy, Now))
            .ShouldContain("whole share");
    }

    [Fact]
    public void What_the_sizer_produced_is_what_the_gate_approves()
    {
        // The two derive the limits independently, so they must agree on a normal signal or
        // one of them is wrong.
        var portfolio = WithCash();
        var signal = Signal();

        var intent = Sizer.Size(signal, portfolio, PriceSnapshot.Empty, Policy);

        Engine.Evaluate(intent.ShouldBeOfType<OrderIntent.Buy>(), signal, portfolio, PriceSnapshot.Empty, Policy, Now)
            .ShouldBeOfType<RiskDecision.Approved>();
    }
}
