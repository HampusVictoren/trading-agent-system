namespace TradingEngine.Domain.ValueObjects;

public record Ticker
{
    public string Value {; }

    public Ticker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ticker kan inte vara tom.", nameof(value));

        Value = value.Trim().ToUpperInvariant();
    }
}
