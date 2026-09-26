using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Aggregates;

/// <summary>
/// Sizing is measured against net asset value, not against cash: a portfolio that is fully
/// invested still has a position limit. Valuing it needs a price for every holding, and the
/// engine has no way to fetch one for an instrument it is not currently analysing until
/// stage 4 adds a quote endpoint. So a missing price is an outcome, not an exception - the
/// portfolio says it cannot be valued rather than guessing at a number.
/// </summary>
public class PortfolioValuationTests
{
    private static readonly Ticker Aapl = new("AAPL");
    private static readonly Ticker Msft = new("MSFT");

    private static Portfolio WithCash(decimal cash = 10_000m) => new(new Money(cash, Money.DefaultCurrency));

    private static Money Nav(PortfolioValuation valuation) =>
        valuation.ShouldBeOfType<PortfolioValuation.Valued>().NetAssetValue;

    [Fact]
    public void A_portfolio_with_no_holdings_is_worth_its_cash()
    {
        Nav(WithCash(10_000m).Value(PriceSnapshot.Empty)).Amount.ShouldBe(10_000m);
    }

    [Fact]
    public void A_holding_is_valued_at_the_given_price_not_at_what_it_cost()
    {
        var portfolio = WithCash(10_000m);
        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));

        // 9,800 cash left, plus 2 shares now worth 150 each.
        Nav(portfolio.Value(PriceSnapshot.Of(Aapl, new Money(150m)))).Amount.ShouldBe(10_100m);
    }

    [Fact]
    public void Every_holding_counts()
    {
        var portfolio = WithCash(10_000m);
        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
        portfolio.ExecuteBuy(Msft, quantity: 1m, new Money(400m));

        var prices = PriceSnapshot.Of(Aapl, new Money(150m)).With(Msft, new Money(500m));

        // 9,400 cash, plus 300 of AAPL, plus 500 of MSFT.
        Nav(portfolio.Value(prices)).Amount.ShouldBe(10_200m);
    }

    [Fact]
    public void A_holding_without_a_price_makes_the_portfolio_unvaluable()
    {
        // The alternative - valuing it at what it cost - overstates a loser, which would
        // raise the position limit exactly when the portfolio had shrunk.
        var portfolio = WithCash(10_000m);
        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
        portfolio.ExecuteBuy(Msft, quantity: 1m, new Money(400m));

        var valuation = portfolio.Value(PriceSnapshot.Of(Aapl, new Money(150m)));

        valuation.ShouldBeOfType<PortfolioValuation.PriceMissing>().Ticker.ShouldBe(Msft);
    }

    [Fact]
    public void A_price_in_another_currency_is_a_bug_not_an_outcome()
    {
        var portfolio = WithCash(10_000m);
        portfolio.ExecuteBuy(Aapl, quantity: 1m, new Money(100m));

        Should.Throw<CurrencyMismatchException>(
            () => portfolio.Value(PriceSnapshot.Of(Aapl, new Money(150m, "EUR"))));
    }

    [Fact]
    public void The_market_value_of_something_not_held_is_zero()
    {
        // The sizer subtracts this from the position headroom, so a first buy gets the
        // whole cap rather than a special case.
        WithCash().MarketValueOf(Aapl, new Money(150m)).Amount.ShouldBe(0m);
    }

    [Fact]
    public void The_market_value_of_a_holding_is_the_quantity_at_the_given_price()
    {
        var portfolio = WithCash();
        portfolio.ExecuteBuy(Aapl, quantity: 3m, new Money(100m));

        portfolio.MarketValueOf(Aapl, new Money(150m)).Amount.ShouldBe(450m);
    }
}
