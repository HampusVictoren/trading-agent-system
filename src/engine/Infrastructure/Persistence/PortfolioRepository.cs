namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
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
}
