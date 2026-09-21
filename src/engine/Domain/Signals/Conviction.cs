namespace Engine.Domain.Signals;

/// <summary>
/// How strongly the agents hold a view, between 0 and 1.
/// </summary>
/// <remarks>
/// It is a value object rather than a double so that sizing code cannot be handed 1.5 or a
/// negative number. It is deliberately *not* a multiplier: an LLM's conviction is not
/// calibrated, so the engine maps it to discrete tiers instead of scaling an amount by it.
/// </remarks>
public readonly record struct Conviction
{
    public double Value { get; }

    public Conviction(double value)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Conviction must be between 0 and 1.");

        Value = value;
    }

    public static bool TryCreate(double value, out Conviction conviction)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            conviction = default;
            return false;
        }

        conviction = new Conviction(value);
        return true;
    }
}
