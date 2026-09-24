using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Risk;
using Engine.Hosting;
using Engine.Hosting.Options;
using Engine.Hosting.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// The point of the whole pull request, checked the only way it can honestly be checked: run
/// the real worker against a real database, throw the process away, and run it again.
/// </summary>
/// <remarks>
/// The wiring below is <c>Program.cs</c>'s, not a copy of it - the same
/// <see cref="EngineOptionsExtensions.AddEngineOptions"/> and
/// <see cref="PersistenceExtensions.AddTradingDatabase"/>. Only two things are substituted:
/// the agent service, because a test may not spend money or wait for an LLM, and the clock,
/// because the quote-age rule would otherwise depend on how long the test took.
/// </remarks>
[Collection(TradingDatabaseCollection.Name)]
public class TradingWorkerPersistenceTests : IAsyncLifetime
{
    private const string Symbol = "AAPL";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private readonly TradingDatabaseFixture _database;

    public TradingWorkerPersistenceTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TradeSignalDto ABuy(double conviction) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = Symbol },
        Stance = "BUY",
        Conviction = conviction,
        Thesis = "Momentum plus rimlig multipel.",
        KeyRisks = ["Multipelkontraktion"],
        HorizonDays = 5,
        ReferencePrice = 100m,
        QuoteAsOf = Now,
        Run = new RunDto { TeamId = "default", TeamVersion = "abc123", Revisions = 0 }
    };

    /// <summary>
    /// One engine process. A cycle interval of an hour means the worker runs exactly one
    /// cycle and then waits, so what the assertions see is one cycle's work rather than
    /// however many fitted into the wait.
    /// </summary>
    private ServiceProvider AnEngine(IAgentClient agents)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentService:BaseUrl"] = "http://127.0.0.1:8000",
                ["AgentService:RequestTimeoutSeconds"] = "30",
                ["AgentService:ApiKey"] = "a-test-key",
                ["RiskPolicy:MaxPositionPercentage"] = "0.05",
                ["RiskPolicy:CashBufferPct"] = "0.10",
                ["RiskPolicy:MaxQuoteAgeSeconds"] = "300",
                ["Trading:Tickers:0"] = Symbol,
                ["Trading:CycleIntervalSeconds"] = "3600",
                ["Trading:TeamId"] = "default",
                ["Trading:OpeningBalanceUsd"] = "10000",
                ["Database:ConnectionString"] = _database.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEngineOptions(configuration);
        services.AddSingleton<RiskEngine>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RiskPolicyOptions>>().Value.ToRiskPolicy());
        services.AddSingleton<PositionSizer>();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton(agents);
        services.AddTradingDatabase();
        services.AddTransient<ProcessProposalUseCase>();

        return services.BuildServiceProvider();
    }

    /// <summary>Starts the real worker, waits for its cycle to be committed, and stops it.</summary>
    private async Task RunOneCycleAsync(ServiceProvider engine, int expectedDecisionsAfterwards)
    {
        var worker = new TradingWorker(
            engine.GetRequiredService<IServiceScopeFactory>(),
            engine.GetRequiredService<IOptions<TradingOptions>>(),
            engine.GetRequiredService<ILogger<TradingWorker>>());

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await WaitForDecisionsAsync(expectedDecisionsAfterwards);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Polls rather than sleeps a fixed time: the cycle is quick but not instant, and a test
    /// that waits a guessed number of milliseconds is a test that fails on somebody else's
    /// machine.
    /// </summary>
    private async Task WaitForDecisionsAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var context = _database.NewContext();
            if (await context.Decisions.CountAsync(TestContext.Current.CancellationToken) >= count)
                return;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The worker did not commit {count} decision(s) within 30 seconds.");
    }

    private static IAgentClient AnAgentServiceThatAnswers(TradeSignalDto signal)
    {
        var client = Substitute.For<IAgentClient>();
        client.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>()).Returns(signal);
        return client;
    }

    [Fact]
    public async Task A_restart_continues_the_account_instead_of_reopening_it()
    {
        // Conviction 0.6 is the half tier, so the first cycle leaves headroom for the second.
        // Cycle one: 5 % of 10 000 is 500, halved is 250, which buys 2 shares at 100.
        // Cycle two: the cap is still 500 and 200 is already held, so 300 halved is 150 -
        // one more share. The second number is only reachable by having read the first.
        var agents = AnAgentServiceThatAnswers(ABuy(conviction: 0.6));

        await using (var firstProcess = AnEngine(agents))
        {
            await RunOneCycleAsync(firstProcess, expectedDecisionsAfterwards: 1);
        }

        // A brand new provider: new pool, new change tracker, nothing carried over in memory.
        // This is the restart.
        await using (var secondProcess = AnEngine(agents))
        {
            await RunOneCycleAsync(secondProcess, expectedDecisionsAfterwards: 2);
        }

        await using var context = _database.NewContext();

        var portfolio = await context.Portfolios
            .Include(held => held.Positions)
            .SingleAsync(TestContext.Current.CancellationToken);

        portfolio.CashBalance.Amount.ShouldBe(9_700m);

        var position = portfolio.Positions.ShouldHaveSingleItem();
        position.Quantity.ShouldBe(3m);
        position.AveragePurchasePrice.Amount.ShouldBe(100m);

        var orders = await context.Orders.OrderBy(order => order.Id).ToListAsync(TestContext.Current.CancellationToken);
        orders.Select(order => order.Quantity).ShouldBe([2m, 1m]);

        var decisions = await context.Decisions.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken);
        decisions.Count.ShouldBe(2);
        decisions.ShouldAllBe(row => row.Outcome == DecisionOutcome.Executed);

        // Every decision points at the ledger line it produced, and at no other.
        decisions.Select(row => row.OrderId).ShouldBe(orders.Select(order => (Guid?)order.Id), ignoreOrder: true);

        // The second cycle saw the money the first one spent.
        decisions[1].AvailableRiskBudgetUsd.ShouldBe(9_800m);
        decisions[1].ExistingQuantity.ShouldBe(2m);
    }

    [Fact]
    public async Task Only_one_portfolio_is_ever_opened()
    {
        // The account is opened on the first cycle that finds nothing stored. A second
        // process must find it rather than open another, which is the failure that would make
        // every later measurement meaningless while looking perfectly healthy.
        var agents = AnAgentServiceThatAnswers(ABuy(conviction: 0.6));

        await using (var firstProcess = AnEngine(agents))
        {
            await RunOneCycleAsync(firstProcess, expectedDecisionsAfterwards: 1);
        }

        await using (var secondProcess = AnEngine(agents))
        {
            await RunOneCycleAsync(secondProcess, expectedDecisionsAfterwards: 2);
        }

        await using var context = _database.NewContext();
        (await context.Portfolios.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task A_cycle_that_never_reached_an_answer_is_still_a_row()
    {
        // No order, no position, no cash moved - and a decision saying the service was down.
        // Measuring only the cycles that produced an answer would make the agent service look
        // more reliable the worse it got.
        var agents = Substitute.For<IAgentClient>();
        agents.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<Task<TradeSignalDto?>>(_ => throw new AgentServiceUnavailableException("the service answered 503"));

        await using (var engine = AnEngine(agents))
        {
            await RunOneCycleAsync(engine, expectedDecisionsAfterwards: 1);
        }

        await using var context = _database.NewContext();

        var decision = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);
        decision.Outcome.ShouldBe(DecisionOutcome.AgentUnavailable);
        decision.Stance.ShouldBeNull();
        decision.OrderId.ShouldBeNull();

        (await context.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // The account was still opened, and still has all its money.
        var portfolio = await context.Portfolios
            .Include(held => held.Positions)
            .SingleAsync(TestContext.Current.CancellationToken);
        portfolio.CashBalance.Amount.ShouldBe(10_000m);
        portfolio.Positions.ShouldBeEmpty();
    }
}
