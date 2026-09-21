namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// A discriminated union on "type". Only equity exists today; a derivative becomes a new
/// variant rather than a breaking change. An unknown discriminator is refused with a
/// JsonException, so the engine never guesses what it was asked to trade.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EquityInstrumentDto), "equity")]
public abstract record InstrumentDto;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquityInstrumentDto : InstrumentDto
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }
}
