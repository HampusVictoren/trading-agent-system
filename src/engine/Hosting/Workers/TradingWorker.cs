namespace Engine.Hosting.Workers;

using System.Diagnostics;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.Trading;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Options;

public class TradingWorker : BackgroundService
{
    /// <summary>The span names a cycle's trace is made of, public so a test can hold them.</summary>
    public const string CycleSpan = "trading.cycle";
    public const string ExitsSpan = "trading.exits";
    public const string SelectSpan = "trading.select";
    public const string AnalysisSpan = "trading.analysis";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingOptions _options;
    private readonly TimeProvider _clock;
    private readonly EngineTelemetry _telemetry;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<TradingOptions> options,
        TimeProvider clock,
        EngineTelemetry telemetry,
        ILogger<TradingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _clock = clock;
        _telemetry = telemetry;
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

        if (_options.Mode != TradingMode.Paper)
            await WarnAboutHoldingsNobodyIsManagingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await RunOneCycleAsync(stoppingToken))
                return;

            if (!await WaitForTheNextCycleAsync(stoppingToken))
                return;
        }
    }

    /// <summary>
    /// One cycle - the switch, the exits, the selection and every analysis - as one trace.
    /// </summary>
    /// <returns>False when the worker is shutting down.</returns>
    /// <remarks>
    /// <para>
    /// The cycle's activity is the root of its trace, and everything the cycle does is a child of
    /// it: the exits, the selection, and one span per instrument analysed. HttpClient puts the
    /// current activity in a <c>traceparent</c> header on every call it makes, so each call to
    /// the agent service carries this cycle's trace id - which is what lets the agent service
    /// continue the same trace rather than start one per request.
    /// </para>
    /// <para>
    /// It ends here, before the wait, so a trace is the fifteen seconds or five minutes of work
    /// and not a quarter of an hour of mostly sleeping. And it is a root on purpose:
    /// <c>Activity.Current</c> is cleared first, so a cycle can never become a child of whatever
    /// happened to be current when the worker started. Changing it here does not leak to the
    /// caller, because an async method's changes to an AsyncLocal stay inside it.
    /// </para>
    /// <para>
    /// What goes on a span is a closed set plus the ticker and the correlation id - the id is what
    /// joins a trace to this log's lines. Never a reason, a thesis, or anything else the agents
    /// wrote: those are in the database, and a trace is not a second copy of them.
    /// </para>
    /// </remarks>
    private async Task<bool> RunOneCycleAsync(CancellationToken stoppingToken)
    {
        Activity.Current = null;
        using var cycle = EngineTelemetry.ActivitySource.StartActivity(CycleSpan);
        cycle?.SetTag("trading.mode", (_options.Mode ?? TradingMode.Shadow).ToString());

        // First, before anything is asked of anyone. The switch stops new buys only, so an
        // engaged one does not stop the cycle: the exits still run, and the holdings are still
        // analysed, because the agents' SELL on a holding is a sale the switch lets through.
        // What it skips is the screen and every candidate, since the only order a candidate
        // can lead to is a buy, and an analysis of one would be LLM time spent on an order
        // that could not be placed.
        var buyingHalted = await IsBuyingHaltedAsync(stoppingToken);
        cycle?.SetTag("trading.kill_switch.engaged", buyingHalted);

        // Before the analyses, not after. A cycle's buying should see the cash and the
        // position headroom the exits have just released, and a position the rules say to
        // close should not survive because an analysis of it happened to come first.
        await RunExitsAsync(stoppingToken);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var selection = buyingHalted
            ? await HoldingsOnlyAsync(stoppingToken)
            : await SelectAsync(today, stoppingToken);
        var verdicts = new Dictionary<AnalysisVerdict, int>();
        var candidatesNotAnalysed = 0;

        foreach (var selected in selection)
        {
            if (stoppingToken.IsCancellationRequested)
                return false;

            // Read again before every candidate, so a switch pulled mid-cycle costs at most the
            // analysis already under way - and that one's buy is stopped by the gate, which
            // reads it again after the agents have answered. A holding is analysed whatever
            // the switch says; holdings come first in the selection anyway.
            if (selected.Source != SelectionSource.Holding && await KillSwitchStateAsync(stoppingToken) is { Engaged: true })
            {
                candidatesNotAnalysed++;
                continue;
            }

            // One scope per analysis, so one change tracker and one transaction per decision.
            using var scope = _scopeFactory.CreateScope();

            // One id per analysis, generated here and logged before the call, so a line in
            // this log can be found in the agent service's - it echoes the id and puts
            // it in every line it writes while handling the request.
            var correlationId = Guid.NewGuid().ToString();

            using var analysis = EngineTelemetry.ActivitySource.StartActivity(AnalysisSpan);
            analysis?.SetTag("trading.ticker", selected.Ticker.Value);
            analysis?.SetTag("trading.selection", selected.Source.ToString());
            analysis?.SetTag("trading.correlation_id", correlationId);

            try
            {
                var verdict = await RunCycleAsync(
                    scope.ServiceProvider, selected, today, correlationId, stoppingToken);

                verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
                analysis?.SetTag("trading.verdict", verdict.ToString());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
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
                Failed(analysis, ex);
            }
            catch (Exception ex)
            {
                // Only a bug, or an outage, reaches this point: every expected outcome of
                // the analysis itself is a result rather than an exception.
                _logger.LogError(
                    ex, "Unexpected failure in the trading cycle for {Ticker}.", selected.Ticker.Value);
                Failed(analysis, ex);
            }
        }

        cycle?.SetTag("trading.selected", selection.Count);
        cycle?.SetTag("trading.analysed", verdicts.GetValueOrDefault(AnalysisVerdict.Due));
        cycle?.SetTag("trading.candidates_skipped", candidatesNotAnalysed);

        LogWhatTheCycleDid(selection.Count, verdicts);

        if (candidatesNotAnalysed > 0)
        {
            _logger.LogWarning(
                "The kill switch was engaged during the cycle: {Count} candidate(s) were not analysed, "
                + "because the only order they could lead to is a buy.",
                candidatesNotAnalysed);
        }

        return true;
    }

    /// <summary>
    /// Marks a span as failed with the exception's type and nothing else. Not the message: an
    /// exception's message can carry what the agent service answered, and the log line written
    /// beside this already has it in full.
    /// </summary>
    private static void Failed(Activity? span, Exception ex)
    {
        span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
        span?.SetTag("error.type", ex.GetType().FullName);
    }

    /// <summary>
    /// Says loudly, once at startup, that Shadow mode is leaving real positions to themselves.
    /// </summary>
    /// <remarks>
    /// Shadow places nothing, and that includes the stop-loss and time-limit sales. An account
    /// that was paper-trading and is restarted in Shadow (which is what the shipped default does
    /// to an engine that never set the mode) keeps its positions, and from then on nothing closes
    /// them; the exits only log what they would have sold. That is correct for Shadow, but it is
    /// a change nobody would guess from the word, so it is a warning that names the setting to
    /// change. Read in a scope of its own, and never allowed to stop the worker: this is
    /// a message, not a check.
    /// </remarks>
    private async Task WarnAboutHoldingsNobodyIsManagingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var portfolio = await scope.ServiceProvider
                .GetRequiredService<IPortfolioRepository>()
                .FindAsync(cancellationToken);

            if (portfolio is null || portfolio.Positions.Count == 0)
                return;

            _logger.LogWarning(
                "Trading mode is {Mode} and the portfolio holds {Count} position(s): {Tickers}. Their "
                + "stop-loss and time-limit exits will be logged but NOT placed, so nothing will close "
                + "them. Set Trading:Mode to Paper (TRADING_MODE=Paper under compose) to keep managing them.",
                _options.Mode,
                portfolio.Positions.Count,
                string.Join(", ", portfolio.Positions.Select(held => held.Ticker.Value).Order(StringComparer.Ordinal)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down before the first cycle, which is not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the portfolio to check for holdings Shadow mode will not manage.");
        }
    }

    /// <returns>False when the worker is shutting down.</returns>
    private async Task<bool> WaitForTheNextCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.CycleInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the kill switch at the start of a cycle, and says so when it is engaged.
    /// </summary>
    /// <remarks>
    /// A warning every time it is found engaged, rather than once: at a fifteen-minute cadence that
    /// is a line per cycle, and an engine whose buying has been stopped should keep saying why for
    /// as long as it is stopped. The read itself fails closed, so "could not read it" arrives here
    /// as engaged with that as the reason.
    /// </remarks>
    private async Task<bool> IsBuyingHaltedAsync(CancellationToken cancellationToken)
    {
        var state = await KillSwitchStateAsync(cancellationToken);

        if (!state.Engaged)
            return false;

        _logger.LogWarning(
            "The kill switch is engaged (since {Since}): {Reason}. No new buys until it is released. The exits "
            + "and sales still run and the holdings are still analysed; the screen and its candidates wait.",
            state.Since?.ToString("u") ?? "unknown",
            state.Reason);

        return true;
    }

    /// <summary>The kill switch, read in a scope of its own.</summary>
    private async Task<KillSwitchState> KillSwitchStateAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IKillSwitch>().ReadAsync(cancellationToken);
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

        using var span = EngineTelemetry.ActivitySource.StartActivity(ExitsSpan);
        span?.SetTag("trading.correlation_id", correlationId);

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
            span?.SetTag("trading.exits.sold", placed.Count);

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
            Failed(span, ex);
        }
        catch (Exception ex)
        {
            // Deliberately does not stop the cycle. The analyses are worth running even when
            // the exits could not, and the alternative is a market data outage that blocks all
            // trading rather than the half of it that needed prices.
            _logger.LogError(ex, "Unexpected failure while applying the exits.");
            Failed(span, ex);
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

        // After the commit, like the line below: the counter counts rows, not intentions. The mode
        // is the one OrderGate decided under and the row records, missing-means-Shadow included.
        _telemetry.DecisionStored(
            result.Outcome,
            _options.Mode ?? TradingMode.Shadow,
            (result as TradeDecisionResult.RejectedByRisk)?.Side);

        LogOutcome(result, portfolio);

        // On the analysis span this runs inside. The outcome and nothing more: its reason can
        // quote the agents, and the row holds it.
        Activity.Current?.SetTag("trading.outcome", result.Outcome.ToString());

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

        using var span = EngineTelemetry.ActivitySource.StartActivity(SelectSpan);
        span?.SetTag("trading.correlation_id", correlationId);

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
            Failed(span, ex);

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

            case TradeDecisionResult.Halted halted:
                _logger.LogWarning("No order for {Ticker}. {Reason}.", halted.Ticker.Value, halted.Reason);
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
