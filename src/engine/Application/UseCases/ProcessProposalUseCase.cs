namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class ProcessProposalUseCase
{
    private readonly IAgentClient _agentClient;
    private readonly IDecisionLog _decisions;
    private readonly PositionSizer _sizer;
    private readonly RiskEngine _riskEngine;
    private readonly RiskPolicy _policy;
    private readonly TradingOptions _trading;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessProposalUseCase> _logger;

    public ProcessProposalUseCase(
        IAgentClient agentClient,
        IDecisionLog decisions,
        PositionSizer sizer,
        RiskEngine riskEngine,
        RiskPolicy policy,
        IOptions<TradingOptions> trading,
        TimeProvider clock,
        ILogger<ProcessProposalUseCase> logger)
    {
        _agentClient = agentClient;
        _decisions = decisions;
        _sizer = sizer;
        _riskEngine = riskEngine;
        _policy = policy;
        _trading = trading.Value;
        _clock = clock;
        _logger = logger;
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
        string tickerSymbol,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        // Configuration is validated at startup, so a ticker that is not a ticker is a bug
        // here rather than an outcome.
        var requested = new Ticker(tickerSymbol);
        var request = BuildRequest(portfolio, requested, _clock.GetUtcNow(), correlationId);

        var cycle = await DecideAsync(portfolio, requested, request, cancellationToken);

        _decisions.Record(ToRecord(portfolio.Id, requested, request, cycle));

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

        // Separated from sizing so that "the agents did not argue for a buy" stays a
        // different fact from "the buy could not be sized". Selling arrives in stage 5.
        if (signal.Stance != Stance.Buy)
        {
            return new Cycle(
                new TradeDecisionResult.NoAction(requested, signal.Stance.ToString().ToUpperInvariant()),
                signal);
        }

        // The price for the instrument being analysed always comes from the signal, so an
        // order is never sized against a quote the agents never saw. Everything else the
        // portfolio holds is asked for here, because the position limit is a share of the
        // portfolio's value and a holding without a price makes that value unknowable.
        var prices = await PricesForOtherHoldingsAsync(portfolio, requested, request, cancellationToken);

        var intent = _sizer.Size(signal, portfolio, prices, _policy);

        if (intent is not OrderIntent.Buy order)
        {
            return new Cycle(
                new TradeDecisionResult.NotSized(requested, ((OrderIntent.None)intent).Reason),
                signal);
        }

        var decision = _riskEngine.Evaluate(order, signal, portfolio, prices, _policy, request.AsOf);

        if (decision is RiskDecision.Rejected rejected)
        {
            return new Cycle(
                new TradeDecisionResult.RejectedByRisk(requested, rejected.Reason),
                signal);
        }

        var placed = portfolio.ExecuteBuy(requested, order.Quantity, order.Price);

        return new Cycle(
            new TradeDecisionResult.Executed(requested, order.Quantity, order.Price),
            signal,
            placed);
    }

    /// <summary>
    /// A price for every other holding, from the agent service's quote endpoint. One call
    /// each: the portfolio holds one or two instruments, and a batch endpoint designed before
    /// there is a third would be designed from guesswork.
    /// </summary>
    /// <remarks>
    /// A quote that cannot be fetched, or that is too old to trust, is simply left out. The
    /// portfolio then cannot be valued and the sizer names the holding that stopped it -
    /// which is the honest outcome, and one the engine already had a word for. Valuing a
    /// holding at what it cost instead would overstate a loser, raising the position
    /// allowance for everything else at exactly the wrong moment.
    /// </remarks>
    private async Task<PriceSnapshot> PricesForOtherHoldingsAsync(
        Portfolio portfolio,
        Ticker analysed,
        TradeSignalRequestDto request,
        CancellationToken cancellationToken)
    {
        var prices = PriceSnapshot.Empty;
        var now = request.AsOf;

        foreach (var position in portfolio.Positions.Where(held => held.Ticker != analysed))
        {
            var quote = await QuoteOrNothingAsync(position.Ticker, request.CorrelationId, cancellationToken);

            if (quote is null)
                continue;

            if (!quote.IsUsableAt(now, _policy.MaxQuoteAge))
            {
                _logger.LogWarning(
                    "The quote for {Ticker} is dated {AsOf:O}, which is outside the {Limit} s window.",
                    position.Ticker.Value, quote.AsOf, _policy.MaxQuoteAge.TotalSeconds);
                continue;
            }

            prices = prices.With(quote.Ticker, quote.Price);
        }

        return prices;
    }

    /// <summary>
    /// A failed quote is not a failed cycle: the analysis already succeeded, and one holding
    /// without a price is a sizing outcome rather than an error. It is logged, because the
    /// outcome alone says a price was missing and not why.
    /// </summary>
    private async Task<InstrumentQuote?> QuoteOrNothingAsync(
        Ticker ticker, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _agentClient.GetQuoteAsync(ticker.Value, correlationId, cancellationToken);
            return dto is null ? null : QuoteMapper.ToDomain(dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down.
        }
        catch (Exception ex) when (ex is AgentServiceUnavailableException or AgentResponseInvalidException)
        {
            _logger.LogWarning(ex, "No usable quote for {Ticker} this cycle.", ticker.Value);
            return null;
        }
    }

    /// <summary>
    /// The cycle as a row. The signal's columns are null when there never was a signal worth
    /// trusting, which is itself a measurement: a stretch of rows with nothing in them says
    /// the agent service was down, not that the agents were cautious.
    /// </summary>
    private static DecisionRecord ToRecord(
        Guid portfolioId, Ticker requested, TradeSignalRequestDto request, Cycle cycle) => new()
        {
            CorrelationId = request.CorrelationId,
            PortfolioId = portfolioId,
            Symbol = requested,

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
