namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// What the engine asks. It carries the limits, so the agents reason inside them instead of
/// against them - today they propose amounts far above the cap and RiskEngine rejects nearly
/// all of them.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradeSignalRequestDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    [JsonPropertyName("team_id")]
    public required string TeamId { get; init; }

    [JsonPropertyName("as_of")]
    public required DateTimeOffset AsOf { get; init; }

    /// <summary>What the portfolio already holds of this instrument, or null.</summary>
    [JsonPropertyName("existing_position")]
    public required ExistingPositionDto? ExistingPosition { get; init; }

    [JsonPropertyName("available_risk_budget")]
    public required decimal AvailableRiskBudget { get; init; }

    [JsonPropertyName("max_position_pct")]
    public required decimal MaxPositionPct { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExistingPositionDto
{
    [JsonPropertyName("quantity")]
    public required decimal Quantity { get; init; }

    [JsonPropertyName("average_price")]
    public required decimal AveragePrice { get; init; }
}
