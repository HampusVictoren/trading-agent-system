namespace Engine.Domain.Signals;

/// <summary>
/// The last time an instrument was actually analysed: the trading day, and the price the agents
/// were shown.
/// </summary>
/// <remarks>
/// A cycle that never reached an answer is not an analysis and does not count as one. That is
/// deliberate rather than incidental: an agent service that was down at nine o'clock should be
/// asked again at nine fifteen, and a rule that counted the attempt would turn a two-minute
/// outage into a lost trading day.
/// </remarks>
public sealed record LastAnalysis(DateOnly On, decimal Price);

/// <summary>Whether an instrument is worth analysing again, and if not, why not.</summary>
public enum AnalysisVerdict
{
    /// <summary>Nothing has been asked about it today, and the price is not where it was.</summary>
    Due,

    /// <summary>It has already been analysed today, so the fact sheet is the one that was used.</summary>
    AlreadyAnalysedToday,

    /// <summary>A new day, but the price has not moved - so nothing the agents read has changed.</summary>
    PriceHasNotMoved
}

/// <summary>
/// Whether an instrument's fact sheet has changed enough to be worth three LLM calls.
/// </summary>
/// <remarks>
/// <para>
/// The roadmap assumed the fact sheet changes once a trading day, because the factors are built
/// from daily bars. It is half right. The returns, the volatility and the turnover are daily,
/// but the <i>price</i> is the provider's live quote, and the distance to the 52-week high is
/// computed from it - so during market hours the fact sheet does move, continuously, and a rule
/// written on the price alone would never skip anything between nine and half past five.
/// </para>
/// <para>
/// So both halves are needed, and each covers the other's blind spot. The day rule stops a
/// question being re-asked every quarter of an hour while the market is open. The price rule
/// stops it being asked at all when the market is shut: the quote is frozen at Friday's close
/// all weekend, so Saturday and Sunday are new days on which nothing has changed. Together they
/// come to <b>at most one analysis per instrument per trading day, and none on a day that is not
/// one</b> - which is a trading calendar the engine never has to be told.
/// </para>
/// <para>
/// What it gives up is a reaction to an intraday move. That is the deterministic exits' job, and
/// the reason they take no signal and run every cycle: a position falling through its stop-loss
/// at eleven o'clock is closed at eleven o'clock, without anybody being asked.
/// </para>
/// </remarks>
public static class FactSheetChange
{
    /// <param name="last">Null when the instrument has never reached an answer.</param>
    /// <param name="price">
    /// The price now, or null when no usable quote could be had. A missing quote falls back to
    /// the day rule alone: refusing to analyse because a price could not be fetched would let one
    /// flaky lookup cost the day, and the analysis has its own market data on the other side.
    /// </param>
    public static AnalysisVerdict Verdict(LastAnalysis? last, decimal? price, DateOnly today)
    {
        if (last is null)
            return AnalysisVerdict.Due;

        if (last.On >= today)
            return AnalysisVerdict.AlreadyAnalysedToday;

        // Equality on the exact number, not a tolerance. The two prices come from the same
        // provider through the same field, so "unchanged" means the same bytes - and a tolerance
        // would be a second, unstated opinion about how much movement matters.
        if (price == last.Price)
            return AnalysisVerdict.PriceHasNotMoved;

        return AnalysisVerdict.Due;
    }
}
