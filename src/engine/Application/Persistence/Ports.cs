namespace Engine.Application.Persistence;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Outcomes;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

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
/// A stored decision that still has something to measure, with what has already been
/// measured about it. Which horizons are *wanted* is configuration, so it is worked out
/// above this rather than here.
/// </summary>
/// <param name="SignalDate">
/// The market day the view was formed on, taken from the quote's timestamp in UTC. For a US
/// market that is unambiguous - quotes arrive between 13:30 and 20:00 UTC, all on the same
/// calendar date - and it is worth revisiting when the universe widens in stage 5.
/// </param>
/// <param name="ModelHorizonDays">The horizon the model asked for, in calendar days.</param>
public sealed record SignalAwaitingMeasurement(
    long DecisionId,
    Ticker Symbol,
    Stance Stance,
    decimal ReferencePrice,
    DateOnly SignalDate,
    int ModelHorizonDays,
    IReadOnlySet<(HorizonUnit Unit, int Days)> AlreadyMeasured);

/// <summary>
/// A measurement that has not reached the agent service yet, in the shape the contract
/// sends it. The correlation id rather than the decision's id, because that is the one
/// identifier both services wrote down.
/// </summary>
public sealed record OutcomeAwaitingDelivery(
    long SignalOutcomeId,
    string CorrelationId,
    HorizonUnit HorizonUnit,
    int HorizonDays,
    OutcomeStatus Status,
    string? Reason,
    string BenchmarkSymbol,
    DateOnly? MeasuredOn,
    decimal? MeasuredPrice,
    decimal? InstrumentReturn,
    decimal? BenchmarkReturn,
    decimal? ExcessReturn,
    decimal? CostFraction,
    decimal? NetEdge,
    bool? Hit);

/// <summary>
/// Where a measurement goes, and what is left to measure. Reading and writing sit on one
/// port because they are two halves of one sweep.
/// </summary>
public interface IOutcomeLog
{
    /// <summary>
    /// Every decision that produced a signal, with the horizons already written for it. A
    /// decision that never reached an answer is not a signal and is filtered out here rather
    /// than retried every night.
    /// </summary>
    Task<IReadOnlyList<SignalAwaitingMeasurement>> AwaitingMeasurementAsync(
        CancellationToken cancellationToken = default);

    void Record(SignalOutcomeRecord outcome);

    /// <summary>
    /// Measurements the agent service has not been told about, oldest first, at most
    /// <paramref name="limit"/> of them.
    /// </summary>
    /// <remarks>
    /// Oldest first so that a backlog drains in the order it happened rather than newest
    /// first forever, and capped because a request is a unit of work with a timeout. What
    /// is left over is picked up by the next sweep.
    /// </remarks>
    Task<IReadOnlyList<OutcomeAwaitingDelivery>> AwaitingDeliveryAsync(
        int limit, CancellationToken cancellationToken = default);

    /// <summary>Queues the marker. It reaches the database on the next commit.</summary>
    void MarkDelivered(long signalOutcomeId);
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
/// Nothing reaches this today. The measurement worker is a second loop but not a second
/// writer of the portfolio - it writes signal_outcomes and outcome_deliveries and nothing
/// else, which is a test. The row version is in place for the writer stage 5 brings, when a
/// lost update would be money rather than a row.
/// </remarks>
public sealed class ConcurrentChangeException : Exception
{
    public ConcurrentChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
