namespace Engine.Domain.Risk;

/// <summary>
/// The hard limits sizing works within, in the domain's own terms. It is built from
/// RiskPolicyOptions at startup rather than read from configuration here: the domain should
/// not know where a number came from, and it should not trust that it was validated either.
/// </summary>
public sealed record RiskPolicy
{
    /// <summary>The largest share of net asset value one position may take.</summary>
    public decimal MaxPositionPct { get; }

    /// <summary>
    /// The share of net asset value that is never spent. Stage 5 adds deterministic exits
    /// and a minimum holding period; a portfolio with no cash cannot act on either.
    /// </summary>
    public decimal CashBufferPct { get; }

    /// <summary>
    /// How stale the quote a signal was formed on may be when the engine acts on it. The
    /// agents fetch a price, spend twelve to fifteen seconds reasoning, and the engine acts
    /// after that - so some age is normal. This bounds it.
    /// </summary>
    public TimeSpan MaxQuoteAge { get; }

    /// <summary>
    /// How long a holding is kept before the agents are allowed to sell it on a new opinion.
    /// </summary>
    /// <remarks>
    /// A churn guard, not a conviction about markets. A cycle runs every minute or so, and the
    /// same model given the same fact sheet answered HOLD at 0.50 and then BUY at 0.60 two
    /// minutes later - so without this, one instrument could be bought and sold several times
    /// in an afternoon on noise, and the commission on every leg would be the only certain
    /// outcome of it. It is measured in calendar time from the last purchase, because that is
    /// what a clock gives; counting trading days here would be borrowing a measurement rule to
    /// answer a question about how fast the system may change its mind.
    ///
    /// A stop-loss and a time limit are exempt, and that is the whole reason an order carries a
    /// trigger. They are not new opinions - they are what was agreed at the purchase - and a
    /// minimum hold that blocked a stop-loss would turn a risk control into a waiting period.
    /// </remarks>
    public TimeSpan MinHoldingPeriod { get; }

    /// <summary>
    /// How far a holding may fall below what it cost before it is sold without asking anyone.
    /// </summary>
    /// <remarks>
    /// Measured against the average purchase price rather than the highest price since the
    /// purchase, which is the other obvious choice. A trailing stop needs a high-water mark, and
    /// the position does not have one: keeping it would mean a column updated every cycle from a
    /// price the engine does not always manage to fetch, so a missed quote would quietly lower
    /// the mark and the stop with it. The purchase price is a number the portfolio already knows
    /// exactly, and it answers the question the thesis actually made: the shares were bought
    /// because they were expected to rise, and they have not.
    ///
    /// Ten percent is set against how much these instruments move rather than against a feeling
    /// about losses. Swedish large caps run at roughly 25-30 % annualised volatility, which is
    /// about 1.8 % a day, so a two-sigma week is an eight percent move: a tighter stop would
    /// mostly sell noise, and a looser one would not be a stop.
    /// </remarks>
    public decimal StopLossPct { get; }

    public RiskPolicy(
        decimal maxPositionPct,
        decimal cashBufferPct,
        TimeSpan maxQuoteAge,
        TimeSpan minHoldingPeriod,
        decimal stopLossPct)
    {
        if (maxPositionPct <= 0m || maxPositionPct > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPositionPct), maxPositionPct, "A position limit must be a share above 0 and at most 1.");
        }

        if (cashBufferPct < 0m || cashBufferPct >= 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cashBufferPct), cashBufferPct, "A cash buffer must be a share from 0 up to but not including 1.");
        }

        if (maxQuoteAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxQuoteAge), maxQuoteAge, "A quote age limit must be a positive period.");
        }

        // Zero is a real answer - it means the agents may reverse a purchase immediately - so it
        // is allowed, and only a negative period is a mistake. There is no upper bound here:
        // "longer than any thesis's horizon" is a rule about the contract, and the domain does
        // not know the contract. RiskPolicyOptions does, and enforces it at startup.
        if (minHoldingPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minHoldingPeriod), minHoldingPeriod, "A minimum holding period cannot be negative.");
        }

        // Neither end is a stop. At zero every holding that is not up would be sold the moment
        // it was priced; at one the price would have to reach zero first.
        if (stopLossPct <= 0m || stopLossPct >= 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopLossPct), stopLossPct, "A stop-loss must be a share above 0 and below 1.");
        }

        MaxPositionPct = maxPositionPct;
        CashBufferPct = cashBufferPct;
        MaxQuoteAge = maxQuoteAge;
        MinHoldingPeriod = minHoldingPeriod;
        StopLossPct = stopLossPct;
    }
}
