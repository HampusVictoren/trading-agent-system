using System.Net;
using System.Text;
using Engine.Application.Interfaces;
using Engine.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// How many HTTP requests one analysis costs is a decision, not a detail. An analysis is
/// 12-15 s of LLM time, so a retry on a failing *response* pays for that work twice - and
/// from stage 4 it would write one logical cycle to the database twice.
/// </summary>
public class AgentClientResilienceTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public CountingHandler(HttpStatusCode status) =>
            _respond = () => new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error_code":"llm_failed","correlation_id":"x"}""", Encoding.UTF8, "application/json")
            };

        public CountingHandler(Exception thrown) => _respond = () => throw thrown;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(_respond());
        }
    }

    /// <summary>Builds the real registration from Program.cs, with a counting handler underneath.</summary>
    private static (IAgentClient Client, ServiceProvider Provider) Build(CountingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentService:BaseUrl"] = "http://127.0.0.1:8000",
                ["AgentService:RequestTimeoutSeconds"] = "30",
                ["RiskPolicy:MaxPositionPercentage"] = "0.05",
                ["Trading:Tickers:0"] = "AAPL",
                ["Trading:CycleIntervalSeconds"] = "15",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEngineOptions(configuration);
        services.AddAgentClient();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IAgentClient>(), provider);
    }

    [Fact]
    public async Task A_failing_response_is_not_retried()
    {
        var handler = new CountingHandler(HttpStatusCode.BadGateway);
        var (client, provider) = Build(handler);
        using var _ = provider;

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));

        handler.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_response_names_its_status_code()
    {
        var handler = new CountingHandler(HttpStatusCode.GatewayTimeout);
        var (client, provider) = Build(handler);
        using var _ = provider;

        var exception = await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("504");
    }

    [Fact]
    public async Task A_connection_that_never_came_up_is_retried_once()
    {
        // Nothing ran on the far side, so trying again is free of the double-work problem.
        var handler = new CountingHandler(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var (client, provider) = Build(handler);
        using var _ = provider;

        await Should.ThrowAsync<AgentServiceUnavailableException>(
            () => client.AnalyzeTickerAsync("AAPL", TestContext.Current.CancellationToken));

        handler.Attempts.ShouldBe(2);
    }
}
