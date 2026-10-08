using System.Text.Json;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Domain.Screening;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// The screen half of the agreement, read from the checked-in files rather than from a copy.
/// </summary>
/// <remarks>
/// Nothing here moves money - a screen chooses what to ask about, not what to buy - so the
/// checks are about a different failure than the signal contract's. What a wrong answer costs
/// here is a cycle spent on the wrong instruments, or a stored shortlist that says the ranking
/// was something it was not, which is the record this stage's own question is answered from.
/// </remarks>
public class ScreenContractTests
{
    private static readonly string ContractsDirectory =
        Path.Combine(AppContext.BaseDirectory, "contracts");

    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(ContractsDirectory, "examples", name));

    private static ScreenResultDto? Parse(string json) =>
        JsonSerializer.Deserialize<ScreenResultDto>(json, ContractSerialization.Options);

    private static readonly IReadOnlySet<Ticker> ExampleUniverse = Universe(
        "NVDA", "AAPL", "MSFT", "TINY");

    private static IReadOnlySet<Ticker> Universe(params string[] symbols)
    {
        var set = new HashSet<Ticker>();
        foreach (var symbol in symbols)
        {
            if (!Ticker.TryCreate(symbol, out var ticker))
                throw new InvalidOperationException($"test setup: '{symbol}' is not a ticker");
            set.Add(ticker);
        }
        return set;
    }

    private static Screen Map(
        ScreenResultDto dto,
        IReadOnlySet<Ticker>? universe = null,
        int limit = 100) =>
        ScreenMapper.ToDomain(dto, universe ?? ExampleUniverse, limit);

    private static string AScreen(string candidates = "", string rejected = "") =>
        $$"""
          {"candidates":[{{candidates}}],"rejected":[{{rejected}}],
           "as_of":"2026-09-26T13:45:02.117Z"}
          """;

    private static string ACandidate(
        string symbol = "NVDA", string score = "1.421", string volatility = "0.2", string turnover = "41250000.0") =>
        $$"""
          {"instrument":{"type":"equity","symbol":"{{symbol}}"},"score":{{score}},
           "return_3m":0.2842,"volatility_30d":{{volatility}},"median_dollar_volume":{{turnover}}}
          """;

    private static string ARejection(string symbol = "TINY", string reason = "turnover 41000 is below the floor") =>
        $$"""
          {"instrument":{"type":"equity","symbol":"{{symbol}}"},"reason":"{{reason}}"}
          """;

    [Fact]
    public void The_checked_in_result_example_reads_into_the_engines_types()
    {
        var screen = Map(Parse(Example("screen-result.json"))!);

        screen.Candidates.Select(candidate => candidate.Ticker.Value).ShouldBe(["NVDA", "AAPL"]);
        screen.Candidates[0].Score.ShouldBe(1.421m);
        screen.Candidates[0].Return3M.ShouldBe(0.2842m);
        screen.Candidates[0].Volatility30D.ShouldBe(0.2m);
        screen.Candidates[0].MedianDollarVolume.ShouldBe(41250000.0m);

        screen.Rejected.Select(rejection => rejection.Ticker.Value).ShouldBe(["MSFT", "TINY"]);
        screen.Rejected[0].Reason.ShouldBe("no close from 90 days ago to compare against");

        screen.AsOf.ShouldBe(new DateTimeOffset(2026, 9, 26, 13, 45, 2, 117, TimeSpan.Zero));
    }

    [Fact]
    public void The_checked_in_request_example_reads_into_the_engines_types()
    {
        var request = JsonSerializer.Deserialize<ScreenRequestDto>(
            Example("screen-request.json"), ContractSerialization.Options)!;

        request.Universe.Count.ShouldBe(4);
        request.Limit.ShouldBe(2);
        request.MinDollarVolume.ShouldBe(5000000m);
        request.CorrelationId.ShouldBe("cycle-2026-09-26-1");
    }

    [Fact]
    public void What_the_engine_writes_is_what_the_request_example_holds()
    {
        // The other direction. Reading the example proves the engine can parse it; this proves
        // the engine's own request has the same field names, which is the half a reader cannot
        // check by eye once a property is renamed on one side only.
        var example = JsonSerializer.Deserialize<JsonElement>(Example("screen-request.json"));

        var request = new ScreenRequestDto
        {
            Universe = [new EquityInstrumentDto { Symbol = "AAPL" }],
            Limit = 2,
            MinDollarVolume = 5000000m,
            CorrelationId = "cycle-2026-09-26-1"
        };

        var written = JsonSerializer.SerializeToElement(request, ContractSerialization.Options);

        written.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(example.EnumerateObject().Select(property => property.Name).Order());
        written.GetProperty("universe")[0].GetProperty("type").GetString().ShouldBe("equity");
    }

    [Fact]
    public void A_field_the_contract_does_not_have_is_refused()
    {
        // unevaluatedProperties: false on the schema's side has to mean Disallow on this one,
        // or the schema is a description rather than a rule. The realistic case is a factor
        // being added to the ranking on the Python side alone.
        var withExtra = AScreen(candidates: ACandidate().TrimEnd().TrimEnd('}') + ""","pe_ratio":34.1}""");

        Should.Throw<JsonException>(() => Parse(withExtra));
    }

    [Fact]
    public void A_screen_that_found_nothing_is_a_valid_answer()
    {
        // Not a failure, and not an error to raise: a universe where nothing cleared the
        // liquidity floor today is a fact about today. The cycle then analyses the holdings
        // and buys nothing, which is the correct outcome rather than a missing one.
        var screen = Map(Parse(AScreen())!);

        screen.Candidates.ShouldBeEmpty();
        screen.Rejected.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../internal")]
    [InlineData("")]
    [InlineData("toolongsymbolxxxx")]
    public void A_symbol_that_is_not_a_ticker_is_refused(string symbol)
    {
        Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(candidates: ACandidate(symbol: symbol)))!));
    }

    [Fact]
    public void A_rejected_symbol_is_checked_like_any_other()
    {
        // The rejections are the list that is easy to treat as free text, because nothing is
        // traded from it. But it is stored, and a symbol in it is joined against the
        // candidates and the ledger when the universe's health is read back.
        Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(rejected: ARejection(symbol: "not a symbol")))!));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    public void A_volatility_that_is_not_positive_is_refused(string volatility)
    {
        // The schema's exclusiveMinimum, which System.Text.Json does not read. It is the
        // divisor of the score, so a zero is not a share that did not move - it is a ranking
        // that could not be computed, and one the agent service leaves out rather than puts
        // first.
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(candidates: ACandidate(volatility: volatility)))!));

        exception.Message.ShouldContain("is not positive");
    }

    [Fact]
    public void A_turnover_below_zero_is_refused()
    {
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(candidates: ACandidate(turnover: "-1")))!));

        exception.Message.ShouldContain("is negative");
    }

    [Fact]
    public void A_rejection_with_no_reason_is_refused()
    {
        // The schema's minLength. A rejection that says nothing is exactly the silent
        // shrinking the list was added to prevent.
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(rejected: ARejection(reason: "   ")))!));

        exception.Message.ShouldContain("without a reason");
    }

    [Fact]
    public void A_reason_longer_than_the_contract_allows_is_refused()
    {
        var tooLong = new string('x', ScreenMapper.MaxReasonLength + 1);

        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(AScreen(rejected: ARejection(reason: tooLong)))!));

        exception.Message.ShouldContain($"longer than {ScreenMapper.MaxReasonLength}");
    }

    [Fact]
    public void Candidates_that_are_not_best_first_are_refused()
    {
        // The contract promises an order and nothing in JSON can express it. The position in
        // this list becomes the stored rank, and the rank is what the comparison against the
        // shortlist groups by - so an unsorted answer would write wrong ranks rather than fail.
        var outOfOrder = AScreen(
            candidates: $"{ACandidate(symbol: "AAPL", score: "0.57")},{ACandidate(symbol: "NVDA", score: "1.42")}");

        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(outOfOrder)!));

        exception.Message.ShouldContain("NVDA scores higher than AAPL");
    }

    [Fact]
    public void Equal_scores_are_an_order_rather_than_a_violation()
    {
        // Non-increasing, not strictly decreasing. Two shares can rank the same, and which of
        // them comes first is then the agent service's tie-break to make.
        var screen = Map(Parse(AScreen(
            candidates: $"{ACandidate(symbol: "AAPL", score: "1.0")},{ACandidate(symbol: "NVDA", score: "1.0")}"))!);

        screen.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void The_same_instrument_twice_among_the_candidates_is_refused()
    {
        var twice = AScreen(candidates: $"{ACandidate(symbol: "NVDA")},{ACandidate(symbol: "NVDA")}");

        var exception = Should.Throw<AgentResponseInvalidException>(() => Map(Parse(twice)!));

        exception.Message.ShouldContain("appears more than once");
    }

    [Fact]
    public void An_instrument_that_is_both_ranked_and_rejected_is_refused()
    {
        // The one a duplicate check over each list separately would miss, and the worse of the
        // two: the cycle would analyse it and record a reason for leaving it out, in the same
        // breath.
        var both = AScreen(candidates: ACandidate(symbol: "NVDA"), rejected: ARejection(symbol: "NVDA"));

        var exception = Should.Throw<AgentResponseInvalidException>(() => Map(Parse(both)!));

        exception.Message.ShouldContain("appears more than once");
    }

    [Fact]
    public void An_instrument_outside_the_requested_universe_is_refused()
    {
        // The screen chooses what may be analysed and bought. An answer that invents a name
        // the engine never asked about must not widen the trading universe by stealth - the
        // same mirror HistoryMapper applies to a history for the wrong symbol.
        var onlyAsked = Universe("NVDA", "TINY");
        var withStranger = AScreen(
            candidates: ACandidate(symbol: "NVDA"),
            rejected: ARejection(symbol: "MSFT"));

        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(withStranger)!, onlyAsked));

        exception.Message.ShouldContain("MSFT");
        exception.Message.ShouldContain("not in the requested universe");
    }

    [Fact]
    public void A_shortlist_longer_than_the_requested_limit_is_refused()
    {
        // ShortlistSize caps how many new names enter analysis. Honouring a longer answer
        // would spend LLM budget the operator did not allocate, even with AnalysisDueCheck.
        var twoCandidates = AScreen(
            candidates: $"{ACandidate(symbol: "NVDA", score: "1.42")},{ACandidate(symbol: "AAPL", score: "0.57")}");

        var exception = Should.Throw<AgentResponseInvalidException>(
            () => Map(Parse(twoCandidates)!, limit: 1));

        exception.Message.ShouldContain("2 candidates");
        exception.Message.ShouldContain("limit is 1");
    }
}
