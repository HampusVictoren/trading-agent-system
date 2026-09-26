using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Risk;

/// <summary>
/// The only component that decides how much money moves. Every rule below is a row rather
/// than prose, because this is where a mistake is expensive and where a bug is cheap to
/// pin: the inputs are numbers and the output is a quantity.
///
/// Throughout: net asset value 10,000 USD, a 5% position limit (500) and a 10% cash buffer
/// (1,000), at a price of 100 unless a test says otherwise.
/// </summary>
public class PositionSizerTests
{
    private static readonly Ticker Aapl = new("AAPL");
    private static readonly Ticker Msft = new("MSFT");
    private static readonly RiskPolicy Policy = new(maxPositionPct: 0.05m, cashBufferPct: 0.10m, maxQuoteAge: TimeSpan.FromMinutes(5));
    private static readonly PositionSizer Sizer = new();

    private static Portfolio WithCash(decimal cash = 10_000m) => new(new Money(cash, Money.DefaultCurrency));

    private static TradeSignal Signal(
        Stance stance = Stance.Buy, double conviction = 0.8, decimal price = 100m, Ticker? about = null) =>
        new(
            new Instrument.Equity(about ?? Aapl),
            stance,
            new Conviction(conviction),
            "thesis",
            ["a risk"],
            HorizonDays: 5,
            new Money(price, Money.DefaultCurrency),
            DateTimeOffset.UtcNow,
            new RunMetadata("default", "v1", Revisions: 0));

    private static decimal QuantityOf(OrderIntent intent) =>
        intent.ShouldBeOfType<OrderIntent.Buy>().Quantity;

    private static string ReasonOf(OrderIntent intent) =>
        intent.ShouldBeOfType<OrderIntent.None>().Reason;

    [Fact]
    public void Full_conviction_spends_the_whole_position_limit()
    {
        // 5% of 10,000 is 500, and 500 buys five shares at 100.
        QuantityOf(Sizer.Size(Signal(conviction: 0.8), WithCash(), PriceSnapshot.Empty, Policy))
            .ShouldBe(5m);
    }

    [Fact]
    public void Half_conviction_spends_half_of_it()
    {
        // 250 buys two and a half shares, and half a share is not a thing.
        QuantityOf(Sizer.Size(Signal(conviction: 0.5), WithCash(), PriceSnapshot.Empty, Policy))
            .ShouldBe(2m);
    }

    [Theory]
    [InlineData(0.39, 0)]
    [InlineData(0.40, 2)]
    [InlineData(0.70, 2)]
    [InlineData(0.71, 5)]
    public void The_tier_boundaries_are_where_they_are_written(double conviction, decimal expected)
    {
        // Spelled out because "0.4-0.7 is half" leaves both ends ambiguous, and a boundary
        // is exactly where a sizing bug hides.
        var intent = Sizer.Size(Signal(conviction: conviction), WithCash(), PriceSnapshot.Empty, Policy);

        if (expected == 0m)
            intent.ShouldBeOfType<OrderIntent.None>();
        else
            QuantityOf(intent).ShouldBe(expected);
    }

    [Fact]
    public void A_conviction_below_the_floor_buys_nothing()
    {
        ReasonOf(Sizer.Size(Signal(conviction: 0.3), WithCash(), PriceSnapshot.Empty, Policy))
            .ShouldContain("conviction");
    }

    [Fact]
    public void A_position_already_at_the_limit_gets_nothing_more()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 5m, new Money(100m));

        // Net asset value is still 10,000, but all 500 of the allowance is used, so the
        // budget is exactly zero rather than merely small.
        ReasonOf(Sizer.Size(Signal(), portfolio, PriceSnapshot.Empty, Policy))
            .ShouldContain("does not reach one share");
    }

    [Fact]
    public void A_partly_filled_position_gets_only_the_remainder()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));

        // 500 of allowance less 200 already held leaves 300, which is three shares.
        QuantityOf(Sizer.Size(Signal(), portfolio, PriceSnapshot.Empty, Policy)).ShouldBe(3m);
    }

    [Fact]
    public void Sizing_is_measured_against_net_asset_value_not_against_cash()
    {
        // The bug this stage exists to prevent. Cash is 1,300 of a 10,000 portfolio, so a
        // limit measured against cash would allow 65. Against net asset value it is 500 -
        // and here the cash buffer binds first anyway, at 300.
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Msft, quantity: 87m, new Money(100m));

        var intent = Sizer.Size(Signal(), portfolio, PriceSnapshot.Of(Msft, new Money(100m)), Policy);

        QuantityOf(intent).ShouldBe(3m);
    }

    [Fact]
    public void The_cash_buffer_is_never_spent()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Msft, quantity: 88m, new Money(100m));

        var intent = Sizer.Size(Signal(), portfolio, PriceSnapshot.Of(Msft, new Money(100m)), Policy);

        // 1,200 cash less the 1,000 buffer leaves 200, so two shares - not the twelve that
        // spending the whole balance would have allowed. Stage 5's exits need that cash.
        QuantityOf(intent).ShouldBe(2m);
    }

    [Fact]
    public void A_budget_that_does_not_reach_one_share_buys_nothing()
    {
        // 500 of allowance at 600 a share. Rounding down is the only honest direction.
        ReasonOf(Sizer.Size(Signal(price: 600m), WithCash(), PriceSnapshot.Empty, Policy))
            .ShouldContain("one share");
    }

    [Theory]
    [InlineData(Stance.Sell)]
    [InlineData(Stance.Hold)]
    public void Only_a_buy_is_sized(Stance stance)
    {
        // Selling arrives in stage 5, with its own table.
        ReasonOf(Sizer.Size(Signal(stance: stance), WithCash(), PriceSnapshot.Empty, Policy))
            .ShouldContain(stance.ToString());
    }

    [Fact]
    public void A_portfolio_that_cannot_be_valued_is_not_sized_against()
    {
        // The engine cannot price a holding it is not analysing until stage 4 adds a quote
        // endpoint. Refusing is the point: valuing MSFT at what it cost would overstate a
        // loser and raise the limit exactly when the portfolio had shrunk.
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Msft, quantity: 1m, new Money(400m));

        ReasonOf(Sizer.Size(Signal(), portfolio, PriceSnapshot.Empty, Policy))
            .ShouldContain("MSFT");
    }

    [Fact]
    public void The_signals_own_price_is_used_even_if_the_caller_supplies_another()
    {
        // The caller passes prices for other holdings. Letting it override the instrument's
        // own price would size the order against a quote the agents never saw.
        var caller = PriceSnapshot.Of(Aapl, new Money(1m, Money.DefaultCurrency));

        QuantityOf(Sizer.Size(Signal(price: 100m), WithCash(), caller, Policy)).ShouldBe(5m);
    }
}
