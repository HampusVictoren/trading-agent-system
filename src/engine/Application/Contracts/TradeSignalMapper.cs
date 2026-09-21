namespace Engine.Application.Contracts;

using Engine.Application.Interfaces;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// Turns what arrived on the wire into what the domain works on.
/// </summary>
/// <remarks>
/// This is the seam. System.Text.Json checks the shape, but not that a value makes sense:
/// a stance of "MAYBE" or a conviction of 1.5 deserialises happily. Everything past this
/// point is checked, so sizing and risk code never has to ask.
///
/// It throws <see cref="AgentResponseInvalidException"/>, the same exception the client
/// already raises for a body that is not the contract, so the use case reports one outcome
/// for "the agents answered with something unusable" however it went wrong.
///
/// The contract has no currency field: every price in it is USD, and the engine's portfolio
/// is USD. Stage 5's wider universe is where that has to become explicit.
/// </remarks>
public static class TradeSignalMapper
{
    private const string ContractCurrency = "USD";

    public static TradeSignal ToDomain(TradeSignalDto dto)
    {
        var instrument = ToInstrument(dto.Instrument);

        if (!Enum.TryParse<Stance>(dto.Stance, ignoreCase: false, out var stance))
            throw Invalid($"the stance '{dto.Stance}' is not one of BUY, SELL or HOLD");

        if (!Conviction.TryCreate(dto.Conviction, out var conviction))
            throw Invalid($"the conviction {dto.Conviction} is not between 0 and 1");

        if (string.IsNullOrWhiteSpace(dto.Thesis))
            throw Invalid("the thesis is empty");

        if (dto.HorizonDays < 1)
            throw Invalid($"the horizon of {dto.HorizonDays} days is not a period");

        if (dto.ReferencePrice <= 0)
            throw Invalid($"the reference price {dto.ReferencePrice} is not a price");

        return new TradeSignal(
            instrument,
            stance,
            conviction,
            dto.Thesis,
            dto.KeyRisks,
            dto.HorizonDays,
            new Money(dto.ReferencePrice, ContractCurrency),
            dto.QuoteAsOf,
            new RunMetadata(dto.Run.TeamId, dto.Run.TeamVersion, dto.Run.Revisions));
    }

    private static Instrument ToInstrument(InstrumentDto dto) => dto switch
    {
        EquityInstrumentDto equity when Ticker.TryCreate(equity.Symbol, out var ticker)
            => new Instrument.Equity(ticker),

        EquityInstrumentDto equity
            => throw Invalid($"'{equity.Symbol}' is not a symbol"),

        // Unreachable while equity is the only variant, but the compiler cannot know that,
        // and a variant added without a case here should say so rather than crash elsewhere.
        _ => throw Invalid($"the instrument type {dto.GetType().Name} is not one the engine trades")
    };

    private static AgentResponseInvalidException Invalid(string reason) =>
        new($"The agent service answered with something unusable: {reason}.");
}
