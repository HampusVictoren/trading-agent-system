namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Engine.Domain.Outcomes;
using Engine.Domain.Signals;
using Microsoft.EntityFrameworkCore;

public sealed class OutcomeLog : IOutcomeLog
{
    private readonly TradingDbContext _context;

    public OutcomeLog(TradingDbContext context) => _context = context;

    /// <remarks>
    /// One query with a correlated subquery, and the diff against the wanted horizons is
    /// done above this. It reads every signal every sweep, which is right while the history
    /// is small and is the first thing to bound when it is not: the natural cut is "nothing
    /// whose longest horizon cannot possibly have passed yet".
    /// </remarks>
    public async Task<IReadOnlyList<SignalAwaitingMeasurement>> AwaitingMeasurementAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.Decisions
            // A decision that never reached an answer is not a signal. Filtering here rather
            // than after loading means the sweep does not carry them around every night.
            .Where(decision =>
                decision.Stance != null
                && decision.ReferencePrice != null
                && decision.QuoteAsOf != null
                && decision.HorizonDays != null)
            .OrderBy(decision => decision.Id)
            .Select(decision => new
            {
                decision.Id,
                decision.Symbol,
                decision.Stance,
                decision.ReferencePrice,
                decision.QuoteAsOf,
                decision.HorizonDays,
                Measured = _context.SignalOutcomes
                    .Where(outcome => outcome.DecisionId == decision.Id)
                    .Select(outcome => new { outcome.HorizonUnit, outcome.HorizonDays })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new SignalAwaitingMeasurement(
                row.Id,
                row.Symbol,
                row.Stance!.Value,
                row.ReferencePrice!.Value,
                DateOnly.FromDateTime(row.QuoteAsOf!.Value.UtcDateTime),
                row.HorizonDays!.Value,
                row.Measured.Select(measured => (measured.HorizonUnit, measured.HorizonDays)).ToHashSet()))
        ];
    }

    /// <summary>Queues the row. It reaches the database when the sweep commits.</summary>
    public void Record(SignalOutcomeRecord outcome) => _context.SignalOutcomes.Add(outcome);

    /// <remarks>
    /// A left join expressed as "no delivery exists", which is what makes the absence of a
    /// row mean undelivered. The decision is joined for one column - the correlation id -
    /// because that is the identifier the agent service knows this measurement by; its own
    /// primary key means nothing on the other side.
    /// </remarks>
    public async Task<IReadOnlyList<OutcomeAwaitingDelivery>> AwaitingDeliveryAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        return await (
            from outcome in _context.SignalOutcomes
            join decision in _context.Decisions on outcome.DecisionId equals decision.Id
            where !_context.OutcomeDeliveries.Any(delivery => delivery.SignalOutcomeId == outcome.Id)
            orderby outcome.Id
            select new OutcomeAwaitingDelivery(
                outcome.Id,
                decision.CorrelationId,
                outcome.HorizonUnit,
                outcome.HorizonDays,
                outcome.Status,
                outcome.Reason,
                outcome.BenchmarkSymbol,
                outcome.MeasuredOn,
                outcome.MeasuredPrice,
                outcome.InstrumentReturn,
                outcome.BenchmarkReturn,
                outcome.ExcessReturn,
                outcome.CostFraction,
                outcome.NetEdge,
                outcome.Hit))
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Queues the marker. It reaches the database on the next commit.</summary>
    public void MarkDelivered(long signalOutcomeId) =>
        _context.OutcomeDeliveries.Add(new OutcomeDelivery { SignalOutcomeId = signalOutcomeId });
}
