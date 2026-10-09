namespace Engine.Application.Persistence;

/// <summary>
/// Whether trading has been stopped by hand. Read by the engine, written by an operator - the
/// engine never engages or releases it itself.
/// </summary>
/// <remarks>
/// <para>
/// A port rather than a setting, because the point is that it takes effect without a deploy and
/// without a restart. A configuration value is read once at startup; this is read before every
/// cycle, before every analysis, and immediately before every order.
/// </para>
/// <para>
/// An implementation must <b>fail closed</b>: when the state cannot be read, or there is no state
/// to read, it answers engaged. Every path that places an order asks it first, so the cost of a
/// wrong "engaged" is a cycle that did nothing, and the cost of a wrong "released" is an order
/// somebody had tried to stop.
/// </para>
/// </remarks>
public interface IKillSwitch
{
    Task<KillSwitchState> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>What the kill switch said, and if it is engaged, why and since when.</summary>
/// <param name="Reason">What the operator wrote when engaging it, or what stopped it being read.</param>
/// <param name="Since">When it was engaged. Null when the state could not be read at all.</param>
public sealed record KillSwitchState(bool Engaged, string? Reason, DateTimeOffset? Since)
{
    public static KillSwitchState Released { get; } = new(false, null, null);

    public static KillSwitchState EngagedBecause(string reason, DateTimeOffset? since = null) =>
        new(true, reason, since);
}
