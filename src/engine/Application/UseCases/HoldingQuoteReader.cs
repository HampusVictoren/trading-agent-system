namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// A usable price for each of the portfolio's holdings, from the agent service's quote
/// endpoint. The engine holds no market data of its own; this is the one place that asks for
/// it on the portfolio's behalf.
/// </summary>
/// <remarks>
/// It was a private method on <see cref="ProcessProposalUseCase"/> until the deterministic
/// exits needed the same thing. They are not quite the same question - an analysis wants every
/// holding *except* the one it is analysing, because that price comes from the signal, while an
/// exit wants all of them - which is the whole of the difference, and it is a parameter.
///
/// A quote that cannot be fetched, or that is too old to trust, is simply left out. What that
/// costs differs by caller and both readings are right: sizing a buy then cannot value the
/// portfolio and says so, while an exit skips one holding and still judges the rest. Nothing
/// substitutes what a holding cost for a price it could not get: that would overstate a loser
/// exactly when it mattered, and it would make a stop-loss read the number it is comparing
/// against on both sides.
/// </remarks>
public sealed class HoldingQuoteReader
{
    private readonly IAgentClient _agentClient;
    private readonly RiskPolicy _policy;
    private readonly ILogger<HoldingQuoteReader> _logger;

    public HoldingQuoteReader(
        IAgentClient agentClient, RiskPolicy policy, ILogger<HoldingQuoteReader> logger)
    {
        _agentClient = agentClient;
        _policy = policy;
        _logger = logger;
    }

    /// <param name="except">A holding to skip, for a caller that already has its price.</param>
    public async Task<IReadOnlyList<InstrumentQuote>> ForHoldingsAsync(
        Portfolio portfolio,
        DateTimeOffset now,
        string correlationId,
        Ticker? except = null,
        CancellationToken cancellationToken = default)
    {
        var quotes = new List<InstrumentQuote>();

        foreach (var position in portfolio.Positions.Where(held => held.Ticker != except))
        {
            var quote = await QuoteOrNothingAsync(position.Ticker, correlationId, cancellationToken);

            if (quote is null)
                continue;

            if (!quote.IsUsableAt(now, _policy.MaxQuoteAge))
            {
                _logger.LogWarning(
                    "The quote for {Ticker} is dated {AsOf:O}, which is outside the {Limit} s window.",
                    position.Ticker.Value, quote.AsOf, _policy.MaxQuoteAge.TotalSeconds);
                continue;
            }

            quotes.Add(quote);
        }

        return quotes;
    }

    /// <summary>
    /// A failed quote is not a failed cycle: one holding without a price is an outcome the
    /// callers already have a word for. It is logged, because the outcome alone says a price
    /// was missing and not why.
    /// </summary>
    private async Task<InstrumentQuote?> QuoteOrNothingAsync(
        Ticker ticker, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _agentClient.GetQuoteAsync(ticker.Value, correlationId, cancellationToken);
            return dto is null ? null : QuoteMapper.ToDomain(dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down.
        }
        catch (Exception ex) when (ex is AgentServiceUnavailableException or AgentResponseInvalidException)
        {
            _logger.LogWarning(ex, "No usable quote for {Ticker} this cycle.", ticker.Value);
            return null;
        }
    }
}
