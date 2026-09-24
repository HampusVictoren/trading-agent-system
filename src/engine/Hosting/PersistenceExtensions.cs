namespace Engine.Hosting;

using Engine.Application.Persistence;
using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the database context and the ports over it. Scoped, which is what
    /// AddDbContext gives by default and what the worker wants: one scope per cycle means
    /// one change tracker per cycle, and nothing survives into the next.
    /// </summary>
    public static IServiceCollection AddTradingDatabase(this IServiceCollection services)
    {
        services.AddDbContext<TradingDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            builder.UseNpgsql(options.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(
                TradingDbContext.MigrationsHistoryTable, TradingDbContext.Schema));

            builder.UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IPortfolioRepository, PortfolioRepository>();
        services.AddScoped<IDecisionLog, DecisionLog>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }

    /// <summary>
    /// Refuses to start when the database is behind the code, and never applies a migration
    /// itself.
    /// </summary>
    /// <remarks>
    /// Applying at startup would make a bad deploy hard to undo: the schema would already
    /// have moved by the time anyone noticed, and the migration was tested in both directions
    /// precisely so that undoing it stays possible. Refusing instead keeps the choice of when
    /// the schema changes with whoever is deploying, while still turning "forgot to migrate"
    /// into a message at startup rather than a column-does-not-exist error mid-cycle.
    ///
    /// It is also the engine's first connection, so an unreachable database fails here with a
    /// sentence about it - the same thing the agent service's lifespan does with its pool.
    /// </remarks>
    public static async Task EnsureTheSchemaIsCurrentAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        string[] pending;
        try
        {
            pending = [.. await context.Database.GetPendingMigrationsAsync(cancellationToken)];
        }
        catch (NpgsqlException e)
        {
            throw new InvalidOperationException(
                "Could not reach the database at startup. Is `docker compose up -d` running, "
                + "and is Database:ConnectionString set?", e);
        }

        if (pending.Length == 0)
            return;

        throw new InvalidOperationException(
            $"The database is behind this build by {pending.Length} migration(s): {string.Join(", ", pending)}. "
            + "Apply them with: dotnet dotnet-ef database update --project src/engine");
    }
}
