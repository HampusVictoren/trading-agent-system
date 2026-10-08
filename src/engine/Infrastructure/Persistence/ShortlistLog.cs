namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class ShortlistLog : IShortlistLog
{
    private readonly TradingDbContext _context;

    public ShortlistLog(TradingDbContext context) => _context = context;

    /// <remarks>
    /// Candidates first, in rank order, then the rejections. Postgres sorts nulls last by
    /// default, so ordering on the rank alone already puts them that way round - but it is
    /// written out, because the caller iterates this list and a change to that default would be
    /// silent.
    /// </remarks>
    public async Task<IReadOnlyList<ShortlistEntry>> ForAsync(
        DateOnly on, CancellationToken cancellationToken = default) =>
        await _context.Shortlists
            .Where(entry => entry.ScreenedOn == on)
            .OrderBy(entry => entry.Rank == null)
            .ThenBy(entry => entry.Rank)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

    public void Record(ShortlistEntry entry) => _context.Shortlists.Add(entry);
}
