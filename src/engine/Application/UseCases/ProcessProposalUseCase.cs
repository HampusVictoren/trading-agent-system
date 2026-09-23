namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

public class ProcessProposalUseCase
{
    private readonly IAgentClient _agentClient;
    private readonly PositionSizer _sizer;
    private readonly RiskEngine _riskEngine;
    private readonly RiskPolicy _policy;
    private readonly TradingOptions _trading;
    private readonly TimeProvider _clock;

    public ProcessProposalUseCase(
        IAgentClient agentClient,
        PositionSizer sizer,
        RiskEngine riskEngine,
        RiskPolicy policy,
        IOptions<TradingOptions> trading,
        TimeProvider clock)
    {
        _agentClient = agentClient;
        _sizer = sizer;
        _riskEngine = riskEngine;
        _policy = policy;
        _trading = trading.Value;
        _clock = clock;
    }

    /// <summary>
    /// Runs one analysis cycle for a ticker and reports what came of it. Every expected
    /// outcome is returned as a <see cref="TradeDecisionResult"/>; exceptions are left for bugs.
    /// </summary>
    /// <remarks>
    /// The order is deliberate. The agents give a view; the sizer turns it into a quantity;
    /// the risk gate re-derives the same limits from the other direction and can still say
    /// no. Nothing between those steps takes a number from the answer - not the price, and
    /// certainly not an amount.
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
        var now = _clock.GetUtcNow();

        TradeSignalDto? dto;
        try
        {
            dto = await _agentClient.GetSignalAsync(
                BuildRequest(portfolio, requested, now, correlationId), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down, which is not a failing agent service.
        }
        catch (AgentServiceUnavailableException ex)
        {
            return new TradeDecisionResult.AgentUnavailable(requested, ex.Message);
        }
        catch (AgentResponseInvalidException ex)
        {
            return new TradeDecisionResult.InvalidResponse(requested, ex.Message);
        }

        if (dto is null)
            return new TradeDecisionResult.InvalidResponse(requested, "the agent service returned an empty body");

        TradeSignal signal;
        try
        {
            // The seam. Everything past here has been checked, so sizing and risk code
            // never has to ask whether a value makes sense.
            signal = TradeSignalMapper.ToDomain(dto);
        }
        catch (AgentResponseInvalidException ex)
        {
            return new TradeDecisionResult.InvalidResponse(requested, ex.Message);
        }

        // The answer must be about what we asked about. Without this check, a model that
        // replies "TSLA" to a question about AAPL makes the engine buy TSLA.
        if (signal.Instrument is not Instrument.Equity equity || equity.Ticker != requested)
            return new TradeDecisionResult.InvalidResponse(requested, $"the answer is about {Describe(signal.Instrument)}");

        // Separated from sizing so that "the agents did not argue for a buy" stays a
        // different fact from "the buy could not be sized". Selling arrives in stage 5.
        if (signal.Stance != Stance.Buy)
            return new TradeDecisionResult.NoAction(requested, signal.Stance.ToString().ToUpperInvariant());

        // PriceSnapshot.Empty: the engine has no quotes for its other holdings until stage 4
        // adds them, so a portfolio holding something else cannot be valued and the sizer
        // says so. With one ticker configured the signal's own price is all that is needed.
        var intent = _sizer.Size(signal, portfolio, PriceSnapshot.Empty, _policy);

        if (intent is not OrderIntent.Buy order)
            return new TradeDecisionResult.NotSized(requested, ((OrderIntent.None)intent).Reason);

        var decision = _riskEngine.Evaluate(order, signal, portfolio, PriceSnapshot.Empty, _policy, now);

        if (decision is RiskDecision.Rejected rejected)
            return new TradeDecisionResult.RejectedByRisk(requested, rejected.Reason);

        portfolio.ExecuteBuy(requested, order.Quantity, order.Price);
        return new TradeDecisionResult.Executed(requested, order.Quantity, order.Price);
    }

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
            // holding - and those arrive in stage 4. No agent reads this figure anyway; it is
            // sent because the decision record should say what the engine could have spent.
            AvailableRiskBudgetUsd = portfolio.CashBalance.Amount,
            MaxPositionPct = _policy.MaxPositionPct,
            CorrelationId = correlationId
        };
    }

    private static string Describe(Instrument instrument) =>
        instrument is Instrument.Equity equity ? equity.Ticker.Value : instrument.GetType().Name;
}
