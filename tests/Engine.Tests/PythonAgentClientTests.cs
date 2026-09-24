using System.Net;
using System.Text;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Infrastructure.Clients.Agents;
using Polly.Timeout;
using Shouldly;

namespace Engine.Tests.Infrastructure.Clients;

public class PythonAgentClientTests
{
    private const string ValidBody =
        """
        {"instrument":{"type":"equity","symbol":"AAPL"},"stance":"BUY","conviction":0.78,
         "thesis":"t","key_risks":["r"],"horizon_days":5,"reference_price":233.12,
         "quote_as_of":"2026-09-23T14:03:00Z",
         "run":{"team_id":"default","team_version":"abc123","revisions":0}}
        """;

    private static readonly TradeSignalRequestDto ARequest = new()
    {
        Instrument = new EquityInstrumentDto { Symbol = "AAPL" },
        TeamId = "default",
        AsOf = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
        ExistingPosition = null,
        AvailableRiskBudgetUsd = 10_000m,
        MaxPositionPct = 0.05m,
        CorrelationId = "cycle-1"
    };

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
            HttpStatusCode.OK, """{"stance":"BUY","conviction":0.8}"""));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            () => client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Returns_the_proposal_when_the_service_honours_the_contract()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, ValidBody));

        var signal = await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken);

        signal.ShouldNotBeNull();
        signal.Stance.ShouldBe("BUY");
        signal.ReferencePrice.ShouldBe(233.12m);
        signal.Instrument.ShouldBeOfType<EquityInstrumentDto>().Symbol.ShouldBe("AAPL");
    }

    [Fact]
    public async Task A_failing_status_becomes_an_unavailable_agent_service()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.InternalServerError, "boom", "text/plain"));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resilience_timeout_becomes_an_unavailable_agent_service()
    {
        // The standard resilience handler reports its own timeout as a Polly exception. Without
        // this translation it reaches the worker as an unknown bug, complete with a stack trace.
        var client = ClientWith(new StubHandler(new TimeoutRejectedException("the pipeline gave up")));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_connection_failure_becomes_an_unavailable_agent_service()
    {
        var client = ClientWith(new StubHandler(new HttpRequestException("connection refused")));

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            async () => await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_html_answer_becomes_an_invalid_response()
    {
        // A proxy or a stray error page answers 200 with HTML.
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, "<html>Gateway</html>", "text/html"));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            async () => await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Broken_json_becomes_an_invalid_response()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, """{"ticker":"AAPL","""));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            async () => await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_token_is_not_reported_as_a_failing_service()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, ValidBody));

        var thrown = await Should.ThrowAsync<OperationCanceledException>(
            async () => await client.GetSignalAsync(ARequest, cts.Token));

        thrown.ShouldNotBeOfType<AgentServiceUnavailableException>();
    }

    [Fact]
    public async Task Sends_the_correlation_id_as_a_header_as_well_as_in_the_body()
    {
        // The agent service puts it in every log line it writes, so one cycle can be
        // followed across both services. Set from the request, so the two cannot disagree.
        var recorder = new RecordingHandler();
        var client = new PythonAgentClient(
            new HttpClient(recorder) { BaseAddress = new Uri("http://127.0.0.1:8000") });

        await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken);

        recorder.Seen.ShouldNotBeNull();
        recorder.Seen!.RequestUri!.AbsolutePath.ShouldBe("/v1/signals");
        recorder.Seen.Headers.GetValues(PythonAgentClient.CorrelationIdHeader)
            .Single().ShouldBe("cycle-1");
    }

    [Fact]
    public async Task Sends_the_request_in_the_shape_the_contract_describes()
    {
        // The instrument goes in the body as a typed object, discriminator and all. Nothing
        // is interpolated into the path, which is what closed half of finding B.
        var recorder = new RecordingHandler();
        var client = new PythonAgentClient(
            new HttpClient(recorder) { BaseAddress = new Uri("http://127.0.0.1:8000") });

        await client.GetSignalAsync(ARequest, TestContext.Current.CancellationToken);

        recorder.Body.ShouldContain("\"type\":\"equity\"");
        recorder.Body.ShouldContain("\"symbol\":\"AAPL\"");
        recorder.Body.ShouldContain("\"max_position_pct\":0.05");
        recorder.Body.ShouldContain("\"existing_position\":null");
    }

    /// <summary>Records the request it was given, then answers with whatever it was told to.</summary>
    private sealed class RecordingHandler(string? answer = null) : HttpMessageHandler
    {
        private readonly string _answer = answer ?? ValidBody;

        public HttpRequestMessage? Seen { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_answer, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string ValidQuote =
        """
        {"instrument":{"type":"equity","symbol":"MSFT"},
         "price":415.25,"currency":"USD","as_of":"2026-09-24T18:44:00Z"}
        """;

    [Fact]
    public async Task Returns_a_quote_when_the_service_honours_the_contract()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, ValidQuote));

        var quote = await client.GetQuoteAsync("MSFT", "cycle-1", TestContext.Current.CancellationToken);

        quote.ShouldNotBeNull();
        quote.Price.ShouldBe(415.25m);
        quote.Currency.ShouldBe("USD");
        quote.Instrument.ShouldBeOfType<EquityInstrumentDto>().Symbol.ShouldBe("MSFT");
    }

    [Fact]
    public async Task A_quote_the_service_could_not_give_is_an_unavailable_agent_service()
    {
        // 422 for an unknown symbol, 503 for a market data outage. The engine treats them
        // alike - this holding has no price this cycle - and the status goes in the message.
        var client = ClientWith(new StubHandler(HttpStatusCode.ServiceUnavailable, "{}"));

        var exception = await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetQuoteAsync("MSFT", "cycle-1", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("503");
        exception.Message.ShouldContain("MSFT");
    }

    [Fact]
    public async Task A_quote_that_is_not_the_contract_is_reported_as_invalid()
    {
        var client = ClientWith(new StubHandler(HttpStatusCode.OK, "<html>not json</html>", "text/html"));

        await Should.ThrowAsync<AgentResponseInvalidException>(
            () => client.GetQuoteAsync("MSFT", "cycle-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_symbol_is_escaped_into_the_path()
    {
        // Ticker has already refused anything that is not a symbol, and the agent service
        // checks the pattern again before it looks anything up. Escaping here means neither
        // of those is the only thing standing between a value and a URL - which is the half
        // of finding B that was about the engine.
        var recorder = new RecordingHandler(ValidQuote);
        var client = ClientWith(recorder);

        await client.GetQuoteAsync("../internal/shutdown", "cycle-1", TestContext.Current.CancellationToken);

        recorder.Seen!.RequestUri!.AbsolutePath.ShouldBe("/v1/quotes/..%2Finternal%2Fshutdown");
    }

    [Fact]
    public async Task A_quote_carries_the_cycles_correlation_id()
    {
        // Without it, the agent service's line about this quote sits under an id that
        // nothing else in either log mentions - and a decision that cannot be traced back to
        // the prices it was made on is a decision stage 4 cannot explain.
        var recorder = new RecordingHandler(ValidQuote);
        var client = ClientWith(recorder);

        await client.GetQuoteAsync("MSFT", "cycle-7", TestContext.Current.CancellationToken);

        recorder.Seen!.Headers.GetValues(PythonAgentClient.CorrelationIdHeader).ShouldBe(["cycle-7"]);
    }
}
