using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Engine.Hosting;
using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// What the schema promises, checked against the schema rather than against the code that is
/// supposed to respect it. Every one of these is a rule that has no C# to enforce it: the
/// database is the enforcement, and a test is the only way to know it still is.
/// </summary>
[Collection(TradingDatabaseCollection.Name)]
public class TradingSchemaTests : IAsyncLifetime
{
    private static readonly Ticker Aapl = new("AAPL");

    private readonly TradingDatabaseFixture _database;

    public TradingSchemaTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> AnAccountThatHasBought()
    {
        await using var context = _database.NewContext();
        var portfolio = new Portfolio(new Money(10_000m, Money.DefaultCurrency));
        portfolio.ExecuteBuy(Aapl, quantity: 2m, new Money(100m));
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return portfolio.Id;
    }

    [Fact]
    public async Task The_ledger_refuses_to_be_rewritten()
    {
        await AnAccountThatHasBought();
        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE trading.orders SET quantity = 999", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task A_decision_cannot_be_edited_after_the_fact()
    {
        // The decision history is the evidence the whole stage exists to collect. Evidence
        // that can be quietly corrected afterwards is not evidence.
        var portfolioId = await AnAccountThatHasBought();

        await using (var writing = _database.NewContext())
        {
            writing.Decisions.Add(ADecision(portfolioId, "cycle-1"));
            await writing.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE trading.decisions SET outcome = 'Executed'", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task A_delete_that_would_match_nothing_is_refused_too()
    {
        // The trigger is statement-level on purpose. A row-level one would let
        // "DELETE FROM trading.orders" succeed silently against an empty table, which reads
        // as permission to try again once there is something in it.
        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "DELETE FROM trading.orders", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task One_holding_per_instrument_is_a_database_rule()
    {
        // The aggregate merges a second buy into the existing position, so this can only be
        // reached by going round it. That is exactly the case a primary key is for.
        var portfolioId = await AnAccountThatHasBought();
        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO trading.positions
                (portfolio_id, symbol, quantity,
                 average_purchase_price_amount, average_purchase_price_currency)
            VALUES ({0}, 'AAPL', 5, 100, 'USD')
            """,
            [portfolioId],
            TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("pk_positions");
    }

    [Fact]
    public async Task The_same_cycle_cannot_be_recorded_twice()
    {
        // A correlation id identifies one analysis. The HTTP client already refuses to retry
        // a failing response so that one cycle costs one decision; this is the half that
        // holds even if that rule is ever relaxed by accident.
        var portfolioId = await AnAccountThatHasBought();

        await using (var first = _database.NewContext())
        {
            first.Decisions.Add(ADecision(portfolioId, "cycle-1"));
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = _database.NewContext();
        second.Decisions.Add(ADecision(portfolioId, "cycle-1"));

        var exception = await Should.ThrowAsync<DbUpdateException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));

        exception.InnerException!.Message.ShouldContain("ix_decisions_correlation_id");
    }

    [Fact]
    public async Task Two_writers_cannot_both_win()
    {
        // Nothing runs two writers yet - the worker is one loop. The scheduled outcome job
        // later in this stage is the second, and a lost update between them would be
        // invisible, so the row version goes in before it arrives rather than after.
        await AnAccountThatHasBought();

        await using var first = _database.NewContext();
        await using var second = _database.NewContext();

        var read = await new PortfolioRepository(first).FindAsync(TestContext.Current.CancellationToken);
        var alsoRead = await new PortfolioRepository(second).FindAsync(TestContext.Current.CancellationToken);

        // Both add to the holding that already exists, so the only row they collide on is the
        // portfolio's. Two new positions would collide on the positions key first, and the
        // test would pass for the wrong reason.
        read!.ExecuteBuy(Aapl, quantity: 1m, new Money(100m));
        alsoRead!.ExecuteBuy(Aapl, quantity: 1m, new Money(150m));

        await new UnitOfWork(first).SaveChangesAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConcurrentChangeException>(
            () => new UnitOfWork(second).SaveChangesAsync(TestContext.Current.CancellationToken));

        // The loser wrote nothing at all, not even the order its cycle had already built.
        await using var reading = _database.NewContext();
        var orders = await reading.Orders.CountAsync(TestContext.Current.CancellationToken);
        orders.ShouldBe(2);
    }

    [Fact]
    public async Task The_migration_goes_down_as_well_as_up()
    {
        // A deploy that cannot be rolled back is a deploy nobody dares make. Down is also
        // where the trigger function hides: dropping the tables does not take it with them,
        // so a down that forgot it would fail the next up on "function already exists".
        await using var context = _database.NewContext();
        var migrator = context.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync("0", TestContext.Current.CancellationToken);

            (await TablesInTradingSchema(context)).ShouldBe(0);
            (await FunctionsInTradingSchema(context)).ShouldBe(0);
            (await ViewsInTradingSchema(context)).ShouldBe(0);

            await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

            (await TablesInTradingSchema(context)).ShouldBe(6);
            (await FunctionsInTradingSchema(context)).ShouldBe(1);

            // The report view depends on two tables, so it has to be dropped before them and
            // rebuilt after. A down migration that forgot it would fail on DROP TABLE.
            (await ViewsInTradingSchema(context)).ShouldBe(1);
        }
        finally
        {
            // Leave the schema as the other tests expect it, whichever assertion failed.
            await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_engine_refuses_to_start_against_a_database_that_is_behind()
    {
        // The alternative - migrating at startup - would move the schema before anyone could
        // decide to. This turns "forgot to migrate" into a sentence naming the command, and
        // leaves when the schema changes with whoever is deploying.
        await using var context = _database.NewContext();
        var migrator = context.GetService<IMigrator>();
        await using var engine = AnEngineAgainst(_database.ConnectionString);

        try
        {
            await migrator.MigrateAsync("0", TestContext.Current.CancellationToken);

            var exception = await Should.ThrowAsync<InvalidOperationException>(
                () => engine.EnsureTheSchemaIsCurrentAsync(TestContext.Current.CancellationToken));

            exception.Message.ShouldContain("InitialTradingSchema");
            exception.Message.ShouldContain("dotnet-ef database update");

            // And the schema is still down: refusing must not be a migration in disguise.
            (await TablesInTradingSchema(context)).ShouldBe(0);
        }
        finally
        {
            await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Current again, so it starts.
        await engine.EnsureTheSchemaIsCurrentAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unreachable_database_says_what_to_do_about_it()
    {
        // The check is also the engine's first connection. In WSL's mirrored networking a
        // dead port hangs rather than refuses, so the short timeout is part of the test.
        await using var engine = AnEngineAgainst(
            "Host=127.0.0.1;Port=1;Database=tradingdb;Username=engine_svc;Timeout=1");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => engine.EnsureTheSchemaIsCurrentAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("docker compose up -d");
    }

    /// <summary>The database half of Program.cs's container, and nothing else.</summary>
    private static ServiceProvider AnEngineAgainst(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddTradingDatabase();

        return services.BuildServiceProvider();
    }

    /// <summary>The migrations history is EF's own bookkeeping, not part of the schema.</summary>
    private static async Task<int> TablesInTradingSchema(TradingDbContext context) =>
        await context.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value" FROM pg_tables
            WHERE schemaname = 'trading' AND tablename NOT LIKE '\_\_%'
            """).SingleAsync(TestContext.Current.CancellationToken);

    /// <summary>The report view, which the down migration has to remove before the tables.</summary>
    private static async Task<int> ViewsInTradingSchema(TradingDbContext context) =>
        await context.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value" FROM pg_views WHERE schemaname = 'trading'
            """).SingleAsync(TestContext.Current.CancellationToken);

    private static async Task<int> FunctionsInTradingSchema(TradingDbContext context) =>
        await context.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value" FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'trading'
            """).SingleAsync(TestContext.Current.CancellationToken);

    private static DecisionRecord ADecision(Guid portfolioId, string correlationId) => new()
    {
        CorrelationId = correlationId,
        PortfolioId = portfolioId,
        Symbol = Aapl,
        TeamId = "default",
        RequestedAt = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
        AvailableRiskBudget = 10_000m,
        MaxPositionPct = 0.05m,
        Outcome = DecisionOutcome.NoAction,
        OutcomeReason = "HOLD",
    };
}
