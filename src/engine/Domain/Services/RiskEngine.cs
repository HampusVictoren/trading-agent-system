namespace Engine.Domain.Services;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Engine.Domain.Exceptions;

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
                $"Köporder för {ticker.Value} på ${intendedSpend.Amount} överskrider riskgränsen på 5% (${maxAllowedSpend}).");
        }

        if (intendedSpend.Amount > portfolio.CashBalance.Amount)
        {
            throw new RiskViolationException(
                $"Otillräckligt kassaflöde. Tillgängligt: ${portfolio.CashBalance.Amount}, Krävs: ${intendedSpend.Amount}.");
        }
    }
}
