namespace Engine.Domain.ValueObjects;

using Engine.Domain.Exceptions;

public record Money
{
    private readonly string _currency = DefaultCurrency;

    public const string DefaultCurrency = "USD";

    public decimal Amount { get; init; }

    /// <summary>
    /// A three-letter code, upper case. The rule lives in the property rather than the
    /// constructor because <c>with</c> bypasses the constructor, and "usd" and "USD" used to
    /// be different currencies - so the same money could refuse to add to itself.
    /// </summary>
    public string Currency
    {
        get => _currency;
        init => _currency = Normalise(value);
    }

    public Money(decimal amount, string currency = DefaultCurrency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(string currency = DefaultCurrency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount + other.Amount };
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount - other.Amount };
    }

    /// <summary>Scales an amount, for instance a risk limit expressed as a share of it.</summary>
    public Money Multiply(decimal factor) => this with { Amount = Amount * factor };

    public Money Divide(decimal divisor)
    {
        if (divisor == 0m)
            throw new DivideByZeroException("Cannot divide money by zero.");

        return this with { Amount = Amount / divisor };
    }

    /// <summary>
    /// The smaller of two amounts. Sizing takes the smaller of the position headroom and the
    /// spendable cash, so that whichever limit binds first is the one that applies.
    /// </summary>
    public static Money Min(Money first, Money second)
    {
        first.EnsureSameCurrency(second);
        return first.Amount <= second.Amount ? first : second;
    }

    private static string Normalise(string currency)
    {
        var trimmed = currency?.Trim().ToUpperInvariant() ?? string.Empty;

        if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"'{currency}' is not a three-letter currency code.", nameof(currency));

        return trimmed;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new CurrencyMismatchException($"Cannot mix currencies {Currency} and {other.Currency}.");
    }
}
