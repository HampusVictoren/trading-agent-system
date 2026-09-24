namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// One instrument's price, exactly as it arrives on the wire. Narrower than the fact sheet
/// the agents read: P/E and sector are for interpreting, and this is for arithmetic.
/// </summary>
/// <remarks>
/// Required members and Disallow, for the same reason the signal DTO has them: an answer
/// that is not this contract must fail loudly rather than deserialise into nulls. Unlike
/// the signal contract this one carries a currency, because a quote can be for an
/// instrument the engine does not price in dollars - and finding out is better than
/// assuming.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuoteDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    [JsonPropertyName("price")]
    public required decimal Price { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("as_of")]
    public required DateTimeOffset AsOf { get; init; }
}
