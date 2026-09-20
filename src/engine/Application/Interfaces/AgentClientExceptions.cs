namespace Engine.Application.Interfaces;

/// <summary>
/// The agent service could not be reached, answered with a failing status, or did not answer
/// in time. Thrown by <see cref="IAgentClient"/> implementations so that callers never have to
/// know which transport or resilience library produced the original failure.
/// </summary>
public sealed class AgentServiceUnavailableException : Exception
{
    public AgentServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent service answered, but not with the agreed contract.</summary>
public sealed class AgentResponseInvalidException : Exception
{
    public AgentResponseInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
