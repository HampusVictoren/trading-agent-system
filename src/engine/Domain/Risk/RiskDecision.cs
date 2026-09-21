namespace Engine.Domain.Risk;

/// <summary>
/// Whether an order may be placed. A closed hierarchy, so a rejection carries its reason
/// instead of arriving as an exception: refusing to trade is the risk rules working, not a
/// failure, and it should not reach a log with a stack trace behind it.
/// </summary>
public abstract record RiskDecision
{
    private RiskDecision() { }

    public sealed record Approved : RiskDecision;

    public sealed record Rejected(string Reason) : RiskDecision;
}
