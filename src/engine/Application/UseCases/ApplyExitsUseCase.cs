namespace Engine.Application.UseCases;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sells what the rules say should be sold, without asking anyone. One pass over the whole
/// portfolio, run before the analyses so that a cycle's buying happens with the cash and the
/// position limits the exits have already released.
/// </summary>
/// <remarks>
/// This is the half of trading the agents cannot do. They are asked about one instrument at a
/// time with no memory of having bought it, and HOLD is the answer they give most often - so
/// without this, a thesis that stopped being true would have no way to end. Being mechanical is
/// the point rather than a limitation: an exit fires on a number, at a size nobody argues about,
/// and stage 8 compares the agents' own SELLs against exactly this baseline.
///
/// It writes no decision. <c>trading.decisions</c> is one row per analysis - it requires a team,
/// a request and the room the engine had at the time - and an exit asked nobody anything, so a
/// row there would need a team id that is not true. What an exit leaves behind is a ledger line
/// whose <c>triggered_by</c> says which rule fired and whose <c>realised_pnl</c> says what it
/// cost or made, which is the whole of what happened. The consequence is that an exit is not
/// measured against the index the way a signal is, because a measurement hangs off a decision -
/// that is a known gap rather than an oversight, and closing it means deciding whether a sale
/// nobody argued for is a thing to score at all.
/// </remarks>
public sealed class ApplyExitsUseCase
{
    private readonly QuoteReader _quotes;
    private readonly RiskEngine _riskEngine;
    private readonly RiskPolicy _policy;
    private readonly OrderGate _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApplyExitsUseCase> _logger;

    public ApplyExitsUseCase(
        QuoteReader quotes,
        RiskEngine riskEngine,
        RiskPolicy policy,
        OrderGate gate,
        TimeProvider clock,
        ILogger<ApplyExitsUseCase> logger)
    {
        _quotes = quotes;
        _riskEngine = riskEngine;
        _policy = policy;
        _gate = gate;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Applies both exits to every holding that can be priced, and returns the ledger lines it
    /// placed. The caller owns the transaction, so the sales and everything else the cycle does
    /// land together or not at all.
    /// </summary>
    public async Task<IReadOnlyList<Order>> ExecuteAsync(
        Portfolio portfolio, string correlationId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var placed = new List<Order>();
        var held = portfolio.Positions.Count;

        // Every holding, including one an analysis is about later in the same cycle: an exit is
        // not an opinion the agents could contradict, and a position the rules say to close
        // should not survive because it happened to be today's subject.
        var quotes = await _quotes.ForHoldingsAsync(portfolio, now, correlationId, cancellationToken: cancellationToken);

        foreach (var quote in quotes)
        {
            // Read again each time round. A sale changes the portfolio, and a holding that was
            // closed by the rule above is gone rather than sitting at zero.
            var position = portfolio.Positions.FirstOrDefault(held => held.Ticker == quote.Ticker);

            if (position is null)
                continue;

            var trigger = ExitRules.Triggered(position, quote.Price, now, _policy);

            if (trigger is null)
                continue;

            var order = await ExitAsync(portfolio, position, quote, trigger.Value, now, cancellationToken);

            if (order is not null)
                placed.Add(order);
        }

        // One line per pass, whether or not anything sold. A pass that judged its holdings and
        // was content used to say nothing at all, which a live run made the case against: with
        // no decision row and no order, silence was indistinguishable from the exits not having
        // run, or from every holding being unpriceable. The two counts are what tells those
        // apart - a judged count below the held count is holdings the engine could not price.
        _logger.LogInformation(
            "The exits judged {Judged} of {Held} holding(s) and sold {Sold}.",
            quotes.Count, held, placed.Count);

        return placed;
    }

    /// <summary>
    /// Sells the whole holding, if the risk gate lets it. Whole rather than part on purpose: a
    /// stop-loss that sold half would leave the position it judged to be wrong, and a thesis
    /// that has expired is not half expired.
    /// </summary>
    private async Task<Order?> ExitAsync(
        Portfolio portfolio,
        Position position,
        InstrumentQuote quote,
        OrderTrigger trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var intent = new OrderIntent.Sell(
            new Instrument.Equity(position.Ticker), position.Quantity, quote.Price, quote.AsOf, trigger);

        // The same gate a signal's sale passes, which is what the overload taking no signal was
        // for. It re-derives the quantity from the holding and lets these two triggers past the
        // minimum holding period - they are what was agreed when the position was opened.
        if (_riskEngine.Evaluate(intent, portfolio, _policy, now) is RiskDecision.Rejected rejected)
        {
            // A refused exit is worth a warning rather than a line of information: the rules
            // wanted out of a position and did not get out.
            _logger.LogWarning(
                "The {Trigger} exit for {Ticker} was refused: {Reason}",
                trigger, position.Ticker.Value, rejected.Reason);

            return null;
        }

        // The same gate an analysis's order passes, asked per sale and immediately before it. An
        // exit is the engine acting without being asked, which makes it the order a stopped engine
        // most needs to not place.
        var permission = await _gate.AskAsync(cancellationToken);

        if (permission is OrderPermission.ShadowOnly)
        {
            // Information, not a warning: in Shadow this is the exit working. The line is the only
            // trace a shadow exit leaves - it writes no decision row in any mode.
            _logger.LogInformation(
                "Shadow mode: the {Trigger} exit would have sold {Quantity} {Ticker} at {Price} {Currency}.",
                trigger, intent.Quantity, position.Ticker.Value, quote.Price.Amount, quote.Price.Currency);

            return null;
        }

        if (permission is not OrderPermission.Granted)
        {
            throw new InvalidOperationException(
                $"The order gate answered {permission.GetType().Name}, which the exits do not handle.");
        }

        var order = portfolio.ExecuteSell(position.Ticker, intent.Quantity, quote.Price, trigger);

        _logger.LogInformation(
            "{Trigger} sold {Quantity} {Ticker} at {Price} {Currency}, realising {Realised}.",
            trigger,
            order.Quantity,
            position.Ticker.Value,
            quote.Price.Amount,
            quote.Price.Currency,
            order.RealisedProfitAndLoss?.Amount);

        return order;
    }
}
