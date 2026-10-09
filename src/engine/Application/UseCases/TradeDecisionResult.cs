namespace Engine.Application.UseCases;

using Engine.Domain.Aggregates.Portfolio;
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
    AgentUnavailable,
    Shadowed,
    Halted
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

    /// <summary>
    /// A trade was executed against the portfolio. The side is carried so a log line can say
    /// which way it went.
    /// </summary>
    /// <remarks>
    /// It is not a new column in <c>trading.decisions</c>. That row already stores the stance
    /// the agents answered, which is what decided the direction, and its <c>order_id</c> points
    /// at the ledger line that records the side as a fact about what was done. A third copy
    /// would be a third thing to keep in step.
    /// </remarks>
    public sealed record Executed(Ticker Ticker, OrderSide Side, decimal Quantity, Money Price)
        : TradeDecisionResult
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
    /// <remarks>
    /// The side is carried for <c>risk_rejections_total</c>, which counts the two apart: a sale
    /// refused by the holding period and a buy refused by the daily limit are different stories.
    /// It is not stored, for the same reason <see cref="Executed"/>'s is not - the row's stance
    /// already says which way the agents wanted to go.
    /// </remarks>
    public sealed record RejectedByRisk(Ticker Ticker, string Reason, OrderSide Side) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.RejectedByRisk;
        public override string? OutcomeReason => Reason;
    }

    /// <summary>The agents proposed no trade, which now means exactly one thing: they held.</summary>
    public sealed record NoAction(Ticker Ticker, string Action) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.NoAction;

        /// <summary>The stance that produced no order. It is the whole reason there was none.</summary>
        public override string? OutcomeReason => Action;
    }

    /// <summary>
    /// The risk gate approved an order and the engine is in Shadow mode, so nothing was placed.
    /// The reason names the order that would have been, because a shadow decision that cannot
    /// say how big it was is not worth comparing against anything.
    /// </summary>
    /// <remarks>
    /// Its own outcome rather than <see cref="Executed"/> with no order behind it. Every report
    /// that asks "what was bought" reads <c>outcome = 'Executed'</c>, and a row that claimed an
    /// execution which never happened would be counted as one by all of them.
    /// </remarks>
    public sealed record Shadowed(Ticker Ticker, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.Shadowed;
        public override string? OutcomeReason => Reason;
    }

    /// <summary>
    /// The risk gate approved an order and the kill switch stopped it, in the moment between the
    /// two. The agents' answer was paid for, so it is still recorded and still measured - it is a
    /// signal like any other - but no order exists.
    /// </summary>
    public sealed record Halted(Ticker Ticker, string Reason) : TradeDecisionResult
    {
        public override DecisionOutcome Outcome => DecisionOutcome.Halted;
        public override string? OutcomeReason => Reason;
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
