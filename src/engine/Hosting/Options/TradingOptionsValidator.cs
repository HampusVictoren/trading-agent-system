namespace Engine.Hosting.Options;

using Engine.Domain.Trading;
using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Options;

/// <summary>
/// Data annotations cannot express "every entry must be a ticker", so this does. A typo in
/// configuration should stop the service at startup rather than reach the agent service.
/// </summary>
/// <remarks>
/// It also refuses <see cref="TradingMode.Live"/>, which binds perfectly well and has nothing
/// behind it.
///
/// The universe also has one rule of its own: no symbol twice. A duplicate would come back from
/// the screen as one instrument ranked twice, which the contract seam refuses - so every cycle
/// would fail on a configuration mistake that is one line to find here and confusing to find
/// there.
/// </remarks>
public sealed class TradingOptionsValidator : IValidateOptions<TradingOptions>
{
    public ValidateOptionsResult Validate(string? name, TradingOptions options)
    {
        var failures = new List<string>();

        failures.AddRange(NotTickers(nameof(options.Universe), options.Universe));

        var duplicates = options.Universe
            .Where(symbol => Ticker.TryCreate(symbol, out _))
            .GroupBy(symbol => new Ticker(symbol))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Value)
            .ToArray();

        if (duplicates.Length > 0)
        {
            failures.Add(
                $"{TradingOptions.SectionName}:{nameof(options.Universe)} names "
                + $"{string.Join(", ", duplicates)} more than once.");
        }

        // Refused here rather than left out of the enum, so that the answer to somebody typing it
        // is a sentence rather than a binding error about an unknown value.
        if (options.Mode == TradingMode.Live)
        {
            failures.Add(
                $"{TradingOptions.SectionName}:{nameof(options.Mode)} is Live, and this system has no "
                + "broker to send an order to. Use Paper for the simulated portfolio or Shadow to "
                + "place nothing.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> NotTickers(string section, IReadOnlyCollection<string> symbols)
    {
        var invalid = symbols.Where(symbol => !Ticker.TryCreate(symbol, out _)).ToArray();

        if (invalid.Length > 0)
        {
            yield return
                $"{TradingOptions.SectionName}:{section} contains {invalid.Length} "
                + $"invalid entr{(invalid.Length == 1 ? "y" : "ies")}.";
        }
    }
}
