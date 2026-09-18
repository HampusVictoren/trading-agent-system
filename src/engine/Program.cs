using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Services;
using Engine.Infrastructure.Clients.Agents;
using Engine.Hosting.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<RiskEngine>(sp => new RiskEngine(maxPositionPercentage: 0.05m));

builder.Services.AddHttpClient<IAgentClient, PythonAgentClient>(client =>
{
    var baseUrl = builder.Configuration["AgentService:BaseUrl"] ?? "http://127.0.0.1:8000";
    client.BaseAddress = new Uri(baseUrl);
});

builder.Services.AddTransient<ProcessProposalUseCase>();
builder.Services.AddHostedService<TradingWorker>();

var host = builder.Build();
await host.RunAsync();
