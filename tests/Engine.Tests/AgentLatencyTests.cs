using System.Net;
using System.Text;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Hosting;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// <c>agent_latency_seconds</c>, through the real registration from <c>Program.cs</c> with a
/// fake transport underneath - so what is timed is what the resilience pipeline makes of a call.
/// </summary>
public class AgentLatencyTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);

    private static readonly TradeSignalRequestDto ASignalRequest = new()
    {
        Instrument = new EquityInstrumentDto { Symbol = "AAPL" },
        TeamId = "default",
        AsOf = AsOf,
        ExistingPosition = null,
        AvailableRiskBudget = 10_000m,
        MaxPositionPct = 0.05m,
        CorrelationId = "cycle-1"
    };

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return respond(request, cancellationToken);
        }

        public static Transport Answering(HttpStatusCode status, string body = "{}") =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }));
    }

    private static (IAgentClient Client, ServiceProvider Provider, MetricCollector<double> Latency) Build(
        Transport transport, int timeoutSeconds = 30)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentService:BaseUrl"] = "http://127.0.0.1:8000",
                ["AgentService:RequestTimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AgentService:ApiKey"] = "a-test-key",
                ["AgentService:OutcomesHmacSecret"] = "a-test-hmac-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEngineOptions(configuration);
        services.AddAgentClient();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => transport));

        var provider = services.BuildServiceProvider();
        var latency = new MetricCollector<double>(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            EngineTelemetry.Name,
            EngineTelemetry.AgentLatencyName);

        return (provider.GetRequiredService<IAgentClient>(), provider, latency);
    }

    [Fact]
    public async Task A_call_that_answers_is_timed_as_a_success()
    {
        var (client, provider, latency) = Build(Transport.Answering(HttpStatusCode.OK, """{"outcomes_received":0}"""));
        using var _ = provider;
        using var __ = latency;

        await client.PostOutcomesAsync(new OutcomeReportDto { Outcomes = [] }, "c-1", TestContext.Current.CancellationToken);

        var measured = latency.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measured.Tags["operation"].ShouldBe(AgentOperation.Outcomes);
        measured.Tags["outcome"].ShouldBe(AgentLatencyHandler.Success);
        measured.Value.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_2xx_is_an_http_error()
    {
        var (client, provider, latency) = Build(Transport.Answering(HttpStatusCode.BadGateway));
        using var _ = provider;
        using var __ = latency;

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetSignalAsync(ASignalRequest, TestContext.Current.CancellationToken));

        var measured = latency.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measured.Tags["operation"].ShouldBe(AgentOperation.Signal);
        measured.Tags["outcome"].ShouldBe(AgentLatencyHandler.HttpError);
    }

    [Fact]
    public async Task A_retried_call_is_one_measurement_because_it_is_timed_outside_the_retry()
    {
        // The connection never came up, so the pipeline tries twice. The engine waited for one
        // call, and that is what the histogram is for: a cycle's length is made of these.
        var transport = new Transport((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var (client, provider, latency) = Build(transport);
        using var _ = provider;
        using var __ = latency;

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetSignalAsync(ASignalRequest, TestContext.Current.CancellationToken));

        transport.Attempts.ShouldBe(2);
        latency.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags["outcome"].ShouldBe(AgentLatencyHandler.Unreachable);
    }

    [Fact]
    public async Task A_call_the_pipeline_gave_up_on_is_a_timeout()
    {
        // One second is the shortest attempt the options allow, and a timeout is not retried.
        var transport = new Transport(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (client, provider, latency) = Build(transport, timeoutSeconds: 1);
        using var _ = provider;
        using var __ = latency;

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetQuoteAsync("AAPL", "c-1", TestContext.Current.CancellationToken));

        var measured = latency.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measured.Tags["operation"].ShouldBe(AgentOperation.Quote);
        measured.Tags["outcome"].ShouldBe(AgentLatencyHandler.Timeout);
        measured.Value.ShouldBeGreaterThanOrEqualTo(0.9);
    }

    [Fact]
    public async Task A_call_cancelled_by_shutdown_is_not_recorded()
    {
        // It says nothing about the agent service, and a timeout-shaped bar at every restart
        // would be noise in exactly the histogram someone reads to see whether it is slow.
        using var shutdown = new CancellationTokenSource();
        var transport = new Transport(async (_, cancellationToken) =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (client, provider, latency) = Build(transport);
        using var _ = provider;
        using var __ = latency;

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.GetSignalAsync(ASignalRequest, shutdown.Token));

        latency.GetMeasurementSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_operation_names_itself_and_none_carries_a_ticker()
    {
        // The label is set by the client, not read off the URL - the path has the ticker in it,
        // and an operation per instrument is the one thing this label must not become.
        var (client, provider, latency) = Build(Transport.Answering(HttpStatusCode.ServiceUnavailable));
        using var _ = provider;
        using var __ = latency;
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<AgentServiceUnavailableException>(() => client.GetSignalAsync(ASignalRequest, ct));
        await Should.ThrowAsync<AgentServiceUnavailableException>(() => client.GetQuoteAsync("AAPL", "c", ct));
        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetHistoryAsync("AAPL", new DateOnly(2026, 9, 1), "c", ct));
        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.PostOutcomesAsync(new OutcomeReportDto { Outcomes = [] }, "c", ct));
        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.GetScreenAsync(
                new ScreenRequestDto
                {
                    Universe = [new EquityInstrumentDto { Symbol = "AAPL" }],
                    Limit = 1,
                    MinDollarVolume = 0m,
                    CorrelationId = "c"
                },
                ct));

        latency.GetMeasurementSnapshot().Select(m => (string)m.Tags["operation"]!).ShouldBe(
            [AgentOperation.Signal, AgentOperation.Quote, AgentOperation.History, AgentOperation.Outcomes, AgentOperation.Screen]);
    }
}
