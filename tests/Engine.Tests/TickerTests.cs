using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.ValueObjects;

public class TickerTests
{
    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData("  msft  ", "MSFT")]
    [InlineData("BRK.B", "BRK.B")]
    public void Normalises_to_trimmed_uppercase(string input, string expected)
    {
        new Ticker(input).Value.ShouldBe(expected);
    }

    [Fact]
    public void Two_tickers_are_equal_after_normalisation()
    {
        // Portfolio.ExecuteBuy relies on this to find an existing position.
        new Ticker(" aapl ").ShouldBe(new Ticker("AAPL"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_an_empty_value(string? input)
    {
        Should.Throw<ArgumentException>(() => new Ticker(input!));
    }

    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData("  msft  ", "MSFT")]
    public void TryCreate_accepts_and_normalises_valid_input(string input, string expected)
    {
        Ticker.TryCreate(input, out var ticker).ShouldBeTrue();

        ticker!.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryCreate_refuses_an_empty_value_without_throwing(string? input)
    {
        // The engine parses agent answers with this. A malformed answer is an expected
        // outcome, so it must not arrive as an exception.
        Ticker.TryCreate(input, out var ticker).ShouldBeFalse();

        ticker.ShouldBeNull();
    }

    [Theory]
    [InlineData("../internal/shutdown")]
    [InlineData("AAPL/../../admin")]
    [InlineData("AAPL?x=1")]
    [InlineData("AA PL")]
    [InlineData("AA$PL")]
    [InlineData("1AAPL")]
    [InlineData("ABCDEFGHIJK")]
    public void Refuses_a_value_that_is_not_a_symbol(string input)
    {
        // Finding B. All three of the first cases were accepted before, and the first two
        // resolved to a different URL entirely when interpolated into a path. The path is
        // gone with the old endpoint, but the rule is what stage 5 needs: screening
        // produces symbols from market data rather than from appsettings.json.
        Should.Throw<ArgumentException>(() => new Ticker(input));
        Ticker.TryCreate(input, out var ticker).ShouldBeFalse();
        ticker.ShouldBeNull();
    }

    [Theory]
    [InlineData("A")]
    [InlineData("BRK.B")]
    [InlineData("ABCDEFGHIJ")]
    [InlineData("RDS-A")]
    public void Accepts_what_the_contract_calls_a_symbol(string input)
    {
        // The same pattern as contracts/trade-signal.schema.json, so a symbol that the
        // agent service accepts cannot be one the engine refuses.
        new Ticker(input).Value.ShouldBe(input);
    }
}
