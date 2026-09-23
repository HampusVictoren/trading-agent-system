using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// The round trip. Until now the portfolio lived in a field on the worker and every restart
/// began at ten thousand dollars with no history; these tests are what says that has stopped
/// being true.
/// </summary>
[Collection(TradingDatabaseCollection.Name)]
public class PortfolioRepositoryTests : IAsyncLifetime
{
    private static readonly Ticker Aapl = new("AAPL");
    private static readonly Ticker Msft = new("MSFT");

    private readonly TradingDatabaseFixture _database;

    public PortfolioRepositoryTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>One cycle: a fresh context, work done through the ports, one commit.</summary>
    private async Task<T> InAScope<T>(Func<IPortfolioRepository, IDecisionLog, IUnitOfWork, Task<T>> cycle)
    {
        await using var context = _database.NewContext();
        var result = await cycle(
            new PortfolioRepository(context), new DecisionLog(context), new UnitOfWork(context));
        return result;
    }

    [Fact]
    public async Task A_portfolio_survives_being_stored_and_read_back()
    {
        var id = await InAScope(async (portfolios, _, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolio.ExecuteBuy(Aapl, quantity: 3m, new Money(210.40m));
            portfolios.Add(portfolio);
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        var reloaded = await InAScope((portfolios, _, _) => portfolios.FindAsync(TestContext.Current.CancellationToken));

        reloaded.ShouldNotBeNull();
        reloaded.Id.ShouldBe(id);
        reloaded.CashBalance.ShouldBe(new Money(9368.80m, "USD"));

        var position = reloaded.Positions.ShouldHaveSingleItem();
        position.Ticker.ShouldBe(Aapl);
        position.Quantity.ShouldBe(3m);
        position.AveragePurchasePrice.ShouldBe(new Money(210.40m, "USD"));
    }

    [Fact]
    public async Task A_portfolio_that_has_never_run_is_absent_rather_than_empty()
    {
        // The difference matters to the worker: nothing stored means "open the account",
        // while a stored portfolio with no positions means "carry on".
        var found = await InAScope((portfolios, _, _) => portfolios.FindAsync(TestContext.Current.CancellationToken));

        found.ShouldBeNull();
    }

    [Fact]
    public async Task Buying_across_two_cycles_leaves_two_lines_in_the_ledger()
    {
        await InAScope(async (portfolios, _, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
            portfolios.Add(portfolio);
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        await InAScope(async (portfolios, _, commit) =>
        {
            var portfolio = await portfolios.FindAsync(TestContext.Current.CancellationToken);
            portfolio!.ExecuteBuy(Aapl, quantity: 1m, new Money(120m));
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        await using var context = _database.NewContext();
        var orders = await context.Orders.OrderBy(order => order.Id).ToListAsync(TestContext.Current.CancellationToken);

        // The second buy merged into the existing position, so the holding alone would say a
        // single purchase had happened. The ledger is what remembers there were two.
        orders.Select(order => order.Quantity).ShouldBe([2m, 1m]);
        orders.ShouldAllBe(order => order.Side == OrderSide.Buy);

        var position = context.Portfolios.Include(portfolio => portfolio.Positions).Single().Positions.Single();
        position.Quantity.ShouldBe(3m);
    }

    [Fact]
    public async Task A_reloaded_portfolio_does_not_carry_the_orders_it_already_placed()
    {
        // NewOrders means what it says. Loading the whole ledger to append one line is work
        // that grows with the account's history, and no domain rule reads it back.
        await InAScope(async (portfolios, _, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
            portfolios.Add(portfolio);
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        var reloaded = await InAScope((portfolios, _, _) => portfolios.FindAsync(TestContext.Current.CancellationToken));

        reloaded!.NewOrders.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_portfolio_is_reported_rather_than_silently_picked()
    {
        // Two rows means two accounts spending the same cash. Answering with whichever sorts
        // first would hide it for exactly as long as it takes to lose money.
        await using (var context = _database.NewContext())
        {
            context.Portfolios.Add(new Portfolio(new Money(10_000m, "USD")));
            context.Portfolios.Add(new Portfolio(new Money(5_000m, "USD")));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => InAScope((portfolios, _, _) => portfolios.FindAsync(TestContext.Current.CancellationToken)));

        exception.Message.ShouldContain("more than one");
    }

    [Fact]
    public async Task A_decision_is_stored_with_the_answer_that_produced_it()
    {
        var portfolioId = await InAScope(async (portfolios, decisions, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolios.Add(portfolio);
            decisions.Record(ADecision(portfolio.Id));
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        await using var context = _database.NewContext();
        var stored = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);

        stored.Id.ShouldBeGreaterThan(0);
        stored.PortfolioId.ShouldBe(portfolioId);
        stored.Symbol.ShouldBe(Msft);
        stored.Stance.ShouldBe(Stance.Buy);
        stored.Conviction.ShouldBe(0.72);
        stored.KeyRisks.ShouldBe(["Multipelkontraktion", "Svag orderingång"]);
        stored.Outcome.ShouldBe(DecisionOutcome.RejectedByRisk);
        stored.OutcomeReason.ShouldBe("the quote is older than the policy allows");
        stored.OrderId.ShouldBeNull();

        // Set by the database, so every row is ordered by the same clock.
        stored.RecordedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task A_failed_call_is_a_decision_too()
    {
        // A cycle that never reached an answer still says something about the system, and a
        // row of nulls is how the measurement sees "we could not ask". Leaving it out would
        // make the agent service look more reliable the worse it got.
        await InAScope(async (portfolios, decisions, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolios.Add(portfolio);
            decisions.Record(new DecisionRecord
            {
                CorrelationId = "cycle-unreachable",
                PortfolioId = portfolio.Id,
                Symbol = Msft,
                TeamId = "default",
                RequestedAt = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
                AvailableRiskBudgetUsd = 10_000m,
                MaxPositionPct = 0.05m,
                Outcome = DecisionOutcome.AgentUnavailable,
                OutcomeReason = "the agent service answered 503",
            });
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        await using var context = _database.NewContext();
        var stored = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);

        stored.Stance.ShouldBeNull();
        stored.ReferencePrice.ShouldBeNull();
        stored.TeamVersion.ShouldBeNull();
        stored.KeyRisks.ShouldBeEmpty();
        stored.Outcome.ShouldBe(DecisionOutcome.AgentUnavailable);
    }

    [Fact]
    public async Task A_quote_timestamp_from_another_offset_is_stored_as_the_same_instant()
    {
        // quote_as_of arrives from another service, which is free to express an instant in
        // whatever offset it likes. Postgres stores an instant, so the offset has to be
        // normalised on the way in rather than refused at three in the morning.
        var quoteAsOf = new DateTimeOffset(2026, 9, 23, 16, 3, 0, TimeSpan.FromHours(2));

        await InAScope(async (portfolios, decisions, commit) =>
        {
            var portfolio = new Portfolio(new Money(10_000m, "USD"));
            portfolios.Add(portfolio);
            decisions.Record(ADecision(portfolio.Id, quoteAsOf));
            await commit.SaveChangesAsync(TestContext.Current.CancellationToken);
            return portfolio.Id;
        });

        await using var context = _database.NewContext();
        var stored = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);

        stored.QuoteAsOf.ShouldBe(quoteAsOf);
        stored.QuoteAsOf!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    private static DecisionRecord ADecision(Guid portfolioId, DateTimeOffset? quoteAsOf = null) => new()
    {
        CorrelationId = "cycle-1",
        PortfolioId = portfolioId,
        Symbol = Msft,
        TeamId = "default",
        RequestedAt = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
        AvailableRiskBudgetUsd = 10_000m,
        MaxPositionPct = 0.05m,
        ExistingQuantity = null,
        ExistingAveragePrice = null,
        TeamVersion = "a1b2c3d4e5f6",
        Revisions = 0,
        Stance = Stance.Buy,
        Conviction = 0.72,
        Thesis = "Momentum plus rimlig multipel.",
        KeyRisks = ["Multipelkontraktion", "Svag orderingång"],
        HorizonDays = 5,
        ReferencePrice = 415.25m,
        ReferenceCurrency = "USD",
        QuoteAsOf = quoteAsOf ?? new DateTimeOffset(2026, 9, 23, 13, 58, 0, TimeSpan.Zero),
        Outcome = DecisionOutcome.RejectedByRisk,
        OutcomeReason = "the quote is older than the policy allows",
    };
}
