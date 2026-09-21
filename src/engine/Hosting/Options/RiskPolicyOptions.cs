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

    /// <summary>The same limits in the domain's own terms, which guards them a second time.</summary>
    public RiskPolicy ToRiskPolicy() =>
        new(MaxPositionPercentage, CashBufferPct!.Value, TimeSpan.FromSeconds(MaxQuoteAgeSeconds));
}
