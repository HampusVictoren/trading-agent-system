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

    public RiskPolicy(decimal maxPositionPct, decimal cashBufferPct)
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

        MaxPositionPct = maxPositionPct;
        CashBufferPct = cashBufferPct;
    }
}
