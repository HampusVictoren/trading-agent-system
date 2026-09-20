using System.Text.Json;
using Engine.Application.Dtos;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Services;
using Engine.Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

public class ProcessProposalUseCaseTests
{
    private const string Requested = "AAPL";

    private static Portfolio NewPortfolio(decimal cash = 10_000m) => new(new Money(cash, "USD"));

    private static InvestmentProposalDto Proposal(string action, decimal amount, string ticker = Requested) =>
        new(ticker, action, amount, 0.8, "reasoning");

    private static ProcessProposalUseCase SutReturning(InvestmentProposalDto? proposal)
    {
        var client = Substitute.For<IAgentClient>();
        client.AnalyzeTickerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(proposal);
        return new ProcessProposalUseCase(client, new RiskEngine());
    }

    private static ProcessProposalUseCase SutThrowing(Exception exception)
    {
        var client = Substitute.For<IAgentClient>();
        client.AnalyzeTickerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(exception);
        return new ProcessProposalUseCase(client, new RiskEngine());
    }

    [Fact]
    public async Task Execute_buys_when_the_proposal_is_within_the_risk_limit()
    {
        var portfolio = NewPortfolio();

        var result = await SutReturning(Proposal("BUY", 400m)).ExecuteAsync(portfolio, Requested, TestContext.Current.CancellationToken);

        var executed = result.ShouldBeOfType<TradeDecisionResult.Executed>();
        executed.Ticker.ShouldBe(new Ticker(Requested));
        portfolio.CashBalance.Amount.ShouldBe(9_600m);
    }

    [Fact]
    public async Task Execute_returns_a_risk_rejection_instead_of_throwing()
    {
        // 1000 USD exceeds the 5 % limit on a 10 000 USD portfolio. That is an expected
        // business outcome, so it comes back as a result; only bugs throw.
        var portfolio = NewPortfolio();

        var result = await SutReturning(Proposal("BUY", 1_000m)).ExecuteAsync(portfolio, Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.RejectedByRisk>();
        portfolio.CashBalance.Amount.ShouldBe(10_000m);
        portfolio.Positions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("HOLD")]
    [InlineData("SELL")] // Selling arrives in stage 5; until then it is simply no action.
    public async Task Execute_takes_no_action_when_the_agents_do_not_propose_a_buy(string action)
    {
        var portfolio = NewPortfolio();

        var result = await SutReturning(Proposal(action, 0m)).ExecuteAsync(portfolio, Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.NoAction>().Action.ShouldBe(action);
        portfolio.CashBalance.Amount.ShouldBe(10_000m);
    }

    [Fact]
    public async Task Execute_refuses_an_answer_about_another_instrument()
    {
        // The known bug: the Ticker was built from the answer, so an answer about TSLA to a
        // question about AAPL bought TSLA. Security item 5 in the roadmap.
        var portfolio = NewPortfolio();

        var result = await SutReturning(Proposal("BUY", 400m, ticker: "TSLA")).ExecuteAsync(portfolio, Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
        portfolio.CashBalance.Amount.ShouldBe(10_000m);
        portfolio.Positions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Execute_refuses_an_answer_without_a_ticker(string ticker)
    {
        var result = await SutReturning(Proposal("BUY", 400m, ticker)).ExecuteAsync(NewPortfolio(), Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
    }

    [Fact]
    public async Task Execute_refuses_an_empty_body()
    {
        var result = await SutReturning(null).ExecuteAsync(NewPortfolio(), Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
    }

    [Fact]
    public async Task Execute_reports_malformed_json_as_an_invalid_response()
    {
        // The client translates the transport's own exceptions; this layer only maps them.
        var invalid = new AgentResponseInvalidException("not the agreed JSON", new JsonException("unexpected token"));
        var result = await SutThrowing(invalid).ExecuteAsync(NewPortfolio(), Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.InvalidResponse>();
    }

    [Fact]
    public async Task Execute_reports_a_connection_failure_as_an_unavailable_agent_service()
    {
        var unavailable = new AgentServiceUnavailableException("no answer", new HttpRequestException("connection refused"));
        var result = await SutThrowing(unavailable).ExecuteAsync(NewPortfolio(), Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.AgentUnavailable>();
    }

    [Fact]
    public async Task Execute_reports_a_timeout_as_an_unavailable_agent_service()
    {
        // Whatever timed out - HttpClient or the resilience pipeline - the client reports it as one thing.
        var unavailable = new AgentServiceUnavailableException("no answer in time", new TaskCanceledException("timeout"));
        var result = await SutThrowing(unavailable).ExecuteAsync(NewPortfolio(), Requested, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<TradeDecisionResult.AgentUnavailable>();
    }

    [Fact]
    public async Task Execute_lets_a_shutdown_cancel_the_cycle()
    {
        // Shutdown is not a failing agent service: the cancellation must reach the worker.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = SutThrowing(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await sut.ExecuteAsync(NewPortfolio(), Requested, cts.Token));
    }
}
