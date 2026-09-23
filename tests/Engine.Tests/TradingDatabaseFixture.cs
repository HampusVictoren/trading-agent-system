using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Engine.Tests.Persistence;

/// <summary>
/// A real Postgres for the tests that are about storage. An in-memory provider would answer
/// most of these questions with its own opinions: it has no triggers, no xmin, no arrays and
/// no permissions, which is four of the five things this schema relies on.
/// </summary>
/// <remarks>
/// The container runs <c>db/init/01-schema.sh</c> - the checked-in script, not a copy - so
/// the roles, the schema ownership and the grants are the ones a fresh volume gets. The
/// tests then connect as <c>engine_svc</c> rather than as the superuser, which is what makes
/// them able to catch the failure that hurts most: a migration that works for postgres and
/// is refused for the role that actually runs it.
/// </remarks>
public sealed class TradingDatabaseFixture : IAsyncLifetime
{
    private const string Database = "tradingdb";

    private const string EngineRole = "engine_svc";

    // Generated per run. Nothing outside this process ever sees them, and a literal here
    // would be a password-shaped string checked into the repository.
    private static readonly string SuperuserPassword = Guid.NewGuid().ToString("N");
    private static readonly string EnginePassword = Guid.NewGuid().ToString("N");
    private static readonly string AgentPassword = Guid.NewGuid().ToString("N");

    // The same image as docker-compose.yml. pgvector is not used by the trading schema, but
    // testing against a different Postgres than production runs is a difference nobody
    // remembers to account for.
    private const string Image = "pgvector/pgvector:0.8.6-pg16";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase(Database)
        .WithUsername("postgres")
        .WithPassword(SuperuserPassword)
        .WithEnvironment("ENGINE_DB_PASSWORD", EnginePassword)
        .WithEnvironment("AGENT_DB_PASSWORD", AgentPassword)
        .WithResourceMapping(
            new FileInfo(Path.Combine(AppContext.BaseDirectory, "db", "init", "01-schema.sh")),
            "/docker-entrypoint-initdb.d/")
        .Build();

    /// <summary>How the engine connects: as engine_svc, which owns the trading schema and
    /// cannot see the agent service's.</summary>
    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(5432),
        Database = Database,
        Username = EngineRole,
        Password = EnginePassword,
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// A context configured exactly as Program.cs configures it. Tests build their own rather
    /// than sharing one, because a change tracker that remembers the previous test would
    /// answer from memory instead of from the database - which is what they are checking.
    /// </summary>
    public TradingDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable(
                TradingDbContext.MigrationsHistoryTable, TradingDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new TradingDbContext(options);
    }

    /// <summary>
    /// Empties the tables between tests. TRUNCATE is deliberately not blocked by the
    /// append-only triggers: they exist to stop a cycle rewriting history, not to stop the
    /// schema's owner from deliberately clearing it.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE trading.decisions, trading.orders, trading.positions, trading.portfolios "
            + "RESTART IDENTITY CASCADE");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// One container for every test that needs a database, started when the first of them runs.
/// A collection fixture rather than an assembly one, so that a run of only the domain tests
/// does not need Docker at all.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TradingDatabaseCollection : ICollectionFixture<TradingDatabaseFixture>
{
    public const string Name = "trading database";
}
