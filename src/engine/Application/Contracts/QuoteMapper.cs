namespace Engine.Application.Contracts;

using Engine.Application.Interfaces;
using Engine.Domain.ValueObjects;

/// <summary>
/// The seam for a quote, matching <see cref="TradeSignalMapper"/>. System.Text.Json checks
/// the shape; a price of zero or a symbol that is not a symbol deserialises happily, so
/// everything the sizer multiplies by a quantity is checked here first.
/// </summary>
public static class QuoteMapper
{
    public static InstrumentQuote ToDomain(QuoteDto dto)
    {
        if (dto.Instrument is not EquityInstrumentDto equity)
            throw Invalid($"the instrument type '{dto.Instrument.GetType().Name}' is not one the engine trades");

        if (!Ticker.TryCreate(equity.Symbol, out var ticker))
            throw Invalid($"the symbol '{equity.Symbol}' is not a ticker");

        if (dto.Price <= 0m)
            throw Invalid($"the price {dto.Price} is not positive");

        Money price;
        try
        {
            // Money normalises and refuses anything that is not a three-letter code. A quote
            // in a currency the portfolio does not hold is not an error here - Money's own
            // guard refuses to add it to a total, which is where the mix actually matters.
            price = new Money(dto.Price, dto.Currency);
        }
        catch (ArgumentException ex)
        {
            throw Invalid($"the currency '{dto.Currency}' is not a currency code", ex);
        }

        return new InstrumentQuote(ticker, price, dto.AsOf);
    }

    private static AgentResponseInvalidException Invalid(string reason) =>
        new($"The quote makes no sense: {reason}.");

    private static AgentResponseInvalidException Invalid(string reason, Exception inner) =>
        new($"The quote makes no sense: {reason}.", inner);
}
