namespace Engine.Domain.ValueObjects;

public record Ticker
{
    public string Value { get; }

    public Ticker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ticker must not be empty.", nameof(value));

        Value = value.Trim().ToUpperInvariant();
    }
}
