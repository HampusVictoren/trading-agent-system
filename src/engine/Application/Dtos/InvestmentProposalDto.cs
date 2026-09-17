namespace Engine.Application.Dtos;

using System.Text.Json.Serialization;

public record InvestmentProposalDto(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reasoning")] string Reasoning
);
