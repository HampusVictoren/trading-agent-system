namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

public class ProcessProposalUseCase
{
    private readonly IAgentClient _agentClient;
    private readonly IPortfolioRepository _portfolios;
    private readonly QuoteReader _quotes;
    private readonly IDecisionLog _decisions;
    private readonly PositionSizer _sizer;
    private readonly RiskEngine _riskEngine;
    private readonly RiskPolicy _policy;
    private readonly TradingOptions _trading;
    private readonly TimeProvider _clock;

    public ProcessProposalUseCase(
        IAgentClient agentClient,
        IPortfolioRepository portfolios,
        QuoteReader quotes,
        IDecisionLog decisions,
        PositionSizer sizer,
        RiskEngine riskEngine,
        RiskPolicy policy,
        IOptions<TradingOptions> trading,
        TimeProvider clock)
    {
        _agentClient = agentClient;
        _portfolios = portfolios;
        _quotes = quotes;
        _decisions = decisions;
        _sizer = sizer;
        _riskEngine = riskEngine;
        _policy = policy;
        _trading = trading.Value;
        _clock = clock;
    }

    /// <summary>
    /// Runs one analysis cycle for a ticker, records it, and reports what came of it. Every
    /// expected outcome is returned as a <see cref="TradeDecisionResult"/>; exceptions are
    /// left for bugs.
    /// </summary>
    /// <remarks>
    /// The record is written here rather than inside the decision, so that none of the
    /// decision's early returns can skip it. It is queued, not committed: the caller owns the
    /// transaction, so the decision, the position change and the ledger line all land together
    /// or not at all.
    /// </remarks>
    public async Task<TradeDecisionResult> ExecuteAsync(
        Portfolio portfolio,
        InstrumentSelection selected,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var requested = selected.Ticker;
        var request = BuildRequest(portfolio, requested, _clock.GetUtcNow(), correlationId);

        var cycle = await DecideAsync(portfolio, requested, request, cancellationToken);

        _decisions.Record(ToRecord(portfolio.Id, selected, request, cycle));

        return cycle.Result;
    }

    /// <summary>
    /// What one cycle produced. The signal and the order are separate from the result because
    /// most outcomes have neither, and the record has to say which.
    /// </summary>
    private sealed record Cycle(TradeDecisionResult Result, TradeSignal? Signal = null, Order? Order = null);

    /// <remarks>
    /// The order is deliberate. The agents give a view; the sizer turns it into a quantity;
    /// the risk gate re-derives the same limits from the other direction and can still say
    /// no. Nothing between those steps takes a number from the answer - not the price, and
    /// certainly not an amount.
    /// </remarks>
    private async Task<Cycle> DecideAsync(
        Portfolio portfolio,
        Ticker requested,
        TradeSignalRequestDto request,
        CancellationToken cancellationToken)
    {
        TradeSignalDto? dto;
        try
        {
            dto = await _agentClient.GetSignalAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down, which is not a failing agent service.
        }
        catch (AgentServiceUnavailableException ex)
        {
            return new Cycle(new TradeDecisionResult.AgentUnavailable(requested, ex.Message));
        }
        catch (AgentResponseInvalidException ex)
        {
            return new Cycle(new TradeDecisionResult.InvalidResponse(requested, ex.Message));
        }

        if (dto is null)
        {
            return new Cycle(new TradeDecisionResult.InvalidResponse(
                requested, "the agent service returned an empty body"));
        }

        TradeSignal signal;
        try
        {
            // The seam. Everything past here has been checked, so sizing and risk code
            // never has to ask whether a value makes sense - and only values that got this
            // far are worth storing, because an answer that is not the contract has no
            // trustworthy numbers in it.
            signal = TradeSignalMapper.ToDomain(dto);
        }
        catch (AgentResponseInvalidException ex)
        {
            return new Cycle(new TradeDecisionResult.InvalidResponse(requested, ex.Message));
        }

        // The answer must be about what we asked about. Without this check, a model that
        // replies "TSLA" to a question about AAPL makes the engine buy TSLA.
        if (signal.Instrument is not Instrument.Equity equity || equity.Ticker != requested)
        {
            return new Cycle(
                new TradeDecisionResult.InvalidResponse(requested, $"the answer is about {Describe(signal.Instrument)}"),
                signal);
        }

        // Separated from sizing so that "the agents did not argue for a trade" stays a
        // different fact from "the trade could not be sized". HOLD is the only stance that
        // stops here now, and it is the one they answer most often.
        if (signal.Stance == Stance.Hold)
        {
            return new Cycle(
                new TradeDecisionResult.NoAction(requested, signal.Stance.ToString().ToUpperInvariant()),
                signal);
        }

        var prices = await PricesForSizingAsync(portfolio, signal, requested, request, cancellationToken);

        // Read once and handed to both halves, so the sizer and the gate cannot disagree about how
        // much of the day is left. A sale needs none of it - selling frees capital rather than
        // committing it - so the query is only made when there is a purchase to bound.
        var deployedToday = signal.Stance == Stance.Buy
            ? await _portfolios.DeployedOnAsync(
                DateOnly.FromDateTime(request.AsOf.UtcDateTime), cancellationToken)
            : Money.Zero();

        var intent = _sizer.Size(signal, portfolio, prices, _policy, deployedToday);

        if (intent is OrderIntent.None nothing)
            return new Cycle(new TradeDecisionResult.NotSized(requested, nothing.Reason), signal);

        // Each direction is judged by the gate written for it. A sale is not put through the
        // buy overload with the arguments it does not need: it has no budget to breach and no
        // valuation to make, and it has a rule of its own about how long the holding has been
        // held. That the two take different parameters is what says so.
        var decision = intent switch
        {
            OrderIntent.Buy buy =>
                _riskEngine.Evaluate(buy, signal, portfolio, prices, _policy, request.AsOf, deployedToday),
            OrderIntent.Sell sell => _riskEngine.Evaluate(sell, portfolio, _policy, request.AsOf),

            // Unreachable: None returned above, and the hierarchy is closed. Throwing rather
            // than defaulting to approved, because the wrong answer here places an order.
            _ => throw new InvalidOperationException($"Sizing produced {intent.GetType().Name}, which has no gate.")
        };

        if (decision is RiskDecision.Rejected rejected)
        {
            return new Cycle(
                new TradeDecisionResult.RejectedByRisk(requested, rejected.Reason),
                signal);
        }

        // request.AsOf rather than the clock read again, so the trade is stamped with the same
        // instant the risk gate judged the quote against. A holding period is counted in days;
        // the seconds between the two would be precision that means nothing.
        //
        // The horizon travels with a purchase because the position is what the deterministic
        // exits read, and they need to know what thesis they are enforcing. A sale carries a
        // trigger instead, saying who asked for it - here, always the agents.
        var placed = intent switch
        {
            OrderIntent.Buy buy =>
                portfolio.ExecuteBuy(requested, buy.Quantity, buy.Price, request.AsOf, signal.HorizonDays),

            OrderIntent.Sell sell =>
                portfolio.ExecuteSell(requested, sell.Quantity, sell.Price, sell.Trigger),

            _ => throw new InvalidOperationException(
                $"Sizing produced {intent.GetType().Name}, which the portfolio cannot execute.")
        };

        return new Cycle(
            new TradeDecisionResult.Executed(requested, placed.Side, placed.Quantity, placed.Price),
            signal,
            placed);
    }

    /// <summary>
    /// The prices sizing needs, which is none for a sale.
    /// </summary>
    /// <remarks>
    /// The price for the instrument being analysed always comes from the signal, so an order is
    /// never sized against a quote the agents never saw. Everything else the portfolio holds is
    /// asked for only when a buy is being sized, because the position limit is a share of the
    /// portfolio's value and a holding without a price makes that value unknowable.
    ///
    /// A sale needs no valuation, so a SELL cycle makes no quote calls at all - which is not
    /// only saved work. It is what keeps a holding the engine cannot price from standing between
    /// the agents and a position they have argued should be closed.
    /// </remarks>
    private async Task<PriceSnapshot> PricesForSizingAsync(
        Portfolio portfolio,
        TradeSignal signal,
        Ticker requested,
        TradeSignalRequestDto request,
        CancellationToken cancellationToken)
    {
        if (signal.Stance != Stance.Buy)
            return PriceSnapshot.Empty;

        var quotes = await _quotes.ForHoldingsAsync(
            portfolio, request.AsOf, request.CorrelationId, except: requested, cancellationToken);

        return quotes.Aggregate(
            PriceSnapshot.Empty, (snapshot, quote) => snapshot.With(quote.Ticker, quote.Price));
    }

    /// <summary>
    /// The cycle as a row. The signal's columns are null when there never was a signal worth
    /// trusting, which is itself a measurement: a stretch of rows with nothing in them says
    /// the agent service was down, not that the agents were cautious.
    /// </summary>
    private static DecisionRecord ToRecord(
        Guid portfolioId, InstrumentSelection selected, TradeSignalRequestDto request, Cycle cycle) => new()
        {
            CorrelationId = request.CorrelationId,
            PortfolioId = portfolioId,
            Symbol = selected.Ticker,
            Selection = selected.Source,

            // The team that was *asked for*, which is known whatever happens. The version is
            // the answer's own, because only an answer has one.
            TeamId = request.TeamId,
            RequestedAt = request.AsOf,

            AvailableRiskBudget = request.AvailableRiskBudget,
            MaxPositionPct = request.MaxPositionPct,
            ExistingQuantity = request.ExistingPosition?.Quantity,
            ExistingAveragePrice = request.ExistingPosition?.AveragePrice,

            TeamVersion = cycle.Signal?.Run.TeamVersion,
            Revisions = cycle.Signal?.Run.Revisions,
            Stance = cycle.Signal?.Stance,
            Conviction = cycle.Signal?.Conviction.Value,
            Thesis = cycle.Signal?.Thesis,
            KeyRisks = cycle.Signal?.KeyRisks.ToArray() ?? [],
            HorizonDays = cycle.Signal?.HorizonDays,
            ReferencePrice = cycle.Signal?.ReferencePrice.Amount,
            ReferenceCurrency = cycle.Signal?.ReferencePrice.Currency,
            QuoteAsOf = cycle.Signal?.QuoteAsOf,

            Outcome = cycle.Result.Outcome,
            OutcomeReason = cycle.Result.OutcomeReason,
            OrderId = cycle.Order?.Id,
        };

    /// <summary>
    /// What the engine asks. The limits go with it so the agents reason inside them, and the
    /// holding goes with it because adding to a position is a different question from
    /// opening one.
    /// </summary>
    private TradeSignalRequestDto BuildRequest(
        Portfolio portfolio, Ticker ticker, DateTimeOffset now, string correlationId)
    {
        var held = portfolio.Positions.FirstOrDefault(position => position.Ticker == ticker);

        return new TradeSignalRequestDto
        {
            Instrument = new EquityInstrumentDto { Symbol = ticker.Value },
            TeamId = _trading.TeamId,
            AsOf = now,
            ExistingPosition = held is null
                ? null
                : new ExistingPositionDto
                {
                    Quantity = held.Quantity,
                    AveragePrice = held.AveragePurchasePrice.Amount
                },
            // The cash balance, not a computed budget: working out what may be spent on this
            // instrument needs the portfolio's net asset value, which needs quotes for every
            // holding - and those arrive with stage 4's quote endpoint. No agent reads this
            // figure anyway; it is sent because the decision record should say what the
            // engine could have spent.
            AvailableRiskBudget = portfolio.CashBalance.Amount,
            MaxPositionPct = _policy.MaxPositionPct,
            CorrelationId = correlationId
        };
    }

    private static string Describe(Instrument instrument) =>
        instrument is Instrument.Equity equity ? equity.Ticker.Value : instrument.GetType().Name;
}
