using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Trading;
using Engine.Domain.ValueObjects;
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

    private static TradeSignalDto ABuy(double conviction, DateTimeOffset? quoteAsOf = null) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = Symbol },
        Stance = "BUY",
        Conviction = conviction,
        Thesis = "Momentum plus rimlig multipel.",
        KeyRisks = ["Multipelkontraktion"],
        HorizonDays = 5,
        ReferencePrice = 100m,
        QuoteAsOf = quoteAsOf ?? Now,
        Run = new RunDto { TeamId = "default", TeamVersion = "abc123", Revisions = 0 }
    };

    /// <summary>
    /// What the quote endpoint answers. Two things read it, and they are deliberately different
    /// numbers from the signal's: the fact-sheet rule compares this price against the last
    /// analysis's reference price to decide whether to analyse at all, and the deterministic exits
    /// value the holding with it. Neither sizes an order - that is the signal's own price, so a
    /// test can move this one to make an analysis due without touching any arithmetic.
    /// </summary>
    private static QuoteDto AQuote(decimal price, DateTimeOffset asOf) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = Symbol },
        Price = price,
        Currency = Money.DefaultCurrency,
        AsOf = asOf
    };

    /// <summary>
    /// One engine process. A cycle interval of an hour means the worker runs exactly one
    /// cycle and then waits, so what the assertions see is one cycle's work rather than
    /// however many fitted into the wait.
    /// </summary>
    /// <summary>Every line the engine logs, so a test can wait for one that leaves no row behind.</summary>
    private readonly CapturedLines _lines = new();

    private sealed class CapturedLines : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Writer(_lines);

        public void Dispose() { }

        private sealed class Writer(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }

    private async Task WaitForALineAsync(string fragment)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_lines.Lines.Any(line => line.Contains(fragment, StringComparison.Ordinal)))
                return;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        // The whole log in the failure, because the line that did not come is rarely the clue.
        throw new TimeoutException(
            $"The worker never logged '{fragment}'. It logged:{Environment.NewLine}"
            + string.Join(Environment.NewLine, _lines.Lines));
    }

    private ServiceProvider AnEngine(
        IAgentClient agents,
        DateTimeOffset? clock = null,
        TradingMode mode = TradingMode.Paper,
        string? alsoInTheUniverse = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentService:BaseUrl"] = "http://127.0.0.1:8000",
                ["AgentService:RequestTimeoutSeconds"] = "30",
                ["AgentService:ApiKey"] = "a-test-key",
                ["AgentService:OutcomesHmacSecret"] = "a-test-hmac-secret",
                ["RiskPolicy:MaxPositionPercentage"] = "0.05",
                ["RiskPolicy:CashBufferPct"] = "0.10",
                ["RiskPolicy:MinHoldingPeriodDays"] = "3",
                ["RiskPolicy:StopLossPercentage"] = "0.10",
                ["RiskPolicy:MaxDailyDeploymentPercentage"] = "0.20",
                ["RiskPolicy:MaxQuoteAgeSeconds"] = "300",
                ["Trading:Universe:0"] = Symbol,
                ["Trading:ShortlistSize"] = "10",
                ["Trading:MinDollarVolume"] = "10000000",
                ["Trading:CycleIntervalMinutes"] = "60",
                ["Trading:TeamId"] = "default",
                ["Trading:OpeningBalance"] = "10000",
                ["Trading:Mode"] = mode.ToString(),
                ["Database:ConnectionString"] = _database.ConnectionString,
            })
            .AddInMemoryCollection(alsoInTheUniverse is null
                ? []
                : new Dictionary<string, string?> { ["Trading:Universe:1"] = alsoInTheUniverse })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(_lines));
        services.AddEngineOptions(configuration);
        services.AddSingleton<RiskEngine>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RiskPolicyOptions>>().Value.ToRiskPolicy());
        services.AddSingleton<PositionSizer>();
        services.AddSingleton<TimeProvider>(new FixedClock(clock ?? Now));
        services.AddSingleton(agents);
        services.AddTradingDatabase();
        services.AddTransient<QuoteReader>();
        services.AddTransient<OrderGate>();
        services.AddSingleton<ShadowExitNotices>();
        services.AddTransient<ProcessProposalUseCase>();
        services.AddTransient<ApplyExitsUseCase>();
        services.AddTransient<SelectShortlistUseCase>();
        services.AddTransient<AnalysisDueCheck>();

        return services.BuildServiceProvider();
    }

    /// <summary>Starts the real worker, waits for a line it logs, and stops it.</summary>
    private async Task RunUntilItLogsAsync(ServiceProvider engine, string fragment)
    {
        var worker = new TradingWorker(
            engine.GetRequiredService<IServiceScopeFactory>(),
            engine.GetRequiredService<IOptions<TradingOptions>>(),
            engine.GetRequiredService<TimeProvider>(),
            engine.GetRequiredService<ILogger<TradingWorker>>());

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await WaitForALineAsync(fragment);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Starts the real worker, waits for its cycle to be committed, and stops it.</summary>
    private async Task RunOneCycleAsync(ServiceProvider engine, int expectedDecisionsAfterwards)
    {
        var worker = new TradingWorker(
            engine.GetRequiredService<IServiceScopeFactory>(),
            engine.GetRequiredService<IOptions<TradingOptions>>(),
            engine.GetRequiredService<TimeProvider>(),
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

    /// <param name="quote">
    /// What the quote endpoint answers. Null means it answers nothing, which is what the
    /// deterministic exits see when the engine cannot price a holding - so a test that does not
    /// pass one is a test where the exits cannot fire.
    /// </param>
    private static IAgentClient AnAgentServiceThatAnswers(TradeSignalDto signal, QuoteDto? quote = null)
    {
        var client = Substitute.For<IAgentClient>();
        client.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>()).Returns(signal);
        client.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(quote);
        return ThatShortlists(client, Symbol);
    }

    /// <summary>
    /// Makes the client answer a screen. Every one of these tests needs it now: the cycle is
    /// today's shortlist plus the holdings, so a client that ranks nothing against an empty
    /// account gives a worker with nothing at all to do.
    /// </summary>
    private static IAgentClient ThatShortlists(IAgentClient client, params string[] symbols)
    {
        client.GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new ScreenResultDto
            {
                Candidates = symbols.Select(symbol => new CandidateDto
                {
                    Instrument = new EquityInstrumentDto { Symbol = symbol },
                    Score = 1.4m,
                    Return3M = 0.28m,
                    Volatility30D = 0.2m,
                    MedianDollarVolume = 41_250_000m
                }).ToArray(),
                Rejected = [],
                AsOf = Now
            });

        return client;
    }

    /// <summary>A screen that ranked nothing, but did look: the day counts as screened.</summary>
    private static IAgentClient ThatRanksNothing(IAgentClient client, DateTimeOffset asOf)
    {
        client.GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new ScreenResultDto
            {
                Candidates = [],
                Rejected =
                [
                    new RejectionDto
                    {
                        Instrument = new EquityInstrumentDto { Symbol = Symbol },
                        Reason = "typical daily turnover 41000 is below the floor"
                    }
                ],
                AsOf = asOf
            });

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
        // This is the restart. It is also the next day, and at a price that has moved - because the
        // same instrument on the same day is no longer analysed twice at all. The prices the
        // arithmetic above depends on are the signal's and are unchanged; the quote only decides
        // whether there is anything to analyse.
        var tomorrow = Now.AddDays(1);
        var moved = AnAgentServiceThatAnswers(
            ABuy(conviction: 0.6, quoteAsOf: tomorrow), AQuote(price: 101m, asOf: tomorrow));

        await using (var secondProcess = AnEngine(moved, clock: tomorrow))
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
        decisions[1].AvailableRiskBudget.ShouldBe(9_800m);
        decisions[1].ExistingQuantity.ShouldBe(2m);
    }

    [Fact]
    public async Task A_shadow_engine_records_its_decisions_and_places_nothing()
    {
        // The same cycle as the restart test's first half - a full-tier buy the risk gate
        // approves - run in Shadow. The row says what would have been bought; the ledger, the
        // positions and the cash say nothing happened.
        await using (var engine = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.9)), mode: TradingMode.Shadow))
        {
            await RunOneCycleAsync(engine, expectedDecisionsAfterwards: 1);
        }

        await using var context = _database.NewContext();

        var decision = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);
        decision.Outcome.ShouldBe(DecisionOutcome.Shadowed);
        decision.TradingMode.ShouldBe(TradingMode.Shadow);
        decision.OutcomeReason.ShouldBe("Shadow mode: would have bought 5 AAPL at 100 SEK");
        decision.OrderId.ShouldBeNull();
        decision.ShadowCost.ShouldBe(500m);

        (await context.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // The account is opened - sizing needs a balance to be a share of - and left untouched.
        var portfolio = await context.Portfolios
            .Include(held => held.Positions)
            .SingleAsync(TestContext.Current.CancellationToken);

        portfolio.CashBalance.Amount.ShouldBe(10_000m);
        portfolio.Positions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shadow_engine_warns_at_startup_that_it_will_not_close_what_the_account_holds()
    {
        // A paper account with a holding, restarted in Shadow, which is what the shipped default
        // does to an engine that never set the mode. Its exits stop selling, so it has to say so.
        await using (var first = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6))))
        {
            await RunOneCycleAsync(first, expectedDecisionsAfterwards: 1);
        }

        await using (var second = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6)), mode: TradingMode.Shadow))
        {
            await RunUntilItLogsAsync(second, "will be logged but NOT placed");
        }

        _lines.Lines.ShouldContain(line =>
            line.StartsWith("Trading mode is Shadow and the portfolio holds 1 position(s): AAPL.", StringComparison.Ordinal)
            && line.Contains("Set Trading:Mode to Paper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shadow_engine_with_nothing_held_has_nothing_to_warn_about()
    {
        await using (var engine = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.9)), mode: TradingMode.Shadow))
        {
            await RunOneCycleAsync(engine, expectedDecisionsAfterwards: 1);
        }

        _lines.Lines.ShouldNotContain(line => line.Contains("NOT placed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The kill switch's proof. A Paper engine holding a position that has fallen through its stop
    /// and an instrument the agents would buy: everything a cycle could act on. The switch is
    /// engaged the way an operator engages it, and the cycle places nothing and asks nobody.
    /// </summary>
    [Fact]
    public async Task With_the_kill_switch_engaged_a_cycle_places_nothing_and_asks_nobody()
    {
        await using (var first = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6))))
        {
            await RunOneCycleAsync(first, expectedDecisionsAfterwards: 1);
        }

        await _database.EngageTheKillSwitchAsync("prices look wrong");

        // A day later at 80: the stop-loss floor is 90, so the exits would sell, and the price has
        // moved, so an analysis would be due and the agents would be asked to buy again.
        var later = Now.AddDays(1);
        var agents = AnAgentServiceThatAnswers(ABuy(conviction: 0.6, quoteAsOf: later), AQuote(price: 80m, asOf: later));
        agents.ClearReceivedCalls();

        await using (var second = AnEngine(agents, clock: later))
        {
            await RunUntilItLogsAsync(second, "Trading is halted by the kill switch");
        }

        // Nothing was asked of the agent service at all - no quote for the exits, no screen, no
        // analysis - so nothing was spent.
        agents.ReceivedCalls().ShouldBeEmpty();

        await using var context = _database.NewContext();

        // The one order and the one decision are the first cycle's; the second left nothing.
        (await context.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await context.Decisions.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var portfolio = await context.Portfolios
            .Include(held => held.Positions)
            .SingleAsync(TestContext.Current.CancellationToken);

        portfolio.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(2m);
        portfolio.CashBalance.Amount.ShouldBe(9_800m);

        _lines.Lines.ShouldContain(line => line.Contains("prices look wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_switch_pulled_while_the_agents_are_thinking_stops_that_order_and_the_rest_of_the_cycle()
    {
        // Two shortlisted instruments. While the agents are answering about the first, an operator
        // engages the switch. The answer comes back a BUY the risk gate approves - and the gate reads
        // the switch again before placing it, so it is recorded as halted with nothing placed. The
        // second instrument is never analysed.
        var agents = Substitute.For<IAgentClient>();
        agents.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((QuoteDto?)null);
        agents.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _database.EngageTheKillSwitch("pulled mid-cycle");
                return ABuy(conviction: 0.9);
            });
        ThatShortlists(agents, Symbol, "MSFT");

        await using (var engine = AnEngine(agents, alsoInTheUniverse: "MSFT"))
        {
            await RunUntilItLogsAsync(engine, "Cycle over 2 instrument(s)");
        }

        await agents.Received(1).GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>());

        await using var context = _database.NewContext();

        var decision = await context.Decisions.SingleAsync(TestContext.Current.CancellationToken);
        decision.Symbol.Value.ShouldBe(Symbol);
        decision.Outcome.ShouldBe(DecisionOutcome.Halted);
        decision.OutcomeReason.ShouldBe("Kill switch engaged: pulled mid-cycle; would have bought 5 AAPL at 100 SEK");
        decision.OrderId.ShouldBeNull();

        (await context.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task The_exits_run_before_the_analyses()
    {
        // The ordering, proved by what the analysis was told rather than by reading the log.
        // Cycle one buys 2 shares at 100 on a five day thesis. Cycle two runs six days later,
        // so the time limit has passed: the exits sell the holding, and only then is the agent
        // service asked - about an instrument the engine no longer holds.
        var later = Now.AddDays(6);

        await using (var first = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6))))
        {
            await RunOneCycleAsync(first, expectedDecisionsAfterwards: 1);
        }

        // 101 rather than 100: the time limit fires either way (the stop-loss floor is 90), but at
        // 100 the price would be exactly where the first analysis left it, and nothing would be
        // analysed at all.
        var agents = AnAgentServiceThatAnswers(
            ABuy(conviction: 0.6, quoteAsOf: later), AQuote(price: 101m, asOf: later));

        await using (var second = AnEngine(agents, clock: later))
        {
            await RunOneCycleAsync(second, expectedDecisionsAfterwards: 2);
        }

        await using var context = _database.NewContext();

        var orders = await context.Orders.ToListAsync(TestContext.Current.CancellationToken);
        orders.Count.ShouldBe(3);

        var sale = orders.Where(order => order.Side == OrderSide.Sell).ShouldHaveSingleItem();
        sale.Trigger.ShouldBe(OrderTrigger.TimeLimit);
        sale.Quantity.ShouldBe(2m);

        var decisions = await context.Decisions.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken);

        // The assertion that could not pass in the other order. Had the analyses run first, the
        // request would have carried the two shares that were still held.
        decisions[1].ExistingQuantity.ShouldBeNull();

        // And the released headroom was there to be used: the whole 5 % cap was free again, so
        // the half tier bought 2 rather than the 1 it would have had with 200 still held.
        decisions[1].Outcome.ShouldBe(DecisionOutcome.Executed);
        orders.Count(order => order.Side == OrderSide.Buy && order.Quantity == 2m).ShouldBe(2);

        var portfolio = await context.Portfolios
            .Include(held => held.Positions)
            .SingleAsync(TestContext.Current.CancellationToken);

        // 10 000 less 200, plus the 202 the sale raised at 101, less 200 again. The sale is priced
        // from the quote and the purchase from the signal, which is why those two differ.
        portfolio.CashBalance.Amount.ShouldBe(9_802m);

        // A new position on a new thesis, so the clock the exits read starts again.
        var position = portfolio.Positions.ShouldHaveSingleItem();
        position.Quantity.ShouldBe(2m);
        position.LastPurchasedAt.ShouldBe(later);
    }

    [Fact]
    public async Task A_holding_is_analysed_although_the_screen_ranked_nothing()
    {
        // The rule the whole selection exists for, end to end. Day one the screen shortlists the
        // instrument and it is bought; day two the screen rejects it outright, so nothing is
        // ranked at all - and it is analysed anyway, because it is held.
        var tomorrow = Now.AddDays(1);

        await using (var first = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6))))
        {
            await RunOneCycleAsync(first, expectedDecisionsAfterwards: 1);
        }

        // No quote, so the deterministic exits cannot price the holding and cannot be what
        // produced the second decision.
        var agents = ThatRanksNothing(
            AnAgentServiceThatAnswers(ABuy(conviction: 0.6, quoteAsOf: tomorrow)), tomorrow);

        await using (var second = AnEngine(agents, clock: tomorrow))
        {
            await RunOneCycleAsync(second, expectedDecisionsAfterwards: 2);
        }

        await using var context = _database.NewContext();

        var decisions = await context.Decisions.OrderBy(row => row.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        decisions.Count.ShouldBe(2);
        decisions[1].Symbol.Value.ShouldBe(Symbol);

        // And it was analysed as something held: the request carried the two shares.
        decisions[1].ExistingQuantity.ShouldBe(2m);

        // One screen stored per trading day, and day two's says the instrument was rejected.
        var screens = await context.Shortlists.OrderBy(row => row.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        screens.Select(entry => entry.ScreenedOn).Distinct().Count().ShouldBe(2);

        // One screen request per day, not per cycle: each process asked exactly once.
        await agents.Received(1).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
        screens.Single(entry => entry.Rank == 1).Symbol.Value.ShouldBe(Symbol);
        screens.Single(entry => entry.Rank is null).RejectedBecause
            .ShouldBe("typical daily turnover 41000 is below the floor");
    }

    [Fact]
    public async Task An_empty_account_is_not_opened_by_the_exits()
    {
        // The exits run first, and the very first cycle of the account's life finds nothing
        // stored. Opening the account there would mean the portfolio existed because a sweep for
        // sales ran, which is an odd thing to have to explain - so they leave it alone and the
        // analysis opens it, exactly as before.
        var agents = AnAgentServiceThatAnswers(ABuy(conviction: 0.6), AQuote(price: 100m, asOf: Now));

        await using var engine = AnEngine(agents);
        await RunOneCycleAsync(engine, expectedDecisionsAfterwards: 1);

        await using var context = _database.NewContext();

        (await context.Portfolios.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await context.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Only_one_portfolio_is_ever_opened()
    {
        // The account is opened on the first cycle that finds nothing stored. A second
        // process must find it rather than open another, which is the failure that would make
        // every later measurement meaningless while looking perfectly healthy.
        await using (var firstProcess = AnEngine(AnAgentServiceThatAnswers(ABuy(conviction: 0.6))))
        {
            await RunOneCycleAsync(firstProcess, expectedDecisionsAfterwards: 1);
        }

        // The next day, at a moved price, so the second cycle has anything to analyse at all.
        var tomorrow = Now.AddDays(1);
        var moved = AnAgentServiceThatAnswers(
            ABuy(conviction: 0.6, quoteAsOf: tomorrow), AQuote(price: 101m, asOf: tomorrow));

        await using (var secondProcess = AnEngine(moved, clock: tomorrow))
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
        var agents = ThatShortlists(Substitute.For<IAgentClient>(), Symbol);
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
