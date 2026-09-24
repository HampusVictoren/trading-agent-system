namespace Engine.Domain.Outcomes;

/// <summary>
/// One day's close. Only the close, because that is all an outcome is measured on - and a
/// day that has a bar is a day the market was open, which is the whole of the trading
/// calendar this system needs.
/// </summary>
/// <remarks>
/// The rule lives in the <c>init</c> accessor rather than in the constructor, because a
/// <c>with</c> expression bypasses the constructor - the same lesson <see cref="ValueObjects.Money"/>
/// learned about currency codes.
/// </remarks>
public sealed record PriceBar
{
    private readonly decimal _close;

    public DateOnly On { get; init; }

    public decimal Close
    {
        get => _close;
        init => _close = value > 0m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A close must be positive.");
    }

    public PriceBar(DateOnly on, decimal close)
    {
        On = on;
        Close = close;
    }
}
