namespace Engine.Domain.Screening;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;

/// <summary>Why an instrument is in a cycle at all.</summary>
/// <remarks>
/// Stored on every decision, because it is the difference between two populations that must not
/// be averaged together: a holding is analysed whatever the ranking says about it, and a
/// candidate is there precisely because the ranking put it near the top. A report that mixed
/// them would be measuring the screen's selection and the portfolio's inertia as one number.
/// </remarks>
public enum SelectionSource
{
    /// <summary>The screen ranked it inside today's shortlist.</summary>
    Shortlist,

    /// <summary>The portfolio holds it, so it is analysed however it ranked.</summary>
    Holding,

    /// <summary>
    /// It came from the configured ticker list, which is how every decision before this stage
    /// was made. Nothing produces it any more; it exists so that the history does not have to
    /// claim it was screened.
    /// </summary>
    FixedList
}

/// <summary>One instrument to analyse this cycle, and why.</summary>
public sealed record InstrumentSelection(Ticker Ticker, SelectionSource Source);

/// <summary>
/// Which instruments a cycle is about: everything the portfolio holds, plus today's shortlist.
/// </summary>
public static class CycleSelection
{
    /// <summary>
    /// The holdings first, by symbol, then the shortlist in rank order with anything already
    /// held left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The holdings are always in, whatever they ranked.</b> That is the half a screen cannot
    /// provide: an instrument that has stopped being worth holding will not appear near the top
    /// of a momentum ranking, so a cycle built from the shortlist alone would never look at the
    /// positions that most need looking at.
    /// </para>
    /// <para>
    /// <b>The holdings come first</b> for the same reason the deterministic exits run before the
    /// analyses: a sale frees cash and position headroom, and a buy decided earlier in the cycle
    /// would have been sized without it. The shortlist then follows in its own order, so the
    /// best-ranked candidate has first claim on whatever is left.
    /// </para>
    /// <para>
    /// A holding that is also on the shortlist appears once, as a holding. The honest answer to
    /// "would this have been analysed if the screen had not picked it?" is yes.
    /// </para>
    /// </remarks>
    /// <param name="portfolio">
    /// Null on the first cycle of an account's life, when nothing is stored yet. No holdings is
    /// the right answer to that rather than something for the caller to special-case.
    /// </param>
    public static IReadOnlyList<InstrumentSelection> ForCycle(
        Portfolio? portfolio, IReadOnlyList<Ticker> shortlist)
    {
        var held = portfolio is null
            ? []
            : portfolio.Positions.Select(position => position.Ticker).OrderBy(ticker => ticker.Value).ToArray();

        return
        [
            .. held.Select(ticker => new InstrumentSelection(ticker, SelectionSource.Holding)),
            .. shortlist
                .Where(ticker => !held.Contains(ticker))
                .Select(ticker => new InstrumentSelection(ticker, SelectionSource.Shortlist))
        ];
    }
}
