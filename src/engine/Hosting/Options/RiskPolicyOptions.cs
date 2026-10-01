namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;
using Engine.Domain.Risk;

/// <summary>The hard limits the engine enforces on every proposal.</summary>
public sealed class RiskPolicyOptions
{
    public const string SectionName = "RiskPolicy";

    /// <summary>
    /// The largest share of the portfolio a single buy may spend. No default on purpose:
    /// a mistyped section must stop the service, not quietly run on a guessed risk limit.
    /// </summary>
    [Range(typeof(decimal), "0.0001", "1.0", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal MaxPositionPercentage { get; init; }

    /// <summary>
    /// The share of net asset value that is never spent. Stage 5 adds deterministic exits and
    /// a minimum holding period, and a portfolio with no cash cannot act on either. One is not
    /// allowed, since it would reserve everything and never let anything be bought.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. Every other setting is caught when it is missing because its
    /// default of zero is outside the allowed range - but zero is a legitimate cash buffer,
    /// so "absent" and "none" would otherwise be the same thing. Required distinguishes them.
    /// </remarks>
    [Required]
    [Range(typeof(decimal), "0.0", "0.9999", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? CashBufferPct { get; init; }

    /// <summary>
    /// How stale the quote a signal was formed on may be when the engine acts on it. Five
    /// minutes is generous against a cycle of seconds: it catches a hung service or a cached
    /// quote rather than ordinary delay. Stage 4 measures real quote ages and can tighten it.
    /// </summary>
    [Range(1, 3600)]
    public int MaxQuoteAgeSeconds { get; init; }

    /// <summary>
    /// How many days a holding is kept before the agents may sell it on a new opinion. The
    /// deterministic exits are exempt, so this slows down changes of mind rather than risk
    /// controls.
    /// </summary>
    /// <remarks>
    /// Nullable and required for the same reason as CashBufferPct: zero is a legitimate value -
    /// it means the agents may reverse a purchase at once - so a missing key would otherwise be
    /// indistinguishable from choosing not to wait.
    ///
    /// The ceiling is the contract's own horizon cap. A minimum hold longer than the longest
    /// thesis anyone may propose would mean no position could ever be sold on a new opinion
    /// within the life of the thesis that bought it, which is a setting that cannot be what
    /// somebody meant.
    /// </remarks>
    [Required]
    [Range(0, 30)]
    public int? MinHoldingPeriodDays { get; init; }

    /// <summary>
    /// How far below what it cost a holding may fall before the engine sells it without asking.
    /// </summary>
    /// <remarks>
    /// Not nullable, unlike the two above, because zero is not a value anybody could mean: it
    /// would sell every holding that was not up. So the default of zero being outside the range
    /// is exactly the check a missing key needs. The ceiling is half, above which a stop-loss
    /// would let a position halve before acting and would not be one.
    /// </remarks>
    [Range(typeof(decimal), "0.01", "0.5", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal StopLossPercentage { get; init; }

    /// <summary>
    /// The most of the portfolio's value the engine may spend on purchases in one trading day.
    /// </summary>
    /// <remarks>
    /// The range below is only a share of a portfolio: "at least <see cref="MaxPositionPercentage"/>"
    /// is a rule about two settings at once, which a data annotation cannot express, so
    /// <see cref="RiskPolicyOptionsValidator"/> carries it and <c>ValidateOnStart</c> is what makes
    /// it a startup failure. <see cref="RiskPolicy"/> refuses the same combination a third time,
    /// because the domain does not trust that configuration was validated.
    /// </remarks>
    [Range(typeof(decimal), "0.01", "1.0", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal MaxDailyDeploymentPercentage { get; init; }

    /// <summary>The same limits in the domain's own terms, which guards them a second time.</summary>
    public RiskPolicy ToRiskPolicy() =>
        new(
            MaxPositionPercentage,
            CashBufferPct!.Value,
            TimeSpan.FromSeconds(MaxQuoteAgeSeconds),
            TimeSpan.FromDays(MinHoldingPeriodDays!.Value),
            StopLossPercentage,
            MaxDailyDeploymentPercentage);
}
