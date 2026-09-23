namespace Engine.Hosting.Workers;

using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

public class TradingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingOptions _options;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(IServiceScopeFactory scopeFactory, IOptions<TradingOptions> options, ILogger<TradingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var portfolio = new Portfolio(new Money(10000m, "USD"));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var tickerSymbol in _options.Tickers)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<ProcessProposalUseCase>();

                // One id per cycle, generated here and logged before the call, so a line in
                // this log can be found in the agent service's - it echoes the id and puts
                // it in every line it writes while handling the request.
                var correlationId = Guid.NewGuid().ToString();

                try
                {
                    _logger.LogInformation(
                        "Requesting analysis for {Ticker} as {CorrelationId}...", tickerSymbol, correlationId);
                    var result = await useCase.ExecuteAsync(portfolio, tickerSymbol, correlationId, stoppingToken);
                    LogOutcome(result, portfolio);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Only a bug reaches this point: every expected outcome is a result.
                    _logger.LogError(ex, "Unexpected failure in the trading cycle for {Ticker}.", tickerSymbol);
                }
            }

            try
            {
                await Task.Delay(_options.CycleInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
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

            case TradeDecisionResult.NotSized notSized:
                _logger.LogInformation("No order for {Ticker}: {Reason}", notSized.Ticker.Value, notSized.Reason);
                break;

            case TradeDecisionResult.RejectedByRisk rejected:
                _logger.LogInformation("Risk rules rejected {Ticker}: {Reason}", rejected.Ticker.Value, rejected.Reason);
                break;

            case TradeDecisionResult.NoAction noAction:
                _logger.LogInformation(
                    "No action for {Ticker}: the agents answered {Stance}.", noAction.Ticker.Value, noAction.Action);
                break;

            case TradeDecisionResult.InvalidResponse invalid:
                _logger.LogWarning("Discarded the answer for {Ticker}: {Reason}", invalid.Requested.Value, invalid.Reason);
                break;

            case TradeDecisionResult.AgentUnavailable unavailable:
                _logger.LogWarning(
                    "Agent service unavailable for {Ticker}: {Reason}", unavailable.Ticker.Value, unavailable.Reason);
                break;

            // Unreachable today: TradeDecisionResult has a private constructor, so a case can
            // only be added in that file. But the compiler cannot prove exhaustiveness for a
            // hierarchy - a switch expression would demand a discard arm too - so the choice
            // is between saying nothing and saying this. An outcome that nobody logs is worse
            // than a noisy line.
            default:
                _logger.LogWarning(
                    "Unhandled trade decision {Outcome}. The worker is behind the result type.",
                    result.GetType().Name);
                break;
        }
    }
}
