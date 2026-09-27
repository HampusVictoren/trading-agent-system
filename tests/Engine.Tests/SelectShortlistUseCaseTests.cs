using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

/// <summary>
/// One screen per trading day, read back on every cycle after the first.
/// </summary>
/// <remarks>
/// The rule this file is about is not a saving taken against the contract, it is the contract's
/// own claim: the factors are computed from daily bars, so two screens on the same day rank the
/// same way. What is worth testing is therefore not that the ranking is right - that is the
/// agent service's, tested there against fixed data - but that the engine asks exactly once, and
/// that every way the screen can fail leaves the holdings analysable.
/// </remarks>
public class SelectShortlistUseCaseTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static readonly DateTimeOffset ScreenedAt =
        new(2026, 9, 27, 7, 30, 0, TimeSpan.Zero);

    private const string CorrelationId = "cycle-1";

    private static ScreenResultDto AScreen(
        IEnumerable<(string Symbol, decimal Score)>? candidates = null,
        IEnumerable<(string Symbol, string Reason)>? rejected = null) => new()
        {
            Candidates = (candidates ?? [("NVDA", 1.4m), ("AAPL", 0.6m)])
                .Select(entry => new CandidateDto
                {
                    Instrument = new EquityInstrumentDto { Symbol = entry.Symbol },
                    Score = entry.Score,
                    Return3M = 0.28m,
                    Volatility30D = 0.2m,
                    MedianDollarVolume = 41_250_000m
                })
                .ToArray(),
            Rejected = (rejected ?? [("TINY", "typical daily turnover 41000 is below the floor")])
                .Select(entry => new RejectionDto
                {
                    Instrument = new EquityInstrumentDto { Symbol = entry.Symbol },
                    Reason = entry.Reason
                })
                .ToArray(),
            AsOf = ScreenedAt
        };

    /// <summary>Holds what it was told, and answers with what it holds - the log, without a database.</summary>
    private sealed class InMemoryShortlists : IShortlistLog
    {
        public List<ShortlistEntry> Stored { get; } = [];

        public Task<IReadOnlyList<ShortlistEntry>> ForAsync(
            DateOnly on, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ShortlistEntry>>(Stored
                .Where(entry => entry.ScreenedOn == on)
                .OrderBy(entry => entry.Rank is null)
                .ThenBy(entry => entry.Rank)
                .ToArray());

        public void Record(ShortlistEntry entry) => Stored.Add(entry);
    }

    private static (SelectShortlistUseCase UseCase, IAgentClient Client, InMemoryShortlists Log) Build(
        ScreenResultDto? answer = null, Exception? thrown = null, string[]? universe = null)
    {
        var client = Substitute.For<IAgentClient>();

        if (thrown is not null)
        {
            client.GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(thrown);
        }
        else
        {
            client.GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>())
                .Returns(answer);
        }

        var log = new InMemoryShortlists();

        var options = Options.Create(new TradingOptions
        {
            Universe = universe ?? ["NVDA", "AAPL", "TINY"],
            ShortlistSize = 2,
            MinDollarVolume = 5_000_000m,
            CycleIntervalMinutes = 15,
            TeamId = "default",
            OpeningBalance = 100_000m
        });

        return (
            new SelectShortlistUseCase(
                client, log, options, NullLogger<SelectShortlistUseCase>.Instance),
            client,
            log);
    }

    [Fact]
    public async Task The_first_cycle_of_the_day_screens_and_stores_what_came_back()
    {
        var (useCase, client, log) = Build(AScreen());

        var shortlist = await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        shortlist.Select(ticker => ticker.Value).ShouldBe(["NVDA", "AAPL"]);

        await client.Received(1).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
        log.Stored.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_rank_is_the_position_in_the_answer_counted_from_one()
    {
        // From the position rather than from the score. The order is what the contract promises
        // and the mapper has already checked; re-deriving it from the score here would have to
        // reproduce the tie-break as well.
        var (useCase, _, log) = Build(AScreen());

        await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        log.Stored.Single(entry => entry.Symbol.Value == "NVDA").Rank.ShouldBe(1);
        log.Stored.Single(entry => entry.Symbol.Value == "AAPL").Rank.ShouldBe(2);
    }

    [Fact]
    public async Task A_rejection_is_stored_with_its_reason_and_no_rank()
    {
        // The half that is easy to drop, because nothing is traded from it. It is also the only
        // way a universe that is quietly rotting ever becomes visible.
        var (useCase, _, log) = Build(AScreen());

        await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        var rejected = log.Stored.Single(entry => entry.Symbol.Value == "TINY");

        rejected.Rank.ShouldBeNull();
        rejected.Score.ShouldBeNull();
        rejected.RejectedBecause.ShouldBe("typical daily turnover 41000 is below the floor");
    }

    [Fact]
    public async Task The_figures_are_stored_beside_the_rank()
    {
        var (useCase, _, log) = Build(AScreen());

        await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        var candidate = log.Stored.Single(entry => entry.Symbol.Value == "NVDA");

        candidate.Score.ShouldBe(1.4m);
        candidate.Return3M.ShouldBe(0.28m);
        candidate.Volatility30D.ShouldBe(0.2m);
        candidate.MedianDollarVolume.ShouldBe(41_250_000m);
        candidate.ScreenedAt.ShouldBe(ScreenedAt);
        candidate.ScreenedOn.ShouldBe(Today);
        candidate.CorrelationId.ShouldBe(CorrelationId);
    }

    [Fact]
    public async Task The_second_cycle_of_the_day_reads_the_stored_shortlist_instead()
    {
        var (useCase, client, log) = Build(AScreen());

        await useCase.ExecuteAsync(Today, "cycle-1", TestContext.Current.CancellationToken);
        var second = await useCase.ExecuteAsync(Today, "cycle-2", TestContext.Current.CancellationToken);

        second.Select(ticker => ticker.Value).ShouldBe(["NVDA", "AAPL"]);

        // The whole point: one batched fetch of the universe per trading day, not one per cycle.
        await client.Received(1).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
        log.Stored.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_new_trading_day_screens_again()
    {
        var (useCase, client, log) = Build(AScreen());

        await useCase.ExecuteAsync(Today, "cycle-1", TestContext.Current.CancellationToken);
        await useCase.ExecuteAsync(Today.AddDays(1), "cycle-2", TestContext.Current.CancellationToken);

        await client.Received(2).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
        log.Stored.Count(entry => entry.ScreenedOn == Today.AddDays(1)).ShouldBe(3);
    }

    [Fact]
    public async Task A_day_where_the_whole_universe_was_rejected_is_a_screened_day()
    {
        // The case that reads as "not screened yet" if only the candidates are counted, and the
        // one where re-screening is guaranteed to produce the same nothing.
        var (useCase, client, _) = Build(AScreen(
            candidates: [],
            rejected: [("NVDA", "no close from 90 days ago"), ("AAPL", "below the floor")]));

        var first = await useCase.ExecuteAsync(Today, "cycle-1", TestContext.Current.CancellationToken);
        var second = await useCase.ExecuteAsync(Today, "cycle-2", TestContext.Current.CancellationToken);

        first.ShouldBeEmpty();
        second.ShouldBeEmpty();
        await client.Received(1).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_request_carries_the_configured_universe_and_its_limits()
    {
        var (useCase, client, _) = Build(AScreen(), universe: ["ERIC-B.ST", "VOLV-B.ST"]);

        await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        var request = (ScreenRequestDto)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentClient.GetScreenAsync))
            .GetArguments()[0]!;

        request.Universe.Cast<EquityInstrumentDto>().Select(instrument => instrument.Symbol)
            .ShouldBe(["ERIC-B.ST", "VOLV-B.ST"]);
        request.Limit.ShouldBe(2);
        request.MinDollarVolume.ShouldBe(5_000_000m);
        request.CorrelationId.ShouldBe(CorrelationId);
    }

    [Fact]
    public async Task A_screen_that_could_not_be_reached_stores_nothing_and_asks_again()
    {
        // Nothing stored is what makes the retry correct: a day is only "screened" once a row
        // says so, so an outage costs candidates for one cycle rather than for the whole day.
        var (useCase, client, log) = Build(thrown: new AgentServiceUnavailableException("503"));

        var shortlist = await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        shortlist.ShouldBeEmpty();
        log.Stored.ShouldBeEmpty();

        await useCase.ExecuteAsync(Today, "cycle-2", TestContext.Current.CancellationToken);
        await client.Received(2).GetScreenAsync(Arg.Any<ScreenRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_screen_that_broke_the_contract_is_no_shortlist_rather_than_a_crash()
    {
        // The mapper throws; the cycle carries on with the holdings. An answer the engine cannot
        // trust must not be able to stop a position from being looked at.
        var (useCase, _, log) = Build(thrown: new AgentResponseInvalidException("not the contract"));

        var shortlist = await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        shortlist.ShouldBeEmpty();
        log.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_body_is_no_shortlist_rather_than_a_crash()
    {
        var (useCase, _, log) = Build(answer: null);

        var shortlist = await useCase.ExecuteAsync(Today, CorrelationId, TestContext.Current.CancellationToken);

        shortlist.ShouldBeEmpty();
        log.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shutting_down_is_not_a_failed_screen()
    {
        var (useCase, _, _) = Build(thrown: new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => useCase.ExecuteAsync(Today, CorrelationId, cancelled.Token));
    }
}
