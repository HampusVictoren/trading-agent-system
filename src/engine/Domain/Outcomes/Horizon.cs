namespace Engine.Domain.Outcomes;

/// <summary>
/// How far ahead an outcome is measured, in the unit the horizon was expressed in. The two
/// units are kept apart rather than converted, because they mean different things and both
/// are stored.
/// </summary>
/// <remarks>
/// The fixed horizons - 1, 5 and 20 - are trading days, so that two signals made a week
/// apart are compared over the same amount of market. The model's own <c>horizon_days</c> is
/// calendar days, because that is what it was asked for: the prompt says "over how long a
/// time you mean the thesis to play out", and a model that writes 30 means a month rather
/// than six weeks. Reading it as trading days would silently reinterpret the one answer that
/// exists to show whether the model can judge time.
/// </remarks>
public abstract record Horizon
{
    private Horizon() { }

    /// <summary>How many, in this horizon's own unit.</summary>
    public abstract int Days { get; }

    /// <summary>A count of bars: days the market was actually open.</summary>
    public sealed record TradingDays : Horizon
    {
        public override int Days { get; }

        public TradingDays(int days) => Days = AtLeastOne(days);
    }

    /// <summary>A count of days on the wall calendar, weekends and holidays included.</summary>
    public sealed record CalendarDays : Horizon
    {
        public override int Days { get; }

        public CalendarDays(int days) => Days = AtLeastOne(days);
    }

    private static int AtLeastOne(int days) => days >= 1
        ? days
        : throw new ArgumentOutOfRangeException(nameof(days), days, "A horizon must be at least one day.");
}
