using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Outcomes;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// Getting a measurement from the engine's record to the agent service's copy, against a
/// real database.
/// </summary>
/// <remarks>
/// The whole reason outcome_deliveries exists is the case in the middle of this file: a
/// sweep runs every 24 hours and measures only what is still unmeasured, so without a marker
/// a thirty-second outage on the other side would cost a day of evidence and nothing would
/// ever notice.
/// </remarks>
[Collection(TradingDatabaseCollection.Name)]
public class OutcomeDeliveryTests : IAsyncLifetime
{
    private readonly TradingDatabaseFixture _database;

    public OutcomeDeliveryTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<long> AMeasurementExists(
        string correlationId = "cycle-1",
        int horizonDays = 5,
        OutcomeStatus status = OutcomeStatus.Measured)
    {
        await using var context = _database.NewContext();

        var portfolio = context.Portfolios.SingleOrDefault()
            ?? context.Portfolios.Add(new Portfolio(new Money(10_000m, Money.DefaultCurrency))).Entity;

        var decision = new DecisionRecord
        {
            CorrelationId = correlationId,
            PortfolioId = portfolio.Id,
            Symbol = new Ticker("AAPL"),
            TeamId = "default",
            RequestedAt = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero),
            AvailableRiskBudget = 10_000m,
            MaxPositionPct = 0.05m,
            TeamVersion = "abc123",
            Revisions = 0,
            Stance = Stance.Buy,
            Conviction = 0.8,
            Thesis = "a thesis",
            KeyRisks = ["a risk"],
            HorizonDays = horizonDays,
            ReferencePrice = 100m,
            ReferenceCurrency = Money.DefaultCurrency,
            QuoteAsOf = new DateTimeOffset(2026, 9, 21, 14, 3, 0, TimeSpan.Zero),
            Outcome = DecisionOutcome.Executed,
        };

        context.Decisions.Add(decision);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outcome = new SignalOutcomeRecord
        {
            DecisionId = decision.Id,
            HorizonUnit = HorizonUnit.TradingDays,
            HorizonDays = horizonDays,
            Status = status,
            Reason = status == OutcomeStatus.NotMeasurable ? "no bar for the benchmark" : null,
            BenchmarkSymbol = "SPY",
            MeasuredOn = status == OutcomeStatus.Measured ? new DateOnly(2026, 10, 1) : null,
            MeasuredPrice = status == OutcomeStatus.Measured ? 344.12m : null,
            InstrumentReturn = status == OutcomeStatus.Measured ? 0.019795m : null,
            BenchmarkReturn = status == OutcomeStatus.Measured ? 0.004311m : null,
            ExcessReturn = status == OutcomeStatus.Measured ? 0.015484m : null,
            CostFraction = status == OutcomeStatus.Measured ? 0.0006m : null,
            NetEdge = status == OutcomeStatus.Measured ? 0.014884m : null,
            Hit = status == OutcomeStatus.Measured ? true : null,
        };

        context.SignalOutcomes.Add(outcome);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return outcome.Id;
    }

    private async Task<DeliveryResult> DeliverAsync(IAgentClient agents, string correlationId = "sweep-1")
    {
        await using var context = _database.NewContext();

        var sut = new ReportOutcomesUseCase(
            new OutcomeLog(context),
            agents,
            new UnitOfWork(context),
            NullLogger<ReportOutcomesUseCase>.Instance);

        return await sut.ReportAsync(correlationId, TestContext.Current.CancellationToken);
    }

    private async Task<int> DeliveredCount()
    {
        await using var context = _database.NewContext();
        return await context.OutcomeDeliveries.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Every_measurement_is_sent_once_and_then_left_alone()
    {
        await AMeasurementExists("cycle-1");
        await AMeasurementExists("cycle-2");
        var agents = Substitute.For<IAgentClient>();

        var first = await DeliverAsync(agents);
        var second = await DeliverAsync(agents);

        first.Delivered.ShouldBe(2);
        second.Delivered.ShouldBe(0);
        (await DeliveredCount()).ShouldBe(2);

        // One request the first time, and none the second: there was nothing to say.
        await agents.Received(1).PostOutcomesAsync(
            Arg.Any<OutcomeReportDto>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_measurement_the_agent_service_never_received_is_sent_again()
    {
        await AMeasurementExists();
        var unreachable = Substitute.For<IAgentClient>();
        unreachable.PostOutcomesAsync(
                Arg.Any<OutcomeReportDto>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AgentServiceUnavailableException("the agent service is down"));

        await Should.ThrowAsync<AgentServiceUnavailableException>(() => DeliverAsync(unreachable));

        // No marker was written, so nothing was lost - which is the whole point of the
        // table. Marking first and posting afterwards would have lost this measurement.
        (await DeliveredCount()).ShouldBe(0);

        var result = await DeliverAsync(Substitute.For<IAgentClient>());
        result.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task The_identifier_that_travels_is_the_one_both_services_wrote_down()
    {
        var outcomeId = await AMeasurementExists("cycle-42");
        var agents = Substitute.For<IAgentClient>();

        await DeliverAsync(agents);

        var sent = (OutcomeReportDto)agents.ReceivedCalls().Single().GetArguments()[0]!;
        var outcome = sent.Outcomes.Single();
        outcome.CorrelationId.ShouldBe("cycle-42");
        outcome.NetEdge.ShouldBe(0.014884m);
        outcome.Hit.ShouldBe(true);

        // The engine's own key stays on the engine's side; it means nothing over there.
        outcomeId.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_measurement_that_could_never_be_made_is_delivered_too()
    {
        // Otherwise the agent service's copy is the measured ones only, which is the
        // population bias this stage refuses everywhere else.
        await AMeasurementExists(status: OutcomeStatus.NotMeasurable);
        var agents = Substitute.For<IAgentClient>();

        await DeliverAsync(agents);

        var sent = (OutcomeReportDto)agents.ReceivedCalls().Single().GetArguments()[0]!;
        var outcome = sent.Outcomes.Single();
        outcome.Status.ShouldBe("NotMeasurable");
        outcome.Reason.ShouldNotBeNullOrWhiteSpace();
        outcome.Hit.ShouldBeNull();
    }

    [Fact]
    public async Task Delivery_moves_no_money()
    {
        // The same rule the sweep has. This loop runs beside the trading worker, and the one
        // thing it must never do is touch the account they share.
        await AMeasurementExists();
        await using (var before = _database.NewContext())
        {
            before.Portfolios.Single().CashBalance.Amount.ShouldBe(10_000m);
        }

        await DeliverAsync(Substitute.For<IAgentClient>());

        await using var after = _database.NewContext();
        after.Portfolios.Single().CashBalance.Amount.ShouldBe(10_000m);
        (await after.Orders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}

/// <summary>
/// The cap, without a database. Filling one with five hundred rows would prove the same
/// thing more slowly, and the rule under test is arithmetic rather than storage.
/// </summary>
public class OutcomeDeliveryBatchTests
{
    private static OutcomeAwaitingDelivery A(long id) => new(
        id, $"cycle-{id}", HorizonUnit.TradingDays, 5, OutcomeStatus.Measured,
        null, "SPY", new DateOnly(2026, 10, 1), 100m, 0.01m, 0.005m, 0.005m, 0.0006m, 0.0044m, true);

    [Fact]
    public async Task A_backlog_is_sent_a_request_at_a_time_and_the_rest_waits()
    {
        var waiting = Enumerable.Range(1, ReportOutcomesUseCase.MaxPerRequest + 3)
            .Select(n => A(n))
            .ToList();
        var log = Substitute.For<IOutcomeLog>();
        log.AwaitingDeliveryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<OutcomeAwaitingDelivery>>(_ => waiting);
        var agents = Substitute.For<IAgentClient>();

        var sut = new ReportOutcomesUseCase(
            log, agents, Substitute.For<IUnitOfWork>(), NullLogger<ReportOutcomesUseCase>.Instance);
        var result = await sut.ReportAsync("sweep-1", TestContext.Current.CancellationToken);

        result.Delivered.ShouldBe(ReportOutcomesUseCase.MaxPerRequest);
        result.Remaining.ShouldBe(3);

        var sent = (OutcomeReportDto)agents.ReceivedCalls().Single().GetArguments()[0]!;
        sent.Outcomes.Count.ShouldBe(ReportOutcomesUseCase.MaxPerRequest);

        // Only what was sent is marked. The rest is still waiting for the next sweep.
        log.Received(ReportOutcomesUseCase.MaxPerRequest).MarkDelivered(Arg.Any<long>());
    }

    [Fact]
    public async Task Nothing_waiting_is_no_request_at_all()
    {
        var log = Substitute.For<IOutcomeLog>();
        log.AwaitingDeliveryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<OutcomeAwaitingDelivery>>(_ => []);
        var agents = Substitute.For<IAgentClient>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        var sut = new ReportOutcomesUseCase(
            log, agents, unitOfWork, NullLogger<ReportOutcomesUseCase>.Instance);
        var result = await sut.ReportAsync("sweep-1", TestContext.Current.CancellationToken);

        result.ShouldBe(DeliveryResult.Nothing);
        await agents.DidNotReceiveWithAnyArgs()
            .PostOutcomesAsync(null!, null!, TestContext.Current.CancellationToken);
        await unitOfWork.DidNotReceiveWithAnyArgs()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
