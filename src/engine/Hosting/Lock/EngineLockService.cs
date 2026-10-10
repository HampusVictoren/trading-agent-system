namespace Engine.Hosting.Lock;

using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

/// <summary>
/// Takes the one-engine lock before anything else starts, holds it for the life of the process,
/// and stops the engine the moment it can no longer show that it holds it.
/// </summary>
/// <remarks>
/// <para>
/// Registered before both workers. The host starts hosted services one at a time, in order,
/// and a start that throws stops the host there - so a second engine is refused before the
/// trading worker or the measurement worker has run a line, and Program.cs turns the refusal into
/// exit code <see cref="AnotherEngineExitCode"/>. The host also stops services in reverse order,
/// so the lock is the last thing released, after both workers have finished.
/// </para>
/// <para>
/// <b>Fail closed.</b> Postgres releases the lock the moment its session ends, so a dropped
/// connection means another engine may already hold it. Every <see cref="CheckInterval"/> the
/// service asks, on the lock's own connection, whether this session still holds the lock; any
/// answer but yes - a closed connection, a terminated backend, a timeout - stops the application,
/// and Program.cs exits with <see cref="LostLockExitCode"/>. It does not try to take the lock
/// again in-process: a restart (compose's <c>on-failure</c>) goes through the whole startup,
/// lock first, and only one of two engines gets past it. The exposure is bounded by the
/// interval plus the shutdown of the step in hand, which is what the trading worker already
/// allows any stop.
/// </para>
/// </remarks>
public sealed class EngineLockService : IHostedService, IAsyncDisposable
{
    /// <summary>Another engine holds this database's lock; this one never started.</summary>
    public const int AnotherEngineExitCode = 3;

    /// <summary>The lock was lost while running; the engine stopped rather than go on unlocked.</summary>
    public const int LostLockExitCode = 4;

    private readonly string _connectionString;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<EngineLockService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    private EngineInstanceLock? _lock;
    private Task? _watch;
    private int _disposed;

    public EngineLockService(
        IOptions<DatabaseOptions> database, IHostApplicationLifetime lifetime, ILogger<EngineLockService> logger)
    {
        _connectionString = database.Value.ConnectionString;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>How often the lock is re-confirmed. Five seconds outside tests.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True once the lock has been lost and the application asked to stop.</summary>
    public bool Lost { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lock = await EngineInstanceLock.TryAcquireAsync(_connectionString, cancellationToken);

        if (_lock is null)
        {
            var holder = await EngineInstanceLock.DescribeHolderAsync(_connectionString, cancellationToken);
            throw new EngineAlreadyRunningException(holder);
        }

        _logger.LogInformation(
            "Took the engine lock (advisory lock {ClassKey}/{ObjectKey}); no other engine can start against this database while this one runs.",
            EngineInstanceLock.ClassKey,
            EngineInstanceLock.ObjectKey);

        _watch = WatchAsync(_lock, _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        // The watch first, the connection after: closing it under a check in flight would make
        // that check fail, and Npgsql does not support a connection used from two places at once.
        if (_watch is not null)
            await _watch.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (_lock is not null)
        {
            await _lock.DisposeAsync();
            _lock = null;
        }
    }

    /// <remarks>
    /// Idempotent: the container disposes this instance once for its own registration and once
    /// for the hosted-service registration that forwards to it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _stopping.CancelAsync();
        if (_lock is not null)
            await _lock.DisposeAsync();
        _stopping.Dispose();
    }

    private async Task WatchAsync(EngineInstanceLock held, CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CheckInterval, stopping);

                // Not cancelled by the stop: cancelling an in-flight Npgsql command sends a cancel
                // request that races the reply, which is a failure mode a stop does not need. The
                // check is bounded by the lock connection's 5 s command timeout instead, and the
                // stop is looked at after it.
                if (await held.IsStillHeldAsync(CancellationToken.None))
                    continue;

                // A check that failed because the host is stopping - the connection going away as
                // part of a normal shutdown - is not a lost lock, and must not turn a clean stop
                // into exit 4.
                if (stopping.IsCancellationRequested)
                    return;

                Lost = true;
                _logger.LogCritical(
                    "Lost the engine lock: its database session ended or stopped answering, so another engine may "
                    + "already hold it. Stopping now rather than trading unlocked; a restart takes the lock again "
                    + "or refuses to start if another engine has it.");
                _lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is stopping; the lock is released in StopAsync.
        }
    }
}

/// <summary>A second engine, refused at startup because the first holds the lock.</summary>
public sealed class EngineAlreadyRunningException : Exception
{
    public EngineAlreadyRunningException(string? holder)
        : base(
            "Another engine is already running against this database"
            + (holder is null ? "" : $" (it holds the engine lock: {holder})")
            + ", and only one may. This one will not start. Stop the other first - `docker compose stop engine` "
            + "for the container, or end the host's `dotnet run` - or point this one at a different database.")
    {
        Holder = holder;
    }

    /// <summary>What could be read about the holder, or null.</summary>
    public string? Holder { get; }
}
