namespace Engine.Domain.ValueObjects;

/// <summary>
/// The prices the engine knows right now. It is deliberately partial: a signal brings a
/// price for the instrument it is about, and the engine has no way to fetch one for a
/// holding it is not analysing until stage 4 adds a quote endpoint. Valuation therefore has
/// to cope with a price being absent rather than assume one.
/// </summary>
public sealed class PriceSnapshot
{
    private readonly IReadOnlyDictionary<Ticker, Money> _prices;

    public static PriceSnapshot Empty { get; } = new(new Dictionary<Ticker, Money>());

    private PriceSnapshot(IReadOnlyDictionary<Ticker, Money> prices) => _prices = prices;

    public static PriceSnapshot Of(Ticker ticker, Money price) => Empty.With(ticker, price);

    public PriceSnapshot With(Ticker ticker, Money price) =>
        new(new Dictionary<Ticker, Money>(_prices.ToDictionary()) { [ticker] = price });

    public bool TryGet(Ticker ticker, out Money price) => _prices.TryGetValue(ticker, out price!);
}
