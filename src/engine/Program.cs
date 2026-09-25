using Engine.Application.UseCases;
using Engine.Domain.Outcomes;
using Engine.Domain.Risk;
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

// RiskEngine holds no state of its own: Evaluate takes the policy, so the limits live in
// one object that ValidateOnStart has already checked.
builder.Services.AddSingleton<RiskEngine>();
builder.Services.AddSingleton<RiskPolicy>(sp =>
    sp.GetRequiredService<IOptions<RiskPolicyOptions>>().Value.ToRiskPolicy());
builder.Services.AddSingleton<PositionSizer>();

// Registered although nothing resolves them yet: the job that measures outcomes arrives in
// the next pull request, and having the options-to-domain mapping in place means a bad
// figure is caught at startup rather than at midnight when the first horizon comes due.
builder.Services.AddSingleton<OutcomePolicy>(sp =>
    sp.GetRequiredService<IOptions<OutcomeOptions>>().Value.ToOutcomePolicy());
builder.Services.AddSingleton<OutcomeCalculator>();

// Injected rather than read from DateTimeOffset.UtcNow, so the quote-age rule is testable
// without waiting for time to pass.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAgentClient();
builder.Services.AddTradingDatabase();

// Polly's telemetry reports a handled timeout at error level. The worker already logs the
// same event in the engine's own words, so demote the library's copy: error level should
// mean something is actually broken.
builder.Services.Configure<TelemetryOptions>(options =>
    options.SeverityProvider = _ => ResilienceEventSeverity.Information);

builder.Services.AddTransient<ProcessProposalUseCase>();
builder.Services.AddTransient<MeasureOutcomesUseCase>();
builder.Services.AddTransient<ReportOutcomesUseCase>();

builder.Services.AddHostedService<TradingWorker>();
builder.Services.AddHostedService<MeasurementWorker>();

var host = builder.Build();

// Before anything runs a cycle: the engine never migrates itself, but it will not start
// against a database that is behind this build.
await host.Services.EnsureTheSchemaIsCurrentAsync();

await host.RunAsync();
