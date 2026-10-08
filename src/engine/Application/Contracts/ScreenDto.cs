namespace Engine.Application.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// What the engine posts to <c>POST /v1/screen</c>. Every policy figure travels with the
/// request rather than living in the agent service's configuration, so the answer depends on
/// nothing the caller cannot see - and a stored shortlist is reproducible from the request
/// that produced it.
/// </summary>
/// <remarks>
/// The universe is here rather than there because the engine owns what it trades. It is the
/// same reasoning as decision 1 one level out: the agent service ranks, the engine decides
/// what is in scope to be ranked.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScreenRequestDto
{
    [JsonPropertyName("universe")]
    public required IReadOnlyList<InstrumentDto> Universe { get; init; }

    /// <summary>How many candidates to return at most, which bounds the cycle's LLM cost.</summary>
    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    /// <summary>The liquidity floor, as typical daily turnover in the instrument's own currency.</summary>
    [JsonPropertyName("min_dollar_volume")]
    public required decimal MinDollarVolume { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }
}

/// <summary>
/// What <c>POST /v1/screen</c> answers, exactly as it arrives on the wire. Required members
/// and Disallow for the same reason as every other DTO here: an answer that is not the
/// contract must fail loudly rather than deserialise into nulls.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScreenResultDto
{
    /// <summary>Best first. Empty is a valid answer: a screen that finds nothing is a fact
    /// about the universe today rather than a failure.</summary>
    [JsonPropertyName("candidates")]
    public required IReadOnlyList<CandidateDto> Candidates { get; init; }

    [JsonPropertyName("rejected")]
    public required IReadOnlyList<RejectionDto> Rejected { get; init; }

    [JsonPropertyName("as_of")]
    public required DateTimeOffset AsOf { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    [JsonPropertyName("score")]
    public required decimal Score { get; init; }

    [JsonPropertyName("return_3m")]
    public required decimal Return3M { get; init; }

    [JsonPropertyName("volatility_30d")]
    public required decimal Volatility30D { get; init; }

    [JsonPropertyName("median_dollar_volume")]
    public required decimal MedianDollarVolume { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RejectionDto
{
    [JsonPropertyName("instrument")]
    public required InstrumentDto Instrument { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
