namespace Engine.Hosting;

using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

        return services;
    }
}
