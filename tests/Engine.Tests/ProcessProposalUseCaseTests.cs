using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
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
        new(maxPositionPct: 0.05m, cashBufferPct: 0.10m, maxQuoteAge: TimeSpan.FromMinutes(5));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Portfolio NewPortfolio(decimal cash = 10_000m) => new(new Money(cash, "USD"));

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

    private static (ProcessProposalUseCase Sut, IAgentClient Client) Build(
        TradeSignalDto? signal = null, Exception? throws = null)
    {
        var client = Substitute.For<IAgentClient>();

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
            Tickers = [Requested],
            CycleIntervalSeconds = 15,
            TeamId = TeamId
        });

        return (
            new ProcessProposalUseCase(
                client, new PositionSizer(), new RiskEngine(), Policy, options, new FixedClock(Now)),
            client);
    }

    private static Task<TradeDecisionResult> Run(
        ProcessProposalUseCase sut, Portfolio portfolio, string correlationId = "cycle-1") =>
        sut.ExecuteAsync(portfolio, Requested, correlationId, TestContext.Current.CancellationToken);

    public class ABuyThatGoesThrough
    {
        [Fact]
        public async Task Is_executed_at_the_quantity_the_sizer_worked_out()
        {
            // 5 % of a 10 000 USD portfolio is 500; conviction 0.9 is above the full tier,
            // so the whole allowance applies. At 100 USD that is five shares - not the one
            // share the old path always bought.
            var portfolio = NewPortfolio();
            var (sut, _) = Build(Signal());

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
            var (sut, _) = Build(Signal(conviction: 0.6));

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
            var (sut, _) = Build(Signal());

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
            var (sut, _) = Build(Signal(price: 250m));

            var executed = (await Run(sut, portfolio)).ShouldBeOfType<TradeDecisionResult.Executed>();

            executed.Price.Amount.ShouldBe(250m);
            executed.Quantity.ShouldBe(2m);
        }
    }

    public class WhatTheEngineAsks
    {
        private static async Task<TradeSignalRequestDto> Sent(Portfolio portfolio)
        {
            var (sut, client) = Build(Signal());

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
            portfolio.ExecuteBuy(new Ticker(Requested), 3m, new Money(210.4m, "USD"));

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
            var (sut, client) = Build(Signal());

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
        [Theory]
        [InlineData("HOLD")]
        [InlineData("SELL")]
        public async Task A_view_that_is_not_a_buy_is_no_action(string stance)
        {
            // Separate from NotSized on purpose: the agents having no case is a different
            // fact from a case that could not be sized. Selling arrives in stage 5.
            var portfolio = NewPortfolio();

            var result = await Run(Build(Signal(stance: stance)).Sut, portfolio);

            result.ShouldBeOfType<TradeDecisionResult.NoAction>().Action.ShouldBe(stance);
            portfolio.Positions.ShouldBeEmpty();
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
        public async Task A_portfolio_holding_something_the_engine_cannot_price_is_not_sized()
        {
            // The known gap, recorded as a test rather than as a comment. The engine has no
            // quotes for its other holdings until stage 4 adds GET /v1/quotes/{symbol}, so a
            // portfolio holding MSFT cannot be valued - and the sizer says which holding
            // stopped it rather than valuing it at what it cost.
            var portfolio = NewPortfolio();
            portfolio.ExecuteBuy(new Ticker("MSFT"), 2m, new Money(400m, "USD"));

            var result = await Run(Build(Signal()).Sut, portfolio);

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
                () => sut.ExecuteAsync(NewPortfolio(), Requested, "cycle-1", cancelled.Token));
        }
    }
}
