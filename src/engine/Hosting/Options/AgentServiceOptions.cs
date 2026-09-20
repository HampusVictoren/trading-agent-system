namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>Where the agent service is, and how long one analysis may take.</summary>
public sealed class AgentServiceOptions
{
    public const string SectionName = "AgentService";

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
    /// Sent as X-Api-Key on every call. It is a secret, so it is never in appsettings.json:
    /// in development it comes from user secrets, and elsewhere from the environment.
    /// </summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;
}
