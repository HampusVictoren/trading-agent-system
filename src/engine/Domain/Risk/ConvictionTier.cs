namespace Engine.Domain.Risk;

using Engine.Domain.Signals;

/// <summary>
/// How much of the available budget a conviction is allowed to use: none, half, or all of it.
/// </summary>
/// <remarks>
/// Deliberately discrete. An LLM's conviction is not calibrated - 0.8 does not mean it is
/// right eight times in ten - so scaling an amount by it linearly reads a precision that is
/// not there. Three tiers say "not worth acting on", "worth a starter position" and "worth
/// the full allowance", which is as much as the number can honestly support.
///
/// Stage 8 revisits the thresholds against measured outcomes. Until there are outcomes,
/// moving them would be guessing, so they are code rather than configuration.
/// </remarks>
public static class ConvictionTier
{
    /// <summary>Below this, no order at all.</summary>
    public const double FloorExclusive = 0.4;

    /// <summary>Above this, the full allowance. At it or below, half.</summary>
    public const double FullAbove = 0.7;

    public static decimal From(Conviction conviction) => conviction.Value switch
    {
        < FloorExclusive => 0m,
        <= FullAbove => 0.5m,
        _ => 1m
    };
}
