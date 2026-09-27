namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

public class RiskEngine
{
    /// <summary>Both services run on one machine, so this only absorbs sub-second drift.</summary>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The last gate before an order is placed. It re-derives the limits that
    /// <see cref="PositionSizer"/> already enforced rather than trusting them: this is the
    /// only code that moves money, and a sizing bug would have to be repeated here to get
    /// past both. It also checks what sizing cannot see - how old the quote is.
    /// </summary>
    /// <param name="now">Passed in rather than read from the clock, so the rule is testable.</param>
    public RiskDecision Evaluate(
        OrderIntent.Buy order,
        TradeSignal signal,
        Portfolio portfolio,
        PriceSnapshot holdingPrices,
        RiskPolicy policy,
        DateTimeOffset now)
    {
        if (PriceTooOld(signal.QuoteAsOf, policy, now) is RiskDecision.Rejected stale)
            return stale;

        if (order.Quantity < 1m)
            return new RiskDecision.Rejected($"the quantity {order.Quantity} is not a whole share");

        if (order.Instrument is not Instrument.Equity equity)
            return new RiskDecision.Rejected("the engine only trades equities");

        var valuation = portfolio.Value(holdingPrices.With(equity.Ticker, order.Price));
        if (valuation is not PortfolioValuation.Valued valued)
        {
            var missing = ((PortfolioValuation.PriceMissing)valuation).Ticker;
            return new RiskDecision.Rejected($"the portfolio cannot be valued: no price for {missing.Value}");
        }

        var cost = order.Price.Multiply(order.Quantity);
        var positionAfter = portfolio.MarketValueOf(equity.Ticker, order.Price).Add(cost);
        var limit = valued.NetAssetValue.Multiply(policy.MaxPositionPct);

        if (positionAfter.Amount > limit.Amount)
        {
            return new RiskDecision.Rejected(
                $"the position would reach {positionAfter.Amount}, past the limit of {limit.Amount}");
        }

        if (cost.Amount > portfolio.CashBalance.Amount)
        {
            return new RiskDecision.Rejected(
                $"the order costs {cost.Amount} and only {portfolio.CashBalance.Amount} is available");
        }

        return new RiskDecision.Approved();
    }

    /// <summary>
    /// The same gate for a sale. It takes no signal and no prices, because both of the things
    /// it checks are facts about the holding and the clock.
    /// </summary>
    /// <remarks>
    /// No signal, because a sale has two authors: the agents, and the deterministic exits that
    /// never asked them anything. The one thing the buy gate read a signal for - when the price
    /// was true - travels on the intent instead, so an exit does not have to invent a signal to
    /// be allowed through the same gate.
    ///
    /// No <see cref="PriceSnapshot"/>, because a sale needs no net asset value: there is no cash
    /// to check and no position limit that a reduction could breach. That keeps a missing quote
    /// for an unrelated holding from blocking an exit, which would be the worst possible moment
    /// for a market data outage to matter.
    ///
    /// What is left is what selling actually risks: selling shares that are not there, and
    /// abandoning a thesis so fast that the commission is the only thing the round trip
    /// achieved. The first is re-derived from the holding, the way the buy gate re-derives the
    /// position limit. The second is this gate's alone, like the price age, because sizing has
    /// no clock to measure it with.
    /// </remarks>
    public RiskDecision Evaluate(
        OrderIntent.Sell order,
        Portfolio portfolio,
        RiskPolicy policy,
        DateTimeOffset now)
    {
        if (PriceTooOld(order.PriceAsOf, policy, now) is RiskDecision.Rejected stale)
            return stale;

        if (order.Quantity < 1m)
            return new RiskDecision.Rejected($"the quantity {order.Quantity} is not a whole share");

        if (order.Instrument is not Instrument.Equity equity)
            return new RiskDecision.Rejected("the engine only trades equities");

        var held = portfolio.Positions.FirstOrDefault(position => position.Ticker == equity.Ticker);

        if (held is null)
            return new RiskDecision.Rejected($"nothing is held of {equity.Ticker.Value}");

        // No short selling. The domain refuses this too, but by throwing: by the time a
        // quantity reaches the aggregate it is a bug, and this is where it is still an outcome.
        if (order.Quantity > held.Quantity)
        {
            return new RiskDecision.Rejected(
                $"the order would sell {order.Quantity} of {equity.Ticker.Value} when {held.Quantity} is held");
        }

        // Only a sale the agents asked for waits. A stop-loss and a time limit are what was
        // agreed when the position was opened, not a change of mind, and a minimum hold that
        // blocked them would turn a risk control into a waiting period.
        if (order.Trigger == OrderTrigger.Signal)
        {
            var heldFor = now - held.LastPurchasedAt;

            if (heldFor < policy.MinHoldingPeriod)
            {
                return new RiskDecision.Rejected(
                    $"{equity.Ticker.Value} was bought {heldFor.TotalHours:F0} h ago, inside the "
                    + $"{policy.MinHoldingPeriod.TotalDays:F0} day minimum holding period");
            }
        }

        return new RiskDecision.Approved();
    }

    /// <summary>
    /// Whether the price an order was sized from is still worth acting on.
    /// </summary>
    /// <remarks>
    /// The future check is not paranoia about clocks: the timestamp comes from the agent
    /// service, and without it a future date would sail past a plain age check and make every
    /// price look fresh for ever.
    /// </remarks>
    private static RiskDecision? PriceTooOld(DateTimeOffset asOf, RiskPolicy policy, DateTimeOffset now)
    {
        if (asOf > now + ClockSkewAllowance)
            return new RiskDecision.Rejected($"the quote is dated {asOf:O}, in the future");

        var age = now - asOf;

        return age > policy.MaxQuoteAge
            ? new RiskDecision.Rejected(
                $"the quote is {age.TotalSeconds:F0} s old, past the {policy.MaxQuoteAge.TotalSeconds:F0} s limit")
            : null;
    }
}
