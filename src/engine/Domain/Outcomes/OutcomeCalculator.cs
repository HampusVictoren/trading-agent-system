namespace Engine.Domain.Outcomes;

using Engine.Domain.Signals;

/// <summary>
/// Turns a stored decision and the market that followed it into a number. This is the
/// function stage 4 exists to produce: it is what makes "are the agents any good" a
/// question with an answer.
/// </summary>
/// <remarks>
/// A pure function of its arguments, holding nothing. Bars go in, a verdict comes out - so
/// every case it has to handle can be written as a test with the expected figure beside it,
/// which is exactly where writing the test first pays.
/// </remarks>
public sealed class OutcomeCalculator
{
    /// <summary>
    /// Four decimals is a basis point; six is a ten-thousandth of one. Past that it is
    /// precision the input never had, and a long decimal tail in a report that reads as
    /// certainty.
    /// </summary>
    private const int Precision = 6;

    /// <summary>
    /// Measures one signal at one horizon. Both series are the caller's business to fetch;
    /// the benchmark is whichever index the policy names.
    /// </summary>
    public OutcomeResult Measure(
        SignalToMeasure signal, BarSeries instrument, BarSeries benchmark, OutcomePolicy policy)
    {
        var landed = instrument.At(signal.SignalDate, signal.Horizon);

        if (landed is null)
        {
            return new OutcomeResult.NotDue(
                $"the instrument has no bar {signal.Horizon.Days} {Unit(signal.Horizon)} after {signal.SignalDate:O}");
        }

        // The benchmark is compared from the signal's day to the same bar. Its base is a
        // close while the instrument's is the price the decision was actually made at - an
        // asymmetry, and a deliberate one: the reference price is what the engine would have
        // paid, and replacing it with that day's close would measure a trade nobody made.
        var benchmarkBase = BenchmarkAt(benchmark, signal.SignalDate);
        if (benchmarkBase is not PriceBar start)
            return Missing(benchmark, signal.SignalDate, "the signal was made");

        var benchmarkEnd = BenchmarkAt(benchmark, landed.On);
        if (benchmarkEnd is not PriceBar finish)
            return Missing(benchmark, landed.On, "the horizon landed");

        var instrumentReturn = Round((landed.Close - signal.ReferencePrice) / signal.ReferencePrice);
        var benchmarkReturn = Round((finish.Close - start.Close) / start.Close);
        var excess = Round(instrumentReturn - benchmarkReturn);

        // HOLD implies no trade, so it pays nothing. Charging it would penalise the one
        // stance that costs nothing to follow.
        var cost = signal.Stance == Stance.Hold ? 0m : policy.RoundTripFraction;

        // The cost is charged to the instrument leg only. The benchmark is a yardstick, not
        // something the engine would ever buy, so charging it costs would invent a trade
        // nobody made - and would flatter the agents, which is the wrong way to be wrong
        // about a number meant to decide whether this project is worth continuing.
        decimal? netEdge = signal.Stance switch
        {
            Stance.Buy => Round(excess - cost),
            Stance.Sell => Round(-excess - cost),
            _ => null
        };

        var hit = signal.Stance switch
        {
            Stance.Buy or Stance.Sell => netEdge > 0m,
            _ => Math.Abs(excess) <= policy.HoldBandPct
        };

        return new OutcomeResult.Measured(
            landed.On, landed.Close, instrumentReturn, benchmarkReturn, excess, cost, netEdge, hit);
    }

    /// <summary>The benchmark's close for a day, which on a closed day is the last one before it.</summary>
    private static PriceBar? BenchmarkAt(BarSeries benchmark, DateOnly on) => benchmark.LastOnOrBefore(on);

    /// <summary>
    /// Tells a benchmark that has not caught up from one with a hole in it. The first is
    /// worth asking about again tomorrow; the second never will be.
    /// </summary>
    private static OutcomeResult Missing(BarSeries benchmark, DateOnly on, string what)
    {
        var latest = benchmark.Latest;

        return latest is null || latest.On < on
            ? new OutcomeResult.NotDue($"the benchmark has no close yet for the day {what}, {on:O}")
            : new OutcomeResult.NotMeasurable($"the benchmark has no close on or before the day {what}, {on:O}");
    }

    private static string Unit(Horizon horizon) =>
        horizon is Horizon.TradingDays ? "trading days" : "calendar days";

    private static decimal Round(decimal value) => Math.Round(value, Precision, MidpointRounding.ToEven);
}
