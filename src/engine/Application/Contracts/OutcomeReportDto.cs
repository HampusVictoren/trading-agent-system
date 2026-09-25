namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;
using Engine.Application.Persistence;

/// <summary>
/// What the engine posts to <c>POST /v1/outcomes</c>. See
/// <c>contracts/outcome.schema.json</c>, which both services read in their tests.
/// </summary>
/// <remarks>
/// The engine owns the measurement; this is a copy, sent so the agents' memory can say what
/// happened afterwards rather than only what was argued at the time. It goes over HTTP
/// because the database is never the integration point between the two services.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutcomeReportDto
{
    [JsonPropertyName("outcomes")]
    public required IReadOnlyList<MeasuredOutcomeDto> Outcomes { get; init; }
}

/// <summary>One signal at one horizon, as the contract spells it.</summary>
/// <remarks>
/// The two enums travel as the strings the engine already stores in
/// <c>trading.signal_outcomes</c> - <c>TradingDays</c>, <c>Measured</c> - rather than in the
/// contract's usual upper case. Two copies of one row that are spelled differently mean a
/// mapping in every head that ever compares the tables.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MeasuredOutcomeDto
{
    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("horizon_unit")]
    public required string HorizonUnit { get; init; }

    [JsonPropertyName("horizon_days")]
    public required int HorizonDays { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("benchmark_symbol")]
    public required string BenchmarkSymbol { get; init; }

    [JsonPropertyName("measured_on")]
    public DateOnly? MeasuredOn { get; init; }

    [JsonPropertyName("measured_price")]
    public decimal? MeasuredPrice { get; init; }

    [JsonPropertyName("instrument_return")]
    public decimal? InstrumentReturn { get; init; }

    [JsonPropertyName("benchmark_return")]
    public decimal? BenchmarkReturn { get; init; }

    [JsonPropertyName("excess_return")]
    public decimal? ExcessReturn { get; init; }

    [JsonPropertyName("cost_fraction")]
    public decimal? CostFraction { get; init; }

    [JsonPropertyName("net_edge")]
    public decimal? NetEdge { get; init; }

    [JsonPropertyName("hit")]
    public bool? Hit { get; init; }

    /// <summary>The stored measurement, in the shape the contract sends it.</summary>
    public static MeasuredOutcomeDto From(OutcomeAwaitingDelivery outcome) => new()
    {
        CorrelationId = outcome.CorrelationId,
        HorizonUnit = outcome.HorizonUnit.ToString(),
        HorizonDays = outcome.HorizonDays,
        Status = outcome.Status.ToString(),
        Reason = outcome.Reason,
        BenchmarkSymbol = outcome.BenchmarkSymbol,
        MeasuredOn = outcome.MeasuredOn,
        MeasuredPrice = outcome.MeasuredPrice,
        InstrumentReturn = outcome.InstrumentReturn,
        BenchmarkReturn = outcome.BenchmarkReturn,
        ExcessReturn = outcome.ExcessReturn,
        CostFraction = outcome.CostFraction,
        NetEdge = outcome.NetEdge,
        Hit = outcome.Hit
    };
}
