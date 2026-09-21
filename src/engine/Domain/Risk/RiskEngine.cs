namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;

public class RiskEngine
{
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
}
