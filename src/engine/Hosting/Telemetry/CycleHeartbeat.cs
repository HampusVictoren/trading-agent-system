namespace Engine.Hosting.Telemetry;

using System.Globalization;
using Engine.Application.Interfaces;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// The engine's healthcheck: a file holding the moment after which, if nothing has written it
/// again, the trading loop should be considered stuck.
/// </summary>
/// <remarks>
/// <para>
/// A Worker has no endpoint, and giving it one would be the engine's first inbound surface - the
/// same reason the kill switch is a table. So the loop writes a file and the image's HEALTHCHECK
/// reads it: <c>test "$(cat file)" -gt "$(date +%s)"</c>. What the file holds is a deadline in Unix
/// seconds rather than a timestamp, so the check needs to know nothing - not the cycle interval,
/// not the agent timeouts. The engine knows both and does the arithmetic.
/// </para>
/// <para>
/// <b>What it means.</b> Healthy says the loop is making progress: it is written when a cycle
/// starts, after the exits, after the selection, before each analysis, after every quote (through
/// <see cref="ICycleProgress"/>, from <c>QuoteReader</c>) and when the cycle ends. It does not say
/// the agent service or the database is up - the loop survives both being down, logs it, and
/// carries on, and an engine that is waiting out an outage correctly is not the engine that needs
/// looking at. A loop that has hung, or a worker that has died while the process lives on, is.
/// </para>
/// <para>
/// <b>The deadline</b> is the cycle interval plus an allowance for one step of a cycle. The longest
/// wait between two beats is the sleep between cycles; the longest step is one call to the agent
/// service that uses both attempts, about <c>2 × RequestTimeoutSeconds + 5 s</c>. One call and not
/// one loop: the exits and the sizing of a buy read a quote per holding, one at a time, and each
/// quote beats, so the gap does not grow with what the portfolio holds. The allowance is twice
/// that, and never under five minutes. At the shipped settings that is 15 min + 8 min 10 s.
/// </para>
/// <para>
/// <b>Before the first cycle.</b> The first cycle starts as the worker does, and its first beat is
/// written before it does anything, so a process that has not finished a cycle is healthy for a
/// whole deadline from then. The seconds between the process starting and the worker starting
/// (configuration, the schema check) are the HEALTHCHECK's start period. A file left by a previous
/// run of the same container is deleted at startup, before anything else, so an engine that crashes
/// on the way up is never reported healthy on the strength of the run before it.
/// </para>
/// <para>
/// A beat that cannot be written is logged once as a warning and otherwise ignored. A full disk
/// should make the container unhealthy, which it will; it should not stop the exits.
/// </para>
/// </remarks>
public sealed class CycleHeartbeat : ICycleProgress
{
    private static readonly TimeSpan MinimumAllowance = TimeSpan.FromMinutes(5);

    private readonly string? _path;
    private readonly TimeSpan _lease;
    private readonly TimeProvider _clock;
    private readonly ILogger<CycleHeartbeat> _logger;
    private int _warned;

    public CycleHeartbeat(
        IOptions<HealthOptions> health,
        IOptions<TradingOptions> trading,
        IOptions<AgentServiceOptions> agents,
        TimeProvider clock,
        ILogger<CycleHeartbeat> logger)
    {
        _path = string.IsNullOrWhiteSpace(health.Value.HeartbeatFile) ? null : health.Value.HeartbeatFile;
        _lease = trading.Value.CycleInterval + Allowance(agents.Value.RequestTimeoutSeconds);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How long a beat keeps the engine healthy.</summary>
    public TimeSpan Lease => _lease;

    /// <summary>The longest one step of a cycle may take: twice one agent call with both attempts.</summary>
    public static TimeSpan Allowance(int requestTimeoutSeconds)
    {
        var oneCall = TimeSpan.FromSeconds((2 * requestTimeoutSeconds) + 5);
        var allowance = oneCall + oneCall;

        return allowance < MinimumAllowance ? MinimumAllowance : allowance;
    }

    /// <summary>
    /// Deletes a heartbeat left by an earlier run. Static and read straight from configuration,
    /// because it runs first thing in <c>Program.cs</c>, before the host - and so before anything
    /// that could fail on the way up.
    /// </summary>
    public static void ClearLeftovers(IConfiguration configuration)
    {
        var path = configuration[$"{HealthOptions.SectionName}:{nameof(HealthOptions.HeartbeatFile)}"];

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to log with yet. The first beat overwrites it anyway, and if that fails
            // too, Beat says so.
        }
    }

    /// <summary>Says the loop is alive until now plus <see cref="Lease"/>.</summary>
    public void Beat()
    {
        if (_path is null)
            return;

        var deadline = (_clock.GetUtcNow() + _lease).ToUnixTimeSeconds();

        try
        {
            // Written beside the file and moved over it, so the healthcheck never reads half a
            // number - an empty read would be a false unhealthy, and a short one a false healthy.
            var staging = _path + ".tmp";
            File.WriteAllText(staging, deadline.ToString(CultureInfo.InvariantCulture));
            File.Move(staging, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _logger.LogWarning(
                    "Could not write the heartbeat to {Path}: {Reason}. The engine keeps trading, and its "
                    + "healthcheck will report it unhealthy. This is said once.",
                    _path, ex.Message);
            }
        }
    }
}
