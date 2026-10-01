using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Outcomes;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// <c>trading.shortlist_edge</c>: did the agents beat the screen that picked their candidates?
/// </summary>
/// <remarks>
/// <para>
/// Every one of these builds its own rows, and that is not a convenience. The view cannot be
/// checked against real data until a horizon has passed on a day that was screened - the first
/// screened day is 2026-10-01 and its shortest horizon is a trading day away - so a test with
/// fabricated measurements is the only thing standing between this view and a number nobody has
/// ever verified. It is also the only way to put a HOLD, a buy and an unmeasurable row in one
/// shortlist on purpose.
/// </para>
/// <para>
/// The figures are chosen so that every average is exact in decimal and can be checked by hand
/// rather than by re-running the same arithmetic the view does.
/// </para>
/// </remarks>
[Collection(TradingDatabaseCollection.Name)]
public class ShortlistEdgeTests : IAsyncLifetime
{
    private static readonly DateOnly ScreenedOn = new(2026, 10, 1);

    private static readonly DateTimeOffset RequestedAt =
        new(2026, 10, 1, 9, 5, 0, TimeSpan.Zero);

    private readonly TradingDatabaseFixture _database;

    public ShortlistEdgeTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>One row of the view, in the shape a reader of psql would see.</summary>
    private sealed record Row(
        DateOnly ScreenedOn,
        int Shortlisted,
        decimal? ShortlistExcessGross,
        int Bought,
        decimal? BoughtExcessGross,
        decimal? AgentsEdgeGross,
        decimal? BoughtEdgeNet);

    private async Task<IReadOnlyList<Row>> ReadTheViewAsync()
    {
        await using var context = _database.NewContext();

        return await context.Database.SqlQueryRaw<Row>(
            """
            -- No aliases: UseSnakeCaseNamingConvention applies to a query type too, so EF
            -- looks for the snake_case column the property maps to.
            SELECT screened_on, shortlisted, shortlist_excess_gross,
                   bought, bought_excess_gross, agents_edge_gross, bought_edge_net
            FROM trading.shortlist_edge
            ORDER BY screened_on
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// One shortlisted instrument, analysed, with a measurement at one trading day.
    /// </summary>
    /// <param name="rank">Null for a rejection, which must not reach the view at all.</param>
    /// <param name="netEdge">
    /// Null is what a HOLD really stores, which is the whole reason this view averages the
    /// excess return instead.
    /// </param>
    private async Task AShortlistedInstrumentAsync(
        Guid portfolioId,
        string symbol,
        int? rank,
        Stance stance,
        DecisionOutcome outcome,
        decimal? excessReturn,
        decimal? netEdge,
        OutcomeStatus status = OutcomeStatus.Measured,
        SelectionSource selection = SelectionSource.Shortlist,
        DateTimeOffset? requestedAt = null)
    {
        await using var context = _database.NewContext();

        context.Shortlists.Add(new ShortlistEntry
        {
            CorrelationId = $"cycle-{symbol}",
            ScreenedOn = ScreenedOn,
            ScreenedAt = RequestedAt,
            Symbol = new Ticker(symbol),
            Rank = rank,
            Score = rank is null ? null : 1m,
            Return3M = rank is null ? null : 0.1m,
            Volatility30D = rank is null ? null : 0.2m,
            MedianDollarVolume = rank is null ? null : 41_250_000m,
            RejectedBecause = rank is null ? "below the floor" : null
        });

        var decision = new DecisionRecord
        {
            CorrelationId = $"cycle-{symbol}",
            PortfolioId = portfolioId,
            Symbol = new Ticker(symbol),
            TeamId = "default",
            TeamVersion = "abc123",
            Selection = selection,
            RequestedAt = requestedAt ?? RequestedAt,
            AvailableRiskBudget = 100_000m,
            MaxPositionPct = 0.05m,
            Stance = stance,
            Conviction = 0.6,
            ReferencePrice = 100m,
            ReferenceCurrency = Money.DefaultCurrency,
            Outcome = outcome
        };

        context.Decisions.Add(decision);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.SignalOutcomes.Add(new SignalOutcomeRecord
        {
            DecisionId = decision.Id,
            HorizonUnit = HorizonUnit.TradingDays,
            HorizonDays = 1,
            Status = status,
            BenchmarkSymbol = "XACT-OMXS30.ST",
            MeasuredOn = status == OutcomeStatus.Measured ? ScreenedOn.AddDays(1) : null,
            MeasuredPrice = status == OutcomeStatus.Measured ? 101m : null,
            InstrumentReturn = excessReturn,
            BenchmarkReturn = status == OutcomeStatus.Measured ? 0m : null,
            ExcessReturn = excessReturn,
            CostFraction = status == OutcomeStatus.Measured ? 0.0003m : null,
            NetEdge = netEdge,
            Hit = status == OutcomeStatus.Measured ? excessReturn > 0m : null,
            Reason = status == OutcomeStatus.Measured ? null : "no bar at the horizon"
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> AnAccountAsync()
    {
        await using var context = _database.NewContext();
        var portfolio = new Portfolio(new Money(100_000m, Money.DefaultCurrency));
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return portfolio.Id;
    }

    [Fact]
    public async Task The_buys_are_compared_against_the_whole_shortlist()
    {
        // Four shortlisted, two of them bought. The buys averaged 6 %, the shortlist 3 %, so the
        // agents picked three points better than the ranking that handed them the candidates -
        // which is the number this whole stage exists to produce.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.08m, 0.0797m);
        await AShortlistedInstrumentAsync(account, "BBB.ST", 2, Stance.Buy, DecisionOutcome.Executed, 0.04m, 0.0397m);
        await AShortlistedInstrumentAsync(account, "CCC.ST", 3, Stance.Hold, DecisionOutcome.NoAction, 0.02m, null);
        await AShortlistedInstrumentAsync(account, "DDD.ST", 4, Stance.Hold, DecisionOutcome.NoAction, -0.02m, null);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.ScreenedOn.ShouldBe(ScreenedOn);
        row.Shortlisted.ShouldBe(4);
        row.ShortlistExcessGross.ShouldBe(0.03m);
        row.Bought.ShouldBe(2);
        row.BoughtExcessGross.ShouldBe(0.06m);
        row.AgentsEdgeGross.ShouldBe(0.03m);
    }

    [Fact]
    public async Task The_shortlist_average_includes_the_holds_rather_than_only_the_buys()
    {
        // The mistake this view was written to avoid, stated as a test. net_edge is null for
        // every HOLD, so a view that averaged it would have reported the buys' own average as
        // the shortlist's - an agents' edge of exactly zero, every time, looking like data.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.10m, 0.0997m);
        await AShortlistedInstrumentAsync(account, "BBB.ST", 2, Stance.Hold, DecisionOutcome.NoAction, 0.00m, null);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        // 5 %, not 10 %: the HOLD is in the denominator.
        row.ShortlistExcessGross.ShouldBe(0.05m);
        row.BoughtExcessGross.ShouldBe(0.10m);
        row.AgentsEdgeGross.ShouldBe(0.05m);
    }

    [Fact]
    public async Task The_net_figure_is_the_buys_alone_and_sits_beside_the_gross_one()
    {
        // Gross against gross is the honest comparison, because the shortlist average is a
        // paper portfolio that paid no commission. The net number is what the account really
        // earned, reported next to it rather than instead of it.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.08m, 0.0770m);
        await AShortlistedInstrumentAsync(account, "BBB.ST", 2, Stance.Hold, DecisionOutcome.NoAction, 0.02m, null);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.BoughtExcessGross.ShouldBe(0.08m);
        row.BoughtEdgeNet.ShouldBe(0.077m);
    }

    [Fact]
    public async Task A_rejected_instrument_is_not_part_of_the_shortlist()
    {
        // It was looked at and filtered out, so it was never a candidate and has no business in
        // an average of what the screen put forward.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.10m, 0.0997m);
        await AShortlistedInstrumentAsync(account, "ZZZ.ST", null, Stance.Hold, DecisionOutcome.NoAction, -0.50m, null);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.Shortlisted.ShouldBe(1);
        row.ShortlistExcessGross.ShouldBe(0.10m);
    }

    [Fact]
    public async Task A_measurement_that_could_not_be_made_is_left_out()
    {
        // A row that says why it is unmeasurable is still a row - that is how the sweep stops
        // retrying it - but it carries no return, and counting it would divide by a population
        // bigger than the one that was measured.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.10m, 0.0997m);
        await AShortlistedInstrumentAsync(
            account, "BBB.ST", 2, Stance.Hold, DecisionOutcome.NoAction, null, null,
            status: OutcomeStatus.NotMeasurable);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.Shortlisted.ShouldBe(1);
        row.ShortlistExcessGross.ShouldBe(0.10m);
    }

    [Fact]
    public async Task A_holding_the_screen_also_ranked_is_counted()
    {
        // Deliberate. The shortlist row exists even though the decision was taken because the
        // portfolio held it, and how the screen rated what was already owned is worth asking.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(
            account, "AAA.ST", 1, Stance.Hold, DecisionOutcome.NoAction, 0.04m, null,
            selection: SelectionSource.Holding);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.Shortlisted.ShouldBe(1);
        row.Bought.ShouldBe(0);
        row.ShortlistExcessGross.ShouldBe(0.04m);
        row.BoughtExcessGross.ShouldBeNull();
    }

    [Fact]
    public async Task A_decision_from_another_day_does_not_join_this_shortlist()
    {
        // The join is on the trading day as well as the symbol, because the same instrument is
        // screened again tomorrow and a measurement belongs to the shortlist that produced it.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, 0.10m, 0.0997m);
        await AShortlistedInstrumentAsync(
            account, "BBB.ST", 2, Stance.Buy, DecisionOutcome.Executed, -0.30m, -0.3003m,
            requestedAt: RequestedAt.AddDays(1));

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.Shortlisted.ShouldBe(1);
        row.ShortlistExcessGross.ShouldBe(0.10m);
    }

    [Fact]
    public async Task A_shortlist_with_nothing_measured_yet_is_no_row_at_all()
    {
        // Today's state, and for the next trading day it is the only state. An empty view is the
        // honest answer to "how did the shortlist do?" before a horizon has passed.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(
            account, "AAA.ST", 1, Stance.Buy, DecisionOutcome.Executed, null, null,
            status: OutcomeStatus.NotMeasurable);

        (await ReadTheViewAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shortlist_nobody_bought_from_reports_no_buys_rather_than_no_row()
    {
        // The result that matters most if it keeps happening: the screen found candidates and
        // the agents declined all of them. The shortlist's own return still has to be visible,
        // or a team that never trades would simply vanish from the report.
        var account = await AnAccountAsync();

        await AShortlistedInstrumentAsync(account, "AAA.ST", 1, Stance.Hold, DecisionOutcome.NoAction, 0.06m, null);
        await AShortlistedInstrumentAsync(account, "BBB.ST", 2, Stance.Hold, DecisionOutcome.NoAction, 0.02m, null);

        var row = (await ReadTheViewAsync()).ShouldHaveSingleItem();

        row.Shortlisted.ShouldBe(2);
        row.ShortlistExcessGross.ShouldBe(0.04m);
        row.Bought.ShouldBe(0);
        row.BoughtExcessGross.ShouldBeNull();
        row.AgentsEdgeGross.ShouldBeNull();
    }
}
