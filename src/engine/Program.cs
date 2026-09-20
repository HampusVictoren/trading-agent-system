using Engine.Application.UseCases;
using Engine.Domain.Services;
using Engine.Hosting;
using Engine.Hosting.Options;
using Engine.Hosting.Workers;
using Microsoft.Extensions.Options;
using Polly.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

// The agent service's API key is a secret, so it is not in appsettings.json. Locally it
// lives in the user secrets store, outside the repository; in a container it comes from
// the environment, where this line is simply a no-op because the store is not there.
builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddEngineOptions(builder.Configuration);

builder.Services.AddSingleton<RiskEngine>(sp =>
    new RiskEngine(sp.GetRequiredService<IOptions<RiskPolicyOptions>>().Value.MaxPositionPercentage));

builder.Services.AddAgentClient();

// Polly's telemetry reports a handled timeout at error level. The worker already logs the
// same event in the engine's own words, so demote the library's copy: error level should
// mean something is actually broken.
builder.Services.Configure<TelemetryOptions>(options =>
    options.SeverityProvider = _ => ResilienceEventSeverity.Information);

builder.Services.AddTransient<ProcessProposalUseCase>();
builder.Services.AddHostedService<TradingWorker>();

var host = builder.Build();
await host.RunAsync();
