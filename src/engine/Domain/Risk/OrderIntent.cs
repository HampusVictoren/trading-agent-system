namespace Engine.Domain.Risk;

using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// What sizing decided. A closed hierarchy, the same private-constructor trick as
/// TradeDecisionResult: not buying is an outcome with a reason, not a null or a zero.
/// </summary>
public abstract record OrderIntent
{
    private OrderIntent(Instrument instrument) => Instrument = instrument;

    public Instrument Instrument { get; init; }

    public sealed record Buy(Instrument Instrument, decimal Quantity, Money Price)
        : OrderIntent(Instrument);

    /// <summary>No order, and why. The reason goes in the log and, from stage 4, the database.</summary>
    public sealed record None(Instrument Instrument, string Reason)
        : OrderIntent(Instrument);
}
