namespace Engine.Application.UseCases;

using Engine.Application.Dtos;
using Engine.Application.Interfaces;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Exceptions;
using Engine.Domain.Services;
using Engine.Domain.ValueObjects;

public class ProcessProposalUseCase
{
    private const string BuyAction = "BUY";

    private readonly IAgentClient _agentClient;
    private readonly RiskEngine _riskEngine;

    public ProcessProposalUseCase(IAgentClient agentClient, RiskEngine riskEngine)
    {
        _agentClient = agentClient;
        _riskEngine = riskEngine;
    }

    /// <summary>
    /// Runs one analysis cycle for a ticker and reports what came of it. Every expected
    /// outcome is returned as a <see cref="TradeDecisionResult"/>; exceptions are left for bugs.
    /// </summary>
    public async Task<TradeDecisionResult> ExecuteAsync(Portfolio portfolio, string tickerSymbol, CancellationToken cancellationToken = default)
    {
        var requested = new Ticker(tickerSymbol);

        InvestmentProposalDto? proposal;
        try
        {
            proposal = await _agentClient.AnalyzeTickerAsync(requested.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down, which is not a failing agent service.
        }
        catch (AgentServiceUnavailableException ex)
        {
            // Unreachable, a failing status code, or no answer in time. The client translates
            // the transport's own exceptions, so this layer never sees HttpClient or Polly.
            return new TradeDecisionResult.AgentUnavailable(requested, ex.Message);
        }
        catch (AgentResponseInvalidException ex)
        {
            // The service answered, but not with the contract - an HTML error page, say.
            return new TradeDecisionResult.InvalidResponse(requested, ex.Message);
        }

        if (proposal is null)
            return new TradeDecisionResult.InvalidResponse(requested, "the agent service returned an empty body");

        if (!Ticker.TryCreate(proposal.Ticker, out var answered))
            return new TradeDecisionResult.InvalidResponse(requested, "the answer carries no ticker");

        // The answer must be about what we asked about. Without this check, a model that
        // replies "TSLA" to a question about AAPL makes the engine buy TSLA.
        if (answered != requested)
            return new TradeDecisionResult.InvalidResponse(requested, $"the answer is about {answered.Value}");

        if (proposal.Action != BuyAction)
            return new TradeDecisionResult.NoAction(requested, proposal.Action);

        var intendedSpend = new Money(proposal.AmountUsd, "USD");

        try
        {
            _riskEngine.ValidateTrade(portfolio, requested, intendedSpend, portfolio.CashBalance);
        }
        catch (RiskViolationException ex)
        {
            // Stage 2 replaces this catch: RiskEngine.Evaluate will return a RiskDecision
            // instead of throwing, since a rejection is a business outcome.
            return new TradeDecisionResult.RejectedByRisk(requested, ex.Message);
        }

        portfolio.ExecuteBuy(requested, quantity: 1m, intendedSpend);
        return new TradeDecisionResult.Executed(requested, 1m, intendedSpend);
    }
}
