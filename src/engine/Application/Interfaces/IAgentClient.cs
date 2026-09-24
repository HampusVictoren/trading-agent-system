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
}
