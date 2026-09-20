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
}
