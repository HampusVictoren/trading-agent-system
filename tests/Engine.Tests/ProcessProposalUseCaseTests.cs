using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

/// <summary>
/// One cycle on the new contract, end to end inside the engine. The agents give a view, the
/// sizer turns it into a quantity, and the risk gate can still refuse it - and every step
/// between them is an outcome rather than an exception.
/// </summary>
public class ProcessProposalUseCaseTests
{
    private const string Requested = "AAPL";
    private const string TeamId = "default";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);

    private static readonly RiskPolicy Policy =
        new(maxPositionPct: 0.05m, cashBufferPct: 0.10m, maxQuoteAge: TimeSpan.FromMinutes(5),
            minHoldingPeriod: TimeSpan.FromDays(3),
        stopLossPct: 0.10m,
        maxDailyDeploymentPct: 1m);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Stands in for the database. The use case queues rows; the worker commits them.</summary>
    private sealed class CapturedDecisions : IDecisionLog
    {
        private readonly List<DecisionRecord> _records = [];

        public void Record(DecisionRecord decision) => _records.Add(decision);

        /// <summary>Not this type's business. Whether an analysis is due is decided above the use
        /// case, precisely so that a skip leaves no row here.</summary>
        public Task<LastAnalysis?> LastAnalysisOfAsync(
            Ticker symbol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The use case does not decide whether it should run.");

        /// <summary>The one row a cycle must produce. Failing here means a cycle wrote none,
        /// or wrote two.</summary>
        public DecisionRecord OfTheCycle => _records.ShouldHaveSingleItem();
    }

    /// <summary>What the worker hands in: the instrument, and why it is being analysed.</summary>
    private static readonly InstrumentSelection AShortlistPick =
        new(new Ticker(Requested), SelectionSource.Shortlist);

    private static Portfolio NewPortfolio(decimal cash = 10_000m) => new(new Money(cash, Money.DefaultCurrency));

    /// <summary>
    /// A portfolio already holding the instrument under analysis, bought <paramref name="daysAgo"/>
    /// before <see cref="Now"/> - seven days by default, which is past the minimum holding period.
    /// </summary>
    private static Portfolio Holding(decimal quantity = 10m, decimal at = 90m, int daysAgo = 7)
    {
        var portfolio = NewPortfolio();

        portfolio.ExecuteBuy(
            new Ticker(Requested), quantity, new Money(at, Money.DefaultCurrency),
            Now.AddDays(-daysAgo), horizonDays: 15);

        return portfolio;
    }

    private static TradeSignalDto Signal(
        string stance = "BUY",
        double conviction = 0.9,
        decimal price = 100m,
        string symbol = Requested,
        DateTimeOffset? quoteAsOf = null) =>
        new()
        {
            Instrument = new EquityInstrumentDto { Symbol = symbol },
            Stance = stance,
            Conviction = conviction,
            Thesis = "a thesis",
            KeyRisks = ["a risk"],
            HorizonDays = 5,
            ReferencePrice = price,
            QuoteAsOf = quoteAsOf ?? Now,
            Run = new RunDto { TeamId = TeamId, TeamVersion = "abc123", Revisions = 0 }
        };

    private static QuoteDto Quote(string symbol, decimal price, DateTimeOffset? asOf = null) => new()
    {
        Instrument = new EquityInstrumentDto { Symbol = symbol },
        Price = price,
        Currency = Money.DefaultCurrency,
        AsOf = asOf ?? Now
    };

    /// <param name="deployedToday">
    /// What the account has already spent on purchases today, as the repository would answer it.
    /// Zero unless a test is about the trading day's own budget.
    /// </param>
    private static (ProcessProposalUseCase Sut, IAgentClient Client, CapturedDecisions Decisions) Build(
        TradeSignalDto? signal = null,
        Exception? throws = null,
        QuoteDto? quote = null,
        decimal deployedToday = 0m)
    {
        var client = Substitute.For<IAgentClient>();
        var decisions = new CapturedDecisions();

        var portfolios = Substitute.For<IPortfolioRepository>();
        portfolios.DeployedOnAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Money(deployedToday, Money.DefaultCurrency));

        // Null unless a test says otherwise, which is what "the engine could not get a price
        // for that holding" looks like from here.
        client.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(quote);

        if (throws is not null)
        {
            client.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(throws);
        }
        else
        {
            client.GetSignalAsync(Arg.Any<TradeSignalRequestDto>(), Arg.Any<CancellationToken>())
                .Returns(signal);
        }

        var options = Options.Create(new TradingOptions
        {
            Universe = [Requested],
            ShortlistSize = 10,
            MinDollarVolume = 10_000_000m,
            CycleIntervalMinutes = 15,
            TeamId = TeamId
        });

        var quotes = new QuoteReader(client, Policy, NullLogger<QuoteReader>.Instance);

        return (
            new ProcessProposalUseCase(
                client, portfolios, quotes, decisions, new PositionSizer(), new RiskEngine(), Policy, options,
                new FixedClock(Now)),
            client,
            decisions);
    }

    private static Task<TradeDecisionResult> Run(
        ProcessProposalUseCase sut, Portfolio portfolio, string correlationId = "cycle-1") =>
        sut.ExecuteAsync(portfolio, AShortlistPick, correlationId, TestContext.Current.CancellationToken);

    public class ABuyThatGoesThrough
    {
        [Fact]
        public async Task Is_executed_at_the_quantity_the_sizer_worked_out()
        {
            // 5 % of a 10 000 USD portfolio is 500; conviction 0.9 is above the full tier,
            // so the whole allowance applies. At 100 USD that is five shares - not the one
            // share the old path always bought.
            var portfolio = NewPortfolio();
            var (sut, _, _) = Build(Signal());

            var result = await Run(sut, portfolio);

            var executed = result.ShouldBeOfType<TradeDecisionResult.Executed>();
            executed.Quantity.ShouldBe(5m);
            executed.Price.Amount.ShouldBe(100m);
            portfolio.CashBalance.Amount.ShouldBe(9_500m);
        }

        [Fact]
        public async Task Adds_to_a_position_that_already_exists()
        {
            // Conviction 0.6 is the middle tier, so each cycle uses half the remaining
            // headroom: two shares, then one more.
            var portfolio = NewPortfolio();
            var (sut, _, _) = Build(Signal(conviction: 0.6));

            await Run(sut, portfolio);
            var second = await Run(sut, portfolio);

            second.ShouldBeOfType<TradeDecisionResult.Executed>().Quantity.ShouldBe(1m);
            portfolio.Positions.Single().Quantity.ShouldBe(3m);
        }

        [Fact]
        public async Task Stops_at_the_position_limit_across_cycles()
        {
            // The first cycle takes the whole 5 % allowance, so the second has no headroom
            // left. The old path had no such limit: it bought one share per cycle for ever,
            // because it measured against cash and never against the position.
            var portfolio = NewPortfolio();
            var (sut, _, _) = Build(Signal());

            await Run(sut, portfolio);
            var second = await Run(sut, portfolio);

            second.ShouldBeOfType<TradeDecisionResult.NotSized>();
            portfolio.Positions.Single().Quantity.ShouldBe(5m);
        }

        [Fact]
        public async Task Prices_the_order_from_the_signal_rather_than_from_anywhere_else()
        {
            // reference_price comes from the fact sheet on the other side, never from a
            // model. This is the engine end of that: the order is priced from it directly.
            var portfolio = NewPortfolio();
            var (sut, _, _) = Build(Signal(price: 250m));

            var executed = (await Run(sut, portfolio)).ShouldBeOfType<TradeDecisionResult.Executed>();

            executed.Price.Amount.ShouldBe(250m);
            executed.Quantity.ShouldBe(2m);
        }
    }

    public class ASellThatGoesThrough
    {
        [Fact]
        public async Task Sells_the_whole_holding_at_full_conviction()
        {
            // Ten shares bought at 90, sold at the signal's 100. The tier scales the holding
            // here rather than a budget, which is the same number meaning the same thing in the
            // other direction.
            var portfolio = Holding();
            var (sut, _, _) = Build(Signal(stance: "SELL"));

            var executed = (await Run(sut, portfolio)).ShouldBeOfType<TradeDecisionResult.Executed>();

            executed.Side.ShouldBe(OrderSide.Sell);
            executed.Quantity.ShouldBe(10m);
            portfolio.Positions.ShouldBeEmpty();

            // 10 000 less 900 spent, plus 1 000 raised.
            portfolio.CashBalance.Amount.ShouldBe(10_100m);
        }

        [Fact]
        public async Task Sells_half_of_it_at_the_middle_tier()
        {
            var portfolio = Holding();
            var (sut, _, _) = Build(Signal(stance: "SELL", conviction: 0.6));

            (await Run(sut, portfolio)).ShouldBeOfType<TradeDecisionResult.Executed>().Quantity.ShouldBe(5m);

            portfolio.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(5m);
        }

        [Fact]
        public async Task A_holding_bought_yesterday_is_rejected_by_risk()
        {
            // Not NotSized: the sale was sized perfectly well and a portfolio rule refused it.
            // This is the churn guard, and the exits are exempt from it by carrying a trigger.
            var portfolio = Holding(daysAgo: 1);
            var (sut, _, _) = Build(Signal(stance: "SELL"));

            var result = await Run(sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.RejectedByRisk>()
                .Reason.ShouldContain("minimum holding period");

            portfolio.Positions.ShouldHaveSingleItem().Quantity.ShouldBe(10m);
        }

        [Fact]
        public async Task Asks_for_no_quotes_at_all()
        {
            // A sale needs no net asset value, so a SELL cycle makes no quote calls. That is not
            // only saved work: it is what keeps a holding the engine cannot price from standing
            // between the agents and a position they have argued should be closed. MSFT here is
            // exactly the holding a buy would have had to fetch.
            var portfolio = Holding();
            portfolio.ExecuteBuy(
                new Ticker("MSFT"), quantity: 1m, new Money(400m, Money.DefaultCurrency),
                Now.AddDays(-7), horizonDays: 15);

            var (sut, client, _) = Build(Signal(stance: "SELL"));

            await Run(sut, portfolio);

            await client.DidNotReceive().GetQuoteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Is_recorded_as_executed_with_the_stance_that_asked_for_it()
        {
            // No side column in trading.decisions: the stance says which way, and order_id
            // points at the ledger line that records the side as a fact about what was done.
            var portfolio = Holding();
            var (sut, _, decisions) = Build(Signal(stance: "SELL"));

            await Run(sut, portfolio);

            var row = decisions.OfTheCycle;
            row.Outcome.ShouldBe(DecisionOutcome.Executed);
            row.Stance.ShouldBe(Stance.Sell);
            row.OrderId.ShouldNotBeNull();
        }
    }

    public class WhatTheEngineAsks
    {
        private static async Task<TradeSignalRequestDto> Sent(Portfolio portfolio)
        {
            var (sut, client, _) = Build(Signal());

            await Run(sut, portfolio);

            return client.ReceivedCalls()
                .Select(call => call.GetArguments()[0])
                .OfType<TradeSignalRequestDto>()
                .Single();
        }

        [Fact]
        public async Task Carries_the_configured_team_and_the_policy_limit()
        {
            // The team is configuration, which is what makes the experiment cycle an
            // experiment: change the team, let it run, compare outcomes in stage 4.
            var request = await Sent(NewPortfolio());

            request.TeamId.ShouldBe(TeamId);
            request.MaxPositionPct.ShouldBe(0.05m);
            request.Instrument.ShouldBeOfType<EquityInstrumentDto>().Symbol.ShouldBe(Requested);
        }

        [Fact]
        public async Task Says_there_is_no_position_rather_than_leaving_it_out()
        {
            (await Sent(NewPortfolio())).ExistingPosition.ShouldBeNull();
        }

        [Fact]
        public async Task Carries_the_holding_when_there_is_one()
        {
            // Adding to a position is a different question from opening one, and the
            // portfolio manager is the only step that is told.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker(Requested), 3m, new Money(210.4m, Money.DefaultCurrency));

            var position = (await Sent(portfolio)).ExistingPosition.ShouldNotBeNull();

            position.Quantity.ShouldBe(3m);
            position.AveragePrice.ShouldBe(210.4m);
        }

        [Fact]
        public async Task Carries_the_cycles_correlation_id()
        {
            // The worker generates it and logs it before the call, so a line in the
            // engine's log can be found in the agent service's - which echoes it and puts
            // it in every line it writes while handling the request.
            var (sut, client, _) = Build(Signal());

            await Run(sut, NewPortfolio(), correlationId: "cycle-42");

            client.ReceivedCalls()
                .Select(call => call.GetArguments()[0])
                .OfType<TradeSignalRequestDto>()
                .Single()
                .CorrelationId.ShouldBe("cycle-42");
        }
    }

    public class WhenNothingIsBought
    {
        [Fact]
        public async Task A_hold_is_no_action()
        {
            // Separate from NotSized on purpose: the agents having no case is a different fact
            // from a case that could not be sized. HOLD is now the only stance that stops here,
            // and it is the one they answer most often.
            var portfolio = NewPortfolio();

            var result = await Run(Build(Signal(stance: "HOLD")).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.NoAction>().Action.ShouldBe("HOLD");
            portfolio.Positions.ShouldBeEmpty();
        }

        [Fact]
        public async Task A_sell_of_something_that_is_not_held_is_not_sized()
        {
            // Not NoAction: the agents did make a case, and the portfolio is the reason nothing
            // happened. Telling those apart is what the two outcomes are for - one says
            // something about the team and the other about what it was asked to act on.
            var portfolio = NewPortfolio();

            var result = await Run(Build(Signal(stance: "SELL")).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.NotSized>().Reason.ShouldContain("nothing is held");
        }

        [Fact]
        public async Task A_conviction_below_the_floor_is_not_sized_rather_than_rejected()
        {
            // The distinction stage 4 needs: this says something about the team, while a
            // risk rejection says something about the portfolio.
            var result = await Run(Build(Signal(conviction: 0.2)).Sut, NewPortfolio());

            result.ShouldBeOfType<TradeDecisionResult.NotSized>()
                .Reason.ShouldContain("below the floor");
        }

        [Fact]
        public async Task A_budget_that_does_not_reach_one_share_is_not_sized()
        {
            // 5 % of 10 000 is 500, and one share costs 900.
            var result = await Run(Build(Signal(price: 900m)).Sut, NewPortfolio());

            result.ShouldBeOfType<TradeDecisionResult.NotSized>()
                .Reason.ShouldContain("does not reach one share");
        }

        [Fact]
        public async Task A_stale_quote_is_rejected_by_the_risk_gate()
        {
            // Past the five-minute limit. The sizer cannot see this; the gate can.
            var stale = Now.AddMinutes(-10);

            var result = await Run(Build(Signal(quoteAsOf: stale)).Sut, NewPortfolio());

            result.ShouldBeOfType<TradeDecisionResult.RejectedByRisk>()
                .Reason.ShouldContain("old");
        }

        [Fact]
        public async Task A_portfolio_is_valued_from_quotes_for_the_holdings_it_is_not_analysing()
        {
            // 2 MSFT at 500 plus 9 200 in cash is 10 200, so 5 % is 510 and conviction 0.9
            // takes the whole allowance: five shares at 100. Sizing against cash alone would
            // have given a different number, and sizing on what MSFT cost would have given a
            // third - which is the point of asking.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker("MSFT"), 2m, new Money(400m, Money.DefaultCurrency));

            var (sut, client, _) = Build(Signal(), quote: Quote("MSFT", 500m));

            var result = await Run(sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.Executed>().Quantity.ShouldBe(5m);
            await client.Received(1).GetQuoteAsync("MSFT", "cycle-1", Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task The_instrument_being_analysed_is_never_quoted_separately()
        {
            // Its price comes from the signal, so an order is never sized against a quote the
            // agents never saw - and asking for it again would be a second price for the same
            // decision.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker(Requested), 1m, new Money(100m, Money.DefaultCurrency));

            var (sut, client, _) = Build(Signal());

            await Run(sut, portfolio);

            await client.DidNotReceive().GetQuoteAsync(Requested, Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_holding_the_engine_cannot_get_a_price_for_is_not_sized()
        {
            // The quote endpoint answered 503, or with something that was not the contract.
            // The portfolio cannot be valued, and the sizer names the holding that stopped it
            // rather than valuing it at what it cost - which would overstate a loser and raise
            // the allowance for everything else at exactly the wrong moment.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker("MSFT"), 2m, new Money(400m, Money.DefaultCurrency));

            var result = await Run(Build(Signal(), quote: null).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.NotSized>()
                .Reason.ShouldContain("no price for MSFT");
        }

        [Fact]
        public async Task A_holding_whose_quote_is_too_old_is_treated_as_having_none()
        {
            // A valuation on a stale price is worse than one that could not be made: the
            // position limit is a share of the portfolio's value, so an out-of-date holding
            // moves the allowance for everything else.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker("MSFT"), 2m, new Money(400m, Money.DefaultCurrency));

            var stale = Quote("MSFT", 500m, asOf: Now.AddMinutes(-10));
            var result = await Run(Build(Signal(), quote: stale).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.NotSized>()
                .Reason.ShouldContain("no price for MSFT");
        }
    }

    public class WhenTheAnswerCannotBeUsed
    {
        [Fact]
        public async Task An_answer_about_another_instrument_is_refused()
        {
            // Without this, a model that replies TSLA to a question about AAPL makes the
            // engine buy TSLA.
            var portfolio = NewPortfolio();

            var result = await Run(Build(Signal(symbol: "TSLA")).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>().Reason.ShouldContain("TSLA");
            portfolio.Positions.ShouldBeEmpty();
        }

        [Fact]
        public async Task A_value_that_makes_no_sense_is_refused_by_the_mapper()
        {
            var result = await Run(Build(Signal(stance: "MAYBE")).Sut, NewPortfolio());

            result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>().Reason.ShouldContain("MAYBE");
        }

        [Fact]
        public async Task An_empty_body_is_refused()
        {
            var result = await Run(Build(signal: null).Sut, NewPortfolio());

            result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
        }

        [Fact]
        public async Task An_unreachable_service_is_reported_as_unavailable()
        {
            var sut = Build(throws: new AgentServiceUnavailableException("no answer")).Sut;

            (await Run(sut, NewPortfolio())).ShouldBeOfType<TradeDecisionResult.AgentUnavailable>();
        }

        [Fact]
        public async Task A_body_that_is_not_the_contract_is_reported_as_invalid()
        {
            var sut = Build(throws: new AgentResponseInvalidException("not JSON")).Sut;

            (await Run(sut, NewPortfolio())).ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
        }

        [Fact]
        public async Task Shutting_down_is_not_a_failing_agent_service()
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var sut = Build(throws: new OperationCanceledException()).Sut;

            await Should.ThrowAsync<OperationCanceledException>(
                () => sut.ExecuteAsync(NewPortfolio(), AShortlistPick, "cycle-1", cancelled.Token));
        }
    }

    /// <summary>
    /// What the cycle leaves behind. Stage 4 measures agents from these rows, so a column that
    /// is wrong here is a conclusion that is wrong later - and by then nothing says so.
    /// </summary>
    public class TheDecisionThatIsRecorded
    {
        [Fact]
        public async Task Says_what_was_asked_and_what_the_engine_was_willing_to_spend()
        {
            var portfolio = NewPortfolio();
            var (sut, _, decisions) = Build(Signal());

            await Run(sut, portfolio);

            var recorded = decisions.OfTheCycle;
            recorded.CorrelationId.ShouldBe("cycle-1");
            recorded.PortfolioId.ShouldBe(portfolio.Id);
            recorded.Symbol.Value.ShouldBe(Requested);
            recorded.TeamId.ShouldBe(TeamId);
            recorded.RequestedAt.ShouldBe(Now);

            // The room the decision was made in. No agent reads either figure, but a decision
            // is only interpretable next to the limits it was made under.
            recorded.AvailableRiskBudget.ShouldBe(10_000m);
            recorded.MaxPositionPct.ShouldBe(0.05m);
            recorded.ExistingQuantity.ShouldBeNull();
        }

        [Fact]
        public async Task Carries_the_answer_and_the_order_it_led_to()
        {
            var portfolio = NewPortfolio();
            var (sut, _, decisions) = Build(Signal());

            var result = await Run(sut, portfolio);

            var recorded = decisions.OfTheCycle;
            recorded.Outcome.ShouldBe(DecisionOutcome.Executed);
            recorded.OutcomeReason.ShouldBeNull();
            recorded.TeamVersion.ShouldBe("abc123");
            recorded.Revisions.ShouldBe(0);
            recorded.Stance.ShouldBe(Engine.Domain.Signals.Stance.Buy);
            recorded.Conviction.ShouldBe(0.9);
            recorded.Thesis.ShouldBe("a thesis");
            recorded.KeyRisks.ShouldBe(["a risk"]);
            recorded.HorizonDays.ShouldBe(5);
            recorded.ReferencePrice.ShouldBe(100m);
            recorded.ReferenceCurrency.ShouldBe(Money.DefaultCurrency);
            recorded.QuoteAsOf.ShouldBe(Now);

            // The ledger line and the reasoning that produced it, joined.
            result.ShouldBeOfType<TradeDecisionResult.Executed>();
            recorded.OrderId.ShouldNotBeNull();
            recorded.OrderId.ShouldBe(portfolio.NewOrders.ShouldHaveSingleItem().Id);
        }

        [Fact]
        public async Task Holds_the_answer_even_when_nothing_was_bought()
        {
            // Measuring only the buys that went through measures the wrong population, which
            // is the whole reason HOLD has to arrive with its conviction intact.
            var (sut, _, decisions) = Build(Signal(stance: "HOLD", conviction: 0.3));

            await Run(sut, NewPortfolio());

            var recorded = decisions.OfTheCycle;
            recorded.Outcome.ShouldBe(DecisionOutcome.NoAction);
            recorded.OutcomeReason.ShouldBe("HOLD");
            recorded.Stance.ShouldBe(Engine.Domain.Signals.Stance.Hold);
            recorded.Conviction.ShouldBe(0.3);
            recorded.OrderId.ShouldBeNull();
        }

        [Fact]
        public async Task Keeps_a_risk_rejection_apart_from_a_sizing_one()
        {
            // The two say different things - one about the portfolio, one about the team -
            // and a report that pooled them would hide which was happening.
            var (sut, _, decisions) = Build(Signal(conviction: 0.1));

            await Run(sut, NewPortfolio());

            decisions.OfTheCycle.Outcome.ShouldBe(DecisionOutcome.NotSized);
            decisions.OfTheCycle.OutcomeReason.ShouldNotBeNullOrEmpty();
            decisions.OfTheCycle.Stance.ShouldBe(Engine.Domain.Signals.Stance.Buy);
        }

        [Fact]
        public async Task Records_a_cycle_that_never_reached_an_answer()
        {
            // A row of nulls is how the measurement sees "we could not ask". Leaving it out
            // would make the agent service look more reliable the worse it got.
            var (sut, _, decisions) = Build(throws: new AgentServiceUnavailableException("no answer"));

            await Run(sut, NewPortfolio());

            var recorded = decisions.OfTheCycle;
            recorded.Outcome.ShouldBe(DecisionOutcome.AgentUnavailable);
            recorded.OutcomeReason.ShouldBe("no answer");
            recorded.Stance.ShouldBeNull();
            recorded.TeamVersion.ShouldBeNull();
            recorded.ReferencePrice.ShouldBeNull();
            recorded.KeyRisks.ShouldBeEmpty();
        }

        [Fact]
        public async Task Stores_nothing_from_an_answer_that_was_not_the_contract()
        {
            // The answer named another instrument, so none of its numbers were checked
            // against anything. Storing them typed as data would make garbage look measured.
            var (sut, _, decisions) = Build(Signal(symbol: "TSLA"));

            await Run(sut, NewPortfolio());

            var recorded = decisions.OfTheCycle;
            recorded.Symbol.Value.ShouldBe(Requested);
            recorded.Outcome.ShouldBe(DecisionOutcome.InvalidResponse);
            recorded.OutcomeReason.ShouldNotBeNull().ShouldContain("TSLA");
        }

        [Fact]
        public async Task Happens_once_per_cycle_whatever_the_outcome()
        {
            // The row is written outside the decision precisely so that none of its early
            // returns can skip it. OfTheCycle fails if a cycle wrote none, or wrote two.
            foreach (var signal in new[] { Signal(), Signal(stance: "SELL"), Signal(conviction: 0.1) })
            {
                var (sut, _, decisions) = Build(signal);
                await Run(sut, NewPortfolio());
                _ = decisions.OfTheCycle;
            }
        }
    }
}
