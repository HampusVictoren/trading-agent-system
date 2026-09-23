namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;

public sealed class DecisionLog : IDecisionLog
{
    private readonly TradingDbContext _context;

    public DecisionLog(TradingDbContext context) => _context = context;

    /// <summary>Queues the row. It reaches the database when the cycle commits, together with
    /// whatever the decision did to the portfolio.</summary>
    public void Record(DecisionRecord decision) => _context.Decisions.Add(decision);
}
