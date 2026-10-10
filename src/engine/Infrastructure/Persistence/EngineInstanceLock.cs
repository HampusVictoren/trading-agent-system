namespace Engine.Infrastructure.Persistence;

using Npgsql;

/// <summary>
/// The database's one-engine lock: a Postgres session-level advisory lock, held on a connection
/// of its own for as long as the engine runs.
/// </summary>
/// <remarks>
/// <para>
/// Two engines against one database are two engines analysing, recording and - in Paper -
/// trading one account. Nothing in the schema stops that, and since stage 7 a plain
/// <c>docker compose up</c> starts an engine beside any that is already running on the host. An
/// advisory lock is the database's own answer: it costs no table and no migration, it belongs to
/// the session that took it, and <b>Postgres releases it when that session ends however it
/// ends</b> - a clean stop, a crash, a killed container or a dropped connection - so a dead engine
/// can never leave the lock behind for a living one to trip over.
/// </para>
/// <para>
/// Advisory locks are scoped to one database, which is exactly "one engine per database". The
/// key is the two-integer form, <c>(<see cref="ClassKey"/>, <see cref="ObjectKey"/>)</c>, so it
/// reads in <c>pg_locks</c> as classid 1413567301 ("TASE" in ASCII), objid 1, objsubid 2. Nothing
/// else in the system takes an advisory lock: EF Core's migration lock, which the migrator and
/// the efbundle use, is a <c>LOCK TABLE</c> on the history table, so migrating is never blocked
/// by a running engine and never blocks one.
/// </para>
/// <para>
/// The connection is unpooled. A pooled connection's Dispose only returns the physical session to
/// the pool, and the session - and the lock - would outlive the object that was meant to release
/// it. Its commands time out after a few seconds, so a check against a database that has gone
/// silent answers "not held" rather than hanging.
/// </para>
/// </remarks>
public sealed class EngineInstanceLock : IAsyncDisposable
{
    /// <summary>"TASE": trading-agent-system, engine.</summary>
    public const int ClassKey = 0x54415345;

    /// <summary>The engine itself; the only thing under <see cref="ClassKey"/>.</summary>
    public const int ObjectKey = 1;

    /// <summary>How the lock's session names itself in <c>pg_stat_activity</c>.</summary>
    public const string ApplicationName = "tas-engine (instance lock)";

    private const string HeldByThisSession = """
        SELECT EXISTS (
            SELECT 1 FROM pg_locks
            WHERE locktype = 'advisory' AND pid = pg_backend_pid()
              AND classid = @class::oid AND objid = @object::oid AND objsubid = 2 AND granted)
        """;

    private readonly NpgsqlConnection _connection;

    private EngineInstanceLock(NpgsqlConnection connection) => _connection = connection;

    /// <summary>The backend process holding the lock, for a test that wants to end it.</summary>
    public int BackendProcessId => _connection.ProcessID;

    /// <summary>
    /// Takes the lock, or returns null when another session holds it. Never waits: a second
    /// engine is refused, not queued behind the first.
    /// </summary>
    /// <exception cref="NpgsqlException">The database could not be reached.</exception>
    public static async Task<EngineInstanceLock?> TryAcquireAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(LockConnectionString(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@class, @object)", connection);
            command.Parameters.AddWithValue("class", ClassKey);
            command.Parameters.AddWithValue("object", ObjectKey);

            if (await command.ExecuteScalarAsync(cancellationToken) is true)
                return new EngineInstanceLock(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>
    /// Whether this session still holds the lock. False, never an exception, for anything that
    /// is not a clear yes: a dropped connection, a terminated backend, a timeout or a database
    /// restarted under it (a new session holds nothing).
    /// </summary>
    public async Task<bool> IsStillHeldAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = new NpgsqlCommand(HeldByThisSession, _connection);
            command.Parameters.AddWithValue("class", ClassKey);
            command.Parameters.AddWithValue("object", ObjectKey);

            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Who holds the lock, as far as this role can see: the holder's application name, address,
    /// process id and when its session began. Null when nobody does or it cannot be read; this
    /// only ever decorates a refusal, it never decides one.
    /// </summary>
    public static async Task<string?> DescribeHolderAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(LockConnectionString(connectionString));
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("""
                SELECT a.pid, coalesce(nullif(a.application_name, ''), '(unnamed)'),
                       coalesce(host(a.client_addr), 'local socket'), a.backend_start
                FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE l.locktype = 'advisory' AND l.granted
                  AND l.classid = @class::oid AND l.objid = @object::oid AND l.objsubid = 2
                """, connection);
            command.Parameters.AddWithValue("class", ClassKey);
            command.Parameters.AddWithValue("object", ObjectKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return $"'{reader.GetString(1)}' from {reader.GetString(2)}, backend pid {reader.GetInt32(0)}, "
                + $"connected since {reader.GetFieldValue<DateTimeOffset>(3):yyyy-MM-dd HH:mm:ss zzz}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Releases the lock by ending its session, which is what releases it for certain.</summary>
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private static string LockConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = ApplicationName,
            Timeout = 10,
            CommandTimeout = 5,
        }.ConnectionString;
}
