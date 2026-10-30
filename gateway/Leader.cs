using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

// One acting gateway across releases (issue 94). In single-revision mode the platform starts the new revision before it stops the old one, so for a
// while two processes run: before this, both swept the board, the new one restored no plant (the old one wrote it only at its SIGTERM, later), and
// sessions kept their stream on the old one while their calls reached the new one (#68). The item lock is a file of one container and excludes
// nothing across the two.
//
// The lease (plugin/scripts/lib/Leader.ps1) is a ref in the work-orders repository whose commit message is the lease record; every change is a
// compare-and-swap with an epoch that only grows. A starting process takes the lease at once with the next epoch (the newest process acts). Only
// the holder acts: it sweeps, ticks (offers that lapse, lease take-backs, QC), runs the back-merge trigger and writes its plant snapshot. It renews
// the lease every timing.leaderRenewSeconds (120; the lease runs timing.leaderLeaseSeconds, 360) and between renewals checks it every
// timing.leaderCheckSeconds (15) with one conditional read (an unchanged ref answers 304, which GitHub does not count against the rate limit).
//
// A holder that finds a later epoch is superseded: it stops acting, first its Wolverine node (W9.10a: drained and released, so the successor's node never starts beside
// a running one), then writes its final snapshot (tagged with its epoch), ends its streams with
// stream.replaced (reconnect: the session's client connects again, and the platform routes it to the new process) and answers every further call
// 503 gateway-replaced. It stays a standby: when the lease is released (a graceful stop of the holder) or runs out (the holder died), it takes the
// lease again, so the last process standing always acts. A process that stops gracefully while it holds the lease writes its final snapshot and
// releases the lease, so its successor does not wait (its node has stopped before either: BeforeHandOver).
//
// Beside the ref (W9.10b): where the gateway has a database, the same process must also hold the SQL application lock (SqlLease.cs, factory.Leadership). The Wolverine node starts only on the holder of
// both, and where the engine acts (V2) the lock is part of acting itself (MayAct): a holder whose connection drops, or that is asked to make room by a newer process, stops its node, saves the plant and
// stands by (the same hand-over as a superseded holder); the newest process asks, the holder releases, the newcomer takes the lock with the next epoch and then the ref. In the shadow run (v1 acts, the
// node is inert) the lock gates the node only, so that a database outage never stops v1. A process without a database (v1 under the kill switch, uat, prod) has no SQL lease and is arbitrated by the ref alone.
sealed class LeaderSettings
{
    public bool On;
    public double LeaseSeconds = 360, RenewSeconds = 120, CheckSeconds = 15, HandoverSeconds = 90, GapRenewMaxMinutes = 120, SnapshotDebounceSeconds = 30, SnapshotRefreshMinutes = 10;
    /// <summary>W9.10b: how often the holder looks at its SQL lock (a dropped connection, a takeover request) and a starting process tries to take it. A takeover request is fresh for four of these (at least 20 s).</summary>
    public double SqlCheckSeconds = 5;
    public int SqlRequestFreshSeconds => (int)Math.Max(20, Math.Ceiling(SqlCheckSeconds * 4));

    public static LeaderSettings Load(string scriptsDir, bool on)
    {
        var path = Environment.GetEnvironmentVariable("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? t = null; try { t = JsonNode.Parse(File.ReadAllText(path))?["timing"]; } catch { }
        static bool Num(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v > 0;
        double D(string k, double d) => Num(Environment.GetEnvironmentVariable("FACTORY_TIMING_" + k), out var e) ? e : Num(t?[k]?.ToString(), out var v) ? v : d;
        var s = new LeaderSettings
        {
            On = on, LeaseSeconds = D("leaderLeaseSeconds", 360), RenewSeconds = D("leaderRenewSeconds", 120), CheckSeconds = D("leaderCheckSeconds", 15),
            HandoverSeconds = D("leaderHandoverSeconds", 90), GapRenewMaxMinutes = D("gapRenewMaxMinutes", 120),
            SnapshotDebounceSeconds = D("plantSnapshotDebounceSeconds", 30), SnapshotRefreshMinutes = D("plantSnapshotRefreshMinutes", 10),
            SqlCheckSeconds = D("leaderSqlCheckSeconds", 5)
        };
        // a renewal must come well inside the lease
        if (s.RenewSeconds > s.LeaseSeconds / 2) s.RenewSeconds = s.LeaseSeconds / 2;
        return s;
    }
}

/// <summary>The rule-script call the lease makes (Runner implements it; a test stands in for the GitHub ref).</summary>
interface IRuleScriptCalls
{
    Task<(int Code, string Output)> RunAsync(string verb, string stdin, TimeSpan timeout);
}

sealed class GatewayLeader(IRuleScriptCalls runner, LeaderSettings cfg, string version) : ILeadership
{
    public enum Role { Off, Starting, Acting, Superseded, Stopping }
    readonly object gate = new();
    Role role = cfg.On ? Role.Starting : Role.Off;
    // this process: unique per start; the platform's revision and replica names make the log lines readable
    public readonly string Holder = $"{Clean(Environment.MachineName)}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";
    readonly string revision = Clean(Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? ""), replica = Clean(Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? Environment.MachineName);
    long epoch; string sha = "", tree = "", etag = "";
    DateTime acquiredAt, renewedAt, until;
    JsonObject? predecessor, supersededBy, standbyCurrent;
    DateTime? supersededAt;
    long renewals, checks, failures, acquisitions;
    readonly TaskCompletionSource acting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LeaderSettings Settings => cfg;

    // ---- W9.10b: the SQL application lock beside the ref ------------------------------------------------------------------------------------------
    ISqlLease? sql;
    bool sqlFences;       // the engine acts (V2): the lock is part of acting; false in the shadow run, where it gates the node only
    int handingOver;      // steps-down running (node leaving, final snapshot, release): nothing starts meanwhile
    long sqlAcquisitions, sqlLost, sqlYielded, sqlFailures;
    string lastSqlFailure = "";
    DateTime? sqlLostAt;
    string? sqlPreviousHolder;

    /// <summary>Set once by the wiring, before the host starts, where a database is configured and the lease is on. fencesActing: the engine acts, so MayAct needs the lock.</summary>
    public void UseSqlLease(ISqlLease lease, bool fencesActing) { sql = lease; sqlFences = fencesActing; }
    public bool HasSqlLease => sql != null;
    public bool SqlFences => sql != null && sqlFences;
    /// <summary>The epoch of the SQL term this process took last (0: none, or no database).</summary>
    public long SqlEpoch => sql?.Epoch ?? 0;
    /// <summary>The epoch the claims of the idempotency ledger carry (W0.1): the SQL term where there is one, else the ref's.</summary>
    public long ClaimEpoch => sql is { Epoch: > 0 } s ? s.Epoch : Epoch;
    /// <summary>The Wolverine node may run only here: this process acts on the ref and, where it has a database, holds the SQL lock; never while a step-down is running.</summary>
    public bool MayRunNode { get { bool hand; lock (gate) hand = handingOver > 0; return !hand && MayAct && (sql == null || sql.Held); } }

    static string Clean(string s) { var c = new string(s.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-').ToArray()); return c.Length > 40 ? c[..40] : c == "" ? "gw" : c; }

    public bool On => cfg.On;
    public Role State { get { lock (gate) return role; } }
    // the sweep, the ticks and the back-merge trigger run only while this is true (always, when the lease is off)
    // (W9.10b: where the engine acts and there is a database, also only while the SQL lock is held)
    public bool MayAct { get { lock (gate) { if (role is not (Role.Off or Role.Acting)) return false; } return !sqlFences || sql == null || sql.Held; } }
    public bool Replaced { get { lock (gate) return role == Role.Superseded; } }
    public long Epoch { get { lock (gate) return epoch; } }
    // The lease this process holds or held last, for the rule scripts that act on the board (Runner.InjectLease, W0.1): its epoch stays what this process took
    // even after it was superseded, which is what makes the scripts refuse their writes. Null while the lease is off or not yet taken.
    public (long Epoch, string Holder)? FenceLease { get { lock (gate) return cfg.On && epoch > 0 ? (epoch, Holder) : null; } }
    public Task ActingTask => acting.Task;
    // called when this process finds itself superseded (stop, final snapshot, streams) and when a standby takes the lease again (restore)
    public Func<JsonObject?, Task>? OnSuperseded { get; set; }
    public Func<JsonObject?, Task>? OnReacquired { get; set; }
    // W9.10a: called each time this process becomes the one that acts (at its start and when a standby took the lease again; with the lease off, at the start), after the
    // plant is restored. The Wolverine node starts here and not earlier: two nodes of one database (Solo: every node is node 1) release each other's rows.
    public Action? OnActing { get; set; }
    // W9.10a: called FIRST in the hand-over (superseded, and the stop of the holder), before the final snapshot is written and before the lease is let go: what must stop
    // acting for the successor to start safely stops here (the Wolverine node: drain, release of its ownership). Bounded by its own timeout; the argument says why.
    public Func<string, Task>? BeforeHandOver { get; set; }
    public async Task RunBeforeHandOverAsync(string why)
    {
        if (BeforeHandOver is not { } f) return;
        try { await f(why); } catch (Exception e) { try { Console.Error.WriteLine($"leader before hand-over: {e.GetType().Name}"); } catch { } }
    }

    JsonObject Args(string action)
    {
        var o = new JsonObject { ["action"] = action, ["holder"] = Holder, ["ttl_seconds"] = cfg.LeaseSeconds, ["version"] = version, ["revision"] = revision, ["replica"] = replica };
        lock (gate) { if (epoch > 0) o["epoch"] = epoch; if (tree != "") o["tree"] = tree; if (sha != "") o["sha"] = sha; if (etag != "") o["etag"] = etag; }
        return o;
    }
    static DateTime? Time(JsonNode? n) => DateTime.TryParse(n?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    static void Log(JsonObject o) { o["at"] = DateTime.UtcNow.ToString("o"); Console.WriteLine(o.ToJsonString()); }

    // Takes the lease with the next epoch; tries until it has it (a gateway that cannot write the lease cannot act anyway). Returns the
    // predecessor's record (null: there was none).
    public async Task<JsonObject?> AcquireAsync(CancellationToken ct, bool requireLock = false)
    {
        var pause = 2.0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // W9.10b: where the engine acts the ref is never written without the SQL lock (a newer process may have asked for it, or the connection may have dropped)
            if (requireLock && sql is { Held: false }) throw new SqlLockLostException();
            var (code, output) = await runner.RunAsync("leader-lease", Args("acquire").ToJsonString(), TimeSpan.FromSeconds(60));
            JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
            if (code == 0 && j?["epoch"] is JsonValue ev && ev.TryGetValue<long>(out var e))
            {
                var pred = j["predecessor"] as JsonObject;
                lock (gate)
                {
                    epoch = e; sha = j["sha"]?.ToString() ?? ""; tree = j["tree"]?.ToString() ?? tree; etag = "";
                    acquiredAt = renewedAt = DateTime.UtcNow; until = Time(j["until"]) ?? DateTime.UtcNow.AddSeconds(cfg.LeaseSeconds);
                    predecessor = pred?.DeepClone() as JsonObject; acquisitions++;
                }
                Log(new JsonObject { ["gateway"] = "leader", ["acquired"] = e, ["holder"] = Holder, ["predecessor"] = pred?.DeepClone() });
                return pred;
            }
            lock (gate) failures++;
            Log(new JsonObject { ["gateway"] = "leader", ["acquire"] = "failed", ["exit"] = code });
            await Task.Delay(TimeSpan.FromSeconds(pause), ct); pause = Math.Min(30, pause * 2);
        }
    }

    public void StartActing()
    {
        bool began;
        lock (gate) { began = role is Role.Starting or Role.Superseded or Role.Off; if (role is Role.Starting or Role.Superseded) role = Role.Acting; }
        acting.TrySetResult();
        // W9.10a: only now (lease taken, the predecessor's snapshot seen, the lease confirmed) may a node that acts start; outside the lock, and a callback that throws never breaks the lease
        // (W9.10b: and the SQL lock held; in the shadow run it may come later, SqlGranted then starts the node)
        if (began && MayRunNode) FireActing();
    }

    void FireActing() { try { OnActing?.Invoke(); } catch (Exception e) { Console.Error.WriteLine($"leader acting callback: {e.GetType().Name}"); } }

    // The lease's own loop: renew or check while acting, watch the lease while superseded.
    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(cfg.CheckSeconds), ct); } catch (OperationCanceledException) { return; }
            try
            {
                var r = State;
                if (r == Role.Acting) await Hold();
                else if (r == Role.Superseded) await Watch(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { lock (gate) failures++; Console.Error.WriteLine($"leader loop: {e.Message}"); }
        }
    }

    // Before a process acts for the first time: is it still the holder? A process superseded while it restored stands by instead.
    public async Task<bool> ConfirmAsync()
    {
        var (code, output) = await runner.RunAsync("leader-lease", Args("check").ToJsonString(), TimeSpan.FromSeconds(60));
        JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
        if (code != 0 || j == null || (j["leader"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb)) return true;   // unreadable: as Hold does, it keeps the lease
        var cur = j["current"] as JsonObject;
        lock (gate) { role = Role.Superseded; supersededAt = DateTime.UtcNow; supersededBy = cur?.DeepClone() as JsonObject; standbyCurrent = cur?.DeepClone() as JsonObject; etag = ""; sha = j["sha"]?.ToString() ?? ""; }
        Log(new JsonObject { ["gateway"] = "leader", ["superseded"] = Epoch, ["by"] = cur?.DeepClone(), ["before"] = "acting" });
        await SupersedeAsync(cur);
        return false;
    }

    async Task Hold()
    {
        bool due; lock (gate) due = (DateTime.UtcNow - renewedAt).TotalSeconds >= cfg.RenewSeconds;
        var (code, output) = await runner.RunAsync("leader-lease", Args(due ? "renew" : "check").ToJsonString(), TimeSpan.FromSeconds(60));
        JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
        if (code != 0 || j == null) { lock (gate) failures++; return; }   // GitHub did not answer: nobody else can take the lease meanwhile either
        var leader = j["leader"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb;
        if (leader)
        {
            lock (gate)
            {
                if (due) { renewals++; renewedAt = DateTime.UtcNow; until = Time(j["until"]) ?? until; etag = ""; } else checks++;
                if (j["sha"]?.ToString() is { Length: > 0 } s) sha = s;
                if (j["etag"]?.ToString() is { Length: > 0 } et) etag = et;
                if (j["tree"]?.ToString() is { Length: > 0 } tr) tree = tr;
            }
            return;
        }
        var cur = j["current"] as JsonObject;
        lock (gate)
        {
            if (role != Role.Acting) return;
            role = Role.Superseded; supersededAt = DateTime.UtcNow; supersededBy = cur?.DeepClone() as JsonObject; standbyCurrent = cur?.DeepClone() as JsonObject; etag = ""; sha = j["sha"]?.ToString() ?? "";
        }
        Log(new JsonObject { ["gateway"] = "leader", ["superseded"] = Epoch, ["by"] = cur?.DeepClone() });
        await SupersedeAsync(cur);
    }

    // The hand-over of a holder that lost the ref (or, in ConfirmAsync, found it lost before it acted): the callback (node leaves, final snapshot, streams end), then the SQL lock is let go, so that the
    // newcomer's node never starts beside a running one and finds the snapshot written (W9.10b).
    async Task SupersedeAsync(JsonObject? cur)
    {
        lock (gate) handingOver++;
        try { if (OnSuperseded is { } f) await f(cur); }
        finally { await ReleaseSqlAsync(); lock (gate) handingOver--; }
    }

    // A standby takes the lease when it was released or ran out; until then it reads the record (conditionally) at every check.
    async Task Watch(CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync("leader-lease", Args("check").ToJsonString(), TimeSpan.FromSeconds(60));
        JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
        if (code != 0 || j == null) { lock (gate) failures++; return; }
        JsonObject? cur;
        lock (gate)
        {
            if (j["unchanged"] is null) { standbyCurrent = j["current"]?.DeepClone() as JsonObject; sha = j["sha"]?.ToString() ?? ""; }
            if (j["etag"]?.ToString() is { Length: > 0 } et) etag = et;
            cur = standbyCurrent;
        }
        var released = cur?["released"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
        var lapsed = cur == null || (Time(cur["until"]) is { } u && u < DateTime.UtcNow);
        bool handing; lock (gate) handing = handingOver > 0;
        if (handing) return;   // a step-down is still running
        var take = released || lapsed;
        if (SqlFences)
        {
            // W9.10b: a process with a database takes the ref only with the SQL lock. While the lock is free, a ref held by the last holder of the lock has no one acting for it (the holder let both go
            // or its connection dropped), and a ref held by this very process has none either (it lost the lock to a newer process that never wrote the ref): the standby does not wait for the ref to
            // run out. The restore still waits for the predecessor's final snapshot as it always does (a holder that stepped down wrote it before it let the lock go). A ref of any other holder (a
            // process without a database, a newer one) is the ref's to decide.
            if (!take)
            {
                try
                {
                    var peek = await sql!.PeekAsync(ct);
                    take = peek.LockFree && cur?["holder"]?.ToString() is { Length: > 0 } refHolder && (refHolder == peek.LastHolder || refHolder == Holder);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) { NoteSqlFailure(e); return; }
            }
            if (!take) return;
            try
            {
                var got = await sql!.TryAcquireAsync(Holder, false, ct);
                if (!got.Acquired) return;
                SqlTaken(got);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { NoteSqlFailure(e); return; }
        }
        if (!take) return;
        lock (gate) { sha = ""; etag = ""; }
        JsonObject? pred;
        try { pred = await AcquireAsync(ct); } catch (OperationCanceledException) { return; }
        Log(new JsonObject { ["gateway"] = "leader", ["reacquired"] = Epoch, ["after"] = released ? "released" : lapsed ? "lapsed" : "sql-lock-free" });
        if (OnReacquired is { } f) await f(pred);
        StartActing();
    }

    // ---- W9.10b ---------------------------------------------------------------------------------------------------------------------------------

    sealed class SqlLockLostException : Exception { }

    /// <summary>
    /// The start of a process that acts: where the engine acts, the SQL lock first (the newest process asks the holder to make room), then the ref, and if the lock is gone once the ref is written (a newer process
    /// asked meanwhile, or the connection dropped) back to the lock, passively this time, before anything is restored or started. Never leaves the ref written by a process that does not hold the lock. Without
    /// a SQL lease, or in the shadow run, it is the ref alone. Returns the predecessor's record of the ref (null: none).
    /// </summary>
    public async Task<JsonObject?> AcquireAllAsync(CancellationToken ct)
    {
        var first = true;
        while (true)
        {
            if (SqlFences)
            {
                await AcquireSqlAsync(ct, request: first); first = false;
                StartSqlLoop(ct);
            }
            try
            {
                var pred = await AcquireAsync(ct, SqlFences);
                if (!SqlFences || sql!.Held) return pred;
            }
            catch (SqlLockLostException) { }
            Log(new JsonObject { ["gateway"] = "leader", ["sqlLostBeforeActing"] = true, ["holder"] = Holder });
        }
    }

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
        lock (gate) { sqlAcquisitions++; sqlPreviousHolder = got.PreviousHolder; }
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
                var got = await sql.TryAcquireAsync(Holder, request, ct);
                if (got.Acquired) { SqlTaken(got); SqlGranted(); return; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { NoteSqlFailure(e); failed = true; }
            pause = failed ? Math.Min(30, Math.Max(cfg.SqlCheckSeconds, pause * 2)) : cfg.SqlCheckSeconds;
            await Task.Delay(TimeSpan.FromSeconds(pause), ct);
        }
    }

    // the lock came after this process already acts on the ref (the shadow run): the node starts now
    void SqlGranted()
    {
        bool fire; lock (gate) fire = role == Role.Acting;
        if (fire && MayRunNode) FireActing();
    }

    /// <summary>The SQL lock's own loop (every leaderSqlCheckSeconds): the holder looks at its lock; in the shadow run a process that acts on the ref and does not hold the lock tries to take it.</summary>
    public async Task SqlLoopAsync(CancellationToken ct)
    {
        if (sql == null) return;
        var first = true; var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                if (sql.Held) await SqlCheckAsync(ct);
                else if (sqlFences)
                {
                    // acting on the ref without the lock (it was let go between the standby's take of the lock and of the ref): the engine must not act, so it stands down and takes both again
                    bool actingWithoutLock, handing; lock (gate) { actingWithoutLock = role == Role.Acting; handing = handingOver > 0; }
                    if (actingWithoutLock && !handing) await SqlStepDownAsync("lost", null);
                }
                else
                {
                    Role r; bool handing; lock (gate) { r = role; handing = handingOver > 0; }
                    if (!handing && r is Role.Starting or Role.Acting)
                    {
                        var got = await sql.TryAcquireAsync(Holder, first, ct);
                        if (got.Acquired) { SqlTaken(got); SqlGranted(); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { NoteSqlFailure(e); failed = true; }
            first = false;
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

    // The holder's lock is gone (the connection dropped) or a newer process asks for it. Where the engine acts the process stops acting: the same hand-over as a superseded holder (node leaves, final snapshot,
    // streams end), then the lock is released and the process stands by; it takes the lock (and the ref) again when it can. In the shadow run v1 keeps acting and only the node leaves.
    async Task SqlStepDownAsync(string why, string? by)
    {
        bool handOver;
        lock (gate)
        {
            handingOver++;
            handOver = sqlFences && role == Role.Acting;
            if (handOver) { role = Role.Superseded; supersededAt = DateTime.UtcNow; supersededBy = new JsonObject { ["holder"] = by, ["via"] = "sql", ["why"] = why }; standbyCurrent = null; etag = ""; }
        }
        Log(new JsonObject { ["gateway"] = "leader", ["sqlStepDown"] = why, ["epoch"] = SqlEpoch, ["by"] = by, ["handOver"] = handOver });
        try
        {
            if (handOver) { if (OnSuperseded is { } f) await f(new JsonObject { ["holder"] = by, ["via"] = "sql" }); }
            else await RunBeforeHandOverAsync("sql-" + why);
        }
        finally { await ReleaseSqlAsync(); lock (gate) handingOver--; }
    }

    /// <summary>Lets the SQL lock go (and ends its connection). A no-op without a SQL lease.</summary>
    public async Task ReleaseSqlAsync()
    {
        if (sql == null) return;
        try { await sql.ReleaseAsync(); } catch (Exception e) { Console.Error.WriteLine($"leader sql release: {e.GetType().Name}"); }
    }

    // At a graceful stop: nothing acts any more. Returns the role the process had (a superseded process stays superseded).
    public Role BeginStop() { lock (gate) { var was = role; if (role is Role.Acting or Role.Starting) role = Role.Stopping; return was; } }
    // ... and the holder releases the lease (until now), naming whose snapshot holds the plant (-1: none), so its successor does not wait
    public async Task<bool> ReleaseAsync(long plantEpoch, TimeSpan timeout)
    {
        if (Epoch == 0) return false;
        var a = Args("release"); a["plant_epoch"] = plantEpoch;
        var (code, _) = await runner.RunAsync("leader-lease", a.ToJsonString(), timeout);
        Log(new JsonObject { ["gateway"] = "leader", ["released"] = Epoch, ["plantEpoch"] = plantEpoch, ["exit"] = code });
        return code == 0;
    }

    // for a test that needs a holder that was superseded without a lease repository behind it (WolverineHostServiceTests)
    internal void MarkSupersededForTest() { lock (gate) { if (role == Role.Acting) { role = Role.Superseded; supersededAt = DateTime.UtcNow; } } }

    public JsonObject? Predecessor { get { lock (gate) return predecessor?.DeepClone() as JsonObject; } }

    public JsonObject Status()
    {
        lock (gate)
        {
            var o = new JsonObject
            {
                ["enabled"] = cfg.On, ["role"] = role.ToString().ToLowerInvariant(), ["holder"] = Holder, ["epoch"] = epoch, ["revision"] = revision, ["replica"] = replica,
                ["acquiredAt"] = acquiredAt == default ? null : acquiredAt.ToString("o"), ["renewedAt"] = renewedAt == default ? null : renewedAt.ToString("o"),
                ["until"] = until == default ? null : until.ToString("o"), ["acquisitions"] = acquisitions, ["renewals"] = renewals, ["checks"] = checks, ["failures"] = failures,
                ["predecessor"] = predecessor?.DeepClone(), ["supersededAt"] = supersededAt?.ToString("o"), ["supersededBy"] = supersededBy?.DeepClone(),
                ["leaseSeconds"] = cfg.LeaseSeconds, ["renewSeconds"] = cfg.RenewSeconds, ["checkSeconds"] = cfg.CheckSeconds, ["handoverSeconds"] = cfg.HandoverSeconds
            };
            // W9.10b: only where there is a database; without one the block is what it was
            if (sql != null)
                o["sql"] = new JsonObject
                {
                    ["held"] = sql.Held, ["epoch"] = sql.Epoch, ["fencesActing"] = sqlFences, ["acquisitions"] = sqlAcquisitions, ["lost"] = sqlLost, ["yielded"] = sqlYielded, ["failures"] = sqlFailures,
                    ["lastFailure"] = lastSqlFailure == "" ? null : lastSqlFailure, ["lostAt"] = sqlLostAt?.ToString("o"), ["previousHolder"] = sqlPreviousHolder, ["checkSeconds"] = cfg.SqlCheckSeconds
                };
            return o;
        }
    }
}
