using Engine.Domain.Outcomes;
using Shouldly;

namespace Engine.Tests.Domain;

/// <summary>
/// The trading calendar, such as it is. Finding E asked for one and got a rule instead: a
/// day with a bar is a day the market was open. These tests are what says that rule survives
/// weekends, holidays, a horizon that has not passed, and data that has not arrived.
/// </summary>
public class BarSeriesTests
{
    private static DateOnly Day(int day) => new(2026, 9, day);

    /// <summary>
    /// A real fortnight: 21 September 2026 is a Monday, so the 26th and 27th are a weekend,
    /// and the 24th is left out as if it were a holiday.
    /// </summary>
    private static BarSeries AWeekAndABit() => new(
    [
        new PriceBar(Day(21), 100m),
        new PriceBar(Day(22), 101m),
        new PriceBar(Day(23), 102m),
        // no bar on the 24th - a holiday
        new PriceBar(Day(25), 104m),
        // the 26th and 27th are a weekend
        new PriceBar(Day(28), 105m),
        new PriceBar(Day(29), 106m),
    ]);

    [Fact]
    public void Bars_out_of_order_are_refused()
    {
        // Every lookup takes the last bar as the latest. A series in the wrong order would
        // make all of them quietly wrong rather than fail.
        Should.Throw<ArgumentException>(() => new BarSeries(
            [new PriceBar(Day(22), 101m), new PriceBar(Day(21), 100m)]));
    }

    [Fact]
    public void A_repeated_day_is_refused()
    {
        Should.Throw<ArgumentException>(() => new BarSeries(
            [new PriceBar(Day(21), 100m), new PriceBar(Day(21), 100m)]));
    }

    [Fact]
    public void A_close_of_zero_is_refused()
    {
        // A return is divided by this. Zero is not a low price; it is a missing one.
        Should.Throw<ArgumentOutOfRangeException>(() => new PriceBar(Day(21), 0m));
    }

    [Fact]
    public void A_with_expression_cannot_smuggle_a_bad_close_past_the_rule()
    {
        var bar = new PriceBar(Day(21), 100m);

        Should.Throw<ArgumentOutOfRangeException>(() => bar with { Close = -1m });
    }

    [Fact]
    public void The_signals_own_day_does_not_count_towards_the_horizon()
    {
        // The day the decision was made is not a day the thesis had to play out in.
        AWeekAndABit().TradingDaysAfter(Day(21), 1)!.On.ShouldBe(Day(22));
    }

    [Fact]
    public void A_horizon_counts_bars_and_so_skips_a_holiday()
    {
        // Two trading days after the 23rd is the 25th and then the 28th, because the 24th is
        // a holiday and the 26th and 27th are a weekend. No holiday table said so; the
        // absence of bars did.
        AWeekAndABit().TradingDaysAfter(Day(23), 2)!.On.ShouldBe(Day(28));
    }

    [Fact]
    public void A_signal_made_on_a_day_with_no_bar_starts_from_the_next_one()
    {
        // The engine analyses at any hour, including on a closed market.
        AWeekAndABit().TradingDaysAfter(Day(27), 1)!.On.ShouldBe(Day(28));
    }

    [Fact]
    public void A_horizon_that_has_not_passed_yet_is_not_an_answer()
    {
        // Not an error either: a scheduled job has to be able to tell "try again tomorrow"
        // from "this will never be measurable".
        AWeekAndABit().TradingDaysAfter(Day(28), 5).ShouldBeNull();
    }

    [Fact]
    public void A_calendar_horizon_lands_on_the_last_close_before_it()
    {
        // The 27th is a Sunday. The honest answer is Friday the 25th's close.
        AWeekAndABit().LastOnOrBefore(Day(27))!.On.ShouldBe(Day(25));
    }

    [Fact]
    public void A_calendar_horizon_the_data_has_not_reached_is_not_an_answer()
    {
        // The series ends on the 29th. Asking about the 30th would otherwise answer with the
        // 29th's close, which is a measurement of a horizon that has not closed.
        AWeekAndABit().LastOnOrBefore(Day(30)).ShouldBeNull();
    }

    [Fact]
    public void A_calendar_horizon_before_the_series_begins_is_not_an_answer()
    {
        AWeekAndABit().LastOnOrBefore(Day(20)).ShouldBeNull();
    }

    [Fact]
    public void The_two_units_are_counted_differently_from_the_same_day()
    {
        // Five days from Monday the 21st: five *trading* days is the 29th, because two
        // non-trading days fall in between; five *calendar* days is the 25th, because the
        // 26th is a Saturday. Reading one as the other would move a measurement by most of
        // a week.
        var series = AWeekAndABit();

        series.At(Day(21), new Horizon.TradingDays(5))!.On.ShouldBe(Day(29));
        series.At(Day(21), new Horizon.CalendarDays(5))!.On.ShouldBe(Day(25));
    }

    [Fact]
    public void A_horizon_of_less_than_a_day_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new Horizon.TradingDays(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new Horizon.CalendarDays(-1));
    }

    [Fact]
    public void An_empty_series_answers_nothing_rather_than_throwing()
    {
        var empty = new BarSeries([]);

        empty.Latest.ShouldBeNull();
        empty.TradingDaysAfter(Day(21), 1).ShouldBeNull();
        empty.LastOnOrBefore(Day(21)).ShouldBeNull();
    }
}
