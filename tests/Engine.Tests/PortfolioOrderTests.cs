using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain;

/// <summary>
/// The ledger. A decision is what the system thought; an order is what it did, and the two
/// are stored separately because most decisions produce no order. These tests hold the one
/// rule that makes the ledger worth trusting: cash cannot move without a line describing it.
/// </summary>
public class PortfolioOrderTests
{
    private static readonly Ticker Aapl = new("AAPL");
    private static readonly Ticker Msft = new("MSFT");

    private static Portfolio APortfolioWith(decimal cash) => new(new Money(cash, Money.DefaultCurrency));

    [Fact]
    public void A_buy_leaves_an_order_describing_it()
    {
        var portfolio = APortfolioWith(10_000m);

        var order = portfolio.ExecuteBuy(Aapl, quantity: 3m, new Money(210.40m));

        order.Ticker.ShouldBe(Aapl);
        order.Side.ShouldBe(OrderSide.Buy);
        order.Quantity.ShouldBe(3m);
        order.Price.ShouldBe(new Money(210.40m, Money.DefaultCurrency));
        order.Notional.ShouldBe(new Money(631.20m, Money.DefaultCurrency));
        order.PortfolioId.ShouldBe(portfolio.Id);
    }

    [Fact]
    public void Every_buy_appends_a_line_rather_than_replacing_one()
    {
        // Adding to a holding merges two positions into one, so the position count alone
        // would say a single buy had happened. The ledger must still show both.
        var portfolio = APortfolioWith(10_000m);

        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
        portfolio.ExecuteBuy(Aapl, quantity: 1m, new Money(120m));

        portfolio.Positions.Count.ShouldBe(1);
        portfolio.NewOrders.Count.ShouldBe(2);
        portfolio.NewOrders.Select(placed => placed.Quantity).ShouldBe([2m, 1m]);
    }

    [Fact]
    public void Two_orders_are_two_different_things()
    {
        var portfolio = APortfolioWith(10_000m);

        var first = portfolio.ExecuteBuy(Aapl, quantity: 1m, new Money(100m));
        var second = portfolio.ExecuteBuy(Msft, quantity: 1m, new Money(100m));

        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public void A_buy_the_cash_cannot_cover_leaves_nothing_behind()
    {
        // The order is appended after the balance check on purpose. Appending first would
        // put a line in the ledger for money that never moved.
        var portfolio = APortfolioWith(100m);

        Should.Throw<InvalidOperationException>(() => portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m)));

        portfolio.NewOrders.ShouldBeEmpty();
        portfolio.Positions.ShouldBeEmpty();
        portfolio.CashBalance.ShouldBe(new Money(100m, Money.DefaultCurrency));
    }

    [Fact]
    public void The_ledger_holds_only_what_this_instance_placed()
    {
        // NewOrders is named for what it is. The repository does not read the stored ledger
        // back, so a freshly loaded portfolio starts empty here - and code that wants the
        // history queries trading.orders instead of asking the aggregate.
        var portfolio = APortfolioWith(10_000m);

        portfolio.NewOrders.ShouldBeEmpty();
    }
}
