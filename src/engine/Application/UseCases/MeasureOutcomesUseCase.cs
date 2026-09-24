namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Domain.Outcomes;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>What one sweep came to. Returned rather than logged only, so a test can say so.</summary>
/// <param name="Measured">Horizons that passed and were scored.</param>
/// <param name="Abandoned">Horizons that can never be scored, written so they stop being retried.</param>
/// <param name="NotDue">Horizons that have not passed yet. They get no row and are asked about again.</param>
/// <param name="Unreachable">Signals skipped because their history could not be fetched at all.</param>
public sealed record SweepResult(int Measured, int Abandoned, int NotDue, int Unreachable)
{
    public static readonly SweepResult Nothing = new(0, 0, 0, 0);

    public int Written => Measured + Abandoned;
}

/// <summary>
/// Measures every signal that has something left to measure. This is what turns a decision
/// history into evidence: until it runs, the rows say what the agents thought and nothing
/// about whether they were right.
/// </summary>
public sealed class MeasureOutcomesUseCase
{
    /// <summary>
    /// How far before a signal's own day the history window starts. A comparison needs the
    /// benchmark's last close *on or before* the signal, and a signal made on a Saturday - or
    /// the Friday of a long weekend - has no such bar within a window that begins on its own
    /// date. Ten days covers any holiday weekend and costs a handful of rows.
    /// </summary>
    private const int LookbackDays = 10;

    private readonly IOutcomeLog _outcomes;
    private readonly IAgentClient _agentClient;
    private readonly OutcomeCalculator _calculator;
    private readonly OutcomePolicy _policy;
    private readonly OutcomeOptions _options;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MeasureOutcomesUseCase> _logger;

    public MeasureOutcomesUseCase(
        IOutcomeLog outcomes,
        IAgentClient agentClient,
        OutcomeCalculator calculator,
        OutcomePolicy policy,
        IOptions<OutcomeOptions> options,
        IUnitOfWork unitOfWork,
        ILogger<MeasureOutcomesUseCase> logger)
    {
        _outcomes = outcomes;
        _agentClient = agentClient;
        _calculator = calculator;
        _policy = policy;
        _options = options.Value;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>
    /// One sweep: find what is unmeasured, fetch the bars it needs, score it, commit.
    /// </summary>
    /// <remarks>
    /// Histories are fetched once per symbol rather than once per measurement, so a sweep
    /// costs one call per instrument plus one for the benchmark however many signals there
    /// are. Everything is written in one transaction, because a half-finished sweep that
    /// left some horizons measured and others not would be indistinguishable from a sweep
    /// that had not run.
    /// </remarks>
    public async Task<SweepResult> SweepAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        var pending = (await _outcomes.AwaitingMeasurementAsync(cancellationToken))
            .Select(signal => (Signal: signal, Horizons: Pending(signal)))
            .Where(work => work.Horizons.Count > 0)
            .ToList();

        if (pending.Count == 0)
            return SweepResult.Nothing;

        var earliest = pending.Min(work => work.Signal.SignalDate).AddDays(-LookbackDays);

        var benchmarkSymbol = new Ticker(_options.BenchmarkSymbol);
        var benchmark = await HistoryOrNothingAsync(benchmarkSymbol, earliest, correlationId, cancellationToken);

        if (benchmark is null)
        {
            // Without the benchmark nothing can be compared to anything, so the sweep stops
            // rather than writing a table full of rows that say the index was unavailable.
            _logger.LogWarning(
                "Sweep {CorrelationId} stopped: no history for the benchmark {Symbol}.",
                correlationId, benchmarkSymbol.Value);

            return SweepResult.Nothing with { Unreachable = pending.Sum(work => work.Horizons.Count) };
        }

        var histories = await HistoriesBySymbolAsync(pending, earliest, correlationId, cancellationToken);

        var result = Score(pending, histories, benchmark);

        if (result.Written > 0)
            await _unitOfWork.SaveChangesAsync(cancellationToken);

        return result;
    }

    /// <summary>The horizons this signal should have and does not: the fixed ones, and its own.</summary>
    private List<Horizon> Pending(SignalAwaitingMeasurement signal)
    {
        List<Horizon> wanted = [.. _options.FixedHorizons(), new Horizon.CalendarDays(signal.ModelHorizonDays)];

        return [.. wanted.Where(horizon => !signal.AlreadyMeasured.Contains((horizon.Unit, horizon.Days)))];
    }

    private async Task<Dictionary<Ticker, BarSeries>> HistoriesBySymbolAsync(
        List<(SignalAwaitingMeasurement Signal, List<Horizon> Horizons)> pending,
        DateOnly from,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var histories = new Dictionary<Ticker, BarSeries>();

        foreach (var symbol in pending.Select(work => work.Signal.Symbol).Distinct())
        {
            var history = await HistoryOrNothingAsync(symbol, from, correlationId, cancellationToken);

            if (history is not null)
                histories[symbol] = history;
        }

        return histories;
    }

    private SweepResult Score(
        List<(SignalAwaitingMeasurement Signal, List<Horizon> Horizons)> pending,
        Dictionary<Ticker, BarSeries> histories,
        BarSeries benchmark)
    {
        var measured = 0;
        var abandoned = 0;
        var notDue = 0;
        var unreachable = 0;

        foreach (var (signal, horizons) in pending)
        {
            if (!histories.TryGetValue(signal.Symbol, out var instrument))
            {
                // No history this sweep. Not a row: the instrument may well answer tomorrow,
                // and writing "unmeasurable" would stop anyone ever asking again.
                unreachable += horizons.Count;
                continue;
            }

            foreach (var horizon in horizons)
            {
                var toMeasure = new SignalToMeasure(
                    signal.Stance, signal.ReferencePrice, signal.SignalDate, horizon);

                switch (_calculator.Measure(toMeasure, instrument, benchmark, _policy))
                {
                    case OutcomeResult.Measured outcome:
                        _outcomes.Record(Row(signal, horizon, outcome));
                        measured++;
                        break;

                    case OutcomeResult.NotMeasurable gap:
                        _outcomes.Record(Abandoned(signal, horizon, gap.Reason));
                        abandoned++;
                        break;

                    default:
                        // NotDue. No row, so the next sweep asks again.
                        notDue++;
                        break;
                }
            }
        }

        return new SweepResult(measured, abandoned, notDue, unreachable);
    }

    private SignalOutcomeRecord Row(
        SignalAwaitingMeasurement signal, Horizon horizon, OutcomeResult.Measured outcome) => new()
        {
            DecisionId = signal.DecisionId,
            HorizonUnit = horizon.Unit,
            HorizonDays = horizon.Days,
            Status = OutcomeStatus.Measured,
            BenchmarkSymbol = _options.BenchmarkSymbol,
            MeasuredOn = outcome.On,
            MeasuredPrice = outcome.Price,
            InstrumentReturn = outcome.InstrumentReturn,
            BenchmarkReturn = outcome.BenchmarkReturn,
            ExcessReturn = outcome.ExcessReturn,
            CostFraction = outcome.CostFraction,
            NetEdge = outcome.NetEdge,
            Hit = outcome.Hit,
        };

    private SignalOutcomeRecord Abandoned(
        SignalAwaitingMeasurement signal, Horizon horizon, string reason) => new()
        {
            DecisionId = signal.DecisionId,
            HorizonUnit = horizon.Unit,
            HorizonDays = horizon.Days,
            Status = OutcomeStatus.NotMeasurable,
            Reason = reason,
            BenchmarkSymbol = _options.BenchmarkSymbol,
        };

    /// <summary>
    /// A history the agent service could not give is not a failed sweep. The signals that
    /// needed it are left for the next one, which is the difference between an outage and a
    /// hole in the data.
    /// </summary>
    private async Task<BarSeries?> HistoryOrNothingAsync(
        Ticker symbol, DateOnly from, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _agentClient.GetHistoryAsync(symbol.Value, from, correlationId, cancellationToken);
            return dto is null ? null : HistoryMapper.ToDomain(dto, symbol);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down.
        }
        catch (Exception ex) when (ex is AgentServiceUnavailableException or AgentResponseInvalidException)
        {
            _logger.LogWarning(ex, "No usable history for {Symbol} in sweep {CorrelationId}.", symbol.Value, correlationId);
            return null;
        }
    }
}
