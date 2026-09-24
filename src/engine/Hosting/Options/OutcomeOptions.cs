namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;
using Engine.Domain.Outcomes;

/// <summary>
/// How a decision is scored once the market has answered. Its own section rather than part
/// of <c>RiskPolicy</c>: that one says what may be traded, this one says how what was traded
/// is judged, and a number that changes a measurement should not sit among numbers that
/// change a decision.
/// </summary>
public sealed class OutcomeOptions
{
    public const string SectionName = "Outcome";

    /// <summary>
    /// Broker commission in basis points of notional, per side.
    /// </summary>
    /// <remarks>
    /// Nullable and required, for the reason <c>CashBufferPct</c> is: zero commission is a
    /// real arrangement, so "absent" and "none" would otherwise be the same value. The
    /// figure here is a plausible one for a liquid US large cap and nothing more - it is the
    /// first thing to replace with a real fee schedule when there is a broker.
    /// </remarks>
    [Required]
    [Range(typeof(decimal), "0.0", "500.0", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? CommissionBps { get; init; }

    /// <summary>What crossing the spread costs, in basis points, per side.</summary>
    [Required]
    [Range(typeof(decimal), "0.0", "500.0", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? SpreadBps { get; init; }

    /// <summary>
    /// How far an instrument may drift from the benchmark before a HOLD counts as wrong.
    /// A band of zero would mean a HOLD is only ever right when the deviation is exactly
    /// nothing, so the lower bound also catches a missing setting.
    /// </summary>
    [Range(typeof(decimal), "0.0001", "1.0", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal HoldBandPct { get; init; }

    /// <summary>
    /// What every signal is compared against. A result with no benchmark says the market
    /// went up, not that the agents were right.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string BenchmarkSymbol { get; init; } = string.Empty;

    /// <summary>
    /// The horizons every signal is measured at whatever it asked for, in trading days.
    /// The model picks its own horizon, so two signals are rarely comparable directly -
    /// these are what make two team versions comparable at all.
    /// </summary>
    [Required]
    [MinLength(1)]
    public int[] FixedHorizonTradingDays { get; init; } = [];

    /// <summary>The same figures in the domain's own terms, which guards them a second time.</summary>
    public OutcomePolicy ToOutcomePolicy() =>
        new(CommissionBps!.Value, SpreadBps!.Value, HoldBandPct);

    /// <summary>The fixed horizons as the domain counts them.</summary>
    public IReadOnlyList<Horizon> FixedHorizons() =>
        [.. FixedHorizonTradingDays.Select(days => new Horizon.TradingDays(days))];
}
