namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>Where the agent service is, and how long one analysis may take.</summary>
public sealed class AgentServiceOptions
{
    public const string SectionName = "AgentService";

    public const string ScopeSignalsWrite = "signals:write";
    public const string ScopeScreenWrite = "screen:write";
    public const string ScopeOutcomesWrite = "outcomes:write";
    public const string ScopeMarketRead = "market:read";

    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Per attempt, not per cycle. One analysis takes 12-15 s with llama3.2, so the default
    /// is generous, but it must be bounded: an unanswered call otherwise blocks the worker.
    /// </summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; init; }

    /// <summary>
    /// Legacy full-access key, sent as X-Api-Key when no scoped key is configured for the
    /// endpoint. It is a secret, so it is never in appsettings.json: in development it comes
    /// from user secrets, and elsewhere from the environment. Prefer the scoped keys below
    /// once both sides have rotated; this property remains required so existing deployments
    /// keep starting.
    /// </summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Optional key that carries only <c>signals:write</c>.</summary>
    public string? SignalsApiKey { get; init; }

    /// <summary>Optional key that carries only <c>screen:write</c>.</summary>
    public string? ScreenApiKey { get; init; }

    /// <summary>Optional key that carries only <c>outcomes:write</c>.</summary>
    public string? OutcomesApiKey { get; init; }

    /// <summary>Optional key that carries only <c>market:read</c>.</summary>
    public string? MarketApiKey { get; init; }

    /// <summary>
    /// Shared secret used to HMAC-SHA256 the body of <c>POST /v1/outcomes</c>. Separate
    /// from the API key so a stolen key alone cannot forge measurements into agent memory.
    /// Required; set via user secrets like ApiKey.
    /// </summary>
    [Required]
    public string OutcomesHmacSecret { get; init; } = string.Empty;

    /// <summary>The key to send for a given scope: scoped override, else the legacy key.</summary>
    public string ApiKeyFor(string scope) => scope switch
    {
        ScopeSignalsWrite when !string.IsNullOrEmpty(SignalsApiKey) => SignalsApiKey,
        ScopeScreenWrite when !string.IsNullOrEmpty(ScreenApiKey) => ScreenApiKey,
        ScopeOutcomesWrite when !string.IsNullOrEmpty(OutcomesApiKey) => OutcomesApiKey,
        ScopeMarketRead when !string.IsNullOrEmpty(MarketApiKey) => MarketApiKey,
        _ => ApiKey
    };
}
