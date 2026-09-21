namespace Engine.Domain.Signals;

using Engine.Domain.ValueObjects;

/// <summary>
/// One answer from the agents, in the engine's own terms. Everything on it has already been
/// checked, so sizing and risk code never has to ask whether a value makes sense.
/// </summary>
/// <param name="ReferencePrice">
/// The quote the view was formed on. The engine sizes from this rather than fetching market
/// data of its own, which would duplicate the integration.
/// </param>
/// <param name="QuoteAsOf">When that quote was taken. A stale signal is refused by RiskEngine.</param>
public sealed record TradeSignal(
    Instrument Instrument,
    Stance Stance,
    Conviction Conviction,
    string Thesis,
    IReadOnlyList<string> KeyRisks,
    int HorizonDays,
    Money ReferencePrice,
    DateTimeOffset QuoteAsOf,
    RunMetadata Run);

/// <summary>
/// How the answer was produced. The engine decides on none of it but stores it, so that
/// stage 4 can compare outcomes per team setup.
/// </summary>
public sealed record RunMetadata(string TeamId, string TeamVersion, int Revisions);
