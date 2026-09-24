namespace Engine.Domain.Outcomes;

/// <summary>
/// What a result is scored against, in the domain's own terms. Deliberately separate from
/// <see cref="Risk.RiskPolicy"/>: that says what may be traded, this says how what was
/// traded is judged, and a number that changes a measurement should not sit among numbers
/// that change a decision.
/// </summary>
/// <remarks>
/// Finding F: an outcome function that ignores commission and spread will say a strategy
/// works when it does not, and over horizons of a few days that cost is often the whole
/// difference between a positive and a negative edge. Built from options at startup, the
/// same way <see cref="Risk.RiskPolicy"/> is - the domain should not know where a number
/// came from, and should not trust that it was validated either.
/// </remarks>
public sealed record OutcomePolicy
{
    /// <summary>Broker commission in basis points of notional, <em>per side</em>.</summary>
    public decimal CommissionBps { get; }

    /// <summary>
    /// What crossing the spread costs in basis points, <em>per side</em>. With the
    /// commission this is one leg of a trade; a round trip is charged twice.
    /// </summary>
    public decimal SpreadBps { get; }

    /// <summary>
    /// How far an instrument may drift from the benchmark before a HOLD counts as wrong, as
    /// a fraction. The roadmap suggests 0.02.
    /// </summary>
    public decimal HoldBandPct { get; }

    public OutcomePolicy(decimal commissionBps, decimal spreadBps, decimal holdBandPct)
    {
        if (commissionBps < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commissionBps), commissionBps, "A cost cannot be negative.");
        }

        if (spreadBps < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spreadBps), spreadBps, "A cost cannot be negative.");
        }

        if (holdBandPct is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdBandPct), holdBandPct, "A band must be a share from 0 to 1.");
        }

        CommissionBps = commissionBps;
        SpreadBps = spreadBps;
        HoldBandPct = holdBandPct;
    }

    /// <summary>
    /// What a round trip costs, as a fraction of notional. Both legs, because a view that is
    /// acted on is a position that is opened and later closed.
    /// </summary>
    public decimal RoundTripFraction => 2m * (CommissionBps + SpreadBps) / 10_000m;
}
