namespace Engine.Hosting.Workers;

using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;

public class TradingWorker : BackgroundService
{
    private const string TickerSymbol = "AAPL";
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(IServiceProvider serviceProvider, ILogger<TradingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var portfolio = new Portfolio(new Money(10000m, "USD"));

        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var useCase = scope.ServiceProvider.GetRequiredService<ProcessProposalUseCase>();

                try
                {
                    _logger.LogInformation("Requesting analysis for {Ticker}...", TickerSymbol);
                    var result = await useCase.ExecuteAsync(portfolio, TickerSymbol, stoppingToken);
                    LogOutcome(result, portfolio);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Only a bug reaches this point: every expected outcome is a result.
                    _logger.LogError(ex, "Unexpected failure in the trading cycle.");
                }
            }

            try
            {
                await Task.Delay(CycleInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One log level per outcome. A risk rejection is the risk rules doing their job, so it
    /// is information rather than an error with a stack trace behind it.
    /// </summary>
    private void LogOutcome(TradeDecisionResult result, Portfolio portfolio)
    {
        switch (result)
        {
            case TradeDecisionResult.Executed executed:
                _logger.LogInformation(
                    "Bought {Quantity} {Ticker} for ${Amount}. Cash left: ${Cash}.",
                    executed.Quantity, executed.Ticker.Value, executed.Price.Amount, portfolio.CashBalance.Amount);
                break;

            case TradeDecisionResult.RejectedByRisk rejected:
                _logger.LogInformation("Risk rules rejected {Ticker}: {Reason}", rejected.Ticker.Value, rejected.Reason);
                break;

            case TradeDecisionResult.NoAction noAction:
                _logger.LogInformation(
                    "No action for {Ticker}: the agents answered {Action}.", noAction.Ticker.Value, noAction.Action);
                break;

            case TradeDecisionResult.InvalidResponse invalid:
                _logger.LogWarning("Discarded the answer for {Ticker}: {Reason}", invalid.Requested.Value, invalid.Reason);
                break;

            case TradeDecisionResult.AgentUnavailable unavailable:
                _logger.LogWarning(
                    "Agent service unavailable for {Ticker}: {Reason}", unavailable.Ticker.Value, unavailable.Reason);
                break;
        }
    }
}
