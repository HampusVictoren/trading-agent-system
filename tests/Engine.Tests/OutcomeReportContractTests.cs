using System.Text.Json;
using Engine.Application.Contracts;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Outcomes;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// What the engine sends to POST /v1/outcomes, read from the checked-in contract rather than
/// from a copy. The agent service's suite reads the same two files, so a field added on one
/// side only fails here rather than during a live sweep.
/// </summary>
public class OutcomeReportContractTests
{
    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "examples", name));

    private static JsonDocument Contract() =>
        JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "outcome.schema.json")));

    private static OutcomeReportDto? Parse(string json) =>
        JsonSerializer.Deserialize<OutcomeReportDto>(json, ContractSerialization.Options);

    private static OutcomeAwaitingDelivery AMeasurement(
        OutcomeStatus status = OutcomeStatus.Measured,
        HorizonUnit unit = HorizonUnit.TradingDays) => new(
            SignalOutcomeId: 7,
            CorrelationId: "cycle-1",
            HorizonUnit: unit,
            HorizonDays: 5,
            Status: status,
            Reason: status == OutcomeStatus.NotMeasurable ? "no bar for the benchmark" : null,
            BenchmarkSymbol: "SPY",
            MeasuredOn: new DateOnly(2026, 10, 1),
            MeasuredPrice: 344.12m,
            InstrumentReturn: 0.019795m,
            BenchmarkReturn: 0.004311m,
            ExcessReturn: 0.015484m,
            CostFraction: 0.0006m,
            NetEdge: 0.014884m,
            Hit: true);

    [Fact]
    public void The_checked_in_example_reads_into_the_engines_own_type()
    {
        var report = Parse(Example("outcomes-measured.json"));

        report!.Outcomes.Count.ShouldBe(2);
        report.Outcomes[0].HorizonUnit.ShouldBe("TradingDays");
        report.Outcomes[0].NetEdge.ShouldBe(0.014884m);
        report.Outcomes[0].Hit.ShouldBe(true);
        report.Outcomes[1].HorizonUnit.ShouldBe("CalendarDays");
        report.Outcomes[1].Hit.ShouldBe(false);
    }

    [Fact]
    public void A_measurement_that_could_never_be_made_is_still_a_report()
    {
        // Sent rather than left out: a signal quietly never measured is one missing from the
        // population any report speaks about.
        var report = Parse(Example("outcomes-not-measurable.json"));

        var outcome = report!.Outcomes.Single();
        outcome.Status.ShouldBe("NotMeasurable");
        outcome.Reason.ShouldNotBeNullOrWhiteSpace();
        outcome.Hit.ShouldBeNull();
        outcome.InstrumentReturn.ShouldBeNull();
    }

    [Theory]
    [InlineData(HorizonUnit.TradingDays, "TradingDays")]
    [InlineData(HorizonUnit.CalendarDays, "CalendarDays")]
    public void A_horizon_goes_on_the_wire_spelled_as_the_engine_stores_it(
        HorizonUnit unit, string expected)
    {
        // Deliberately not the contract's usual upper case. Two copies of one row spelled
        // differently mean a mapping in every head that ever compares the two tables.
        MeasuredOutcomeDto.From(AMeasurement(unit: unit)).HorizonUnit.ShouldBe(expected);
    }

    [Theory]
    [InlineData(OutcomeStatus.Measured, "Measured")]
    [InlineData(OutcomeStatus.NotMeasurable, "NotMeasurable")]
    public void A_status_goes_on_the_wire_spelled_as_the_engine_stores_it(
        OutcomeStatus status, string expected)
    {
        MeasuredOutcomeDto.From(AMeasurement(status)).Status.ShouldBe(expected);
    }

    [Fact]
    public void The_measurement_is_identified_by_the_id_both_services_wrote_down()
    {
        // Not SignalOutcomeId: the engine's own primary key means nothing on the other side.
        var sent = MeasuredOutcomeDto.From(AMeasurement());

        sent.CorrelationId.ShouldBe("cycle-1");

        // The key itself is not a field on the wire at all. Asserting on its value would be
        // no assertion: any digit shows up somewhere in a payload full of returns.
        JsonSerializer.Serialize(sent, ContractSerialization.Options)
            .ShouldNotContain("signal_outcome_id");
        typeof(MeasuredOutcomeDto).GetProperty("SignalOutcomeId").ShouldBeNull();
    }

    [Fact]
    public void A_report_serialises_to_the_names_the_contract_uses()
    {
        var json = JsonSerializer.Serialize(
            new OutcomeReportDto { Outcomes = [MeasuredOutcomeDto.From(AMeasurement())] },
            ContractSerialization.Options);

        var round = Parse(json);

        round!.Outcomes.Single().CorrelationId.ShouldBe("cycle-1");
        json.ShouldContain("\"correlation_id\"");
        json.ShouldContain("\"benchmark_symbol\"");
        json.ShouldContain("\"net_edge\"");
        json.ShouldContain("\"measured_on\":\"2026-10-01\"");
    }

    [Fact]
    public void The_batch_the_engine_sends_is_capped_at_what_the_contract_accepts()
    {
        // The number 500 is written in three places - here, in the agent service's
        // MAX_OUTCOMES_PER_REQUEST, and in the contract - and until now each side only
        // tested its own copy, so the two could drift without anything going red.
        //
        // Drift is not a slow recovery, it is a stop. If the other side's cap were the
        // lower one, every sweep would send the same oversized batch, get a 422, mark
        // nothing delivered and do it again - silently, apart from one error line a day.
        // The same reasoning guards trading.hit_rate's conviction thresholds, which repeat
        // ConvictionTier's constants in SQL.
        using var contract = Contract();

        var maxItems = contract.RootElement
            .GetProperty("properties")
            .GetProperty("outcomes")
            .GetProperty("maxItems")
            .GetInt32();

        maxItems.ShouldBe(ReportOutcomesUseCase.MaxPerRequest);
    }

    [Fact]
    public void A_field_the_contract_does_not_have_is_refused_rather_than_ignored()
    {
        var json = Example("outcomes-measured.json").Replace(
            "\"hit\": true", "\"hit\": true, \"amount_usd\": 1000", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => Parse(json));
    }
}
