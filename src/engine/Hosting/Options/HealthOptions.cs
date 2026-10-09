namespace Engine.Hosting.Options;

/// <summary>Where the engine says it is alive, for a healthcheck to read.</summary>
public sealed class HealthOptions
{
    public const string SectionName = "Health";

    /// <summary>
    /// The heartbeat file. Unset means no heartbeat is written, which is right for an engine run
    /// with <c>dotnet run</c>: nothing reads it there. The engine image sets it to
    /// <c>/tmp/engine.heartbeat</c>, and its HEALTHCHECK reads the same path.
    /// </summary>
    public string? HeartbeatFile { get; init; }
}
