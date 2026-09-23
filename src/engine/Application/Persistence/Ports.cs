namespace Engine.Application.Persistence;

using Engine.Domain.Aggregates.Portfolio;

/// <summary>
/// The stored portfolio. The engine runs exactly one, so there is no id to pass: a second
/// row would mean two accounts trading the same cash, which is a fault rather than a
/// feature. When the system grows to several, this gains an argument and the check below
/// becomes a lookup.
/// </summary>
public interface IPortfolioRepository
{
    /// <summary>
    /// The portfolio with its positions, or null the first time the engine ever runs.
    /// Orders are not loaded: nothing in the domain reads them back, and fetching the whole
    /// ledger to append one line would grow with the account's history.
    /// </summary>
    Task<Portfolio?> FindAsync(CancellationToken cancellationToken = default);

    void Add(Portfolio portfolio);
}

/// <summary>
/// Where an analysis cycle goes once it has happened. Writing is deferred to
/// <see cref="IUnitOfWork"/> so that the decision, the position change and the ledger line
/// from one cycle are one transaction - a cycle that half happened is worse than one that
/// did not.
/// </summary>
public interface IDecisionLog
{
    void Record(DecisionRecord decision);
}

/// <summary>
/// One commit per cycle. It exists as its own port rather than as a method on the repository
/// because a cycle touches three tables and they have to move together.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrentChangeException">
    /// Somebody else changed the portfolio between it being read and being written.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The portfolio moved under us. Its own type rather than the ORM's, so that a caller can
/// decide what to do about it without the application layer learning what a DbContext is.
/// </summary>
/// <remarks>
/// Nothing runs two writers today - the worker is one loop - so this is unreachable until
/// stage 4's scheduled outcome job joins it. That is exactly when a lost update would be
/// invisible, which is why the row version is in place before the second writer arrives.
/// </remarks>
public sealed class ConcurrentChangeException : Exception
{
    public ConcurrentChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
