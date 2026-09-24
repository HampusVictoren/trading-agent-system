namespace Engine.Hosting.Workers;

using Engine.Application.Persistence;
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

    /// <remarks>
    /// The worker holds no portfolio. Each cycle reads it, changes it and commits it inside
    /// one scope, which is what makes a restart continue rather than begin - and it is also
    /// why there is no shared mutable state left to get wrong when a second ticker, or a
    /// second worker, arrives.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var tickerSymbol in _options.Tickers)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                // One scope per cycle, so one change tracker and one transaction per decision.
                using var scope = _scopeFactory.CreateScope();

                // One id per cycle, generated here and logged before the call, so a line in
                // this log can be found in the agent service's - it echoes the id and puts
                // it in every line it writes while handling the request.
                var correlationId = Guid.NewGuid().ToString();

                try
                {
                    await RunCycleAsync(scope.ServiceProvider, tickerSymbol, correlationId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ConcurrentChangeException ex)
                {
                    // An expected outcome rather than a bug, so no stack trace. Nothing was
                    // traded either: the buy is in the same transaction as the decision, so a
                    // commit that fails costs an LLM call and nothing else. The next cycle
                    // reads the portfolio again.
                    _logger.LogError(
                        "Cycle {CorrelationId} for {Ticker} was not stored: {Reason}",
                        correlationId, tickerSymbol, ex.Message);
                }
                catch (Exception ex)
                {
                    // Only a bug, or an outage, reaches this point: every expected outcome of
                    // the analysis itself is a result rather than an exception.
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
    /// One cycle: read the portfolio, decide, commit. The outcome is logged only after the
    /// commit, so a line in this log means a row in the database rather than an intention.
    /// </summary>
    private async Task RunCycleAsync(
        IServiceProvider services, string tickerSymbol, string correlationId, CancellationToken cancellationToken)
    {
        var portfolios = services.GetRequiredService<IPortfolioRepository>();
        var useCase = services.GetRequiredService<ProcessProposalUseCase>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();

        var portfolio = await portfolios.FindAsync(cancellationToken) ?? OpenTheAccount(portfolios);

        _logger.LogInformation(
            "Requesting analysis for {Ticker} as {CorrelationId}...", tickerSymbol, correlationId);

        var result = await useCase.ExecuteAsync(portfolio, tickerSymbol, correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        LogOutcome(result, portfolio);
    }

    /// <summary>Happens once in the account's life: the first cycle against an empty database.</summary>
    private Portfolio OpenTheAccount(IPortfolioRepository portfolios)
    {
        var portfolio = new Portfolio(new Money(_options.OpeningBalanceUsd, Money.DefaultCurrency));
        portfolios.Add(portfolio);

        _logger.LogInformation(
            "No portfolio was stored, so one was opened with ${Balance} {Currency}.",
            _options.OpeningBalanceUsd, Money.DefaultCurrency);

        return portfolio;
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
