namespace Engine.Hosting.Workers;

using Engine.Application.UseCases;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Sweeps for signals whose horizon has passed and scores them. Writes only
/// <c>signal_outcomes</c> and never touches the portfolio.
/// </summary>
/// <remarks>
/// It runs once at startup and then on its interval. Starting immediately is what makes a
/// restart useful rather than a day lost, and it means an operator can see whether
/// measurement works without waiting until tomorrow.
/// </remarks>
public sealed class MeasurementWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutcomeOptions _options;
    private readonly ILogger<MeasurementWorker> _logger;

    public MeasurementWorker(
        IServiceScopeFactory scopeFactory, IOptions<OutcomeOptions> options, ILogger<MeasurementWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // One scope per sweep, so one change tracker and one transaction - the same
            // shape a trading cycle has.
            using var scope = _scopeFactory.CreateScope();
            var correlationId = Guid.NewGuid().ToString();

            try
            {
                var sweep = scope.ServiceProvider.GetRequiredService<MeasureOutcomesUseCase>();
                var result = await sweep.SweepAsync(correlationId, stoppingToken);

                Report(result, correlationId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep costs nothing but a day: nothing was written, and the same
                // signals are still unmeasured when the next one runs.
                _logger.LogError(ex, "Sweep {CorrelationId} failed.", correlationId);
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Report(SweepResult result, string correlationId)
    {
        if (result is { Written: 0, NotDue: 0, Unreachable: 0 })
        {
            _logger.LogDebug("Sweep {CorrelationId}: nothing to measure.", correlationId);
            return;
        }

        _logger.LogInformation(
            "Sweep {CorrelationId}: measured {Measured}, abandoned {Abandoned}, "
            + "{NotDue} not due yet, {Unreachable} without history.",
            correlationId, result.Measured, result.Abandoned, result.NotDue, result.Unreachable);
    }
}
