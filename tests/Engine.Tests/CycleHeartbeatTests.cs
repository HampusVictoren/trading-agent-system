using Engine.Hosting.Options;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// The heartbeat file the engine image's HEALTHCHECK reads: a deadline in Unix seconds, which
/// the check compares with <c>date +%s</c> and nothing else.
/// </summary>
public sealed class CycleHeartbeatTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("heartbeat-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CycleHeartbeat AHeartbeat(
        string? path, int intervalMinutes = 15, int requestTimeoutSeconds = 120, ILogger<CycleHeartbeat>? logger = null) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new HealthOptions { HeartbeatFile = path }),
            Microsoft.Extensions.Options.Options.Create(new TradingOptions { CycleIntervalMinutes = intervalMinutes }),
            Microsoft.Extensions.Options.Options.Create(new AgentServiceOptions { RequestTimeoutSeconds = requestTimeoutSeconds }),
            new FixedClock(Now),
            logger ?? NullLogger<CycleHeartbeat>.Instance);

    private string File(string name = "engine.heartbeat") => Path.Combine(_directory, name);

    [Fact]
    public void A_beat_writes_the_deadline_the_interval_and_one_steps_allowance_away()
    {
        // 15 minutes, plus twice one signal call with both attempts: 2 × (2 × 120 + 5) = 490 s.
        var path = File();

        AHeartbeat(path).Beat();

        long.Parse(System.IO.File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(Now.ToUnixTimeSeconds() + (15 * 60) + 490);
    }

    [Theory]
    [InlineData(120, 490)]
    [InlineData(600, 2410)]
    // Twice 65 s is 130 s, below the floor: a short timeout does not make a short lease.
    [InlineData(30, 300)]
    public void The_allowance_follows_the_agent_timeout_and_never_drops_below_five_minutes(int timeout, int seconds) =>
        CycleHeartbeat.Allowance(timeout).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void A_longer_interval_is_a_longer_lease()
    {
        // The sleep between cycles is the longest gap between two beats, so it is the lease.
        AHeartbeat(File(), intervalMinutes: 60).Lease.ShouldBe(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(490));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_path_nothing_is_written_and_nothing_fails(string? path)
    {
        AHeartbeat(path).Beat();

        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Fact]
    public void Each_beat_replaces_the_last_and_leaves_no_staging_file_behind()
    {
        var path = File();
        var heartbeat = AHeartbeat(path);

        heartbeat.Beat();
        heartbeat.Beat();

        Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName).ShouldBe(["engine.heartbeat"]);
    }

    [Fact]
    public void A_heartbeat_that_cannot_be_written_is_said_once_and_never_thrown()
    {
        // A missing directory, standing in for a full disk or a read-only file system. The engine
        // keeps trading; its healthcheck reports it, which is the right place for it.
        var logger = Substitute.For<ILogger<CycleHeartbeat>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var heartbeat = AHeartbeat(Path.Combine(_directory, "missing", "engine.heartbeat"), logger: logger);

        Should.NotThrow(heartbeat.Beat);
        Should.NotThrow(heartbeat.Beat);

        logger.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning)
            .ShouldBe(1);
    }

    [Fact]
    public void A_heartbeat_left_by_an_earlier_run_is_cleared_at_startup()
    {
        // Otherwise a container restarted into a crash would be reported healthy on the strength
        // of the run before it, for as long as that run's deadline lasted.
        var path = File();
        System.IO.File.WriteAllText(path, "99999999999");

        CycleHeartbeat.ClearLeftovers(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Health:HeartbeatFile"] = path })
            .Build());

        System.IO.File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Clearing_with_nothing_there_or_nothing_configured_is_not_an_error()
    {
        Should.NotThrow(() => CycleHeartbeat.ClearLeftovers(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Health:HeartbeatFile"] = File("never-written") })
            .Build()));

        Should.NotThrow(() => CycleHeartbeat.ClearLeftovers(new ConfigurationBuilder().Build()));
    }
}
