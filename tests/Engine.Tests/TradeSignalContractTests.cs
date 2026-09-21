using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Domain.Signals;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// The contract in contracts/ is the agreement between the two services, and this reads the
/// checked-in files rather than a copy, so that drift is what fails. It uses
/// ContractSerialization.Options - the same object production uses - or it would prove
/// nothing about how the engine actually reads an answer.
/// </summary>
public class TradeSignalContractTests
{
    private static readonly string ContractsDirectory =
        Path.Combine(AppContext.BaseDirectory, "contracts");

    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(ContractsDirectory, "examples", name));

    private static TradeSignalDto? Signal(string json) =>
        JsonSerializer.Deserialize<TradeSignalDto>(json, ContractSerialization.Options);

    [Fact]
    public void The_checked_in_examples_are_where_the_test_expects_them()
    {
        // A copy that silently stops being copied would make every test below vacuous.
        Directory.EnumerateFiles(Path.Combine(ContractsDirectory, "examples"), "*.json")
            .Count().ShouldBe(5);
    }

    [Fact]
    public void A_signal_example_reads_into_the_engines_types()
    {
        var signal = Signal(Example("signal-buy.json"))!;

        signal.Stance.ShouldBe("BUY");
        signal.Conviction.ShouldBe(0.78);
        signal.HorizonDays.ShouldBe(5);
        signal.ReferencePrice.ShouldBe(233.12m);
        signal.KeyRisks.Count.ShouldBe(2);
        signal.Run.TeamId.ShouldBe("default");
        signal.Run.Revisions.ShouldBe(0);
        signal.Instrument.ShouldBeOfType<EquityInstrumentDto>().Symbol.ShouldBe("AAPL");
    }

    [Theory]
    [InlineData("signal-buy.json")]
    [InlineData("signal-hold.json")]
    [InlineData("signal-discriminator-last.json")]
    public void Every_signal_example_reads_and_becomes_a_domain_signal(string name)
    {
        var signal = TradeSignalMapper.ToDomain(Signal(Example(name))!);

        signal.Instrument.ShouldBeOfType<Instrument.Equity>();
        signal.Thesis.ShouldNotBeNullOrWhiteSpace();
        signal.Conviction.Value.ShouldBeInRange(0, 1);
        signal.ReferencePrice.Amount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void An_example_with_the_discriminator_last_still_reads()
    {
        // This is the whole reason that example is checked in. System.Text.Json refuses an
        // out-of-order discriminator unless AllowOutOfOrderMetadataProperties is set, and
        // pydantic writes "type" first only by accident of field order.
        var signal = Signal(Example("signal-discriminator-last.json"))!;

        signal.Instrument.ShouldBeOfType<EquityInstrumentDto>().Symbol.ShouldBe("BRK.B");
    }

    [Fact]
    public void Reading_a_discriminator_last_without_the_setting_fails()
    {
        // Documents what the setting buys, so that removing it cannot look harmless.
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<TradeSignalDto>(
            Example("signal-discriminator-last.json"), new JsonSerializerOptions()));
    }

    [Theory]
    [InlineData("request.json", 3.0)]
    [InlineData("request-no-position.json", null)]
    public void A_request_example_reads(string name, double? expectedQuantity)
    {
        var request = JsonSerializer.Deserialize<TradeSignalRequestDto>(
            Example(name), ContractSerialization.Options)!;

        request.TeamId.ShouldBe("default");
        request.MaxPositionPct.ShouldBe(0.05m);
        ((double?)request.ExistingPosition?.Quantity).ShouldBe(expectedQuantity);
    }

    [Fact]
    public void An_amount_in_the_answer_is_refused()
    {
        // Decision 1: the agents never say how much. A field that reappeared would mean the
        // Python side had started deciding size again.
        var withAmount = Example("signal-buy.json")
            .Replace("\"stance\"", "\"amount_usd\": 500, \"stance\"", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => Signal(withAmount));
    }

    [Fact]
    public void An_instrument_type_the_engine_does_not_trade_is_refused()
    {
        var asOption = Example("signal-buy.json")
            .Replace("\"equity\"", "\"option\"", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => Signal(asOption));
    }

    [Fact]
    public void A_missing_field_is_refused()
    {
        var withoutHorizon = Example("signal-buy.json")
            .Replace("\"horizon_days\": 5,", "", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => Signal(withoutHorizon));
    }

    [Theory]
    [InlineData("\"BUY\"", "\"MAYBE\"", "not one of BUY, SELL or HOLD")]
    [InlineData("0.78", "1.5", "not between 0 and 1")]
    [InlineData("\"AAPL\"", "\"\"", "not a symbol")]
    public void A_value_that_deserialises_but_makes_no_sense_is_refused_at_the_seam(
        string from, string to, string expectedReason)
    {
        // The shape is fine, so System.Text.Json is happy. The mapper is what catches it.
        var dto = Signal(Example("signal-buy.json").Replace(from, to, StringComparison.Ordinal))!;

        var exception = Should.Throw<AgentResponseInvalidException>(() => TradeSignalMapper.ToDomain(dto));

        exception.Message.ShouldContain(expectedReason);
    }

    [Fact]
    public void The_schema_and_the_engines_type_require_the_same_fields()
    {
        // Without this the schema file is documentation that can quietly go stale. Stage 3
        // closes the loop from the other side, where Python dumps its schema and compares.
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(ContractsDirectory, "trade-signal.schema.json")));

        var required = schema.RootElement.GetProperty("required")
            .EnumerateArray().Select(field => field.GetString()!).OrderBy(name => name);

        var onTheType = typeof(TradeSignalDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .OrderBy(name => name);

        onTheType.ShouldBe(required);
    }
}
