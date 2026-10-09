namespace Engine.Hosting.Workers;

using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.Trading;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

public class TradingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<TradingOptions> options,
        TimeProvider clock,
        ILogger<TradingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _clock = clock;
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
        // Once, first, and in words. Which mode a process ran in is the first thing to establish
        // when reading what it did, and a log that only said it per decision would not say it at
        // all on a day when nothing was due.
        _logger.LogInformation(
            "Trading mode is {Mode}: {Meaning}",
            _options.Mode,
            _options.Mode == TradingMode.Paper
                ? "approved orders are executed against the simulated portfolio."
                : "every decision is recorded and no order is placed.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Before the analyses, not after. A cycle's buying should see the cash and the
            // position headroom the exits have just released, and a position the rules say to
            // close should not survive because an analysis of it happened to come first.
            await RunExitsAsync(stoppingToken);

            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var selection = await SelectAsync(today, stoppingToken);
            var verdicts = new Dictionary<AnalysisVerdict, int>();

            foreach (var selected in selection)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                // One scope per analysis, so one change tracker and one transaction per decision.
                using var scope = _scopeFactory.CreateScope();

                // One id per analysis, generated here and logged before the call, so a line in
                // this log can be found in the agent service's - it echoes the id and puts
                // it in every line it writes while handling the request.
                var correlationId = Guid.NewGuid().ToString();

                try
                {
                    var verdict = await RunCycleAsync(
                        scope.ServiceProvider, selected, today, correlationId, stoppingToken);

                    verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
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
                        correlationId, selected.Ticker.Value, ex.Message);
                }
                catch (Exception ex)
                {
                    // Only a bug, or an outage, reaches this point: every expected outcome of
                    // the analysis itself is a result rather than an exception.
                    _logger.LogError(
                        ex, "Unexpected failure in the trading cycle for {Ticker}.", selected.Ticker.Value);
                }
            }

            LogWhatTheCycleDid(selection.Count, verdicts);

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
    /// One pass of the deterministic exits, in a scope and a transaction of its own.
    /// </summary>
    /// <remarks>
    /// Separate from the analyses rather than folded into the first one, because it is one pass
    /// over the whole portfolio rather than something about a ticker - and because a failed
    /// commit here should cost the exits and not an LLM call that had already been paid for.
    /// It has its own correlation id, which is what ties a sale in the ledger to the lines this
    /// log wrote about it; there is no analysis on the other side to share one with.
    ///
    /// An empty database is left alone. Opening the account here would mean the exits created
    /// the portfolio the analyses then traded, and an account that exists because a sweep for
    /// sales ran is an odd thing to have to explain.
    /// </remarks>
    private async Task RunExitsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var correlationId = Guid.NewGuid().ToString();

        try
        {
            var portfolios = services.GetRequiredService<IPortfolioRepository>();
            var exits = services.GetRequiredService<ApplyExitsUseCase>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            var portfolio = await portfolios.FindAsync(cancellationToken);

            if (portfolio is null || portfolio.Positions.Count == 0)
                return;

            var placed = await exits.ExecuteAsync(portfolio, correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            // After the commit, like every other outcome in this worker: a line here means a
            // row. The use case logs each sale as it happens; this is the count that survived.
            if (placed.Count > 0)
            {
                _logger.LogInformation(
                    "The exits sold {Count} holding(s) as {CorrelationId}. Cash: {Cash} {Currency}.",
                    placed.Count, correlationId, portfolio.CashBalance.Amount, portfolio.CashBalance.Currency);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down, which is not a failure.
        }
        catch (ConcurrentChangeException ex)
        {
            // Nothing was sold: the sales are in the same transaction as nothing else, so a
            // failed commit costs one pass. The next cycle reads the portfolio again, and a
            // stop-loss that should have fired still should.
            _logger.LogError(
                "The exits were not stored under {CorrelationId}: {Reason}", correlationId, ex.Message);
        }
        catch (Exception ex)
        {
            // Deliberately does not stop the cycle. The analyses are worth running even when
            // the exits could not, and the alternative is a market data outage that blocks all
            // trading rather than the half of it that needed prices.
            _logger.LogError(ex, "Unexpected failure while applying the exits.");
        }
    }

    /// <summary>
    /// One cycle: read the portfolio, decide, commit. The outcome is logged only after the
    /// commit, so a line in this log means a row in the database rather than an intention.
    /// </summary>
    private async Task<AnalysisVerdict> RunCycleAsync(
        IServiceProvider services,
        InstrumentSelection selected,
        DateOnly today,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // Before the portfolio is even read, because a cycle that is not due does nothing at all -
        // including opening an account. Nothing is queued on this scope, so there is nothing to
        // commit either.
        var due = services.GetRequiredService<AnalysisDueCheck>();

        var verdict = await due.ForAsync(
            selected.Ticker, today, _clock.GetUtcNow(), correlationId, cancellationToken);

        if (verdict != AnalysisVerdict.Due)
        {
            _logger.LogDebug(
                "No analysis for {Ticker}: {Verdict}.", selected.Ticker.Value, verdict);

            return verdict;
        }

        var portfolios = services.GetRequiredService<IPortfolioRepository>();
        var useCase = services.GetRequiredService<ProcessProposalUseCase>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();

        var portfolio = await portfolios.FindAsync(cancellationToken) ?? OpenTheAccount(portfolios);

        // Why it is being analysed goes in the line, because "the screen picked it" and "we own
        // it" are two different cycles to be reading about at three in the morning.
        _logger.LogInformation(
            "Requesting analysis for {Ticker} ({Source}) as {CorrelationId}...",
            selected.Ticker.Value, selected.Source, correlationId);

        var result = await useCase.ExecuteAsync(portfolio, selected, correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        LogOutcome(result, portfolio);

        return verdict;
    }

    /// <summary>
    /// One line per cycle, whatever it did - including when it did nothing.
    /// </summary>
    /// <remarks>
    /// Written because the alternative is unreadable rather than because the numbers are
    /// interesting. Nearly every cycle now skips nearly everything: an instrument is analysed once
    /// a trading day, so fourteen of fifteen cycles have nothing to say, and at a fifteen-minute
    /// interval that is a log where silence means both "nothing had changed" and "the worker
    /// stopped". Naming the counts tells those two apart at a glance.
    /// </remarks>
    private void LogWhatTheCycleDid(int selected, Dictionary<AnalysisVerdict, int> verdicts)
    {
        if (selected == 0)
        {
            _logger.LogInformation("Nothing to analyse this cycle: no holdings and no shortlist.");
            return;
        }

        _logger.LogInformation(
            "Cycle over {Selected} instrument(s): {Analysed} analysed, {Today} already done today, "
            + "{Unmoved} unchanged in price.",
            selected,
            verdicts.GetValueOrDefault(AnalysisVerdict.Due),
            verdicts.GetValueOrDefault(AnalysisVerdict.AlreadyAnalysedToday),
            verdicts.GetValueOrDefault(AnalysisVerdict.PriceHasNotMoved));
    }

    /// <summary>
    /// What this cycle is about: today's shortlist, screened once and stored, plus everything the
    /// portfolio holds.
    /// </summary>
    /// <remarks>
    /// Its own scope and transaction, like the exits, and for the same reason: a screen that
    /// cannot be stored should cost the screen rather than an analysis that had already been paid
    /// for. It runs before the loop because the loop's length is what it decides.
    ///
    /// A failed commit returns no shortlist rather than the list it had in hand. The candidates
    /// would still be analysable, but the decisions made from them would point at a screen that
    /// is not in the database - and a shortlist that cannot be read back is one this stage's own
    /// question cannot be asked of. The holdings are analysed either way.
    /// </remarks>
    private async Task<IReadOnlyList<InstrumentSelection>> SelectAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var correlationId = Guid.NewGuid().ToString();

        try
        {
            var portfolios = services.GetRequiredService<IPortfolioRepository>();
            var shortlists = services.GetRequiredService<SelectShortlistUseCase>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            var portfolio = await portfolios.FindAsync(cancellationToken);

            var shortlist = await shortlists.ExecuteAsync(today, correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return CycleSelection.ForCycle(portfolio, shortlist);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return []; // Shutting down; the loop below checks the token before it does anything.
        }
        catch (Exception ex)
        {
            // Deliberately does not stop the cycle. Without a shortlist there are no new
            // candidates, which is a worse cycle than usual - but the holdings still need
            // looking at, and reaching them needs the portfolio rather than the screen.
            _logger.LogError(ex, "Could not select a shortlist, so only the holdings will be analysed.");

            return await HoldingsOnlyAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The fallback when the screen or its commit failed: the portfolio's own holdings, in a fresh
    /// scope because the failed one's change tracker still holds whatever did not commit.
    /// </summary>
    private async Task<IReadOnlyList<InstrumentSelection>> HoldingsOnlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var portfolios = scope.ServiceProvider.GetRequiredService<IPortfolioRepository>();

            return CycleSelection.ForCycle(await portfolios.FindAsync(cancellationToken), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            // The database is unreachable, which is the one failure this worker cannot work
            // around: nothing can be decided without the portfolio. The next cycle tries again.
            _logger.LogError(ex, "Could not read the portfolio either, so this cycle does nothing.");

            return [];
        }
    }

    /// <summary>Happens once in the account's life: the first cycle against an empty database.</summary>
    private Portfolio OpenTheAccount(IPortfolioRepository portfolios)
    {
        var portfolio = new Portfolio(new Money(_options.OpeningBalance, Money.DefaultCurrency));
        portfolios.Add(portfolio);

        _logger.LogInformation(
            "No portfolio was stored, so one was opened with {Balance} {Currency}.",
            _options.OpeningBalance, Money.DefaultCurrency);

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
                    "{Side} {Quantity} {Ticker} at {Amount} {Currency}. Cash left: {Cash}.",
                    executed.Side == OrderSide.Buy ? "Bought" : "Sold",
                    executed.Quantity,
                    executed.Ticker.Value,
                    executed.Price.Amount,
                    executed.Price.Currency,
                    portfolio.CashBalance.Amount);
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

            case TradeDecisionResult.Shadowed shadowed:
                _logger.LogInformation("No order for {Ticker}. {Reason}.", shadowed.Ticker.Value, shadowed.Reason);
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
