using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Services;
using Engine.Hosting;
using Engine.Hosting.Options;
using Engine.Hosting.Workers;
using Engine.Infrastructure.Clients.Agents;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddEngineOptions(builder.Configuration);

builder.Services.AddSingleton<RiskEngine>(sp =>
    new RiskEngine(sp.GetRequiredService<IOptions<RiskPolicyOptions>>().Value.MaxPositionPercentage));

builder.Services
    .AddHttpClient<IAgentClient, PythonAgentClient>((sp, client) =>
    {
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AgentServiceOptions>>().Value.BaseUrl);

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

        options.TotalRequestTimeout.Timeout = attempt + attempt + TimeSpan.FromSeconds(5);
        options.CircuitBreaker.SamplingDuration = attempt + attempt; // must be at least twice the attempt timeout
    });

// Polly's telemetry reports a handled timeout at error level. The worker already logs the
// same event in the engine's own words, so demote the library's copy: error level should
// mean something is actually broken.
builder.Services.Configure<TelemetryOptions>(options =>
    options.SeverityProvider = _ => ResilienceEventSeverity.Information);

builder.Services.AddTransient<ProcessProposalUseCase>();
builder.Services.AddHostedService<TradingWorker>();

var host = builder.Build();
await host.RunAsync();
