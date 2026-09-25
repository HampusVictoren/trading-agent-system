namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The engine's half of the database. It owns the <c>trading</c> schema and nothing else:
/// the agent service's tables are in <c>agent</c>, under a role this connection cannot even
/// list. The two services talk over HTTP, never through a shared table.
/// </summary>
public sealed class TradingDbContext : DbContext
{
    public const string Schema = "trading";

    /// <summary>
    /// EF's own bookkeeping goes in <c>trading</c> too. engine_svc owns that schema and may
    /// not create anything in <c>public</c>, so the default location would fail on the first
    /// migration - as permission denied, long after the connection string looked fine.
    /// </summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public TradingDbContext(DbContextOptions<TradingDbContext> options)
        : base(options)
    {
    }

    public DbSet<Portfolio> Portfolios => Set<Portfolio>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<DecisionRecord> Decisions => Set<DecisionRecord>();

    public DbSet<SignalOutcomeRecord> SignalOutcomes => Set<SignalOutcomeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);
    }
}
