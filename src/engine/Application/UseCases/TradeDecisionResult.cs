namespace Engine.Application.UseCases;

using Engine.Domain.ValueObjects;

/// <summary>
/// The outcome of one analysis cycle. Expected outcomes are returned rather than thrown,
/// so that a risk rejection can be logged as business information while stack traces stay
/// reserved for actual bugs. The hierarchy is closed - the private constructor means only
/// the cases nested below can derive from it - so a switch over the cases covers them all.
/// </summary>
public abstract record TradeDecisionResult
{
    private TradeDecisionResult() { }

    /// <summary>A buy was executed against the portfolio.</summary>
    public sealed record Executed(Ticker Ticker, decimal Quantity, Money Price) : TradeDecisionResult;

    /// <summary>The proposal broke a risk rule. An expected outcome, not an error.</summary>
    public sealed record RejectedByRisk(Ticker Ticker, string Reason) : TradeDecisionResult;

    /// <summary>The agents did not propose a buy. Selling arrives in stage 5.</summary>
    public sealed record NoAction(Ticker Ticker, string Action) : TradeDecisionResult;

    /// <summary>The answer did not honour the contract, for example by naming another instrument.</summary>
    public sealed record InvalidResponse(Ticker Requested, string Reason) : TradeDecisionResult;

    /// <summary>The agent service could not be reached, or did not answer in time.</summary>
    public sealed record AgentUnavailable(Ticker Ticker, string Reason) : TradeDecisionResult;
}
