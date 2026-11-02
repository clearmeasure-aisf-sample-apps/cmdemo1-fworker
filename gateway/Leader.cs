using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

// One acting gateway across releases (issue 94; the lease is the SQL application lock since W9.10b and the only one since V3.2). In single-revision mode the platform starts the new revision before it stops
// the old one, so for a while two processes run. The one that holds the application lock "fworker:leader" (SqlLease.cs, factory.Leadership) acts: it sweeps, ticks, runs the Wolverine node, the jobs and the
// back-merge trigger, and keeps the plant in the database (SnapshotCoordinator). The lock lives exactly as long as the connection it was taken on, so a holder that dies or loses its network has lost it
// without anyone having to notice; the epoch of its term (one more each time a process takes the lock) is what the claims of the idempotency ledger and the plant state carry.
//
// The newest process wins. A starting process that finds the lock taken writes a takeover request and waits; the holder sees it at its next look (every timing.leaderSqlCheckSeconds, 5) and steps down: first its
// Wolverine node (drained and released, so the successor's node never starts beside a running one), then the final plant snapshot into the database, then the streams end with stream.replaced (reconnect: the
// session's client connects again, and the platform routes it to the new process); only then does it let the lock go. The newcomer takes the lock with the next epoch, restores the plant from the database and
// only then acts. A stepped-down process answers every further call 503 gateway-replaced and stays a standby: when the lock is free (and no newer process asked for it) it takes it again, so the last process
// standing always acts. A holder that loses its connection stops acting at its next look (at most about twice the command timeout later, the watchdog of SqlLease.CheckAsync; MayAct is false from then on)
// and steps down the same way.
//
// A process that has no database (uat and prod today, a suite's second gateway, FACTORY_LEADER_LEASE=0) has no lease: LeaderSettings.On is false, every such process acts at once, and none keeps a plant.
sealed class LeaderSettings
{
    public bool On;
    public double GapRenewMaxMinutes = 120, SnapshotDebounceSeconds = 30, SnapshotRefreshMinutes = 10;
    /// <summary>How often the holder looks at its SQL lock (a dropped connection, a takeover request) and a standby or a starting process tries to take it. A takeover request is fresh for four of these (at least 20 s).</summary>
    public double SqlCheckSeconds = 5;
    public int SqlRequestFreshSeconds => (int)Math.Max(20, Math.Ceiling(SqlCheckSeconds * 4));

    public static LeaderSettings Load(string scriptsDir, bool on)
    {
        var path = Environment.GetEnvironmentVariable("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? t = null; try { t = JsonNode.Parse(File.ReadAllText(path))?["timing"]; } catch { }
        static bool Num(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v > 0;
        double D(string k, double d) => Num(Environment.GetEnvironmentVariable("FACTORY_TIMING_" + k), out var e) ? e : Num(t?[k]?.ToString(), out var v) ? v : d;
        return new LeaderSettings
        {
            On = on, GapRenewMaxMinutes = D("gapRenewMaxMinutes", 120),
            SnapshotDebounceSeconds = D("plantSnapshotDebounceSeconds", 30), SnapshotRefreshMinutes = D("plantSnapshotRefreshMinutes", 10),
            SqlCheckSeconds = D("leaderSqlCheckSeconds", 5)
        };
    }
}

sealed class GatewayLeader(LeaderSettings cfg) : ILeadership
{
    public enum Role { Off, Starting, Acting, Superseded, Stopping }
    readonly object gate = new();
    Role role = cfg.On ? Role.Starting : Role.Off;
    // this process: unique per start; the platform's revision and replica names make the log lines readable
    public readonly string Holder = $"{Clean(Environment.MachineName)}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";
    readonly string revision = Clean(Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? ""), replica = Clean(Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? Environment.MachineName);
    DateTime acquiredAt;
    JsonObject? supersededBy;
    DateTime? supersededAt;
    readonly TaskCompletionSource acting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LeaderSettings Settings => cfg;

    ISqlLease? sql;
    int handingOver;      // steps-down running (node leaving, final snapshot, release): nothing starts meanwhile
    volatile bool stopping;   // the process is stopping (BeginStop): it takes no lock and starts no acting any more, whatever role it had
    long sqlAcquisitions, sqlLost, sqlYielded, sqlFailures;
    string lastSqlFailure = "";
    DateTime? sqlLostAt;
    string? sqlPreviousHolder;

    /// <summary>Set once by the wiring, before the host starts, where the lease is on (which needs a database). Without one the process has no lease and acts at once.</summary>
    public void UseSqlLease(ISqlLease lease) => sql = lease;
    public bool HasSqlLease => sql != null;
    /// <summary>This process holds the SQL lock right now. The scripts of the schema ran before the lock was first tried (SqlLease.TryAcquireAsync), so the tables exist whenever this is true.</summary>
    public bool SqlHeld => sql is { Held: true };
    /// <summary>The epoch of the SQL term this process took last (0: none, or no lease). It stays after the term ends.</summary>
    public long SqlEpoch => sql?.Epoch ?? 0;
    /// <summary>The epoch the claims of the idempotency ledger and the plant state carry (W0.1): the SQL term; 0 without a lease.</summary>
    public long ClaimEpoch => SqlEpoch;
    public long Epoch => SqlEpoch;
    /// <summary>The Wolverine node may run only here: this process acts and holds the SQL lock (if it has a lease); never while a step-down is running.</summary>
    public bool MayRunNode { get { bool hand; lock (gate) hand = handingOver > 0; return !hand && MayAct; } }

    static string Clean(string s) { var c = new string(s.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-').ToArray()); return c.Length > 40 ? c[..40] : c == "" ? "gw" : c; }

    public bool On => cfg.On;
    public Role State { get { lock (gate) return role; } }
    // the sweep, the ticks and the back-merge trigger run only while this is true (always, without a lease). The lock is part of acting: a holder whose connection dropped acts no more, whatever its role says.
    public bool MayAct { get { lock (gate) { if (role is not (Role.Off or Role.Acting)) return false; } return sql == null || sql.Held; } }
    public bool Replaced { get { lock (gate) return role == Role.Superseded; } }
    public Task ActingTask => acting.Task;
    // called when this process stands down (stop of the node, final snapshot, streams) and when a standby takes the lock again (restore)
    public Func<JsonObject?, Task>? OnSuperseded { get; set; }
    public Func<Task>? OnReacquired { get; set; }
    // W9.10a: called each time this process becomes the one that acts (at its start and when a standby took the lock again; without a lease, at the start), after the plant is restored.
    // The Wolverine node starts here and not earlier: two nodes of one database (Solo: every node is node 1) release each other's rows.
    public Action? OnActing { get; set; }
    // W9.10a: called FIRST in the hand-over (stood down, and the stop of the holder), before the final snapshot is written and before the lock is let go: what must stop acting for the successor to
    // start safely stops here (the Wolverine node: drain, release of its ownership). Bounded by its own timeout; the argument says why.
    public Func<string, Task>? BeforeHandOver { get; set; }
    public async Task RunBeforeHandOverAsync(string why)
    {
        if (BeforeHandOver is not { } f) return;
        try { await f(why); } catch (Exception e) { try { Console.Error.WriteLine($"leader before hand-over: {e.GetType().Name}"); } catch { } }
    }

    static void Log(JsonObject o) { o["at"] = DateTime.UtcNow.ToString("o"); Console.WriteLine(o.ToJsonString()); }

    /// <summary>
    /// The start of a process that acts: takes the SQL lock, trying until it has it (the newest process asks the holder to make room and waits for it to release), and starts the lock's loop. Returns at once
    /// without a lease. A process that loses the start race to a newer one is marked superseded while it waits (the status and /ready say "replaced", not a start that never ends).
    /// </summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        await AcquireSqlAsync(ct);
        StartSqlLoop(ct);
    }

    /// <summary>
    /// Before a process acts for the first time (after the restore): does it still hold the lock? A process that lost it while it restored (a newer one asked, the connection dropped) stands by instead:
    /// the lock is let go (the loop did that when it saw the loss) and the standby loop takes it again when it can. Returns whether it may act.
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        if (sql == null || sql.Held) return true;
        bool marked;
        lock (gate) { marked = role == Role.Starting; if (marked) MarkSuperseded(new JsonObject { ["via"] = "sql", ["why"] = "lost-before-acting" }); }
        if (marked)
        {
            Log(new JsonObject { ["gateway"] = "leader", ["superseded"] = Epoch, ["before"] = "acting", ["via"] = "sql", ["why"] = "lost-before-acting", ["holder"] = Holder });
            await SupersedeAsync(new JsonObject { ["via"] = "sql", ["why"] = "lost-before-acting" });
        }
        return false;
    }

    void MarkSuperseded(JsonObject by) { role = Role.Superseded; supersededAt = DateTime.UtcNow; supersededBy = by; }

    /// <summary>
    /// The process acts from now on: the plant is restored and the lock is held. When the lock went since (or a step-down is running) it does not, and a process that never acted stands by, so that the
    /// standby loop takes the lock again.
    /// </summary>
    public void StartActing()
    {
        bool began = false;
        lock (gate)
        {
            if (stopping) return;
            if (role is Role.Off) began = true;
            else if (role is Role.Starting or Role.Superseded)
            {
                if (handingOver == 0 && (sql == null || sql.Held)) { role = Role.Acting; began = true; }
                else if (role == Role.Starting) MarkSuperseded(new JsonObject { ["via"] = "sql", ["why"] = "lost-before-acting" });
            }
        }
        if (!began) return;
        acting.TrySetResult();
        // W9.10a: only now (lock taken, the predecessor's plant restored) may a node that acts start; outside the lock, and a callback that throws never breaks the lease
        if (MayRunNode) FireActing();
    }

    void FireActing() { try { OnActing?.Invoke(); } catch (Exception e) { Console.Error.WriteLine($"leader acting callback: {e.GetType().Name}"); } }

    /// <summary>The standby's loop: a process that stood down (a newer one took over, or its own lock was lost) takes the lock again when it is free and no newer process asked for it, restores, and acts.</summary>
    public async Task LoopAsync(CancellationToken ct)
    {
        if (sql == null) return;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var failed = false;
            try { await WatchAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { NoteSqlFailure(e); failed = true; }
            failures = failed ? Math.Min(failures + 1, 6) : 0;
            var pause = failed ? Math.Min(60, cfg.SqlCheckSeconds * Math.Pow(2, failures)) : cfg.SqlCheckSeconds;
            try { await Task.Delay(TimeSpan.FromSeconds(pause), ct); } catch (OperationCanceledException) { return; }
        }
    }

    // A standby takes the lock when it is free; a passive try yields to a newer process that asked for it (SqlLease.TryAcquireAsync). Taken, it restores the plant from the database (the stepped-down holder
    // wrote its final state before it let the lock go) and acts - but only if it still holds the lock when the restore is done (StartActing).
    async Task WatchAsync(CancellationToken ct)
    {
        bool standby, handing;
        lock (gate) { standby = role == Role.Superseded; handing = handingOver > 0; }
        if (!standby || handing || sql!.Held) return;
        if (stopping) return;
        var got = await sql.TryAcquireAsync(Holder, false, ct);
        if (!got.Acquired) return;
        SqlTaken(got);
        if (stopping) { await ReleaseSqlAsync(); return; }   // (the stop began meanwhile and has already let go of what it held: this lock would be held by a process that is ending)
        Log(new JsonObject { ["gateway"] = "leader", ["reacquired"] = got.Epoch, ["holder"] = Holder });
        if (OnReacquired is { } f) await f();
        StartActing();
    }

    // The hand-over of a holder that lost the lock before it acted: the callback (node leaves, final snapshot, streams end), then the SQL lock is let go, so that the newcomer's node never starts beside a
    // running one and finds the snapshot written (W9.10b).
    async Task SupersedeAsync(JsonObject? cur)
    {
        lock (gate) handingOver++;
        try { if (OnSuperseded is { } f) await f(cur); }
        finally { if (!stopping) await ReleaseSqlAsync(); lock (gate) handingOver--; }
    }

    // ---- the SQL application lock ---------------------------------------------------------------------------------------------------------------

    int sqlLoopStarted;
    /// <summary>Starts the SQL lock's loop once.</summary>
    public void StartSqlLoop(CancellationToken ct)
    {
        if (sql == null || Interlocked.Exchange(ref sqlLoopStarted, 1) != 0) return;
        _ = Task.Run(() => SqlLoopAsync(ct));
    }

    void NoteSqlFailure(Exception e)
    {
        lock (gate) { sqlFailures++; lastSqlFailure = e is SqlException se ? $"SqlException {se.Number}" : e.GetType().Name; }   // (the type and the number only: a message can name the server)
    }

    void SqlTaken(SqlLeaseAcquire got)
    {
        lock (gate) { sqlAcquisitions++; sqlPreviousHolder = got.PreviousHolder; acquiredAt = DateTime.UtcNow; }
        Log(new JsonObject { ["gateway"] = "leader", ["sqlAcquired"] = got.Epoch, ["holder"] = Holder, ["previous"] = got.PreviousHolder });
    }

    /// <summary>Takes the SQL lock, trying until it has it: a starting process asks the holder to make room (the newest process wins) and waits for it to release. Returns at once when there is no SQL lease.</summary>
    public async Task AcquireSqlAsync(CancellationToken ct, bool request = true)
    {
        if (sql == null) return;
        var pause = 0.0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var failed = false;
            try
            {
                if (stopping) throw new OperationCanceledException(ct);
                var got = await sql.TryAcquireAsync(Holder, request, ct);
                if (got.Acquired) { SqlTaken(got); if (stopping) { await ReleaseSqlAsync(); throw new OperationCanceledException(ct); } return; }
                // a miss that is not the newest process's own ask (it concedes to a newer one, or it only waits its turn): this process has lost the start race and waits behind another, which is what a
                // superseded process is, so it says so (the status and /ready say "replaced" and not a start that never ends)
                if (got.Conceded || !request) MarkLostStartRace();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { NoteSqlFailure(e); failed = true; }
            pause = failed ? Math.Min(30, Math.Max(cfg.SqlCheckSeconds, pause * 2)) : cfg.SqlCheckSeconds;
            await Task.Delay(TimeSpan.FromSeconds(pause), ct);
        }
    }

    void MarkLostStartRace()
    {
        bool marked; Role was;
        lock (gate)
        {
            was = role; marked = role == Role.Starting;
            if (marked) MarkSuperseded(new JsonObject { ["via"] = "sql", ["why"] = "start-race" });
        }
        if (marked) Log(new JsonObject { ["gateway"] = "leader", ["superseded"] = Epoch, ["before"] = was.ToString().ToLowerInvariant(), ["via"] = "sql", ["why"] = "start-race", ["holder"] = Holder });
    }

    /// <summary>The SQL lock's own loop (every leaderSqlCheckSeconds): the holder looks at its lock, and a process that acts without it stands down.</summary>
    public async Task SqlLoopAsync(CancellationToken ct)
    {
        if (sql == null) return;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                if (sql.Held) await SqlCheckAsync(ct);
                else
                {
                    // acting without the lock (a look gave it up, or it went between the standby's take of the lock and its start of acting): the process stands down and takes the lock again as a standby
                    bool actingWithoutLock, handing; lock (gate) { actingWithoutLock = role == Role.Acting; handing = handingOver > 0; }
                    if (actingWithoutLock && !handing) await SqlStepDownAsync("lost", null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { NoteSqlFailure(e); failed = true; }
            // a database that does not answer is not asked (and the scripts are not applied) every few seconds: the pause doubles up to a minute
            failures = failed ? Math.Min(failures + 1, 6) : 0;
            var pause = failed ? Math.Min(60, cfg.SqlCheckSeconds * Math.Pow(2, failures)) : cfg.SqlCheckSeconds;
            try { await Task.Delay(TimeSpan.FromSeconds(pause), ct); } catch (OperationCanceledException) { return; }
        }
    }

    async Task SqlCheckAsync(CancellationToken ct)
    {
        var check = await sql!.CheckAsync(Holder, ct);
        if (check.State == SqlLeaseState.Held) return;
        if (check.State == SqlLeaseState.Lost) { lock (gate) { sqlLost++; sqlLostAt = DateTime.UtcNow; if (check.Failure != null) lastSqlFailure = check.Failure; } }
        else lock (gate) sqlYielded++;
        await SqlStepDownAsync(check.State == SqlLeaseState.Lost ? "lost" : "takeover", check.RequestedBy);
    }

    // The holder's lock is gone (the connection dropped) or a newer process asks for it. The process stops acting: the same hand-over for either (node leaves, final snapshot, streams end), then the lock is
    // released and the process stands by; it takes the lock again when it can. A process that was not acting yet (starting, restoring) only lets the node and the lock go.
    async Task SqlStepDownAsync(string why, string? by)
    {
        bool handOver;
        lock (gate)
        {
            handingOver++;
            handOver = role == Role.Acting;
            if (handOver) MarkSuperseded(new JsonObject { ["holder"] = by, ["via"] = "sql", ["why"] = why });
        }
        Log(new JsonObject { ["gateway"] = "leader", ["sqlStepDown"] = why, ["epoch"] = SqlEpoch, ["by"] = by, ["handOver"] = handOver });
        try
        {
            if (handOver) { if (OnSuperseded is { } f) await f(new JsonObject { ["holder"] = by, ["via"] = "sql" }); }
            else await RunBeforeHandOverAsync("sql-" + why);
        }
        finally { if (!stopping) await ReleaseSqlAsync(); lock (gate) handingOver--; }   // (while stopping, the stop sequence owns the release: it saves the plant first)
    }

    /// <summary>Waits for a step-down that is running (node leaving, final plant, release), at most the time given: the stop of a process that stood down must not let the lock go under it.</summary>
    public async Task WaitForHandOverAsync(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout) { lock (gate) if (handingOver == 0) return; await Task.Delay(50); }
    }

    /// <summary>The stop of a process that stands by: the SQL lock it may still hold, and the takeover request it wrote, are let go. Without it nobody acts until the request goes stale.</summary>
    public Task ReleaseStandbyAsync() => ReleaseSqlAsync();

    /// <summary>Lets the SQL lock go (and ends its connection). A no-op without a SQL lease.</summary>
    public async Task ReleaseSqlAsync()
    {
        if (sql == null) return;
        try { await sql.ReleaseAsync(); } catch (Exception e) { Console.Error.WriteLine($"leader sql release: {e.GetType().Name}"); }
    }

    // At a graceful stop: nothing acts any more. Returns the role the process had (a superseded process stays superseded).
    public Role BeginStop() { lock (gate) { stopping = true; var was = role; if (role is Role.Acting or Role.Starting) role = Role.Stopping; return was; } }

    // for a test that needs a holder that was superseded without a database behind it (WolverineHostServiceTests)
    internal void MarkSupersededForTest() { lock (gate) { if (role == Role.Acting) { role = Role.Superseded; supersededAt = DateTime.UtcNow; } } }

    /// <summary>The "leader" block of /api/v1/status and /api/v1/dashboard. The release check reads <c>role</c> (acting), <c>sql.held</c> (true) and <c>epoch</c> (the SQL term).</summary>
    public JsonObject Status()
    {
        lock (gate)
        {
            var o = new JsonObject
            {
                ["enabled"] = cfg.On, ["role"] = role.ToString().ToLowerInvariant(), ["holder"] = Holder, ["epoch"] = SqlEpoch, ["revision"] = revision, ["replica"] = replica,
                ["acquiredAt"] = acquiredAt == default ? null : acquiredAt.ToString("o"), ["acquisitions"] = sqlAcquisitions,
                ["supersededAt"] = supersededAt?.ToString("o"), ["supersededBy"] = supersededBy?.DeepClone()
            };
            // only where there is a lease; without one the block is the role alone
            if (sql != null)
                o["sql"] = new JsonObject
                {
                    ["held"] = sql.Held, ["epoch"] = sql.Epoch, ["acquisitions"] = sqlAcquisitions, ["lost"] = sqlLost, ["yielded"] = sqlYielded, ["failures"] = sqlFailures,
                    ["lastFailure"] = lastSqlFailure == "" ? null : lastSqlFailure, ["lostAt"] = sqlLostAt?.ToString("o"), ["previousHolder"] = sqlPreviousHolder, ["checkSeconds"] = cfg.SqlCheckSeconds
                };
            return o;
        }
    }
}
