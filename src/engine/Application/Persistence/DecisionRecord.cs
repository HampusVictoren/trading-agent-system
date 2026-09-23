namespace Engine.Application.Persistence;

using Engine.Application.UseCases;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// One analysis cycle, exactly as the engine saw it: what it asked, what came back, and what
/// it did about it. Append-only in practice - nothing updates a decision once it is written.
/// </summary>
/// <remarks>
/// <para>
/// The engine stores what reaches the engine. The agent service's own working - the fact
/// sheet each step was given and what the intermediate steps answered - is stored on the
/// Python side against the same correlation id, because it is that service's data and
/// widening the contract with fields the engine never reads would make the engine the owner
/// of somebody else's internals. Attributing a result to one agent is then a join across two
/// databases, done when the question is asked rather than on every cycle.
/// </para>
/// <para>
/// The prices are plain numbers with a currency beside them rather than <see cref="Money"/>.
/// This is a log row, not a domain object: nothing computes from it inside the engine, and a
/// report reads the columns directly.
/// </para>
/// </remarks>
public sealed class DecisionRecord
{
    /// <summary>Set by the database. The order it hands out is the order decisions happened in.</summary>
    public long Id { get; private set; }

    /// <summary>
    /// The id the cycle ran under, shared with the agent service's logs and its stored fact
    /// sheet. Unique, so a call that is somehow made twice cannot become two decisions.
    /// </summary>
    public required string CorrelationId { get; init; }

    public required Guid PortfolioId { get; init; }

    /// <summary>What was asked about - not what the answer was about. An answer naming another
    /// instrument is an outcome, and the row has to say which question produced it.</summary>
    public required Ticker Symbol { get; init; }

    public required string TeamId { get; init; }

    /// <summary>The <c>as_of</c> the engine sent, from its injected clock.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    // What the engine was willing to spend at the moment it asked. No agent reads these, but
    // a decision is only interpretable next to the room it was made in.
    public required decimal AvailableRiskBudgetUsd { get; init; }
    public required decimal MaxPositionPct { get; init; }
    public decimal? ExistingQuantity { get; init; }
    public decimal? ExistingAveragePrice { get; init; }

    // The answer, when there was one. Null all the way across means the call failed before a
    // signal existed, which is itself worth measuring.
    public string? TeamVersion { get; init; }
    public int? Revisions { get; init; }
    public Stance? Stance { get; init; }
    public double? Conviction { get; init; }
    public string? Thesis { get; init; }
    public string[] KeyRisks { get; init; } = [];
    public int? HorizonDays { get; init; }
    public decimal? ReferencePrice { get; init; }
    public string? ReferenceCurrency { get; init; }
    public DateTimeOffset? QuoteAsOf { get; init; }

    // What the engine did about it.
    public required DecisionOutcome Outcome { get; init; }
    public string? OutcomeReason { get; init; }

    /// <summary>The ledger line, when the decision produced one. Only an executed buy does.</summary>
    public Guid? OrderId { get; init; }

    /// <summary>Set by the database, so the row's own clock is the one that ordered it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }
}
