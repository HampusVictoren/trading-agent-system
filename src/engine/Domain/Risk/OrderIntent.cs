namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
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

    /// <summary>
    /// A sale of part or all of a holding.
    /// </summary>
    /// <remarks>
    /// It carries two things a buy does not, and both are there because a sale has more than
    /// one author. <paramref name="PriceAsOf"/> is when the price was true: a buy's price always
    /// comes from the signal and the risk gate can read the signal's own timestamp, whereas a
    /// sale can be sized from a plain quote by a rule that never asked an agent anything.
    /// <paramref name="Trigger"/> is which author it was, which decides whether the minimum
    /// holding period applies and is what stage 8 compares the two kinds of exit by.
    /// </remarks>
    public sealed record Sell(
        Instrument Instrument,
        decimal Quantity,
        Money Price,
        DateTimeOffset PriceAsOf,
        OrderTrigger Trigger)
        : OrderIntent(Instrument);

    /// <summary>No order, and why. The reason goes in the log and, from stage 4, the database.</summary>
    public sealed record None(Instrument Instrument, string Reason)
        : OrderIntent(Instrument);
}
