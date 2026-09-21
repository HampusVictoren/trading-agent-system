namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

public class RiskEngine
{
    /// <summary>Both services run on one machine, so this only absorbs sub-second drift.</summary>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    private readonly decimal _maxPositionPercentage;

    public RiskEngine(decimal maxPositionPercentage = 0.05m)
    {
        _maxPositionPercentage = maxPositionPercentage;
    }

    /// <summary>
    /// The old path's risk check. It throws, which is why a rejection reaches the worker as
    /// an exception. Stage 3 deletes it along with the old endpoint; Evaluate replaces it.
    /// </summary>
    public void ValidateTrade(Portfolio portfolio, Ticker ticker, Money intendedSpend, Money totalPortfolioValue)
    {
        var maxAllowedSpend = totalPortfolioValue.Amount * _maxPositionPercentage;

        if (intendedSpend.Amount > maxAllowedSpend)
        {
            throw new RiskViolationException(
                $"Buy order for {ticker.Value} of ${intendedSpend.Amount} exceeds the 5% risk limit (${maxAllowedSpend}).");
        }

        if (intendedSpend.Amount > portfolio.CashBalance.Amount)
        {
            throw new RiskViolationException(
                $"Insufficient cash. Available: ${portfolio.CashBalance.Amount}, required: ${intendedSpend.Amount}.");
        }
    }

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
        // quote_as_of comes from the agent service, so a future date would sail past a plain
        // age check and make every quote look fresh for ever.
        if (signal.QuoteAsOf > now + ClockSkewAllowance)
            return new RiskDecision.Rejected($"the quote is dated {signal.QuoteAsOf:O}, in the future");

        var age = now - signal.QuoteAsOf;
        if (age > policy.MaxQuoteAge)
        {
            return new RiskDecision.Rejected(
                $"the quote is {age.TotalSeconds:F0} s old, past the {policy.MaxQuoteAge.TotalSeconds:F0} s limit");
        }

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
}
