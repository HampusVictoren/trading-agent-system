namespace Engine.Hosting;

using Engine.Application.Interfaces;
using Engine.Hosting.Options;
using Engine.Infrastructure.Clients.Agents;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

public static class AgentClientExtensions
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// Registers the agent client together with the resilience policy it runs under.
    /// It lives here rather than in Program.cs so that the retry rule can be tested:
    /// how many HTTP requests one analysis costs is a decision, not a detail.
    /// </summary>
    public static IServiceCollection AddAgentClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<IAgentClient, PythonAgentClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AgentServiceOptions>>().Value;

                client.BaseAddress = new Uri(options.BaseUrl);

                // The agent service refuses an analysis without this. It is set once here
                // rather than per request, so no code path can forget it.
                client.DefaultRequestHeaders.Add(ApiKeyHeader, options.ApiKey);

                // The resilience pipeline below owns the timeouts. HttpClient's own 100 s default
                // would cut across the whole pipeline and report a less useful cancellation.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                var attempt = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<AgentServiceOptions>>().Value.RequestTimeoutSeconds);

                options.AttemptTimeout.Timeout = attempt;

                // One extra attempt covers a blip. The real retry is the next cycle, and an
                // analysis is expensive enough that hammering a struggling service is worse.
                options.Retry.MaxRetryAttempts = 1;

                // Retry only when the connection never came up. The default rule also retries
                // a failing response, but the agent service answers 5xx *after* spending a
                // cycle's worth of LLM time - about 30 s on a 14B model - so a retry pays
                // twice for nothing, and from stage 4 it would write one logical cycle to the
                // database twice. A timeout is excluded for the same reason: the first
                // request may still be running on the far side.
                options.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError });

                options.TotalRequestTimeout.Timeout = attempt + attempt + TimeSpan.FromSeconds(5);
                options.CircuitBreaker.SamplingDuration = attempt + attempt; // must be at least twice the attempt timeout
            });

        return services;
    }
}
