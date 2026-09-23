namespace Engine.Hosting;

using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

public static class EngineOptionsExtensions
{
    /// <summary>
    /// Binds and validates the engine's configuration. ValidateOnStart moves a broken setting
    /// from "strange behaviour three cycles in" to "the service refuses to start", which is
    /// what you want from the numbers that decide how much money a buy may spend.
    /// </summary>
    public static IServiceCollection AddEngineOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentServiceOptions>()
            .Bind(configuration.GetSection(AgentServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RiskPolicyOptions>()
            .Bind(configuration.GetSection(RiskPolicyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TradingOptions>, TradingOptionsValidator>();
        services.AddOptions<TradingOptions>()
            .Bind(configuration.GetSection(TradingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
