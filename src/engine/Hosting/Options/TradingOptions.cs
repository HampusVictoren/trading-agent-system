namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>What the worker trades, and how often it looks.</summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    [Required]
    [MinLength(1)]
    public string[] Tickers { get; init; } = [];

    [Range(1, 3600)]
    public int CycleIntervalSeconds { get; init; }

    /// <summary>
    /// Which team setup the agent service should run. Configuration rather than code, because
    /// that is what makes the experiment cycle an experiment: change the team, let it run, and
    /// compare outcomes per team_version in stage 4. An id the service does not know is a 422.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string TeamId { get; init; } = string.Empty;

    public TimeSpan CycleInterval => TimeSpan.FromSeconds(CycleIntervalSeconds);
}
