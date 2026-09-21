namespace Engine.Domain.Exceptions;

/// <summary>
/// Two amounts in different currencies were combined. The engine is USD-only today, so this
/// means a bug rather than a business outcome - but it is a domain rule, not an
/// InvalidOperationException, so a reader can tell the two apart in a log.
/// </summary>
public class CurrencyMismatchException : Exception
{
    public CurrencyMismatchException(string message) : base(message) { }
}
