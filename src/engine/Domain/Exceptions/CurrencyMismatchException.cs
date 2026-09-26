namespace Engine.Domain.Exceptions;

/// <summary>
/// Two amounts in different currencies were combined. The engine keeps one account in one
/// currency and trades a universe listed in that currency, so today this means a bug rather
/// than a business outcome - but it is a domain rule, not an InvalidOperationException, so a
/// reader can tell the two apart in a log.
///
/// It becomes reachable the moment an instrument outside the account's currency is admitted,
/// and turning it into a stated outcome rather than a throw is finding D's remaining half.
/// A portfolio holding a dollar position after the account moved to kronor is the one case
/// that exists in practice today, which is why resetting the holdings is part of that move.
/// </summary>
public class CurrencyMismatchException : Exception
{
    public CurrencyMismatchException(string message) : base(message) { }
}
