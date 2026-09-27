namespace Engine.Domain.Screening;

using Engine.Domain.ValueObjects;

/// <summary>
/// One ranking of a universe, as the engine received it: who made the shortlist, who did not,
/// and which day's data the ranking was computed from.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns the universe and sends it, so this is the answer to a question it asked
/// rather than a list it was given. Nothing here is computed on this side - the factors and
/// the score come from the agent service, which is where the market data integration lives -
/// but the symbols have been through <see cref="Ticker"/> before they reach this type, so
/// anything holding a <see cref="Screen"/> is holding instruments the engine can trade.
/// </para>
/// <para>
/// The rejected instruments travel with the candidates rather than being dropped at the
/// seam. A universe that quietly shrinks is the failure that is impossible to see afterwards:
/// a systematic data outage in one sector reads, months later, exactly like a decision never
/// to hold anything in it.
/// </para>
/// </remarks>
/// <param name="AsOf">
/// When the ranking was computed. The factors are built from daily bars, so two screens on the
/// same trading day rank the same way - which is what makes a stored shortlist the shortlist
/// for that day rather than for that minute.
/// </param>
public sealed record Screen(
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<Rejection> Rejected,
    DateTimeOffset AsOf);

/// <summary>
/// One ranked instrument, with the figures its score was computed from.
/// </summary>
/// <remarks>
/// The figures travel with the score because a ranking nobody can check is a ranking nobody
/// will question. They are also what makes this stage's own question answerable afterwards:
/// "did the agents beat the screen that picked their candidates?" needs the screen as it was
/// at the time, not as the ranking would come out today.
/// </remarks>
/// <param name="Score">Risk-adjusted momentum: the three-month return over realised volatility.</param>
public sealed record Candidate(
    Ticker Ticker,
    decimal Score,
    decimal Return3M,
    decimal Volatility30D,
    decimal MedianDollarVolume);

/// <summary>An instrument the screen looked at and left out, with the reason in words.</summary>
public sealed record Rejection(Ticker Ticker, string Reason);
