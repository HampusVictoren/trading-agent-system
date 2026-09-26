namespace Engine.Tests.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

/// <summary>
/// The half of trading the agents cannot do. One pass over the portfolio, before any analysis,
/// selling what the rules say should be sold.
/// </summary>
/// <remarks>
/// Throughout: bought at 100 on a 15 day thesis, with a 10 % stop, so the floor is 90.
/// </remarks>
public class ApplyExitsUseCaseTests
{
    private static readonly Ticker Eric = new("ERIC-B.ST");
    private static readonly Ticker Volvo = new("VOLV-B.ST");

    private static readonly DateTimeOffset Bought = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly RiskPolicy Policy = new(
        maxPositionPct: 0.05m,
        cashBufferPct: 0.10m,
        maxQuoteAge: TimeSpan.FromMinutes(5),
        minHoldingPeriod: TimeSpan.FromDays(3),
        stopLossPct: 0.10m);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static QuoteDto Quote(Ticker ticker, decimal price, DateTimeOffset asOf) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = ticker.Value },
        Price = price,
        Currency = Money.DefaultCurrency,
        AsOf = asOf
    };

    /// <summary>A portfolio holding what is asked for, bought at 100 on a 15 day thesis.</summary>
    private static Portfolio Holding(params Ticker[] tickers)
    {
        var portfolio = new Portfolio(new Money(100_000m, Money.DefaultCurrency));

        foreach (var ticker in tickers)
        {
            portfolio.ExecuteBuy(
                ticker, quantity: 10m, new Money(100m, Money.DefaultCurrency), Bought, horizonDays: 15);
        }

        return portfolio;
    }

    /// <summary>
    /// The use case with a quote per instrument. A symbol left out of <paramref name="prices"/>
    /// is one the engine could not get a price for, which is what an outage looks like from here.
    /// </summary>
    private static (ApplyExitsUseCase Sut, DateTimeOffset Now) Build(
        DateTimeOffset now, params (Ticker Ticker, decimal Price)[] prices)
    {
        var client = Substitute.For<IAgentClient>();

        client.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((QuoteDto?)null);

        foreach (var (ticker, price) in prices)
        {
            client.GetQuoteAsync(ticker.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Quote(ticker, price, now));
        }

        var reader = new HoldingQuoteReader(client, Policy, NullLogger<HoldingQuoteReader>.Instance);

        return (
            new ApplyExitsUseCase(
                reader, new RiskEngine(), Policy, new FixedClock(now),
                NullLogger<ApplyExitsUseCase>.Instance),
            now);
    }

    private static Task<IReadOnlyList<Order>> Run(ApplyExitsUseCase sut, Portfolio portfolio) =>
        sut.ExecuteAsync(portfolio, "exits-1", TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_holding_that_has_fallen_past_the_stop_is_sold_whole()
    {
        var (sut, _) = Build(Bought.AddDays(1), (Eric, 85m));
        var portfolio = Holding(Eric);

        var placed = await Run(sut, portfolio);

        var order = placed.ShouldHaveSingleItem();
        order.Trigger.ShouldBe(OrderTrigger.StopLoss);
        order.Side.ShouldBe(OrderSide.Sell);
        order.Quantity.ShouldBe(10m);

        // Whole rather than part: a stop-loss that sold half would leave the position it just
        // judged to be wrong.
        portfolio.Positions.ShouldBeEmpty();
        order.RealisedProfitAndLoss!.Amount.ShouldBe(-150m);
    }

    [Fact]
    public async Task A_holding_whose_thesis_has_expired_is_sold_whole()
    {
        var (sut, _) = Build(Bought.AddDays(15), (Eric, 104m));
        var portfolio = Holding(Eric);

        var placed = await Run(sut, portfolio);

        placed.ShouldHaveSingleItem().Trigger.ShouldBe(OrderTrigger.TimeLimit);
        portfolio.Positions.ShouldBeEmpty();

        // The thesis expiring says nothing about the price. Selling a winner is the point: the
        // position no longer has a thesis, so holding it is holding something nobody argued for.
        placed[0].RealisedProfitAndLoss!.Amount.ShouldBe(40m);
    }

    [Fact]
    public async Task A_holding_that_neither_rule_touches_is_left_alone()
    {
        var (sut, _) = Build(Bought.AddDays(5), (Eric, 104m));
        var portfolio = Holding(Eric);

        (await Run(sut, portfolio)).ShouldBeEmpty();

        portfolio.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(10m);
        portfolio.NewOrders.Count.ShouldBe(1); // The buy, and nothing since.
    }

    [Fact]
    public async Task A_stop_loss_fires_inside_the_minimum_holding_period()
    {
        // The reason an order carries a trigger at all. The agents may not sell for three days;
        // a stop-loss is not a change of mind, and one that had to wait would be a waiting
        // period rather than a risk control.
        var (sut, _) = Build(Bought.AddHours(2), (Eric, 80m));
        var portfolio = Holding(Eric);

        (await Run(sut, portfolio)).ShouldHaveSingleItem().Trigger.ShouldBe(OrderTrigger.StopLoss);
    }

    [Fact]
    public async Task A_holding_with_no_quote_is_skipped_and_the_rest_still_judged()
    {
        // The property the sell path was built for. A buy needs the whole portfolio priced and
        // is refused when one holding is missing; an exit judges what it can see. Otherwise a
        // market data outage would hold every position until the data came back - with the
        // exits unable to fire for exactly the same reason.
        var (sut, _) = Build(Bought.AddDays(1), (Eric, 85m));
        var portfolio = Holding(Eric, Volvo);

        var placed = await Run(sut, portfolio);

        placed.ShouldHaveSingleItem().Ticker.ShouldBe(Eric);
        portfolio.Positions.ShouldHaveSingleItem().Ticker.ShouldBe(Volvo);
    }

    [Fact]
    public async Task Two_holdings_can_exit_in_one_pass()
    {
        var (sut, _) = Build(Bought.AddDays(1), (Eric, 85m), (Volvo, 60m));
        var portfolio = Holding(Eric, Volvo);

        var placed = await Run(sut, portfolio);

        placed.Select(order => order.Ticker).ShouldBe([Eric, Volvo], ignoreOrder: true);
        portfolio.Positions.ShouldBeEmpty();

        // 10 x 85 plus 10 x 60 on top of the 98,000 left after two purchases of 1,000.
        portfolio.CashBalance.Amount.ShouldBe(99_450m);
    }

    [Fact]
    public async Task A_stale_quote_is_no_quote_at_all()
    {
        // The reader drops a price it should not act on, so the exit never sees it. Selling on
        // an old price would realise a figure that was never true.
        var now = Bought.AddDays(1);
        var client = Substitute.For<IAgentClient>();

        client.GetQuoteAsync(Eric.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Quote(Eric, 85m, now.AddMinutes(-6)));

        var sut = new ApplyExitsUseCase(
            new HoldingQuoteReader(client, Policy, NullLogger<HoldingQuoteReader>.Instance),
            new RiskEngine(),
            Policy,
            new FixedClock(now),
            NullLogger<ApplyExitsUseCase>.Instance);

        var portfolio = Holding(Eric);

        (await Run(sut, portfolio)).ShouldBeEmpty();
        portfolio.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(10m);
    }
}
