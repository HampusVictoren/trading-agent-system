namespace Engine.Domain.ValueObjects;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

public partial record Ticker
{
    /// <summary>
    /// The same rule as the contract's <c>symbol</c>: an uppercase letter, then up to nine
    /// more letters, digits, dots or hyphens. Checked after normalising, so "aapl" passes
    /// and "../internal/shutdown" does not.
    /// </summary>
    /// <remarks>
    /// Finding B was two-sided. The interpolation half closed when the contract moved the
    /// symbol into a request body, but the format rule is the half that still matters:
    /// stage 5's screening produces symbols from market data rather than from configuration,
    /// and a value object that accepts anything non-blank is no rule at all.
    /// </remarks>
    [GeneratedRegex(@"^[A-Z][A-Z0-9.\-]{0,15}$")]
    private static partial Regex Format();

    public string Value { get; }

    public Ticker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ticker must not be empty.", nameof(value));

        var normalised = value.Trim().ToUpperInvariant();

        if (!Format().IsMatch(normalised))
            throw new ArgumentException($"'{value}' is not a ticker symbol.", nameof(value));

        Value = normalised;
    }

    /// <summary>
    /// Builds a ticker from untrusted input, such as an agent's answer, without throwing.
    /// A malformed answer is an expected outcome and must not look like a bug.
    /// </summary>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out Ticker? ticker)
    {
        if (string.IsNullOrWhiteSpace(value) || !Format().IsMatch(value.Trim().ToUpperInvariant()))
        {
            ticker = null;
            return false;
        }

        ticker = new Ticker(value);
        return true;
    }
}
