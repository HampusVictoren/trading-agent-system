using System.Text.Json;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// The other half of the agreement, read from the checked-in file rather than from a copy.
/// A quote is arithmetic rather than opinion, so what matters here is that nothing
/// unchecked reaches the sizer: it multiplies this price by a quantity and spends money.
/// </summary>
public class QuoteContractTests
{
    private static readonly string ContractsDirectory =
        Path.Combine(AppContext.BaseDirectory, "contracts");

    private static string Example() =>
        File.ReadAllText(Path.Combine(ContractsDirectory, "examples", "quote.json"));

    private static QuoteDto? Parse(string json) =>
        JsonSerializer.Deserialize<QuoteDto>(json, ContractSerialization.Options);

    private static string AQuote(
        string symbol = "MSFT", string price = "415.25", string currency = "USD") =>
        $$"""
          {"instrument":{"type":"equity","symbol":"{{symbol}}"},
           "price":{{price}},"currency":"{{currency}}","as_of":"2026-09-24T18:44:00Z"}
          """;

    [Fact]
    public void The_checked_in_example_reads_into_the_engines_types()
    {
        var quote = QuoteMapper.ToDomain(Parse(Example())!);

        quote.Ticker.Value.ShouldBe("MSFT");
        quote.Price.Amount.ShouldBe(415.25m);
        quote.Price.Currency.ShouldBe("USD");
        quote.AsOf.ShouldBe(new DateTimeOffset(2026, 9, 24, 18, 44, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_field_the_contract_does_not_have_is_refused()
    {
        // unevaluatedProperties: false on the schema's side has to mean Disallow on this
        // one, or the schema is a description rather than a rule. The fact sheet's own
        // fields are the realistic case: they exist, they are next door, and they are not
        // part of this contract.
        var withExtra = Example().TrimEnd().TrimEnd('}') + ""","pe_ratio":34.1}""";

        Should.Throw<JsonException>(() => Parse(withExtra));
    }

    [Theory]
    [InlineData("0", "the price 0 is not positive")]
    [InlineData("-1.5", "is not positive")]
    public void A_price_that_is_not_a_price_is_refused(string price, string expected)
    {
        // System.Text.Json is happy with either. The sizer divides a budget by this number
        // and floors the result, so a zero is an infinite order and a negative one is worse.
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => QuoteMapper.ToDomain(Parse(AQuote(price: price))!));

        exception.Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("../internal")]
    [InlineData("")]
    [InlineData("toolongsymbol")]
    public void A_symbol_that_is_not_a_ticker_is_refused(string symbol)
    {
        Should.Throw<AgentResponseInvalidException>(
            () => QuoteMapper.ToDomain(Parse(AQuote(symbol: symbol))!));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("DOLLAR")]
    [InlineData("")]
    public void A_currency_that_is_not_a_code_is_refused(string currency)
    {
        // Money's own rule, reported as a contract failure rather than as an
        // ArgumentException from three layers down.
        var exception = Should.Throw<AgentResponseInvalidException>(
            () => QuoteMapper.ToDomain(Parse(AQuote(currency: currency))!));

        exception.Message.ShouldContain("is not a currency code");
    }

    [Fact]
    public void A_quote_in_another_currency_is_carried_rather_than_assumed()
    {
        // The signal contract has no currency and everything in it is USD. This one does,
        // so a non-dollar price arrives as what it is. Mixing it into a dollar portfolio is
        // refused by Money, which is where that rule belongs - not here.
        var quote = QuoteMapper.ToDomain(Parse(AQuote(currency: "sek"))!);

        quote.Price.Currency.ShouldBe("SEK");
    }
}
