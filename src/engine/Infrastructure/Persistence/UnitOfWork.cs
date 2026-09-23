namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Commits a cycle. It wraps the context rather than being it, so that the one place the
/// application layer calls into the database is also the one place an ORM failure is
/// translated into something the application layer has a name for.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly TradingDbContext _context;

    public UnitOfWork(TradingDbContext context) => _context = context;

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new ConcurrentChangeException(
                "The portfolio changed between being read and being written. The cycle's "
                + "decision was not stored, and the next cycle starts from the current state.",
                e);
        }
    }
}
