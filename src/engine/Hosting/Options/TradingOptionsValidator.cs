namespace Engine.Hosting.Options;

using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Options;

/// <summary>
/// Data annotations cannot express "every entry must be a ticker", so this does. A typo in
/// configuration should stop the service at startup rather than reach the agent service.
/// </summary>
public sealed class TradingOptionsValidator : IValidateOptions<TradingOptions>
{
    public ValidateOptionsResult Validate(string? name, TradingOptions options)
    {
        var invalid = options.Tickers.Where(ticker => !Ticker.TryCreate(ticker, out _)).ToArray();

        return invalid.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{TradingOptions.SectionName}:Tickers contains {invalid.Length} invalid entr{(invalid.Length == 1 ? "y" : "ies")}.");
    }
}
