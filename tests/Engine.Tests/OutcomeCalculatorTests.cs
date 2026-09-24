using Engine.Domain.Outcomes;
using Engine.Domain.Signals;
using Shouldly;

namespace Engine.Tests.Domain;

/// <summary>
/// The function stage 4 exists to produce. Every case is written with the expected figure
/// beside it, because that figure is the specification: a measurement that is wrong here is
/// a conclusion that is wrong later, and by then nothing says so.
/// </summary>
public class OutcomeCalculatorTests
{
    private static DateOnly Day(int day) => new(2026, 9, day);

    private const decimal ReferencePrice = 100m;

    /// <summary>Five basis points of commission and five of spread, per side: a round trip
    /// costs 0.002 of notional.</summary>
    private static readonly OutcomePolicy Policy = new(commissionBps: 5m, spreadBps: 5m, holdBandPct: 0.02m);

    private static readonly OutcomePolicy Free = new(commissionBps: 0m, spreadBps: 0m, holdBandPct: 0.02m);

    private readonly OutcomeCalculator _sut = new();

    /// <summary>21 September 2026 is a Monday; the 24th is left out as a holiday and the
    /// 26th and 27th are a weekend.</summary>
    private static BarSeries Instrument(params decimal[] closes) => Series(closes);

    private static BarSeries Series(decimal[] closes)
    {
        int[] days = [22, 23, 25, 28, 29];
        return new BarSeries(closes.Select((close, i) => new PriceBar(Day(days[i]), close)));
    }

    /// <summary>The benchmark also has a close on the signal's own day, which is what a
    /// comparison is measured from.</summary>
    private static BarSeries Benchmark(params decimal[] closes)
    {
        int[] days = [21, 22, 23, 25, 28, 29];
        return new BarSeries(closes.Select((close, i) => new PriceBar(Day(days[i]), close)));
    }

    private static SignalToMeasure ASignal(Stance stance, Horizon? horizon = null) =>
        new(stance, ReferencePrice, Day(21), horizon ?? new Horizon.TradingDays(1));

    private OutcomeResult.Measured Measure(
        SignalToMeasure signal, BarSeries instrument, BarSeries benchmark, OutcomePolicy? policy = null) =>
        _sut.Measure(signal, instrument, benchmark, policy ?? Policy)
            .ShouldBeOfType<OutcomeResult.Measured>();

    [Fact]
    public void A_buy_that_beat_the_index_is_a_hit()
    {
        // The instrument went 100 to 102, so two per cent; the index 400 to 404, so one.
        // The excess is one per cent, the round trip costs 0.2, and 0.8 is left.
        var result = Measure(
            ASignal(Stance.Buy),
            Instrument(102m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m));

        result.On.ShouldBe(Day(22));
        result.Price.ShouldBe(102m);
        result.InstrumentReturn.ShouldBe(0.02m);
        result.BenchmarkReturn.ShouldBe(0.01m);
        result.ExcessReturn.ShouldBe(0.01m);
        result.CostFraction.ShouldBe(0.002m);
        result.NetEdge.ShouldBe(0.008m);
        result.Hit.ShouldBeTrue();
    }

    [Fact]
    public void Costs_are_what_turn_a_thin_win_into_a_loss()
    {
        // Finding F, as a single test. The same bars and the same signal: with costs the
        // view was wrong, without them it was right. Over a horizon of days that difference
        // is often the whole of the edge, which is why the measurement carries it.
        var signal = ASignal(Stance.Buy);
        var instrument = Instrument(101.1m, 103m, 105m, 110m, 106m);
        var benchmark = Benchmark(400m, 404m, 408m, 412m, 420m, 416m);

        var charged = Measure(signal, instrument, benchmark);
        var free = Measure(signal, instrument, benchmark, Free);

        charged.ExcessReturn.ShouldBe(0.001m);
        charged.NetEdge.ShouldBe(-0.001m);
        charged.Hit.ShouldBeFalse();

        free.ExcessReturn.ShouldBe(0.001m);
        free.NetEdge.ShouldBe(0.001m);
        free.Hit.ShouldBeTrue();
    }

    [Fact]
    public void A_sell_is_right_when_the_instrument_does_worse_than_the_index()
    {
        // Down two per cent while the index rose one: the view was worth three per cent,
        // less the same round trip, because a short is a trade too.
        var result = Measure(
            ASignal(Stance.Sell),
            Instrument(98m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m));

        result.ExcessReturn.ShouldBe(-0.03m);
        result.NetEdge.ShouldBe(0.028m);
        result.Hit.ShouldBeTrue();
    }

    [Fact]
    public void A_sell_on_an_instrument_that_beat_the_index_is_a_miss()
    {
        var result = Measure(
            ASignal(Stance.Sell),
            Instrument(102m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m));

        result.NetEdge.ShouldBe(-0.012m);
        result.Hit.ShouldBeFalse();
    }

    [Fact]
    public void A_hold_is_right_while_the_instrument_stays_near_the_index()
    {
        // Half a per cent against the index's one: a deviation of 0.5, inside the two per
        // cent band.
        var result = Measure(
            ASignal(Stance.Hold),
            Instrument(100.5m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m));

        result.ExcessReturn.ShouldBe(-0.005m);
        result.Hit.ShouldBeTrue();
    }

    [Fact]
    public void A_hold_on_something_that_ran_away_is_a_miss()
    {
        // Five per cent against the index's one is four per cent of movement the view said
        // would not happen.
        var result = Measure(
            ASignal(Stance.Hold),
            Instrument(105m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m));

        result.ExcessReturn.ShouldBe(0.04m);
        result.Hit.ShouldBeFalse();
    }

    [Fact]
    public void A_hold_pays_nothing_because_it_implies_no_trade()
    {
        // Charging it would penalise the one stance that costs nothing to follow, and would
        // make doing nothing look worse than it is.
        var expensive = new OutcomePolicy(commissionBps: 100m, spreadBps: 100m, holdBandPct: 0.02m);

        var result = Measure(
            ASignal(Stance.Hold),
            Instrument(100.5m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m),
            expensive);

        result.CostFraction.ShouldBe(0m);
        result.NetEdge.ShouldBeNull();
        result.Hit.ShouldBeTrue();
    }

    [Fact]
    public void The_two_horizon_units_land_on_different_days()
    {
        // Five trading days from Monday the 21st is the 29th; five calendar days is the
        // 25th, because the 26th is a Saturday. The same signal, measured a week apart.
        var instrument = Instrument(102m, 103m, 105m, 110m, 106m);
        var benchmark = Benchmark(400m, 404m, 408m, 412m, 420m, 416m);

        Measure(ASignal(Stance.Buy, new Horizon.TradingDays(5)), instrument, benchmark).On.ShouldBe(Day(29));
        Measure(ASignal(Stance.Buy, new Horizon.CalendarDays(5)), instrument, benchmark).On.ShouldBe(Day(25));
    }

    [Fact]
    public void A_horizon_that_has_not_passed_is_not_due_rather_than_missing()
    {
        // The difference is the whole point: a scheduled job asks again tomorrow for this
        // one, and never asks again for the other.
        var result = _sut.Measure(
            ASignal(Stance.Buy, new Horizon.TradingDays(20)),
            Instrument(102m, 103m, 105m, 110m, 106m),
            Benchmark(400m, 404m, 408m, 412m, 420m, 416m),
            Policy);

        result.ShouldBeOfType<OutcomeResult.NotDue>().Reason.ShouldContain("20 trading days");
    }

    [Fact]
    public void A_benchmark_that_has_not_caught_up_is_not_due_either()
    {
        // The index's data arrives a day late. Nothing is wrong; it is early.
        var benchmark = new BarSeries([new PriceBar(Day(21), 400m)]);

        var result = _sut.Measure(
            ASignal(Stance.Buy), Instrument(102m, 103m, 105m, 110m, 106m), benchmark, Policy);

        result.ShouldBeOfType<OutcomeResult.NotDue>();
    }

    [Fact]
    public void A_benchmark_with_no_close_before_the_signal_can_never_be_measured()
    {
        // A hole rather than a delay: the index has data after the signal but none at or
        // before it, so there is nothing to measure the comparison from, today or ever.
        var benchmark = new BarSeries(
            [new PriceBar(Day(22), 404m), new PriceBar(Day(23), 408m), new PriceBar(Day(25), 412m)]);

        var result = _sut.Measure(
            ASignal(Stance.Buy), Instrument(102m, 103m, 105m, 110m, 106m), benchmark, Policy);

        result.ShouldBeOfType<OutcomeResult.NotMeasurable>().Reason.ShouldContain("the signal was made");
    }

    [Fact]
    public void The_benchmarks_own_closed_days_do_not_stop_a_measurement()
    {
        // The index has no bar on the day the horizon landed - a different exchange holiday.
        // Its last close before that day is the honest comparison, and waiting for one that
        // will never come would lose the measurement.
        var benchmark = new BarSeries(
            [new PriceBar(Day(21), 400m), new PriceBar(Day(23), 408m), new PriceBar(Day(25), 412m)]);

        var result = Measure(ASignal(Stance.Buy), Instrument(102m, 103m, 105m, 110m, 106m), benchmark);

        // The 22nd has no index close, so the 21st's is used: no movement, and the whole two
        // per cent counts as excess.
        result.BenchmarkReturn.ShouldBe(0m);
        result.ExcessReturn.ShouldBe(0.02m);
    }

    [Fact]
    public void A_cost_or_a_band_that_makes_no_sense_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new OutcomePolicy(-1m, 0m, 0.02m));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutcomePolicy(0m, -1m, 0.02m));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutcomePolicy(0m, 0m, 1.5m));
    }
}
