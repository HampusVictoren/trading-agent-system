namespace Engine.Application.Persistence;

using Engine.Domain.ValueObjects;

/// <summary>
/// One instrument's place in one trading day's screen: either a rank with the figures behind
/// it, or a reason it was left out.
/// </summary>
/// <remarks>
/// <para>
/// One table rather than two, with the columns of whichever half applies left null. It follows
/// <see cref="DecisionRecord"/>, where the signal's columns are null when there never was a
/// signal: this is a log row, and what a log row has to be able to say is "this is what
/// happened to this instrument today", not "here is a fully populated candidate".
/// </para>
/// <para>
/// The rejections are stored rather than only logged, because they are the only way a universe
/// that is quietly rotting ever becomes visible. A symbol whose listing went inactive fails
/// the same way every day, and a year later the absence of a whole sector reads exactly like a
/// decision never to hold anything in it.
/// </para>
/// <para>
/// What it cannot say is what the screen thought of an instrument that ranked below the
/// shortlist's cut: the agent service truncates to the requested limit, so those appear in
/// neither half of the answer. That bounds what can be asked afterwards - the agents can be
/// compared against the shortlist, but the ranking itself cannot be validated against the
/// names it passed over.
/// </para>
/// </remarks>
public sealed class ShortlistEntry
{
    /// <summary>Set by the database.</summary>
    public long Id { get; private set; }

    /// <summary>The cycle that asked for this screen, so a shortlist joins to the decisions
    /// made from it and to the agent service's own log lines about the same request.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The trading day this screen counts as, from the engine's clock. It is the key the
    /// engine looks a stored shortlist up by, and it is unique per symbol: one verdict per
    /// instrument per day.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived from <see cref="ScreenedAt"/>, because those two are not the
    /// same fact and are allowed to disagree. This is the day the engine is working on; that is
    /// when the other service computed the figures. Deriving one from the other would make a
    /// screen started seconds before midnight belong to a different day than the cycle that
    /// asked for it.
    /// </remarks>
    public required DateOnly ScreenedOn { get; init; }

    /// <summary>The <c>as_of</c> the agent service reported: when the ranking was computed.</summary>
    public required DateTimeOffset ScreenedAt { get; init; }

    public required Ticker Symbol { get; init; }

    /// <summary>Its place in the shortlist, best first from 1. Null for a rejection.</summary>
    public int? Rank { get; init; }

    // The figures the score was computed from, all null for a rejection. They are stored rather
    // than recomputed because a ranking has to be readable as it was on the day, not as it
    // would come out now.
    public decimal? Score { get; init; }
    public decimal? Return3M { get; init; }
    public decimal? Volatility30D { get; init; }
    public decimal? MedianDollarVolume { get; init; }

    /// <summary>Why it was left out, in the agent service's own words. Null for a candidate.</summary>
    public string? RejectedBecause { get; init; }

    /// <summary>Set by the database, so the row's own clock is the one that ordered it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }
}
