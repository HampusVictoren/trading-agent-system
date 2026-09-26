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
/// The contract has no currency field: every price in it is the account's currency, and the
/// account and the universe are both Swedish. The quote contract does carry one, because a
/// quote can be for an instrument the engine does not price in kronor - and that is the seam
/// where a mismatch would surface, which is finding D's remaining half.
/// </remarks>
public static class TradeSignalMapper
{
    // What a price in this contract is denominated in. It is the account's currency rather
    // than the instrument's, and the two are the same only because the universe is Swedish;
    // Money.DefaultCurrency is where that is decided.
    private const string ContractCurrency = Money.DefaultCurrency;

    // The caps in contracts/trade-signal.schema.json. System.Text.Json does not read JSON
    // Schema, so the engine has to enforce them itself - and it has to, because from stage 4
    // every thesis is stored and every one of them is read back into a prompt.
    //
    // public so the contract test can read the checked-in schema and compare, the way it
    // already does for ReportOutcomesUseCase.MaxPerRequest. Each of these numbers lives in
    // three places - pydantic, the schema, here - and until now only the first two were
    // held together.
    public const int MaxThesisLength = 2000;
    public const int MaxRiskLength = 300;
    public const int MaxRisks = 5;

    // Calendar days. See the schema for why 30; the short version is that the fixed
    // horizons reach 20 trading days and stage 5's time-limit exit fires on this number.
    public const int MaxHorizonDays = 30;

    public static TradeSignal ToDomain(TradeSignalDto dto)
    {
        var instrument = ToInstrument(dto.Instrument);

        // Spelled out rather than Enum.TryParse: the contract says BUY, not Buy or buy, and
        // a case-insensitive parse would quietly accept a spelling the schema forbids.
        var stance = dto.Stance switch
        {
            "BUY" => Stance.Buy,
            "SELL" => Stance.Sell,
            "HOLD" => Stance.Hold,
            _ => throw Invalid($"the stance '{dto.Stance}' is not one of BUY, SELL or HOLD")
        };

        if (!Conviction.TryCreate(dto.Conviction, out var conviction))
            throw Invalid($"the conviction {dto.Conviction} is not between 0 and 1");

        if (string.IsNullOrWhiteSpace(dto.Thesis))
            throw Invalid("the thesis is empty");

        if (dto.Thesis.Length > MaxThesisLength)
            throw Invalid($"the thesis is {dto.Thesis.Length} characters, past the {MaxThesisLength} limit");

        if (dto.KeyRisks.Count > MaxRisks)
            throw Invalid($"there are {dto.KeyRisks.Count} key risks, past the {MaxRisks} limit");

        if (dto.KeyRisks.Any(risk => risk.Length > MaxRiskLength))
            throw Invalid($"a key risk is longer than the {MaxRiskLength} character limit");

        if (dto.HorizonDays < 1)
            throw Invalid($"the horizon of {dto.HorizonDays} days is not a period");

        if (dto.HorizonDays > MaxHorizonDays)
            throw Invalid(
                $"the horizon of {dto.HorizonDays} days is past the {MaxHorizonDays} day limit");

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
