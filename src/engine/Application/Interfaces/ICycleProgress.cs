namespace Engine.Application.Interfaces;

/// <summary>
/// Lets a use case say that the cycle it is part of is still moving. Implemented by the engine's
/// heartbeat, which is what the container's healthcheck reads.
/// </summary>
/// <remarks>
/// A port rather than a dependency on the heartbeat itself, because the application layer knows
/// nothing about files or healthchecks - only that some of its loops run for as long as the agent
/// service takes to answer, once per holding, and that whoever watches the cycle needs to hear
/// from it more often than once per loop.
/// </remarks>
public interface ICycleProgress
{
    /// <summary>The cycle has just finished a step that waited on something outside the process.</summary>
    void Beat();
}

/// <summary>For a use case run where nobody watches the cycle, such as a test.</summary>
public sealed class NoCycleProgress : ICycleProgress
{
    public static readonly NoCycleProgress Instance = new();

    private NoCycleProgress() { }

    public void Beat() { }
}
