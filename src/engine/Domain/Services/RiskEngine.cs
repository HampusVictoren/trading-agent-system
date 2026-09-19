namespace Engine.Domain.Services;

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
