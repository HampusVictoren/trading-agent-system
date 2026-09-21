namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// Turns a view into a quantity. This is the only place that decides how much money moves,
/// which is what keeps a prompt injection from becoming a large order: the agents supply a
/// direction and a conviction, never an amount.
/// </summary>
public sealed class PositionSizer
{
    /// <summary>
    /// Sizes one signal. <paramref name="holdingPrices"/> is what the caller knows about the
    /// portfolio's *other* holdings; the price for the signal's own instrument always comes
    /// from the signal, so an order cannot be sized against a quote the agents never saw.
    /// </summary>
    public OrderIntent Size(
        TradeSignal signal, Portfolio portfolio, PriceSnapshot holdingPrices, RiskPolicy policy)
    {
        if (signal.Stance != Stance.Buy)
            return new OrderIntent.None(signal.Instrument, $"the stance is {signal.Stance}");

        // Unreachable while equity is the only variant, but a variant added without a case
        // here should refuse rather than be sized as if it were a share.
        if (signal.Instrument is not Instrument.Equity equity)
            return new OrderIntent.None(signal.Instrument, "the engine only trades equities");

        var price = signal.ReferencePrice;
        var valuation = portfolio.Value(holdingPrices.With(equity.Ticker, price));

        if (valuation is not PortfolioValuation.Valued valued)
        {
            var missing = ((PortfolioValuation.PriceMissing)valuation).Ticker;
            return new OrderIntent.None(
                signal.Instrument, $"the portfolio cannot be valued: no price for {missing.Value}");
        }

        var tier = ConvictionTier.From(signal.Conviction);
        if (tier == 0m)
        {
            return new OrderIntent.None(
                signal.Instrument, $"the conviction {signal.Conviction.Value} is below the floor");
        }

        // The limit is a share of net asset value, not of cash: a portfolio that is fully
        // invested still has a position limit, and measuring against cash would let a single
        // holding grow without bound as long as cash kept coming in.
        var headroom = valued.NetAssetValue
            .Multiply(policy.MaxPositionPct)
            .Subtract(portfolio.MarketValueOf(equity.Ticker, price));

        var spendable = portfolio.CashBalance.Subtract(valued.NetAssetValue.Multiply(policy.CashBufferPct));

        // Whichever limit binds first is the one that applies.
        var budget = Money.Min(headroom, spendable).Multiply(tier);
        var quantity = decimal.Floor(budget.Amount / price.Amount);

        if (quantity < 1m)
        {
            return new OrderIntent.None(
                signal.Instrument,
                $"the budget of {budget.Amount} {budget.Currency} does not reach one share at {price.Amount}");
        }

        return new OrderIntent.Buy(signal.Instrument, quantity, price);
    }
}
