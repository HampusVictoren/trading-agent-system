namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// An instrument's closes, exactly as they arrive on the wire. Required members and
/// Disallow for the same reason as every other DTO here: an answer that is not the contract
/// must fail loudly rather than deserialise into nulls.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    /// <summary>Oldest first. An empty array is a valid answer: it means nothing has traded
    /// since the date asked about.</summary>
    [JsonPropertyName("bars")]
    public required IReadOnlyList<BarDto> Bars { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BarDto
{
    [JsonPropertyName("on")]
    public required DateOnly On { get; init; }

    [JsonPropertyName("close")]
    public required decimal Close { get; init; }
}
