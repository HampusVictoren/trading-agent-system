namespace Engine.Application.UseCases;

using Engine.Application.Persistence;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// Whether to spend three LLM calls on an instrument this cycle. The rule is
/// <see cref="FactSheetChange"/>'s; this is the reading it needs.
/// </summary>
/// <remarks>
/// It sits above <see cref="ProcessProposalUseCase"/> rather than inside it, and that is the
/// important part. The use case records every cycle it runs, by design, so a skip decided in
/// there would write a row - and <c>decisions</c> would fill with non-analyses that the outcome
/// sweep would then try to measure. What is not analysed leaves no trace in that table, which is
/// the only way the measured population stays the analyses.
/// </remarks>
public sealed class AnalysisDueCheck
{
    private readonly IDecisionLog _decisions;
    private readonly QuoteReader _quotes;

    public AnalysisDueCheck(IDecisionLog decisions, QuoteReader quotes)
    {
        _decisions = decisions;
        _quotes = quotes;
    }

    /// <remarks>
    /// The quote is fetched only when there is something to compare it against. An instrument
    /// that has never been analysed is due whatever it costs today, so asking for its price first
    /// would be a request whose answer cannot change the outcome.
    /// </remarks>
    public async Task<AnalysisVerdict> ForAsync(
        Ticker symbol,
        DateOnly today,
        DateTimeOffset now,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var last = await _decisions.LastAnalysisOfAsync(symbol, cancellationToken);

        if (last is null)
            return AnalysisVerdict.Due;

        if (last.On >= today)
            return AnalysisVerdict.AlreadyAnalysedToday;

        var quote = await _quotes.UsablePriceAsync(symbol, now, correlationId, cancellationToken);

        return FactSheetChange.Verdict(last, quote?.Price.Amount, today);
    }
}
