namespace Engine.Application.Interfaces;

using Engine.Application.Contracts;

public interface IAgentClient
{
    /// <summary>
    /// Asks the agent service about one instrument. The instrument travels in the body as a
    /// typed object, so nothing is interpolated into a path.
    /// </summary>
    Task<TradeSignalDto?> GetSignalAsync(
        TradeSignalRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One instrument's price, so the engine can value a holding it is not analysing. No
    /// model runs behind it.
    /// </summary>
    /// <remarks>
    /// It lives on the same port as the analysis because it is the same service, reached
    /// over the same client with the same key. That also means it inherits the client's
    /// resilience policy, which does not retry a failing response - a rule written for a
    /// call that costs 12-15 s of LLM time, and stricter than a GET needs. The cost of
    /// leaving it stricter is one cycle without a price for one holding; the cost of a
    /// second client is a second place for the key and the timeouts to drift.
    ///
    /// The correlation id is not optional. Every request in this system carries one, and a
    /// quote is part of the cycle that priced a decision - without it, the reason a holding
    /// had no price sits in the agent service's log under an id nothing else mentions.
    /// </remarks>
    Task<QuoteDto?> GetQuoteAsync(
        string symbol, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// An instrument's closes from a date onwards, which is how an outcome is measured and
    /// what stands in for a trading calendar: a day with no bar is a day the market was
    /// shut. An empty series is a valid answer.
    /// </summary>
    Task<HistoryDto?> GetHistoryAsync(
        string symbol, DateOnly from, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the agent service what a sweep measured. The engine keeps the record; this is
    /// the copy that lets the agents' memory say how a past thesis turned out.
    /// </summary>
    /// <remarks>
    /// The only call the engine makes that is not part of answering a question. It may fail
    /// without costing a decision - nothing is waiting on it - which is why a failure is an
    /// exception the caller logs and retries next sweep rather than one that stops a cycle.
    /// </remarks>
    Task PostOutcomesAsync(
        OutcomeReportDto report, string correlationId, CancellationToken cancellationToken = default);
}
