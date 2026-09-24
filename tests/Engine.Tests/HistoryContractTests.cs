using System.Text.Json;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// The price series, read from the checked-in contract rather than from a copy. Everything a
/// measurement divides by passes through this mapper, so what it refuses is what cannot
/// become a silently wrong number later.
/// </summary>
public class HistoryContractTests
{
    private static readonly Ticker Msft = new("MSFT");

    private static string Example() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "examples", "quote-history.json"));

    private static HistoryDto? Parse(string json) =>
        JsonSerializer.Deserialize<HistoryDto>(json, ContractSerialization.Options);

    private static string AHistory(string symbol = "MSFT", string bars = """{"on":"2026-09-21","close":410.10}""") =>
        $$"""{"instrument":{"type":"equity","symbol":"{{symbol}}"},"bars":[{{bars}}]}""";

    [Fact]
    public void The_checked_in_example_reads_into_a_series()
    {
        var series = HistoryMapper.ToDomain(Parse(Example())!, Msft);

        series.Count.ShouldBe(4);
        series.Latest!.On.ShouldBe(new DateOnly(2026, 9, 25));
        series.Latest.Close.ShouldBe(415.25m);
    }

    [Fact]
    public void An_empty_series_is_a_valid_answer()
    {
        // Nothing has traded since the date asked about, which the measurement reads as a
        // horizon that has not passed. Refusing it here would turn "too early" into "broken".
        var series = HistoryMapper.ToDomain(Parse(AHistory(bars: ""))!, Msft);

        series.Count.ShouldBe(0);
        series.Latest.ShouldBeNull();
    }

    [Fact]
    public void A_history_about_another_instrument_is_refused()
    {
        // Without this, an answer about AAPL would be measured as if it were MSFT - the same
        // failure the signal path already refuses, in the place that would be easier to miss.
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => HistoryMapper.ToDomain(Parse(AHistory(symbol: "AAPL"))!, Msft));

        exception.Message.ShouldContain("about AAPL, not MSFT");
    }

    [Fact]
    public void Bars_out_of_order_are_refused()
    {
        // Every lookup takes the last bar as the latest one, so a provider that changed its
        // ordering would make every measurement quietly wrong rather than fail.
        var reversed = """{"on":"2026-09-22","close":412.75},{"on":"2026-09-21","close":410.10}""";

        Should.Throw<AgentResponseInvalidException>(
            () => HistoryMapper.ToDomain(Parse(AHistory(bars: reversed))!, Msft));
    }

    [Fact]
    public void A_close_that_is_not_a_price_is_refused()
    {
        // A return is divided by this. Zero is not a low price; it is a missing one.
        Should.Throw<AgentResponseInvalidException>(
            () => HistoryMapper.ToDomain(Parse(AHistory(bars: """{"on":"2026-09-21","close":0}"""))!, Msft));
    }

    [Fact]
    public void A_field_the_contract_does_not_have_is_refused()
    {
        var withExtra = AHistory(bars: """{"on":"2026-09-21","close":410.10,"volume":1200}""");

        Should.Throw<JsonException>(() => Parse(withExtra));
    }
}
