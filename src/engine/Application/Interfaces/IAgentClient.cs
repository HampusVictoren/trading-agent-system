namespace Engine.Application.Interfaces;

using Engine.Application.Dtos;

public interface IAgentClient
{
    Task<InvestmentProposalDto?> AnalyzeTickerAsync(string ticker, CancellationToken cancellationToken = default);
}
