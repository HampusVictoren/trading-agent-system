using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

/// <summary>
/// The reading behind <see cref="FactSheetChange"/>: what was stored, and what the price is now.
/// </summary>
/// <remarks>
/// The rule itself is tested against fixed values next door. What is worth checking here is the
/// I/O around it - that a quote is not fetched when its answer cannot change anything, and that
/// what counts as "last analysed" is a cycle that actually reached an answer.
/// </remarks>
public class AnalysisDueCheckTests
{
    private static readonly Ticker Eric = new("ERIC-B.ST");
    private static readonly DateOnly Monday = new(2026, 9, 28);
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 5, 0, TimeSpan.Zero);

    private static readonly RiskPolicy Policy = new(
        maxPositionPct: 0.05m,
        cashBufferPct: 0.10m,
        maxQuoteAge: TimeSpan.FromMinutes(5),
        minHoldingPeriod: TimeSpan.FromDays(3),
        stopLossPct: 0.10m,
        maxDailyDeploymentPct: 1m);

    private static QuoteDto AQuote(decimal price, DateTimeOffset? asOf = null) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = Eric.Value },
        Price = price,
        Currency = Money.DefaultCurrency,
        AsOf = asOf ?? Now
    };

    private static (AnalysisDueCheck Check, IAgentClient Client) Build(
        LastAnalysis? last, QuoteDto? quote = null)
    {
        var decisions = Substitute.For<IDecisionLog>();
        decisions.LastAnalysisOfAsync(Arg.Any<Ticker>(), Arg.Any<CancellationToken>()).Returns(last);

        var client = Substitute.For<IAgentClient>();
        client.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(quote);

        var quotes = new QuoteReader(client, Policy, NoCycleProgress.Instance, NullLogger<QuoteReader>.Instance);

        return (new AnalysisDueCheck(decisions, quotes), client);
    }

    [Fact]
    public async Task An_instrument_never_analysed_is_due_without_a_quote_being_fetched()
    {
        // The answer cannot change whatever the price is, so asking for it would be a request made
        // for nothing - once per never-analysed candidate, on the first cycle of every new symbol
        // in the universe.
        var (check, client) = Build(last: null);

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.Due);
        await client.DidNotReceive().GetQuoteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_instrument_analysed_today_is_skipped_without_a_quote_being_fetched()
    {
        // The common case by a wide margin: fourteen of fifteen cycles in an hour. It costs one
        // indexed read and no network at all.
        var (check, client) = Build(new LastAnalysis(Monday, 100m));

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.AlreadyAnalysedToday);
        await client.DidNotReceive().GetQuoteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_price_that_has_not_moved_since_yesterday_is_read_from_the_quote()
    {
        var (check, _) = Build(new LastAnalysis(Monday.AddDays(-1), 100m), AQuote(100m));

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.PriceHasNotMoved);
    }

    [Fact]
    public async Task A_price_that_has_moved_since_yesterday_is_due()
    {
        var (check, _) = Build(new LastAnalysis(Monday.AddDays(-1), 100m), AQuote(101.5m));

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public async Task A_quote_that_could_not_be_fetched_leaves_the_day_rule_to_decide()
    {
        var (check, _) = Build(new LastAnalysis(Monday.AddDays(-1), 100m), quote: null);

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public async Task A_quote_too_old_to_trust_is_no_quote()
    {
        // The policy's own rule, applied here as everywhere else. A price from an hour ago could
        // say "unchanged" about a market that has moved twice since.
        var (check, _) = Build(
            new LastAnalysis(Monday.AddDays(-1), 100m), AQuote(100m, Now.AddHours(-1)));

        var verdict = await check.ForAsync(Eric, Monday, Now, "cycle-1", TestContext.Current.CancellationToken);

        verdict.ShouldBe(AnalysisVerdict.Due);
    }

    [Fact]
    public async Task The_quote_carries_the_cycles_own_correlation_id()
    {
        var (check, client) = Build(new LastAnalysis(Monday.AddDays(-1), 100m), AQuote(100m));

        await check.ForAsync(Eric, Monday, Now, "cycle-9", TestContext.Current.CancellationToken);

        await client.Received(1).GetQuoteAsync("ERIC-B.ST", "cycle-9", Arg.Any<CancellationToken>());
    }
}
