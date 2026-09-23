namespace Engine.Application.UseCases;

using Engine.Domain.ValueObjects;

/// <summary>
/// The stored name of an outcome. It is a closed set written to trading.decisions, so the
/// names are part of the schema: renaming one invalidates every row already written, and the
/// report view that groups by it.
/// </summary>
public enum DecisionOutcome
{
    Executed,
    NotSized,
    RejectedByRisk,
    NoAction,
    InvalidResponse,
    AgentUnavailable
}

/// <summary>
/// The outcome of one analysis cycle. Expected outcomes are returned rather than thrown,
/// so that a risk rejection can be logged as business information while stack traces stay
/// reserved for actual bugs. The hierarchy is closed - the private constructor means only
/// the cases nested below can derive from it - so a switch over the cases covers them all.
/// </summary>
/// <remarks>
/// <see cref="Outcome"/> and <see cref="OutcomeReason"/> are abstract rather than worked out
/// by a switch somewhere else. A switch expression over a hierarchy needs a discard arm,
/// which silently absorbs a new case; an abstract member does not compile until the new case
/// answers for itself. Stage 4 writes both to the database, and a decision recorded as
/// nothing is a decision that never happened.
/// </remarks>
public abstract record TradeDecisionResult
{
    private TradeDecisionResult() { }

    /// <summary>How the cycle ended, in the vocabulary the decisions table stores.</summary>
    public abstract DecisionOutcome Outcome { get; }

    /// <summary>
    /// The same explanation the log line carries, in the one place persistence can reach it
    /// without knowing which case it is holding. Null only when the outcome says it all.
    /// </summary>
    public abstract string? OutcomeReason { get; }

    /// <summary>A buy was executed against the portfolio.</summary>
    public sealed record Executed(Ticker Ticker, decimal Quantity, Money Price) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.Executed;
        public override string? OutcomeReason => null;
    }

    /// <summary>
    /// Sizing produced no order, and why. Kept apart from <see cref="RejectedByRisk"/>
    /// because they are different facts: a conviction below the floor says something about
    /// the team, while a rejected order says something about the portfolio. Stage 4 wants to
    /// tell them apart, and pooling them would hide which one is happening.
    /// </summary>
    public sealed record NotSized(Ticker Ticker, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.NotSized;
        public override string? OutcomeReason => Reason;
    }

    /// <summary>The order broke a risk rule. An expected outcome, not an error.</summary>
    public sealed record RejectedByRisk(Ticker Ticker, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.RejectedByRisk;
        public override string? OutcomeReason => Reason;
    }

    /// <summary>The agents did not propose a buy. Selling arrives in stage 5.</summary>
    public sealed record NoAction(Ticker Ticker, string Action) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.NoAction;

        /// <summary>The stance that was not a buy. It is the whole reason there was no action.</summary>
        public override string? OutcomeReason => Action;
    }

    /// <summary>The answer did not honour the contract, for example by naming another instrument.</summary>
    public sealed record InvalidResponse(Ticker Requested, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.InvalidResponse;
        public override string? OutcomeReason => Reason;
    }

    /// <summary>The agent service could not be reached, or did not answer in time.</summary>
    public sealed record AgentUnavailable(Ticker Ticker, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.AgentUnavailable;
        public override string? OutcomeReason => Reason;
    }
}
