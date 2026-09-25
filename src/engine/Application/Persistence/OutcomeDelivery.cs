namespace Engine.Application.Persistence;

/// <summary>
/// A measurement that has reached the agent service. One row per delivered outcome, and no
/// row at all until it lands.
/// </summary>
/// <remarks>
/// It is a table of its own rather than a column on <see cref="SignalOutcomeRecord"/>,
/// because that table is append-only and a delivery marker would have to be an UPDATE. The
/// separation is also the truer model: whether a measurement has been copied somewhere is a
/// fact about a side effect, not about the measurement.
/// <para>
/// Without it, a sweep that could not reach the agent service would lose those outcomes for
/// good - the next sweep measures only what is still unmeasured, so a thirty-second outage
/// would silently cost a day of evidence. That is the same failure the stage refuses
/// everywhere else, and this is what makes delivery retry itself.
/// </para>
/// </remarks>
public sealed class OutcomeDelivery
{
    public required long SignalOutcomeId { get; init; }

    /// <summary>Set by the database, so every row is ordered by the same clock.</summary>
    public DateTimeOffset DeliveredAt { get; private set; }
}
