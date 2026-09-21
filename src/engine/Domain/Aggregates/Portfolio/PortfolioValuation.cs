namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.ValueObjects;

/// <summary>
/// What a portfolio is worth, or why it cannot be said. A closed hierarchy: the private
/// constructor keeps both cases in this file.
/// </summary>
public abstract record PortfolioValuation
{
    private PortfolioValuation() { }

    /// <summary>Cash plus the market value of every holding.</summary>
    public sealed record Valued(Money NetAssetValue) : PortfolioValuation;

    /// <summary>
    /// A holding had no price, so there is no honest number. Valuing it at what it cost
    /// would overstate a loser, which would raise the position limit exactly when the
    /// portfolio had shrunk.
    /// </summary>
    public sealed record PriceMissing(Ticker Ticker) : PortfolioValuation;
}
