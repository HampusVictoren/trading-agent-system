namespace Engine.Application.Persistence;

using Engine.Domain.Outcomes;

/// <summary>How a measurement ended. A row exists for both, and for neither does it change.</summary>
public enum OutcomeStatus
{
    /// <summary>The horizon passed and the numbers are here.</summary>
    Measured,

    /// <summary>
    /// There is a hole that waiting will not fill. Recorded rather than left out, because a
    /// signal that quietly never gets measured is a signal missing from the population the
    /// report speaks about - and because otherwise the sweep retries it every night forever.
    /// </summary>
    NotMeasurable
}

/// <summary>
/// One signal measured at one horizon. Append-only, like the decision it points at: the
/// numbers in it are the evidence stage 4 exists to collect.
/// </summary>
/// <remarks>
/// Everything needed to re-derive the verdict is stored, including the cost charged and the
/// benchmark used. Changing the band or the fee schedule later then re-scores the history
/// rather than discarding it - which matters, because those figures are guesses today.
/// </remarks>
public sealed class SignalOutcomeRecord
{
    public long Id { get; private set; }

    public required long DecisionId { get; init; }

    // The two columns a horizon takes. Five trading days and five calendar days are
    // different measurements, and one number cannot say which.
    public required HorizonUnit HorizonUnit { get; init; }
    public required int HorizonDays { get; init; }

    public required OutcomeStatus Status { get; init; }

    /// <summary>Why it can never be measured, and null when it was.</summary>
    public string? Reason { get; init; }

    /// <summary>What the signal was compared against. Stored, because it is configuration today.</summary>
    public required string BenchmarkSymbol { get; init; }

    // Null for a row that could not be measured. Nullable rather than zero: a return of zero
    // is a real answer, and a report must not average it in with the ones that never happened.
    public DateOnly? MeasuredOn { get; init; }
    public decimal? MeasuredPrice { get; init; }
    public decimal? InstrumentReturn { get; init; }
    public decimal? BenchmarkReturn { get; init; }
    public decimal? ExcessReturn { get; init; }
    public decimal? CostFraction { get; init; }
    public decimal? NetEdge { get; init; }
    public bool? Hit { get; init; }

    /// <summary>Set by the database, so every row is ordered by the same clock.</summary>
    public DateTimeOffset RecordedAt { get; private set; }
}
