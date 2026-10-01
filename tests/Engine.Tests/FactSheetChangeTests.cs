using Engine.Domain.Signals;
using Shouldly;

namespace Engine.Tests.Domain.Signals;

/// <summary>
/// When an instrument is worth three LLM calls again.
/// </summary>
/// <remarks>
/// The rule is two conditions because the fact sheet has two kinds of field. The returns, the
/// volatility and the turnover come from daily bars; the price is the provider's live quote, and
/// the distance to the 52-week high is computed from it. Either half alone leaves a hole - the
/// day rule would analyse three times over a weekend, and the price rule would analyse every
/// quarter of an hour while the market is open.
/// </remarks>
public class FactSheetChangeTests
{
    private static readonly DateOnly Monday = new(2026, 9, 28);

    [Fact]
    public void An_instrument_that_has_never_been_analysed_is_due()
    {
        FactSheetChange.Verdict(last: null, price: 100m, Monday).ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public void An_instrument_analysed_today_is_not_asked_about_again()
    {
        // The half that stops one question being re-asked every quarter of an hour. Before this,
        // the engine asked about the same fact sheet every fifteen seconds - which is the part of
        // finding G that survived the model change.
        var last = new LastAnalysis(Monday, 100m);

        FactSheetChange.Verdict(last, price: 104m, Monday).ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
    }

    [Fact]
    public void Today_and_an_unchanged_price_is_reported_as_today()
    {
        // The commonest state of all: a second cycle a quarter of an hour later, nothing moved.
        // Both verdicts skip, so trading does not depend on which one it is - but the cycle's
        // summary line counts them separately, and that line exists precisely to tell "already
        // done today" apart from "the market is shut". Found by mutation: swapping the order of
        // the two checks left every other test in this file green.
        var last = new LastAnalysis(Monday, 100m);

        FactSheetChange.Verdict(last, price: 100m, Monday).ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
    }

    [Fact]
    public void Today_wins_over_the_price_having_moved()
    {
        // Stated as its own test because the order of the two checks is the rule. A price that has
        // moved since this morning's analysis is the normal state of an open market, and treating
        // it as a reason to analyse again would make the day rule unreachable.
        var last = new LastAnalysis(Monday, 100m);

        FactSheetChange.Verdict(last, price: 250m, Monday).ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
    }

    [Fact]
    public void A_new_day_at_the_same_price_is_a_day_the_market_was_shut()
    {
        // Saturday, and the quote is still Friday's close. No calendar was consulted: a market
        // that is not trading cannot move a price, so the engine waits by itself.
        var last = new LastAnalysis(Monday.AddDays(-1), 100m);

        FactSheetChange.Verdict(last, price: 100m, Monday).ShouldBe(AnalysisVerdict.PriceHasNotMoved);
    }

    [Fact]
    public void A_new_day_at_a_new_price_is_due()
    {
        var last = new LastAnalysis(Monday.AddDays(-1), 100m);

        FactSheetChange.Verdict(last, price: 100.5m, Monday).ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public void A_move_of_one_ore_counts()
    {
        // Equality on the exact number rather than a tolerance. A tolerance would be a second,
        // unstated opinion about how much movement matters - and the two prices come from the same
        // provider through the same field, so "unchanged" really does mean the same number.
        var last = new LastAnalysis(Monday.AddDays(-1), 100m);

        FactSheetChange.Verdict(last, price: 100.01m, Monday).ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public void A_new_day_with_no_quote_is_due()
    {
        // Falling back to the day rule. Refusing to analyse because a price could not be fetched
        // would let one flaky lookup cost the day - and the analysis has its own market data on
        // the other side, so the quote is only ever evidence about whether to bother.
        var last = new LastAnalysis(Monday.AddDays(-1), 100m);

        FactSheetChange.Verdict(last, price: null, Monday).ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public void Today_with_no_quote_is_still_not_asked_about_again()
    {
        var last = new LastAnalysis(Monday, 100m);

        FactSheetChange.Verdict(last, price: null, Monday).ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
    }

    [Fact]
    public void An_analysis_dated_after_today_is_treated_as_today()
    {
        // A clock that went backwards, or a row written by a process ahead of this one. Greater
        // than or equal rather than equal, so the engine does not respond to it by analysing the
        // same instrument every cycle until the calendar catches up.
        var last = new LastAnalysis(Monday.AddDays(1), 100m);

        FactSheetChange.Verdict(last, price: 250m, Monday).ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
    }
}
