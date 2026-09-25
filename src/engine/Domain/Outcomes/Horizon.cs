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
public enum HorizonUnit
{
    TradingDays,
    CalendarDays
}

public abstract record Horizon
{
    private Horizon() { }

    /// <summary>How many, in this horizon's own unit.</summary>
    public abstract int Days { get; }

    /// <summary>Which unit that is. Stored beside the count, because five trading days and
    /// five calendar days are different measurements and a single number cannot say which.</summary>
    public abstract HorizonUnit Unit { get; }

    /// <summary>A count of bars: days the market was actually open.</summary>
    public sealed record TradingDays : Horizon
    {
        public override int Days { get; }

        public override HorizonUnit Unit => HorizonUnit.TradingDays;

        public TradingDays(int days) => Days = AtLeastOne(days);
    }

    /// <summary>A count of days on the wall calendar, weekends and holidays included.</summary>
    public sealed record CalendarDays : Horizon
    {
        public override int Days { get; }

        public override HorizonUnit Unit => HorizonUnit.CalendarDays;

        public CalendarDays(int days) => Days = AtLeastOne(days);
    }

    /// <summary>Rebuilds a horizon from the two columns it is stored as.</summary>
    public static Horizon Of(HorizonUnit unit, int days) => unit switch
    {
        HorizonUnit.TradingDays => new TradingDays(days),
        HorizonUnit.CalendarDays => new CalendarDays(days),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown horizon unit.")
    };

    private static int AtLeastOne(int days) => days >= 1
        ? days
        : throw new ArgumentOutOfRangeException(nameof(days), days, "A horizon must be at least one day.");
}
