using System.Text.Json;
using Engine.Application.Dtos;
using Shouldly;

namespace Engine.Tests.Application.Dtos;

/// <summary>
/// The DTO is one half of the cross-service contract. It used to accept a body that
/// honoured none of it: System.Text.Json fills a missing member with null however
/// non-nullable the property is, so `action` became null, the engine logged "No action"
/// and said nothing was wrong - forever. Stage 3 replaces action/amount_usd with
/// stance/conviction, so a contract break has to be loud before that migration, not after.
/// </summary>
public class InvestmentProposalDtoTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private const string Complete =
        """{"ticker":"AAPL","action":"BUY","amount_usd":400,"confidence":0.8,"reasoning":"because"}""";

    private static InvestmentProposalDto? Deserialise(string json) =>
        JsonSerializer.Deserialize<InvestmentProposalDto>(json, Options);

    [Fact]
    public void An_answer_that_honours_the_contract_is_accepted()
    {
        var proposal = Deserialise(Complete);

        proposal!.Ticker.ShouldBe("AAPL");
        proposal.Action.ShouldBe("BUY");
        proposal.AmountUsd.ShouldBe(400m);
        proposal.Confidence.ShouldBe(0.8);
        proposal.Reasoning.ShouldBe("because");
    }

    [Theory]
    [InlineData("ticker")]
    [InlineData("action")]
    [InlineData("amount_usd")]
    [InlineData("confidence")]
    [InlineData("reasoning")]
    public void A_missing_field_is_refused(string field)
    {
        var withoutField = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Complete)!
                .Where(pair => pair.Key != field)
                .ToDictionary(pair => pair.Key, pair => pair.Value));

        Should.Throw<JsonException>(() => Deserialise(withoutField));
    }

    [Fact]
    public void An_empty_object_is_refused()
    {
        Should.Throw<JsonException>(() => Deserialise("{}"));
    }

    [Fact]
    public void A_field_the_engine_does_not_know_is_refused()
    {
        // An added field means the contract moved without the engine being told. Both
        // services live in one repository and merge together, so that is a mistake.
        var withExtra =
            """{"ticker":"AAPL","action":"BUY","amount_usd":400,"confidence":0.8,"reasoning":"x","sentiment":0.7}""";

        Should.Throw<JsonException>(() => Deserialise(withExtra));
    }

    [Fact]
    public void The_contract_planned_for_stage_3_is_refused_until_the_engine_knows_it()
    {
        // This is the migration the finding was about: if the agent service ships the new
        // shape first, the engine must say so rather than log "No action" for ever.
        var stageThreeShape =
            """{"instrument":{"type":"equity","symbol":"AAPL"},"stance":"BUY","conviction":0.8}""";

        Should.Throw<JsonException>(() => Deserialise(stageThreeShape));
    }
}
