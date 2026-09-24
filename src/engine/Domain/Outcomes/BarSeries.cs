namespace Engine.Domain.Outcomes;

/// <summary>
/// A price series, oldest first, and the only trading calendar this system has.
/// </summary>
/// <remarks>
/// <para>
/// Finding E asked for a trading calendar and got this instead. A day with a bar is a day
/// the market was open: there is no holiday table to maintain, it is right for whichever
/// exchange the instrument trades on without anyone saying which, and a missing day is a
/// fact about the data rather than an assumption about the world. What it deliberately does
/// not answer is "is the market open right now" - that is a question about cadence, which
/// belongs to stage 5.
/// </para>
/// <para>
/// Every lookup can answer "not yet". A horizon that has not passed is not an error, and
/// telling it apart from a horizon that never will is what lets a scheduled job know whether
/// to try again.
/// </para>
/// </remarks>
public sealed class BarSeries
{
    private readonly IReadOnlyList<PriceBar> _bars;

    public BarSeries(IEnumerable<PriceBar> bars)
    {
        _bars = [.. bars];

        for (var i = 1; i < _bars.Count; i++)
        {
            // Every method below takes the last bar as the latest one, so a series in the
            // wrong order would make all of them quietly wrong rather than fail.
            if (_bars[i].On <= _bars[i - 1].On)
            {
                throw new ArgumentException(
                    $"Bars must be oldest first with no repeats; {_bars[i].On:O} follows {_bars[i - 1].On:O}.",
                    nameof(bars));
            }
        }
    }

    public int Count => _bars.Count;

    public PriceBar? Latest => _bars.Count == 0 ? null : _bars[^1];

    /// <summary>
    /// The <paramref name="count"/>-th bar after <paramref name="start"/>, or null when that
    /// many trading days have not passed yet.
    /// </summary>
    /// <remarks>
    /// Strictly after: the signal's own day is the day the decision was made, not a day the
    /// thesis had to play out in. A horizon that lands on a weekend or a holiday needs no
    /// special case, because such a day has no bar to land on.
    /// </remarks>
    public PriceBar? TradingDaysAfter(DateOnly start, int count)
    {
        var seen = 0;

        foreach (var bar in _bars)
        {
            if (bar.On <= start)
                continue;

            if (++seen == count)
                return bar;
        }

        return null;
    }

    /// <summary>
    /// The last bar at or before <paramref name="date"/>, or null when the series does not
    /// yet reach that far.
    /// </summary>
    /// <remarks>
    /// The null case is the important one. If the target date is a Saturday, the answer is
    /// Friday's close - but only once a bar on or after the Saturday exists, because until
    /// then there is no way to tell "the market was shut" from "the data has not arrived".
    /// Waiting costs a day and returns the same number; not waiting measures a horizon that
    /// has not closed.
    /// </remarks>
    public PriceBar? LastOnOrBefore(DateOnly date)
    {
        if (_bars.Count == 0 || _bars[^1].On < date)
            return null;

        PriceBar? found = null;

        foreach (var bar in _bars)
        {
            if (bar.On > date)
                break;

            found = bar;
        }

        return found;
    }

    /// <summary>The bar a horizon lands on, whichever unit it is counted in.</summary>
    public PriceBar? At(DateOnly signalDate, Horizon horizon) => horizon switch
    {
        Horizon.TradingDays trading => TradingDaysAfter(signalDate, trading.Days),
        Horizon.CalendarDays calendar => LastOnOrBefore(signalDate.AddDays(calendar.Days)),

        // Unreachable: the hierarchy is closed by its private constructor. A discard arm is
        // the price of a switch expression over one, and saying so beats returning null.
        _ => throw new ArgumentOutOfRangeException(nameof(horizon), horizon, "Unknown horizon unit.")
    };
}
