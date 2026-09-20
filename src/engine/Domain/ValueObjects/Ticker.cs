namespace Engine.Domain.ValueObjects;

using System.Diagnostics.CodeAnalysis;

public record Ticker
{
    public string Value { get; }

    public Ticker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ticker must not be empty.", nameof(value));

        Value = value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Builds a ticker from untrusted input, such as an agent's answer, without throwing.
    /// A malformed answer is an expected outcome and must not look like a bug.
    /// </summary>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out Ticker? ticker)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ticker = null;
            return false;
        }

        ticker = new Ticker(value);
        return true;
    }
}
