namespace Engine.Hosting.Telemetry;

using Microsoft.Extensions.DependencyInjection.Extensions;

public static class EngineTelemetryExtensions
{
    /// <summary>
    /// The engine's instruments, and nothing that exports them. Idempotent, because both
    /// <c>Program.cs</c> and <see cref="AgentClientExtensions.AddAgentClient"/> need them and
    /// neither should have to know whether the other ran first.
    /// </summary>
    public static IServiceCollection AddEngineTelemetry(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EngineTelemetry>();
        services.TryAddTransient<AgentLatencyHandler>();

        return services;
    }
}
