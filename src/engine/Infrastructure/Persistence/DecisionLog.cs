namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

public sealed class DecisionLog : IDecisionLog
{
    private readonly TradingDbContext _context;

    public DecisionLog(TradingDbContext context) => _context = context;

    /// <summary>Queues the row. It reaches the database when the cycle commits, together with
    /// whatever the decision did to the portfolio.</summary>
    public void Record(DecisionRecord decision) => _context.Decisions.Add(decision);

    /// <remarks>
    /// Ordered by <c>requested_at</c> and then by id, because a single transaction stamps every
    /// row it writes with the same clock - so the id is what breaks the tie, and it is handed out
    /// in the order the decisions happened.
    ///
    /// The date is taken in UTC. Every instrument in the universe trades in Stockholm, where a
    /// session runs from 07:00 to 15:30 UTC, so a trading day and a UTC date are the same thing
    /// here. That is an assumption about the universe rather than about the world, and it is the
    /// one to revisit first if the universe ever crosses a date line.
    /// </remarks>
    public async Task<LastAnalysis?> LastAnalysisOfAsync(
        Ticker symbol, CancellationToken cancellationToken = default) =>
        await _context.Decisions
            .Where(decision => decision.Symbol == symbol && decision.ReferencePrice != null)
            .OrderByDescending(decision => decision.RequestedAt)
            .ThenByDescending(decision => decision.Id)
            .Select(decision => new LastAnalysis(
                DateOnly.FromDateTime(decision.RequestedAt.UtcDateTime), decision.ReferencePrice!.Value))
            .FirstOrDefaultAsync(cancellationToken);
}
