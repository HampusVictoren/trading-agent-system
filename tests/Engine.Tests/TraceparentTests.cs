using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Engine.Application.Interfaces;
using Engine.Hosting;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// The half of "one cycle is one trace" that the engine owns: every call to the agent service
/// carries the cycle's trace id in a W3C <c>traceparent</c> header.
/// </summary>
/// <remarks>
/// Against a real socket, because the header is written by the HTTP stack underneath
/// <c>HttpClient</c> - a fake primary handler would replace exactly the part under test. The
/// registration is <see cref="AgentClientExtensions.AddAgentClient"/>, as in <c>Program.cs</c>.
/// </remarks>
public class TraceparentTests
{
    private sealed class OneShotServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public OneShotServer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }

        public int Port { get; }

        /// <summary>Answers one request with a quote, and returns the headers it arrived with.</summary>
        public async Task<WebHeaderCollection> AnswerOneAsync()
        {
            var context = await _listener.GetContextAsync();
            var headers = new WebHeaderCollection { context.Request.Headers };

            var body = Encoding.UTF8.GetBytes(
                """{"instrument":{"type":"equity","symbol":"AAPL"},"price":100,"currency":"SEK","as_of":"2026-09-24T14:00:00Z"}""");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();

            return headers;
        }

        public void Dispose() => _listener.Close();
    }

    private static ServiceProvider AnAgentClientFor(int port)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentService:BaseUrl"] = $"http://127.0.0.1:{port}",
                ["AgentService:RequestTimeoutSeconds"] = "30",
                ["AgentService:ApiKey"] = "a-test-key",
                ["AgentService:OutcomesHmacSecret"] = "a-test-hmac-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEngineOptions(configuration);
        services.AddAgentClient();
        return services.BuildServiceProvider();
    }

    /// <summary>What the engine's exporter subscribes to when an endpoint is configured.</summary>
    private static ActivityListener ListeningToTheEngine() => new()
    {
        ShouldListenTo = source => source.Name == EngineTelemetry.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public async Task A_call_made_inside_a_cycle_carries_the_cycles_trace_id()
    {
        using var listener = ListeningToTheEngine();
        ActivitySource.AddActivityListener(listener);
        using var server = new OneShotServer();
        await using var provider = AnAgentClientFor(server.Port);
        var client = provider.GetRequiredService<IAgentClient>();

        using var cycle = EngineTelemetry.ActivitySource.StartActivity("a cycle");
        cycle.ShouldNotBeNull();

        var answered = server.AnswerOneAsync();
        await client.GetQuoteAsync("AAPL", "c-1", TestContext.Current.CancellationToken);
        var headers = await answered;

        // version-traceid-parentid-flags. The parent may be the cycle's span or an HTTP span
        // under it, depending on who else listens; the trace is the cycle's either way.
        var traceparent = headers["traceparent"].ShouldNotBeNull();
        var parts = traceparent.Split('-');
        parts.Length.ShouldBe(4);
        parts[0].ShouldBe("00");
        parts[1].ShouldBe(cycle.TraceId.ToHexString());
        parts[3].ShouldBe("01"); // sampled, so the agent service records its half too
    }

    [Fact]
    public async Task The_trace_header_carries_nothing_but_the_trace()
    {
        // No baggage and no tracestate: the context that crosses the wire is ids and a flag, so
        // nothing the engine knows about a decision can ride along with it.
        using var listener = ListeningToTheEngine();
        ActivitySource.AddActivityListener(listener);
        using var server = new OneShotServer();
        await using var provider = AnAgentClientFor(server.Port);
        var client = provider.GetRequiredService<IAgentClient>();

        using var cycle = EngineTelemetry.ActivitySource.StartActivity("a cycle");

        var answered = server.AnswerOneAsync();
        await client.GetQuoteAsync("AAPL", "c-1", TestContext.Current.CancellationToken);
        var headers = await answered;

        headers["baggage"].ShouldBeNull();
        headers["Correlation-Context"].ShouldBeNull();
        headers["tracestate"].ShouldBeNull();
    }
}
