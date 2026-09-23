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
}
