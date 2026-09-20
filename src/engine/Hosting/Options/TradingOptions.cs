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

    public TimeSpan CycleInterval => TimeSpan.FromSeconds(CycleIntervalSeconds);
}
