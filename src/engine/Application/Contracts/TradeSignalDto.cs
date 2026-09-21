namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// The agents' answer, exactly as it arrives on the wire. It carries a view and no amount:
/// the engine decides the size, which is what bounds the damage a prompt injection can do.
/// </summary>
/// <remarks>
/// Required members and Disallow for the same reason as InvestmentProposalDto: an answer
/// that is not this contract has to fail loudly rather than deserialise into nulls.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradeSignalDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    [JsonPropertyName("stance")]
    public required string Stance { get; init; }

    [JsonPropertyName("conviction")]
    public required double Conviction { get; init; }

    [JsonPropertyName("thesis")]
    public required string Thesis { get; init; }

    [JsonPropertyName("key_risks")]
    public required IReadOnlyList<string> KeyRisks { get; init; }

    [JsonPropertyName("horizon_days")]
    public required int HorizonDays { get; init; }

    [JsonPropertyName("reference_price")]
    public required decimal ReferencePrice { get; init; }

    [JsonPropertyName("quote_as_of")]
    public required DateTimeOffset QuoteAsOf { get; init; }

    [JsonPropertyName("run")]
    public required RunDto Run { get; init; }
}

/// <summary>How the answer was produced. The engine stores it and decides on none of it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunDto
{
    [JsonPropertyName("team_id")]
    public required string TeamId { get; init; }

    [JsonPropertyName("team_version")]
    public required string TeamVersion { get; init; }

    [JsonPropertyName("revisions")]
    public required int Revisions { get; init; }
}
