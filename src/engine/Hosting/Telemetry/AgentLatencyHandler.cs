namespace Engine.Hosting.Telemetry;

using Polly.Timeout;

/// <summary>Names a call to the agent service for <c>agent_latency_seconds</c>.</summary>
/// <remarks>
/// Set by <c>PythonAgentClient</c> on each request rather than worked out from the URL here,
/// because the URL carries a ticker in its path, and the one thing an operation label must not
/// be is a value per instrument.
/// </remarks>
public static class AgentOperation
{
    public static readonly HttpRequestOptionsKey<string> Key = new("Engine.AgentOperation");

    public const string Signal = "signal";
    public const string Quote = "quote";
    public const string History = "history";
    public const string Outcomes = "outcomes";
    public const string Screen = "screen";

    /// <summary>What a request nobody named is recorded as, so it shows up rather than vanishing.</summary>
    public const string Unnamed = "unnamed";

    public static void Name(HttpRequestMessage message, string operation) =>
        message.Options.Set(Key, operation);
}

/// <summary>
/// Times every call to the agent service into <c>agent_latency_seconds</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered <em>outside</em> the resilience handler, so what it times is the call as the
/// engine waited for it - both attempts and the pause between them, when there was a retry. That
/// is the number a cycle's length is made of; a per-attempt figure is what the HTTP client's own
/// <c>http.client.request.duration</c> already is.
/// </para>
/// <para>
/// The outcome is a closed set: <c>success</c> for a 2xx, <c>http_error</c> for any other status
/// (the agent service answered, and said no), <c>timeout</c> when the pipeline gave up waiting,
/// and <c>unreachable</c> for everything else. A call cancelled because the engine is shutting
/// down is not recorded at all - it says nothing about the agent service.
/// </para>
/// </remarks>
public sealed class AgentLatencyHandler(EngineTelemetry telemetry, TimeProvider clock) : DelegatingHandler
{
    public const string Success = "success";
    public const string HttpError = "http_error";
    public const string Timeout = "timeout";
    public const string Unreachable = "unreachable";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = request.Options.TryGetValue(AgentOperation.Key, out var named) ? named : AgentOperation.Unnamed;
        var started = clock.GetTimestamp();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            telemetry.AgentCallCompleted(
                operation, response.IsSuccessStatusCode ? Success : HttpError, clock.GetElapsedTime(started));

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            telemetry.AgentCallCompleted(
                operation,
                ex is TimeoutRejectedException ? Timeout : Unreachable,
                clock.GetElapsedTime(started));

            throw;
        }
    }
}
