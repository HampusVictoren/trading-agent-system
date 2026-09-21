using System.Net;
using System.Text;
using Engine.Application.Interfaces;
using Engine.Infrastructure.Clients.Agents;
using Polly.Timeout;
using Shouldly;

namespace Engine.Tests.Infrastructure.Clients;

public class PythonAgentClientTests
{
    private const string ValidBody =
        """{"ticker":"AAPL","action":"BUY","amount_usd":400,"confidence":0.8,"reasoning":"because"}""";

    /// <summary>Answers every request with whatever the test decided, without a network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(HttpStatusCode status, string body, string mediaType = "application/json") =>
            _respond = () => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };

        public StubHandler(Exception thrown) => _respond = () => throw thrown;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond());
    }

    private static PythonAgentClient ClientWith(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000") });

    [Fact]
    public async Task Translates_an_answer_that_is_missing_a_contract_field()
    {
        // The DTO refuses it, and the client turns the JsonException into the port's own
        // exception, so the worker reports a broken contract instead of a quiet "No action".
        var client = ClientWith(new StubHandler(
            HttpStatusCode.OK, """{"ticker":"AAPL","confidence":0.8}"""));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            () => client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Returns_the_proposal_when_the_service_honours_the_contract()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, ValidBody));

        var proposal = await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken);

        proposal.ShouldNotBeNull();
        proposal.Action.ShouldBe("BUY");
        proposal.AmountUsd.ShouldBe(400m);
    }

    [Fact]
    public async Task A_failing_status_becomes_an_unavailable_agent_service()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.InternalServerError, "boom", "text/plain"));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resilience_timeout_becomes_an_unavailable_agent_service()
    {
        // The standard resilience handler reports its own timeout as a Polly exception. Without
        // this translation it reaches the worker as an unknown bug, complete with a stack trace.
        var client = ClientWith(new StubHandler(new TimeoutRejectedException("the pipeline gave up")));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_connection_failure_becomes_an_unavailable_agent_service()
    {
        var client = ClientWith(new StubHandler(new HttpRequestException("connection refused")));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_html_answer_becomes_an_invalid_response()
    {
        // A proxy or a stray error page answers 200 with HTML.
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, "<html>Gateway</html>", "text/html"));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            async () => await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Broken_json_becomes_an_invalid_response()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, """{"ticker":"AAPL","""));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            async () => await client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_token_is_not_reported_as_a_failing_service()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, ValidBody));

        var thrown = await Should.ThrowAsync<OperationCanceledException>(
            async () => await client.AnalyzeTickerAsync("AAPL", cts.Token));

        thrown.ShouldNotBeOfType<AgentServiceUnavailableException>();
    }
}
