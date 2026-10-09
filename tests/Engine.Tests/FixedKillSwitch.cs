using Engine.Application.Persistence;

namespace Engine.Tests;

/// <summary>A kill switch that says what it was told to, and counts how often it was asked.</summary>
internal sealed class FixedKillSwitch(KillSwitchState state) : IKillSwitch
{
    public static FixedKillSwitch Released() => new(KillSwitchState.Released);

    public static FixedKillSwitch Engaged(string reason = "stopped by a test") =>
        new(KillSwitchState.EngagedBecause(reason, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)));

    public int Reads { get; private set; }

    public Task<KillSwitchState> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(state);
    }
}
