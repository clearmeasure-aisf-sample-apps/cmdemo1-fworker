using System.Data;
using Microsoft.Data.SqlClient;

// The leader lease as a SQL application lock (W9.10b, docs/design/fworker-wolverine-implementation.md, revision 2026-10-30). Where the gateway has a database, the process that holds the
// application lock "fworker:leader" is the one whose Wolverine node may run (and, where the engine acts, the one that acts at all). The lock is sp_getapplock with owner Session on a connection of its
// own that is never pooled and never retried: it lives exactly as long as that connection, so a holder that dies, is killed or loses its network has lost the lock without anyone having to notice.
// factory.Leadership (script 170) holds one row beside it: the epoch of the term (one more each time a process takes the lock), the holder, and the takeover request of a newer process.
//
// The newest process still wins: a starting process that finds the lock taken writes a takeover request (and refreshes it at every try); the holder sees it at its next check, stops its node, saves the
// plant and releases (GatewayLeader.SqlStepDownAsync); the newcomer takes the lock and increments the epoch. A request that is not refreshed (its writer died) is stale after RequestFreshSeconds and
// ignored. A newcomer whose request was overwritten by a still newer one concedes and waits passively. The GitHub-ref lease (Leader.cs) stays beside this one as the arbitration with a process that has
// no database (v1 under the kill switch, uat, prod); a process without a database never constructs this class.
interface ISqlLease
{
    /// <summary>True while this process holds the lock (set false as soon as a check finds it gone).</summary>
    bool Held { get; }
    /// <summary>The epoch of the last term this process took (0: never); it stays after the term ends, like the epoch of the GitHub lease.</summary>
    long Epoch { get; }
    /// <summary>One try to take the lock. With request, a refused try writes (or refreshes) the takeover request. Throws where the database cannot be reached (the caller tries again).</summary>
    Task<SqlLeaseAcquire> TryAcquireAsync(string holder, bool request, CancellationToken ct);
    /// <summary>Looks at the lock the holder holds: still ours, gone (the connection dropped), or asked for by a newer process. Any failure counts as gone.</summary>
    Task<SqlLeaseCheck> CheckAsync(string holder, CancellationToken ct);
    /// <summary>Whether the lock is free right now and who held it last, without taking it. Throws where the database cannot be reached.</summary>
    Task<SqlLeasePeek> PeekAsync(CancellationToken ct);
    /// <summary>Releases the lock (if held) and ends the connection.</summary>
    Task ReleaseAsync();
}

enum SqlLeaseState { Held, Lost, TakeoverRequested }
readonly record struct SqlLeaseAcquire(bool Acquired, long Epoch = 0, string? PreviousHolder = null, bool Conceded = false);
readonly record struct SqlLeaseCheck(SqlLeaseState State, string? RequestedBy = null, string? Failure = null);
readonly record struct SqlLeasePeek(bool LockFree, string? LastHolder, long Epoch);

sealed class SqlLease : ISqlLease
{
    public const string Resource = "fworker:leader";
    readonly string connectionString;
    readonly Func<CancellationToken, Task> prepare;
    readonly int requestFreshSeconds;
    readonly SemaphoreSlim io = new(1, 1);
    readonly object gate = new();
    SqlConnection? conn;
    bool prepared, requested, conceded, held;
    long epoch;
    string? me;   // the holder name this lease acts for (set by the first try), so that a stop can withdraw its own takeover request

    /// <param name="connectionString">The database's connection string; the lock's connection is made from it with pooling and connection resiliency off.</param>
    /// <param name="prepare">Brings the schema up (the scripts of Fworker.Database) before the first use: the leadership row is a table of script 170.</param>
    /// <param name="requestFreshSeconds">How long a takeover request counts without being refreshed.</param>
    public SqlLease(string connectionString, Func<CancellationToken, Task> prepare, int requestFreshSeconds)
    {
        this.connectionString = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0, ApplicationName = "fworker-leader" }.ConnectionString;
        this.prepare = prepare; this.requestFreshSeconds = Math.Max(1, requestFreshSeconds);
    }

    public bool Held { get { lock (gate) return held; } }
    public long Epoch { get { lock (gate) return epoch; } }
    void SetHeld(bool value, long? term = null) { lock (gate) { held = value; if (term is { } t) epoch = t; } }

    async Task<SqlConnection> ConnectionAsync(CancellationToken ct)
    {
        if (conn is { State: ConnectionState.Open }) return conn;
        Drop();
        var c = new SqlConnection(connectionString);
        try { await c.OpenAsync(ct); } catch { c.Dispose(); throw; }
        conn = c;
        return c;
    }

    // the connection ends, and with it the session lock; pooling is off, so the server sees the session end at once
    void Drop() { var c = conn; conn = null; if (c != null) { try { c.Dispose(); } catch { } } }

    SqlCommand Command(SqlConnection c, string text, int timeoutSeconds = 15)
    {
        var cmd = new SqlCommand(text, c) { CommandTimeout = timeoutSeconds };
        cmd.Parameters.Add("@r", SqlDbType.NVarChar, 255).Value = Resource;
        cmd.Parameters.Add("@fresh", SqlDbType.Int).Value = requestFreshSeconds;
        return cmd;
    }

    public async Task<SqlLeaseAcquire> TryAcquireAsync(string holder, bool request, CancellationToken ct)
    {
        await io.WaitAsync(ct);
        try
        {
            me = holder;
            if (Held) return new SqlLeaseAcquire(true, Epoch);
            if (!prepared) { await prepare(ct); prepared = true; }
            var c = await ConnectionAsync(ct);
            try
            {
                int granted;
                await using (var cmd = Command(c, "DECLARE @s int; EXEC @s = sp_getapplock @Resource = @r, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @s;"))
                    granted = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                if (granted < 0)
                {
                    // 'granted < 0': another session holds it (-1 timeout), or the call failed (-2 .. -999): nothing was taken
                    if (granted == -1 && request && !conceded) await RequestAsync(c, holder, ct);
                    return new SqlLeaseAcquire(false, Conceded: conceded);
                }
                // the lock is ours: a newer process that asked for it first is let through (the newest process wins); the lock is given back at once
                var row = await ReadRowAsync(c, holder, ct);
                if (row.RequestedByOther)
                {
                    await ReleaseLockAsync(c, ct);
                    return new SqlLeaseAcquire(false);
                }
                var (term, previous) = await TakeTermAsync(c, holder, ct);
                SetHeld(true, term);
                lock (gate) { requested = false; conceded = false; }
                return new SqlLeaseAcquire(true, term, previous);
            }
            catch { Drop(); SetHeld(false); throw; }
        }
        finally { io.Release(); }
    }

    // the request of a starting process: the first one overwrites whatever stands there (it is the newest), later ones only refresh their own; finding another's means a newer process asked after us
    async Task RequestAsync(SqlConnection c, string holder, CancellationToken ct)
    {
        await using var cmd = Command(c, """
            UPDATE factory.Leadership SET TakeoverBy = @h, TakeoverAt = SYSDATETIMEOFFSET() WHERE Id = 1 AND (@first = 1 OR TakeoverBy = @h);
            IF @@ROWCOUNT > 0 SELECT 1;
            ELSE IF EXISTS (SELECT 1 FROM factory.Leadership WHERE Id = 1) SELECT 2;
            ELSE SELECT 0;
            """);
        cmd.Parameters.Add("@h", SqlDbType.NVarChar, 100).Value = holder;
        bool first; lock (gate) first = !requested;
        cmd.Parameters.Add("@first", SqlDbType.Bit).Value = first;
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        lock (gate) { if (result == 1) requested = true; else if (result == 2) conceded = true; }
    }

    readonly record struct Row(bool Found, bool RequestedByOther);

    async Task<Row> ReadRowAsync(SqlConnection c, string holder, CancellationToken ct)
    {
        await using var cmd = Command(c, """
            SELECT TakeoverBy, CASE WHEN TakeoverAt >= DATEADD(SECOND, -@fresh, SYSDATETIMEOFFSET()) THEN 1 ELSE 0 END FROM factory.Leadership WHERE Id = 1;
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new Row(false, false);
        var by = r.IsDBNull(0) ? null : r.GetString(0);
        return new Row(true, by != null && by != holder && r.GetInt32(1) == 1);
    }

    async Task<(long Epoch, string? Previous)> TakeTermAsync(SqlConnection c, string holder, CancellationToken ct)
    {
        await using var cmd = Command(c, """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @prev nvarchar(100) = (SELECT Holder FROM factory.Leadership WHERE Id = 1);
            UPDATE factory.Leadership SET Epoch = Epoch + 1, Holder = @h, AcquiredAt = SYSDATETIMEOFFSET(), TakeoverBy = NULL, TakeoverAt = NULL WHERE Id = 1;
            IF @@ROWCOUNT = 0 INSERT INTO factory.Leadership (Id, Epoch, Holder, AcquiredAt) VALUES (1, 1, @h, SYSDATETIMEOFFSET());
            SELECT Epoch, @prev FROM factory.Leadership WHERE Id = 1;
            COMMIT TRANSACTION;
            """);
        cmd.Parameters.Add("@h", SqlDbType.NVarChar, 100).Value = holder;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("the leadership row is missing after it was written");
        return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    static async Task ReleaseLockAsync(SqlConnection c, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("EXEC sp_releaseapplock @Resource = @r, @LockOwner = N'Session';", c) { CommandTimeout = 10 };
        cmd.Parameters.Add("@r", SqlDbType.NVarChar, 255).Value = Resource;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SqlLeaseCheck> CheckAsync(string holder, CancellationToken ct)
    {
        await io.WaitAsync(ct);
        try
        {
            if (!Held) return new SqlLeaseCheck(SqlLeaseState.Lost);
            try
            {
                var c = conn ?? throw new InvalidOperationException("no connection");
                // the session's own view of the lock (a reconnected session has none), and the row: the term must still be ours
                // (a lock that does not show is asked for once more before it counts as gone)
                for (var look = 1; ; look++)
                {
                    await using var cmd = Command(c, """
                        SELECT APPLOCK_MODE(N'public', @r, N'Session'), l.Epoch, l.Holder, l.TakeoverBy, CASE WHEN l.TakeoverAt >= DATEADD(SECOND, -@fresh, SYSDATETIMEOFFSET()) THEN 1 ELSE 0 END
                        FROM (SELECT 1 AS One) d LEFT JOIN factory.Leadership l ON l.Id = 1;
                        """, 10);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct)) return Lost("no answer");
                    var mode = r.GetString(0);
                    var rowEpoch = r.IsDBNull(1) ? -1 : r.GetInt64(1);
                    var rowHolder = r.IsDBNull(2) ? null : r.GetString(2);
                    if (mode != "Exclusive" || rowHolder != holder || rowEpoch != Epoch)
                    {
                        if (look == 1) continue;
                        return Lost(mode != "Exclusive" ? "the lock is gone" : "the term is another's");
                    }
                    var by = r.IsDBNull(3) ? null : r.GetString(3);
                    if (by != null && by != holder && r.GetInt32(4) == 1) return new SqlLeaseCheck(SqlLeaseState.TakeoverRequested, by);
                    return new SqlLeaseCheck(SqlLeaseState.Held);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { return Lost(e is SqlException se ? $"SqlException {se.Number}" : e.GetType().Name); }
        }
        finally { io.Release(); }
    }

    // The holder has lost the lock: it acts no more. The connection is NOT ended here: a lock the server still holds (a failed look, a timeout) stays held until ReleaseAsync, which the step-down calls
    // after the node has left and the plant is saved, so that no other process can take it while this node still runs.
    SqlLeaseCheck Lost(string why) { SetHeld(false); return new SqlLeaseCheck(SqlLeaseState.Lost, Failure: why); }

    public async Task<SqlLeasePeek> PeekAsync(CancellationToken ct)
    {
        await io.WaitAsync(ct);
        try
        {
            if (!prepared) { await prepare(ct); prepared = true; }
            var c = await ConnectionAsync(ct);
            try
            {
                await using var cmd = Command(c, """
                    SELECT APPLOCK_TEST(N'public', @r, N'Exclusive', N'Session'), l.Holder, l.Epoch FROM (SELECT 1 AS One) d LEFT JOIN factory.Leadership l ON l.Id = 1;
                    """, 10);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new SqlLeasePeek(r.GetInt32(0) == 1, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt64(2));
            }
            catch { Drop(); throw; }
        }
        finally { io.Release(); }
    }

    public async Task ReleaseAsync()
    {
        await io.WaitAsync();
        try
        {
            var c = conn;
            if (c is { State: ConnectionState.Open })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // a stopping newcomer withdraws its own request (it is not honoured when stale, but the holder need not step down for it meanwhile)
                if (me != null) { try { await using var clear = Command(c, "UPDATE factory.Leadership SET TakeoverBy = NULL, TakeoverAt = NULL WHERE Id = 1 AND TakeoverBy = @h;"); clear.Parameters.Add("@h", SqlDbType.NVarChar, 100).Value = me; await clear.ExecuteNonQueryAsync(cts.Token); } catch { } }
                try { await ReleaseLockAsync(c, cts.Token); } catch { }
            }
            SetHeld(false); Drop();
            lock (gate) { requested = false; conceded = false; }
        }
        finally { io.Release(); }
    }
}
