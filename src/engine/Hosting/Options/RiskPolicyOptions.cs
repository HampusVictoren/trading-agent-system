namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;

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
}
