namespace Engine.Infrastructure.Persistence;

using Engine.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// The kill switch as the latest row of <c>trading.kill_switch</c>, read fresh every time and
/// failing closed.
/// </summary>
/// <remarks>
/// <para>
/// A row in the database rather than a file, an endpoint or an environment variable. The engine
/// has no web server to put an endpoint on; an environment variable needs a restart, which is the
/// thing a kill switch exists to not need; and a file would have to be mounted into the container
/// and written by a user the image does not have. The database is already the one thing both a
/// host-run and a containerised engine read, an operator already reaches it with
/// <c>docker exec trading-db psql</c>, and a row can say who and why.
/// </para>
/// <para>
/// <c>AsNoTracking</c>, because the context is the cycle's own: a tracked row would be answered
/// from memory the second time, and the second time is the read just before an order.
/// </para>
/// </remarks>
public sealed class KillSwitch : IKillSwitch
{
    private readonly TradingDbContext _context;
    private readonly ILogger<KillSwitch> _logger;

    public KillSwitch(TradingDbContext context, ILogger<KillSwitch> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<KillSwitchState> ReadAsync(CancellationToken cancellationToken = default)
    {
        KillSwitchEvent? latest;
        try
        {
            latest = await _context.KillSwitch
                .AsNoTracking()
                .OrderByDescending(flip => flip.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutting down, which is not a reason to report the switch as anything.
        }
        catch (Exception ex)
        {
            // Closed. An engine that cannot read the switch cannot know it was not pulled, and a
            // database the engine cannot read is not one it could have stored an order in anyway.
            _logger.LogError(ex, "Could not read the kill switch, so trading is treated as halted.");

            return KillSwitchState.EngagedBecause($"the kill switch could not be read ({ex.GetType().Name})");
        }

        // The migration writes a released row, so an empty table means somebody removed it. That
        // is not a state anyone chose, so it is not one to trade under.
        if (latest is null)
        {
            return KillSwitchState.EngagedBecause(
                "trading.kill_switch is empty; insert a row with engaged = false to release it");
        }

        return latest.Engaged
            ? KillSwitchState.EngagedBecause(latest.Reason, latest.ChangedAt)
            : KillSwitchState.Released;
    }
}
