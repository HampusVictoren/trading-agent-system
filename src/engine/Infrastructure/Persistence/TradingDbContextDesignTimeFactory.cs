namespace Engine.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// How <c>dotnet ef</c> builds a context without starting the engine. Going through the host
/// instead would mean the worker's whole configuration - the agent service's key included -
/// had to be present on a machine that only wants to write a migration file.
/// </summary>
/// <remarks>
/// <c>migrations add</c> and <c>migrations script</c> never open a connection, so the
/// placeholder below is enough for them. A command that does connect reads
/// <c>ENGINE_DATABASE_URL</c>; the engine itself never looks at that variable, because its
/// connection string is configuration like any other.
/// </remarks>
public sealed class TradingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    private const string ConnectionStringVariable = "ENGINE_DATABASE_URL";

    private const string NoConnectionNeeded = "Host=design.invalid;Database=tradingdb;Username=engine_svc";

    public TradingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) ?? NoConnectionNeeded;

        var builder = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                TradingDbContext.MigrationsHistoryTable, TradingDbContext.Schema))
            .UseSnakeCaseNamingConvention();

        return new TradingDbContext(builder.Options);
    }
}
