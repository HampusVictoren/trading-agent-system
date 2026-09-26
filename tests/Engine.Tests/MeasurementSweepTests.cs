using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Outcomes;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// The sweep, against a real database. This is the point of the stage: until it runs, the
/// rows say what the agents thought and nothing about whether they were right.
/// </summary>
[Collection(TradingDatabaseCollection.Name)]
public class MeasurementSweepTests : IAsyncLifetime
{
    private static readonly Ticker Aapl = new("AAPL");

    private const string Benchmark = "SPY";

    /// <summary>21 September 2026 is a Monday; the 24th is a holiday here and the 26th and
    /// 27th are a weekend.</summary>
    private static DateOnly Day(int day) => new(2026, 9, day);

    private readonly TradingDatabaseFixture _database;

    public MeasurementSweepTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly OutcomeOptions Options = new()
    {
        CommissionBps = 1m,
        SpreadBps = 2m,
        HoldBandPct = 0.02m,
        BenchmarkSymbol = Benchmark,
        FixedHorizonTradingDays = [1, 5, 20],
        SweepIntervalHours = 24,
    };

    private static HistoryDto History(string symbol, params (int Day, decimal Close)[] bars) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = symbol },
        Bars = [.. bars.Select(bar => new BarDto { On = Day(bar.Day), Close = bar.Close })]
    };

    /// <summary>The instrument rises steadily; the index rises a little less.</summary>
    private static HistoryDto AaplHistory() =>
        History("AAPL", (22, 102m), (23, 103m), (25, 105m), (28, 110m), (29, 106m));

    private static HistoryDto SpyHistory() =>
        History(Benchmark, (21, 400m), (22, 404m), (23, 408m), (25, 412m), (28, 420m), (29, 416m));

    private IAgentClient AgentServiceWith(HistoryDto? instrument = null, HistoryDto? benchmark = null)
    {
        var client = Substitute.For<IAgentClient>();

        client.GetHistoryAsync("AAPL", Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(instrument ?? AaplHistory());
        client.GetHistoryAsync(Benchmark, Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(benchmark ?? SpyHistory());

        return client;
    }

    /// <summary>A portfolio and one decision that produced a signal, as the engine would have stored it.</summary>
    private async Task<long> ASignalWasMade(
        Stance stance = Stance.Buy,
        double conviction = 0.8,
        int horizonDays = 5,
        string correlationId = "cycle-1",
        string teamVersion = "abc123",
        decimal referencePrice = 100m)
    {
        await using var context = _database.NewContext();

        var portfolio = context.Portfolios.SingleOrDefault()
            ?? context.Portfolios.Add(new Portfolio(new Money(10_000m, Money.DefaultCurrency))).Entity;

        var decision = new DecisionRecord
        {
            CorrelationId = correlationId,
            PortfolioId = portfolio.Id,
            Symbol = Aapl,
            TeamId = "default",
            RequestedAt = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero),
            AvailableRiskBudget = 10_000m,
            MaxPositionPct = 0.05m,
            TeamVersion = teamVersion,
            Revisions = 0,
            Stance = stance,
            Conviction = conviction,
            Thesis = "a thesis",
            KeyRisks = ["a risk"],
            HorizonDays = horizonDays,
            ReferencePrice = referencePrice,
            ReferenceCurrency = Money.DefaultCurrency,
            QuoteAsOf = new DateTimeOffset(2026, 9, 21, 14, 3, 0, TimeSpan.Zero),
            Outcome = DecisionOutcome.Executed,
        };

        context.Decisions.Add(decision);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return decision.Id;
    }

    /// <summary>A cycle that never reached an answer: a decision, but not a signal.</summary>
    private async Task ACallFailed(string correlationId)
    {
        await using var context = _database.NewContext();
        var portfolioId = context.Portfolios.Single().Id;

        context.Decisions.Add(new DecisionRecord
        {
            CorrelationId = correlationId,
            PortfolioId = portfolioId,
            Symbol = Aapl,
            TeamId = "default",
            RequestedAt = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero),
            AvailableRiskBudget = 10_000m,
            MaxPositionPct = 0.05m,
            Outcome = DecisionOutcome.AgentUnavailable,
            OutcomeReason = "the agent service answered 503",
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<SweepResult> SweepAsync(IAgentClient agents, string correlationId = "sweep-1")
    {
        await using var context = _database.NewContext();

        var sut = new MeasureOutcomesUseCase(
            new OutcomeLog(context),
            agents,
            new OutcomeCalculator(),
            Options.ToOutcomePolicy(),
            Microsoft.Extensions.Options.Options.Create(Options),
            new UnitOfWork(context),
            NullLogger<MeasureOutcomesUseCase>.Instance);

        return await sut.SweepAsync(correlationId, TestContext.Current.CancellationToken);
    }

    private async Task<List<SignalOutcomeRecord>> StoredOutcomes()
    {
        await using var context = _database.NewContext();
        return await context.SignalOutcomes
            .OrderBy(outcome => outcome.HorizonUnit).ThenBy(outcome => outcome.HorizonDays)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Every_horizon_that_has_passed_is_scored_and_the_rest_waits()
    {
        // Four horizons are wanted: 1, 5 and 20 trading days, and the model's own 5 calendar
        // days. The series ends on the 29th, so twenty trading days have not passed.
        await ASignalWasMade();

        var result = await SweepAsync(AgentServiceWith());

        result.Measured.ShouldBe(3);
        result.NotDue.ShouldBe(1);
        result.Abandoned.ShouldBe(0);

        var outcomes = await StoredOutcomes();
        outcomes.Count.ShouldBe(3);

        // Five calendar days from Monday the 21st is Saturday the 26th, so the answer is
        // Friday the 25th's close - a week earlier than five *trading* days would give.
        var calendar = outcomes.Single(outcome => outcome.HorizonUnit == HorizonUnit.CalendarDays);
        calendar.HorizonDays.ShouldBe(5);
        calendar.MeasuredOn.ShouldBe(Day(25));
        calendar.MeasuredPrice.ShouldBe(105m);

        var fiveTradingDays = outcomes.Single(outcome =>
            outcome.HorizonUnit == HorizonUnit.TradingDays && outcome.HorizonDays == 5);
        fiveTradingDays.MeasuredOn.ShouldBe(Day(29));
    }

    [Fact]
    public async Task The_numbers_on_a_row_are_the_ones_the_calculator_worked_out()
    {
        // One trading day: 100 to 102 against the index's 400 to 404. Two per cent against
        // one, a round trip of six basis points, and 0.94 per cent of edge left.
        await ASignalWasMade();

        await SweepAsync(AgentServiceWith());

        var oneDay = (await StoredOutcomes()).Single(outcome =>
            outcome.HorizonUnit == HorizonUnit.TradingDays && outcome.HorizonDays == 1);

        oneDay.Status.ShouldBe(OutcomeStatus.Measured);
        oneDay.InstrumentReturn.ShouldBe(0.02m);
        oneDay.BenchmarkReturn.ShouldBe(0.01m);
        oneDay.ExcessReturn.ShouldBe(0.01m);
        oneDay.CostFraction.ShouldBe(0.0006m);
        oneDay.NetEdge.ShouldBe(0.0094m);
        oneDay.Hit.ShouldBe(true);

        // Stored because both are guesses today: a row has to stay interpretable after
        // somebody improves them.
        oneDay.BenchmarkSymbol.ShouldBe(Benchmark);
    }

    [Fact]
    public async Task Running_the_sweep_twice_writes_one_row_per_horizon()
    {
        // The unique index would refuse a second write, so this failing looks like a crash
        // rather than a duplicate - which is the point of having it.
        await ASignalWasMade();

        var first = await SweepAsync(AgentServiceWith(), "sweep-1");
        var second = await SweepAsync(AgentServiceWith(), "sweep-2");

        first.Measured.ShouldBe(3);
        second.Measured.ShouldBe(0);
        second.NotDue.ShouldBe(1);

        (await StoredOutcomes()).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_horizon_that_was_not_due_is_measured_once_the_bars_arrive()
    {
        // The difference between "not yet" and "never" is the whole reason they are separate
        // results: this one gets no row on Monday and a row on Tuesday.
        await ASignalWasMade(horizonDays: 1);

        var tooEarly = History("AAPL", (22, 102m));
        var first = await SweepAsync(AgentServiceWith(instrument: tooEarly), "sweep-1");

        // One trading day and one calendar day have passed; five and twenty have not.
        first.Measured.ShouldBe(2);
        first.NotDue.ShouldBe(2);

        var second = await SweepAsync(AgentServiceWith(), "sweep-2");

        second.Measured.ShouldBe(1);   // five trading days, now that the 29th exists
        second.NotDue.ShouldBe(1);     // twenty still has not

        (await StoredOutcomes()).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_signal_that_can_never_be_measured_is_written_down_and_left_alone()
    {
        // The index has no close on or before the signal, so there is nothing to compare
        // from - today or ever. Without a row the sweep would retry it every night, and the
        // report would silently be about fewer signals than were made.
        var benchmarkWithAHole = History(Benchmark, (22, 404m), (23, 408m), (25, 412m), (28, 420m), (29, 416m));

        await ASignalWasMade();

        var first = await SweepAsync(AgentServiceWith(benchmark: benchmarkWithAHole), "sweep-1");
        first.Abandoned.ShouldBe(3);
        first.Measured.ShouldBe(0);

        var abandoned = (await StoredOutcomes()).First();
        abandoned.Status.ShouldBe(OutcomeStatus.NotMeasurable);
        abandoned.Reason.ShouldNotBeNullOrEmpty();
        abandoned.InstrumentReturn.ShouldBeNull();
        abandoned.Hit.ShouldBeNull();

        // The second sweep has nothing left to say about those three horizons.
        var second = await SweepAsync(AgentServiceWith(benchmark: benchmarkWithAHole), "sweep-2");
        second.Abandoned.ShouldBe(0);
        second.Measured.ShouldBe(0);
    }

    [Fact]
    public async Task A_cycle_that_never_reached_an_answer_is_not_a_signal()
    {
        // It is a decision and it belongs in the decision history, but there is no view to
        // be right or wrong about, so it is filtered out in the query rather than retried.
        await ASignalWasMade();
        await ACallFailed("cycle-2");

        await SweepAsync(AgentServiceWith());

        (await StoredOutcomes()).Select(outcome => outcome.DecisionId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_instrument_whose_history_is_unavailable_waits_rather_than_being_abandoned()
    {
        // An outage is not a hole in the data. Writing "unmeasurable" here would stop anyone
        // ever asking again for a signal that is perfectly measurable tomorrow.
        var agents = Substitute.For<IAgentClient>();
        agents.GetHistoryAsync("AAPL", Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<HistoryDto?>>(_ => throw new AgentServiceUnavailableException("503"));
        agents.GetHistoryAsync(Benchmark, Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SpyHistory());

        await ASignalWasMade();

        var result = await SweepAsync(agents);

        result.Unreachable.ShouldBe(4);
        result.Measured.ShouldBe(0);
        (await StoredOutcomes()).ShouldBeEmpty();

        // And it is measurable again as soon as the service answers.
        (await SweepAsync(AgentServiceWith(), "sweep-2")).Measured.ShouldBe(3);
    }

    [Fact]
    public async Task The_sweep_moves_no_money()
    {
        // Measuring is not trading. The sweep is the second writer in this process, and the
        // one thing it must never do is touch the portfolio.
        await ASignalWasMade();

        await using (var context = _database.NewContext())
        {
            var portfolio = context.Portfolios.Include(held => held.Positions).Single();
            portfolio.ExecuteBuy(Aapl, 2m, new Money(100m));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SweepAsync(AgentServiceWith());

        await using var reading = _database.NewContext();
        var after = reading.Portfolios.Include(held => held.Positions).Single();

        after.CashBalance.Amount.ShouldBe(9_800m);
        after.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(2m);
        (await reading.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task A_measurement_cannot_be_rewritten()
    {
        await ASignalWasMade();
        await SweepAsync(AgentServiceWith());

        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE trading.signal_outcomes SET hit = false", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("append-only");
    }


    /// <summary>One value out of the report view, which is how this data is actually read.</summary>
    private const string OneTradingDay = "horizon_unit = 'TradingDays' AND horizon_days = 1";

    private async Task<T> FromTheView<T>(string column, string where = OneTradingDay)
    {
        await using var context = _database.NewContext();

        // EF1002 warns about interpolation, and is right to in general. A column name cannot
        // be a parameter, and both pieces here are literals written a few lines above - no
        // value in this method ever came from outside the file.
#pragma warning disable EF1002
        return await context.Database
            .SqlQueryRaw<T>($"SELECT {column} AS \"Value\" FROM trading.hit_rate WHERE {where}")
            .SingleAsync(TestContext.Current.CancellationToken);
#pragma warning restore EF1002
    }

    [Fact]
    public async Task The_view_answers_the_only_question_that_matters()
    {
        // Two views of the same instrument over the same bars: the buy was right and the
        // sell was wrong, because AAPL beat the index. Grouping by stance is what stops a
        // team that only ever says HOLD from looking good.
        // Two buys on the same bars: the first was formed at 100 and the instrument closed
        // at 102, the second at 102 and it closed flat while the index rose. One right, one
        // wrong. And a sell, which was wrong because AAPL beat the index - grouping by
        // stance is what stops a team that only ever says HOLD from looking good.
        await ASignalWasMade(stance: Stance.Buy, correlationId: "cycle-right");
        await ASignalWasMade(stance: Stance.Buy, correlationId: "cycle-wrong", referencePrice: 102m);
        await ASignalWasMade(stance: Stance.Sell, correlationId: "cycle-sell");

        await SweepAsync(AgentServiceWith());

        var buys = $"{OneTradingDay} AND stance = 'Buy'";
        (await FromTheView<long>("measured", buys)).ShouldBe(2);
        (await FromTheView<long>("hits", buys)).ShouldBe(1);
        (await FromTheView<decimal>("hit_rate", buys)).ShouldBe(0.500m);

        var sells = $"{OneTradingDay} AND stance = 'Sell'";
        (await FromTheView<long>("measured", sells)).ShouldBe(1);
        (await FromTheView<long>("hits", sells)).ShouldBe(0);

        // Gross and net are both reported, because the difference between them is the whole
        // of finding F.
        (await FromTheView<decimal>("avg_excess_gross", buys))
            .ShouldBeGreaterThan(await FromTheView<decimal>("avg_edge_net", buys));
    }

    [Fact]
    public async Task A_row_that_could_not_be_measured_stays_out_of_the_report()
    {
        // It is in the table so the sweep stops retrying it, and out of the view because a
        // hit rate computed over rows with no result is not a hit rate.
        var benchmarkWithAHole = History(Benchmark, (22, 404m), (23, 408m), (25, 412m), (28, 420m), (29, 416m));

        await ASignalWasMade();
        await SweepAsync(AgentServiceWith(benchmark: benchmarkWithAHole));

        await using var context = _database.NewContext();

        (await context.SignalOutcomes.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);

        var reported = await context.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM trading.hit_rate")
            .SingleAsync(TestContext.Current.CancellationToken);

        reported.ShouldBe(0);
    }

    [Theory]
    [InlineData(0.39)]
    [InlineData(0.40)]
    [InlineData(0.70)]
    [InlineData(0.71)]
    public async Task The_views_conviction_boundaries_are_the_engines_own(double conviction)
    {
        // The view repeats ConvictionTier's thresholds in SQL, which is a duplication worth
        // having - a report that needs a deploy to change is a report nobody runs - but only
        // because this test drives each boundary from both sides. Move a constant in C# and
        // the two stop agreeing here rather than in a conclusion six months later.
        await ASignalWasMade(conviction: conviction);
        await SweepAsync(AgentServiceWith());

        var expected = ConvictionTier.From(new Conviction(conviction)) switch
        {
            0m => "below floor",
            0.5m => "half",
            _ => "full"
        };

        (await FromTheView<string>("conviction_tier")).ShouldBe(expected);
    }
}
