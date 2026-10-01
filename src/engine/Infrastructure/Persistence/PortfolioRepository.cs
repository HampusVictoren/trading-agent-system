namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Engine.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

public sealed class PortfolioRepository : IPortfolioRepository
{
    private readonly TradingDbContext _context;

    public PortfolioRepository(TradingDbContext context) => _context = context;

    public async Task<Portfolio?> FindAsync(CancellationToken cancellationToken = default)
    {
        // Take(2) rather than SingleAsync: asking for two makes "there is more than one" a
        // fact this method can report in the engine's own words, instead of an ORM message
        // about a sequence containing more than one element.
        var portfolios = await _context.Portfolios
            .Include(portfolio => portfolio.Positions)
            .OrderBy(portfolio => portfolio.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (portfolios.Count > 1)
        {
            throw new InvalidOperationException(
                "trading.portfolios holds more than one row. The engine trades one account, "
                + "so a second portfolio means two of them are spending the same cash.");
        }

        return portfolios.FirstOrDefault();
    }

    public void Add(Portfolio portfolio) => _context.Portfolios.Add(portfolio);

    /// <remarks>
    /// Summed in the database rather than in memory: the whole point of not loading the ledger is
    /// that it grows. <c>placed_at</c> is a shadow property filled by the database's own
    /// <c>now()</c>, so the day is read in UTC - which is the same day the engine works in, since
    /// every instrument in the universe trades in Stockholm.
    ///
    /// A day with no purchases has no rows, and <c>SumAsync</c> over nothing is zero rather than
    /// null for a <c>decimal</c>, so there is no empty case to special-case.
    ///
    /// <b>It does not filter on a portfolio</b>, and leans on the same invariant
    /// <see cref="FindAsync"/> enforces: the engine trades one account, and a second row there is
    /// reported as a fault rather than silently picked. If that ever stops being true this sum has
    /// to be scoped before anything else is, because a shared daily budget across two accounts
    /// would let each of them spend the other's.
    /// </remarks>
    public async Task<Money> DeployedOnAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var spent = await _context.Orders
            .Where(order => order.Side == OrderSide.Buy)
            .Where(order => DateOnly.FromDateTime(
                EF.Property<DateTimeOffset>(order, OrderConfiguration.PlacedAtColumn).UtcDateTime) == day)
            .SumAsync(order => order.Price.Amount * order.Quantity, cancellationToken);

        return new Money(spent, Money.DefaultCurrency);
    }
}
