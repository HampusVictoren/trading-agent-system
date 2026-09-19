namespace Engine.Application.UseCases;

using Engine.Application.Interfaces;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Services;
using Engine.Domain.ValueObjects;

public class ProcessProposalUseCase
{
    private readonly IAgentClient _agentClient;
    private readonly RiskEngine _riskEngine;

    public ProcessProposalUseCase(IAgentClient agentClient, RiskEngine riskEngine)
    {
        _agentClient = agentClient;
        _riskEngine = riskEngine;
    }

    public async Task ExecuteAsync(Portfolio portfolio, string tickerSymbol, CancellationToken cancellationToken = default)
    {
        var proposal = await _agentClient.AnalyzeTickerAsync(tickerSymbol, cancellationToken);

        if (proposal == null || proposal.Action != "BUY")
            return;

        var ticker = new Ticker(proposal.Ticker);
        var intendedSpend = new Money(proposal.AmountUsd, "USD");

        _riskEngine.ValidateTrade(portfolio, ticker, intendedSpend, portfolio.CashBalance);
        portfolio.ExecuteBuy(ticker, quantity: 1m, intendedSpend);
    }
}
