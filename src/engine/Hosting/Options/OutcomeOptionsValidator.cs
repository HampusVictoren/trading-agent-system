namespace Engine.Hosting.Options;

using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Options;

/// <summary>
/// Data annotations cannot say "every horizon is at least a day" or "the benchmark is a
/// symbol", so this does. Both would otherwise fail much later: the first when a measurement
/// was attempted, the second when the agent service answered 422 for a quote nobody could
/// explain.
/// </summary>
public sealed class OutcomeOptionsValidator : IValidateOptions<OutcomeOptions>
{
    public ValidateOptionsResult Validate(string? name, OutcomeOptions options)
    {
        var problems = new List<string>();

        if (!Ticker.TryCreate(options.BenchmarkSymbol, out _))
            problems.Add($"{OutcomeOptions.SectionName}:BenchmarkSymbol is not a ticker symbol.");

        var tooShort = options.FixedHorizonTradingDays.Where(days => days < 1).ToArray();
        if (tooShort.Length > 0)
        {
            problems.Add(
                $"{OutcomeOptions.SectionName}:FixedHorizonTradingDays contains {tooShort.Length} horizon(s) below one day.");
        }

        if (options.FixedHorizonTradingDays.Distinct().Count() != options.FixedHorizonTradingDays.Length)
        {
            // A repeated horizon would mean two rows for one measurement, which the unique
            // index on the outcomes table will refuse anyway - better here than at midnight.
            problems.Add($"{OutcomeOptions.SectionName}:FixedHorizonTradingDays repeats a horizon.");
        }

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems);
    }
}
