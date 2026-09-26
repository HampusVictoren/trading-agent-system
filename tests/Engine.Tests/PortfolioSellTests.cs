namespace Engine.Tests.Domain;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;
using Shouldly;

/// <summary>
/// Selling, which is asymmetric with buying in every way that matters.
/// </summary>
/// <remarks>
/// A sale raises cash rather than spending it and reduces a position rather than growing one,
/// so neither the cash check nor the position limit applies. What does apply is the question a
/// buy never has to ask - whether the shares are there - and the figure a buy never produces,
/// which is what was realised.
/// </remarks>
public class PortfolioSellTests
{
    private static readonly Ticker Eric = new("ERIC-B.ST");
    private static readonly DateTimeOffset Bought = PortfolioTestExtensions.BoughtAt;

    private const int Horizon = PortfolioTestExtensions.ThesisHorizonDays;

    /// <summary>A portfolio holding `quantity` of ERIC-B.ST at `at`, and the cash that is left.</summary>
    private static Portfolio Holding(decimal quantity, decimal at, decimal cash = 100_000m)
    {
        var portfolio = new Portfolio(new Money(cash));
        portfolio.ExecuteBuy(Eric, quantity, new Money(at), Bought, Horizon);
        return portfolio;
    }

    private static Position TheHolding(Portfolio portfolio) =>
        portfolio.Positions.Single(held => held.Ticker == Eric);

    public class ASaleMovesTheCashAndTheShares : PortfolioSellTests
    {
        [Fact]
        public void Part_of_a_holding_leaves_the_rest_behind()
        {
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteSell(Eric, 10m, new Money(100m));

            TheHolding(portfolio).Quantity.ShouldBe(16m);
        }

        [Fact]
        public void The_proceeds_reach_the_cash_balance()
        {
            // 100 000 - 26 x 95 = 97 530 after the buy, then + 10 x 100.
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteSell(Eric, 10m, new Money(100m));

            portfolio.CashBalance.Amount.ShouldBe(98_530m);
        }

        [Fact]
        public void Selling_the_whole_holding_removes_it_rather_than_leaving_a_zero()
        {
            // "One row per holding" has to keep meaning what it says, and the position cap
            // should see a clean slate on the next buy rather than a row of no shares.
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteSell(Eric, 26m, new Money(100m));

            portfolio.Positions.ShouldBeEmpty();
        }

        [Fact]
        public void The_ledger_gets_a_sell_line()
        {
            var portfolio = Holding(quantity: 26m, at: 95m);

            var order = portfolio.ExecuteSell(Eric, 10m, new Money(100m));

            order.Side.ShouldBe(OrderSide.Sell);
            order.Quantity.ShouldBe(10m);
            order.Price.Amount.ShouldBe(100m);
            portfolio.NewOrders.Count.ShouldBe(2);
        }
    }

    public class WhatWasRealised : PortfolioSellTests
    {
        [Theory]
        // bought at, sold at, quantity sold, realised
        [InlineData(95, 100, 10, 50)]
        [InlineData(95, 90, 10, -50)]
        [InlineData(95, 95, 10, 0)]
        [InlineData(100, 325.20, 1, 225.20)]
        public void Is_the_difference_against_what_the_shares_cost(
            decimal bought, decimal sold, decimal quantity, decimal expected)
        {
            // A loss is a figure rather than an error, which is why Money permits a negative
            // amount and this is not clamped at zero.
            var portfolio = Holding(quantity: 26m, at: bought);

            var order = portfolio.ExecuteSell(Eric, quantity, new Money(sold));

            order.RealisedProfitAndLoss!.Amount.ShouldBe(expected);
        }

        [Fact]
        public void Is_null_on_a_buy_because_a_purchase_realises_nothing()
        {
            var portfolio = new Portfolio(new Money(100_000m));

            var order = portfolio.ExecuteBuy(Eric, 10m, new Money(95m), Bought, Horizon);

            order.RealisedProfitAndLoss.ShouldBeNull();
        }

        [Fact]
        public void Is_measured_against_the_average_when_a_holding_was_built_in_two_buys()
        {
            // 10 at 90 and 10 at 110 averages 100, so selling at 120 realises 20 a share -
            // not 30 against the first buy or 10 against the second.
            var portfolio = new Portfolio(new Money(100_000m));
            portfolio.ExecuteBuy(Eric, 10m, new Money(90m), Bought, Horizon);
            portfolio.ExecuteBuy(Eric, 10m, new Money(110m), Bought, Horizon);

            var order = portfolio.ExecuteSell(Eric, 5m, new Money(120m));

            order.RealisedProfitAndLoss!.Amount.ShouldBe(100m);
        }

        [Fact]
        public void Leaves_the_average_purchase_price_where_it_was()
        {
            // Selling does not change what the remaining shares cost. An average that moved on
            // a sale would make every later realised figure wrong.
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteSell(Eric, 10m, new Money(500m));

            TheHolding(portfolio).AveragePurchasePrice.Amount.ShouldBe(95m);
        }
    }

    public class WhatASaleRefuses : PortfolioSellTests
    {
        [Fact]
        public void Selling_more_than_is_held_is_refused()
        {
            // No short selling. It is a deliberate no, and it is enforced here as well as in
            // sizing because this is the only code that moves shares.
            var portfolio = Holding(quantity: 26m, at: 95m);

            Should.Throw<InvalidOperationException>(
                () => portfolio.ExecuteSell(Eric, 27m, new Money(100m)));
        }

        [Fact]
        public void Selling_something_that_is_not_held_is_refused()
        {
            var portfolio = new Portfolio(new Money(100_000m));

            Should.Throw<InvalidOperationException>(
                () => portfolio.ExecuteSell(Eric, 1m, new Money(100m)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Selling_a_quantity_that_is_not_a_quantity_is_refused(decimal quantity)
        {
            var portfolio = Holding(quantity: 26m, at: 95m);

            Should.Throw<ArgumentOutOfRangeException>(
                () => portfolio.ExecuteSell(Eric, quantity, new Money(100m)));
        }

        [Fact]
        public void A_price_in_another_currency_is_a_bug_not_an_outcome()
        {
            var portfolio = Holding(quantity: 26m, at: 95m);

            Should.Throw<CurrencyMismatchException>(
                () => portfolio.ExecuteSell(Eric, 10m, new Money(100m, "EUR")));
        }

        [Fact]
        public void A_refused_sale_leaves_the_cash_and_the_shares_alone()
        {
            // The position is what refuses an impossible quantity, and it is reduced before the
            // cash moves - so a refusal cannot half-happen.
            var portfolio = Holding(quantity: 26m, at: 95m);
            var cashBefore = portfolio.CashBalance;

            Should.Throw<InvalidOperationException>(
                () => portfolio.ExecuteSell(Eric, 27m, new Money(100m)));

            portfolio.CashBalance.ShouldBe(cashBefore);
            TheHolding(portfolio).Quantity.ShouldBe(26m);
            portfolio.NewOrders.Count.ShouldBe(1);
        }
    }

    public class TheClockTheExitsWillRead : PortfolioSellTests
    {
        private static readonly DateTimeOffset Later = Bought.AddDays(3);

        [Fact]
        public void A_buy_stamps_the_holding_with_when_and_on_what_thesis()
        {
            var portfolio = Holding(quantity: 26m, at: 95m);

            TheHolding(portfolio).LastPurchasedAt.ShouldBe(Bought);
            TheHolding(portfolio).ThesisHorizonDays.ShouldBe(Horizon);
        }

        [Fact]
        public void Adding_to_a_holding_restarts_the_clock_on_the_new_thesis()
        {
            // Including a top-up of a winner: the thesis being acted on is the new one, and it
            // is the new one the exits should judge.
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteBuy(Eric, 10m, new Money(100m), Later, horizonDays: 7);

            TheHolding(portfolio).LastPurchasedAt.ShouldBe(Later);
            TheHolding(portfolio).ThesisHorizonDays.ShouldBe(7);
        }

        [Fact]
        public void A_sale_leaves_the_clock_alone()
        {
            // Reducing a position does not restart its thesis. This is what makes "a HOLD does
            // not extend the clock" a property of the design: only a buy moves these.
            var portfolio = Holding(quantity: 26m, at: 95m);

            portfolio.ExecuteSell(Eric, 10m, new Money(100m));

            TheHolding(portfolio).LastPurchasedAt.ShouldBe(Bought);
            TheHolding(portfolio).ThesisHorizonDays.ShouldBe(Horizon);
        }

        [Fact]
        public void Reopening_a_closed_holding_starts_a_new_clock()
        {
            // Selling out removes the row, so the next buy is a first buy - which is right: it
            // is a new position on a new thesis, not a continuation of the old one.
            var portfolio = Holding(quantity: 26m, at: 95m);
            portfolio.ExecuteSell(Eric, 26m, new Money(100m));

            portfolio.ExecuteBuy(Eric, 5m, new Money(105m), Later, horizonDays: 21);

            TheHolding(portfolio).LastPurchasedAt.ShouldBe(Later);
            TheHolding(portfolio).ThesisHorizonDays.ShouldBe(21);
            TheHolding(portfolio).AveragePurchasePrice.Amount.ShouldBe(105m);
        }
    }
}
