namespace Engine.Application.Contracts;

using Engine.Application.Interfaces;
using Engine.Domain.Outcomes;
using Engine.Domain.ValueObjects;

/// <summary>
/// The seam for a price series. Everything a measurement divides by passes through here, so
/// nothing past this point has to ask whether a close is a number or whether the days are in
/// the order every lookup assumes.
/// </summary>
public static class HistoryMapper
{
    /// <summary>
    /// Checks the answer is about the instrument that was asked about, then hands the series
    /// to the domain. Without that check, an answer about another symbol would be measured
    /// as if it were this one - the same failure the signal path already refuses.
    /// </summary>
    public static BarSeries ToDomain(HistoryDto dto, Ticker expected)
    {
        if (dto.Instrument is not EquityInstrumentDto equity)
            throw Invalid($"the instrument type '{dto.Instrument.GetType().Name}' is not one the engine trades");

        if (!Ticker.TryCreate(equity.Symbol, out var ticker))
            throw Invalid($"the symbol '{equity.Symbol}' is not a ticker");

        if (ticker != expected)
            throw Invalid($"the history is about {ticker.Value}, not {expected.Value}");

        try
        {
            // PriceBar refuses a close that is not positive and BarSeries refuses days out
            // of order or repeated, which is where an upstream change would otherwise become
            // a silently wrong measurement.
            return new BarSeries(dto.Bars.Select(bar => new PriceBar(bar.On, bar.Close)));
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw Invalid(ex.Message, ex);
        }
    }

    private static AgentResponseInvalidException Invalid(string reason) =>
        new($"The history makes no sense: {reason}.");

    private static AgentResponseInvalidException Invalid(string reason, Exception inner) =>
        new($"The history makes no sense: {reason}.", inner);
}
