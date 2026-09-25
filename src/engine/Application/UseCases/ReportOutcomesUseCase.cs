namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Microsoft.Extensions.Logging;

/// <summary>What one delivery attempt came to.</summary>
/// <param name="Delivered">Measurements the agent service has now been told about.</param>
/// <param name="Remaining">
/// Measurements still waiting, because the batch was capped. They go on the next sweep.
/// </param>
public sealed record DeliveryResult(int Delivered, int Remaining)
{
    public static readonly DeliveryResult Nothing = new(0, 0);
}

/// <summary>
/// Sends what the engine measured to the agent service, and remembers that it landed.
/// </summary>
/// <remarks>
/// It is separate from <see cref="MeasureOutcomesUseCase"/> because the two fail
/// differently. Measuring is arithmetic over stored rows and either works or does not;
/// delivery crosses a network to a service that may be down, and has to be able to pick up
/// where it left off. Keeping them apart is also what lets a delivery attempt drain a
/// backlog that has nothing to do with today's sweep.
/// </remarks>
public sealed class ReportOutcomesUseCase
{
    /// <summary>
    /// The contract's cap. A request is a unit of work with a timeout rather than a bulk
    /// load, and what does not fit is picked up by the next sweep.
    /// </summary>
    public const int MaxPerRequest = 500;

    private readonly IOutcomeLog _outcomes;
    private readonly IAgentClient _agentClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ReportOutcomesUseCase> _logger;

    public ReportOutcomesUseCase(
        IOutcomeLog outcomes,
        IAgentClient agentClient,
        IUnitOfWork unitOfWork,
        ILogger<ReportOutcomesUseCase> logger)
    {
        _outcomes = outcomes;
        _agentClient = agentClient;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>
    /// Sends everything undelivered, up to the cap, and writes the markers only once the
    /// agent service has accepted them.
    /// </summary>
    /// <remarks>
    /// The order is what makes this safe to repeat. A failed post leaves no markers, so the
    /// next sweep sends the same batch again; a post that succeeded and a commit that then
    /// failed sends it a second time, which the agent service ignores because storing is
    /// idempotent. The failure this order cannot produce is the one that matters: a
    /// measurement marked delivered that never arrived.
    /// </remarks>
    public async Task<DeliveryResult> ReportAsync(
        string correlationId, CancellationToken cancellationToken = default)
    {
        // One more than the cap, so "there is a backlog" is known without a second query.
        var waiting = await _outcomes.AwaitingDeliveryAsync(MaxPerRequest + 1, cancellationToken);
        if (waiting.Count == 0)
        {
            return DeliveryResult.Nothing;
        }

        var batch = waiting.Take(MaxPerRequest).ToList();
        var report = new OutcomeReportDto
        {
            Outcomes = [.. batch.Select(MeasuredOutcomeDto.From)]
        };

        await _agentClient.PostOutcomesAsync(report, correlationId, cancellationToken);

        foreach (var outcome in batch)
        {
            _outcomes.MarkDelivered(outcome.SignalOutcomeId);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var remaining = waiting.Count - batch.Count;
        _logger.LogInformation(
            "Delivery {CorrelationId}: sent {Delivered} outcomes, {Remaining} still waiting.",
            correlationId, batch.Count, remaining);

        return new DeliveryResult(batch.Count, remaining);
    }
}
