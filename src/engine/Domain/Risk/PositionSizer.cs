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
    /// <remarks>
    /// The two directions share the stance check, the instrument check and the conviction
    /// floor, and nothing after that. A buy is bounded by what the portfolio can afford and by
    /// how much of it one holding may be; a sale is bounded only by what is held. That
    /// asymmetry is the design rather than a shortcut - see <see cref="SizeSell"/>.
    /// </remarks>
    /// <param name="deployedToday">
    /// What the account has already spent on purchases in the trading day being decided in. A
    /// sale ignores it: selling frees capital rather than committing it, and a daily cap that
    /// stopped an exit would be the opposite of a risk control.
    /// </param>
    public OrderIntent Size(
        TradeSignal signal,
        Portfolio portfolio,
        PriceSnapshot holdingPrices,
        RiskPolicy policy,
        Money deployedToday)
    {
        if (signal.Stance is not (Stance.Buy or Stance.Sell))
            return new OrderIntent.None(signal.Instrument, $"the stance is {signal.Stance}");

        // Unreachable while equity is the only variant, but a variant added without a case
        // here should refuse rather than be sized as if it were a share.
        if (signal.Instrument is not Instrument.Equity equity)
            return new OrderIntent.None(signal.Instrument, "the engine only trades equities");

        var tier = ConvictionTier.From(signal.Conviction);
        if (tier == 0m)
        {
            return new OrderIntent.None(
                signal.Instrument, $"the conviction {signal.Conviction.Value} is below the floor");
        }

        return signal.Stance == Stance.Buy
            ? SizeBuy(signal, equity, portfolio, holdingPrices, policy, tier, deployedToday)
            : SizeSell(signal, equity, portfolio, tier);
    }

    private static OrderIntent SizeBuy(
        TradeSignal signal,
        Instrument.Equity equity,
        Portfolio portfolio,
        PriceSnapshot holdingPrices,
        RiskPolicy policy,
        decimal tier,
        Money deployedToday)
    {
        var price = signal.ReferencePrice;
        var valuation = portfolio.Value(holdingPrices.With(equity.Ticker, price));

        if (valuation is not PortfolioValuation.Valued valued)
        {
            var missing = ((PortfolioValuation.PriceMissing)valuation).Ticker;
            return new OrderIntent.None(
                signal.Instrument, $"the portfolio cannot be valued: no price for {missing.Value}");
        }

        // The limit is a share of net asset value, not of cash: a portfolio that is fully
        // invested still has a position limit, and measuring against cash would let a single
        // holding grow without bound as long as cash kept coming in.
        var headroom = valued.NetAssetValue
            .Multiply(policy.MaxPositionPct)
            .Subtract(portfolio.MarketValueOf(equity.Ticker, price));

        var spendable = portfolio.CashBalance.Subtract(valued.NetAssetValue.Multiply(policy.CashBufferPct));

        // What is left of the trading day's own allowance. A third term in the same min rather
        // than a separate refusal, so an order shrinks against it exactly the way it already
        // shrinks against the cash buffer - and a day with nothing left simply sizes to nothing.
        var leftToday = valued.NetAssetValue
            .Multiply(policy.MaxDailyDeploymentPct)
            .Subtract(deployedToday);

        // Whichever limit binds first is the one that applies.
        var budget = Money.Min(Money.Min(headroom, spendable), leftToday).Multiply(tier);
        var quantity = decimal.Floor(budget.Amount / price.Amount);

        if (quantity < 1m)
        {
            return new OrderIntent.None(
                signal.Instrument,
                $"the budget of {budget.Amount} {budget.Currency} does not reach one share at {price.Amount}");
        }

        return new OrderIntent.Buy(signal.Instrument, quantity, price);
    }

    /// <summary>
    /// Sizes a sale as a share of the holding: the whole position above the full conviction
    /// threshold, half of it above the floor, nothing below.
    /// </summary>
    /// <remarks>
    /// The same tiers as a buy, on purpose. A conviction of 0.5 means the same thing whichever
    /// way it points - worth acting on, but not with everything - and a second set of
    /// thresholds for selling would be two sets of numbers to calibrate in stage 8 with
    /// exactly the same absence of evidence behind both.
    ///
    /// Nothing here values the portfolio, and that is deliberate rather than an omission. A
    /// sale needs no net asset value: there is no cash to check, because it raises cash, and no
    /// position limit to check, because it can only reduce one. Which means a holding whose
    /// quote could not be fetched cannot stop a sale - and the alternative is worse than it
    /// sounds, because a market data outage would then hold the portfolio in every position it
    /// had until the data came back, with the exits unable to fire for the same reason.
    /// </remarks>
    private static OrderIntent SizeSell(
        TradeSignal signal, Instrument.Equity equity, Portfolio portfolio, decimal fraction)
    {
        var held = portfolio.Positions.FirstOrDefault(position => position.Ticker == equity.Ticker);

        if (held is null)
        {
            return new OrderIntent.None(
                signal.Instrument, $"nothing is held of {equity.Ticker.Value}");
        }

        // Rounded down, like a buy, because a fraction of a share is not a thing anyone can
        // sell. A single share at half conviction therefore stays: the holding is smaller than
        // the smallest sale, and rounding up would let a moderate conviction do what only a
        // strong one is meant to do - close the position.
        var quantity = decimal.Floor(held.Quantity * fraction);

        if (quantity < 1m)
        {
            return new OrderIntent.None(
                signal.Instrument,
                $"{fraction} of the {held.Quantity} held of {equity.Ticker.Value} does not reach one share");
        }

        return new OrderIntent.Sell(
            signal.Instrument, quantity, signal.ReferencePrice, signal.QuoteAsOf, OrderTrigger.Signal);
    }
}
