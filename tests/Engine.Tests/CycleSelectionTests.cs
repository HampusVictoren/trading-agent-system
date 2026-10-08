using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Screening;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Screening;

/// <summary>
/// Which instruments a cycle is about. A pure function over a portfolio and a shortlist, which
/// is what lets the rule that matters most be stated as a test rather than as a comment in a
/// worker: <b>a holding is analysed whatever it ranked.</b>
/// </summary>
public class CycleSelectionTests
{
    private static readonly Ticker Eric = new("ERIC-B.ST");
    private static readonly Ticker Volv = new("VOLV-B.ST");
    private static readonly Ticker Abb = new("ABB.ST");

    private static Portfolio AnAccountHolding(params Ticker[] tickers)
    {
        var portfolio = new Portfolio(new Money(100_000m, Money.DefaultCurrency));

        foreach (var ticker in tickers)
            portfolio.ExecuteBuy(ticker, quantity: 1m, new Money(100m));

        return portfolio;
    }

    [Fact]
    public void An_empty_account_analyses_the_shortlist_and_nothing_else()
    {
        var selection = CycleSelection.ForCycle(AnAccountHolding(), [Abb, Eric]);

        selection.Select(entry => entry.Ticker).ShouldBe([Abb, Eric]);
        selection.ShouldAllBe(entry => entry.Source == SelectionSource.Shortlist);
    }

    [Fact]
    public void A_holding_the_screen_did_not_rank_is_analysed_anyway()
    {
        // The half a screen cannot provide. An instrument that has stopped being worth holding
        // will not appear near the top of a momentum ranking, so a cycle built from the
        // shortlist alone would never look at the positions that most need looking at.
        var selection = CycleSelection.ForCycle(AnAccountHolding(Volv), [Abb]);

        selection.Select(entry => entry.Ticker).ShouldBe([Volv, Abb]);
        selection[0].Source.ShouldBe(SelectionSource.Holding);
        selection[1].Source.ShouldBe(SelectionSource.Shortlist);
    }

    [Fact]
    public void The_holdings_come_before_the_shortlist()
    {
        // The same reasoning as running the exits before the analyses: a sale frees cash and
        // position headroom, and a buy decided earlier in the cycle would have been sized
        // without it.
        var selection = CycleSelection.ForCycle(AnAccountHolding(Volv), [Abb, Eric]);

        selection.Select(entry => entry.Ticker).ShouldBe([Volv, Abb, Eric]);
    }

    [Fact]
    public void The_shortlist_keeps_the_order_it_was_ranked_in()
    {
        // Rank order, so the best-ranked candidate has first claim on whatever cash is left.
        var selection = CycleSelection.ForCycle(AnAccountHolding(), [Volv, Abb, Eric]);

        selection.Select(entry => entry.Ticker).ShouldBe([Volv, Abb, Eric]);
    }

    [Fact]
    public void An_instrument_that_is_both_held_and_ranked_is_analysed_once()
    {
        // Once, and as a holding: the honest answer to "would this have been analysed if the
        // screen had not picked it?" is yes. Twice would be two decisions about one instrument
        // in one cycle, the second of them sized against the first one's purchase.
        var selection = CycleSelection.ForCycle(AnAccountHolding(Eric), [Eric, Abb]);

        selection.Select(entry => entry.Ticker).ShouldBe([Eric, Abb]);
        selection[0].Source.ShouldBe(SelectionSource.Holding);
    }

    [Fact]
    public void Holdings_are_ordered_by_symbol_rather_than_by_however_they_were_read()
    {
        // Not a preference. The order decides which sale frees cash first, so leaving it to
        // whatever the database returned would make a cycle's outcome depend on row order.
        var selection = CycleSelection.ForCycle(AnAccountHolding(Volv, Abb, Eric), []);

        selection.Select(entry => entry.Ticker.Value).ShouldBe(["ABB.ST", "ERIC-B.ST", "VOLV-B.ST"]);
    }

    [Fact]
    public void No_portfolio_yet_is_no_holdings_rather_than_a_special_case()
    {
        // The first cycle of an account's life, before anything is stored.
        var selection = CycleSelection.ForCycle(portfolio: null, [Abb]);

        selection.Select(entry => entry.Ticker).ShouldBe([Abb]);
    }

    [Fact]
    public void Nothing_held_and_nothing_ranked_is_a_cycle_with_nothing_to_do()
    {
        // The state the engine is in when a screen failed against an empty account. It has to be
        // an empty list rather than anything else: the worker's loop over it is what does nothing.
        CycleSelection.ForCycle(AnAccountHolding(), []).ShouldBeEmpty();
    }
}
