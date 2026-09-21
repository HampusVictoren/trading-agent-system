namespace Engine.Application.Dtos;

using System.Text.Json.Serialization;

/// <summary>
/// The engine's half of the cross-service contract, mirroring InvestmentProposal in
/// src/agents/app/domain/models.py.
/// </summary>
/// <remarks>
/// Every member is <c>required</c> and unknown members are refused, so that an answer
/// which is not this contract fails loudly. Without that, System.Text.Json fills a
/// missing member with null however non-nullable the property is, and the engine logged
/// "No action" for ever instead of reporting a broken contract. Both services live in
/// one repository and merge together, so an added field is a mistake rather than a
/// version skew - and stage 3 replaces action/amount_usd, which is exactly when this
/// has to be loud. The resulting JsonException is translated by PythonAgentClient.
///
/// The properties are init-only rather than positional because <c>required</c> cannot be
/// put on a positional record's parameters.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record InvestmentProposalDto
{
    [JsonPropertyName("ticker")]
    public required string Ticker { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("amount_usd")]
    public required decimal AmountUsd { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("reasoning")]
    public required string Reasoning { get; init; }
}
