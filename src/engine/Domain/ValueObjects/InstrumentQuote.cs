namespace Engine.Domain.ValueObjects;

/// <summary>
/// One instrument's price and when it was taken. The engine holds no market data of its
/// own; this is what the agent service's quote endpoint answers with, once it has been
/// checked.
/// </summary>
/// <remarks>
/// It carries its own timestamp because a valuation made on a stale price is worse than one
/// that could not be made at all: the position limit is a share of the portfolio's value,
/// so an out-of-date holding raises or lowers the allowance for everything else.
/// </remarks>
public sealed record InstrumentQuote(Ticker Ticker, Money Price, DateTimeOffset AsOf)
{
    /// <summary>
    /// A small allowance for the two services' clocks disagreeing, matching the one the risk
    /// engine already makes for a signal's quote.
    /// </summary>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether this price may be used at <paramref name="now"/>. A quote dated in the future
    /// is refused as well as an old one: it means somebody's clock is wrong, and a price
    /// from a wrong clock is not a price.
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now, TimeSpan maxAge) =>
        AsOf <= now + ClockSkewAllowance && now - AsOf <= maxAge;
}
