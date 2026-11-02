using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Workflows.PlantState;

// The plant in the database (PlantSnapshot.cs makes and takes the document; src/Fworker.Workflows/PlantState keeps it in factory.PlantState and the rows beside it, script 240; Leader.cs, issue 94): the restore
// before the first sweep, the write of every change, the final write of a hand-over or a stop, and what /api/v1/status shows of it. LeadershipService decides when each of these runs. Since V3.2 this is the only
// copy of the plant: the snapshot ref of the work-orders repository and the rule scripts that wrote it are gone. The SQL lock fences the terms: a holder that stepped down wrote its last state before it let the lock
// go, a dead one wrote its last periodic one, and a writer of an older epoch than the stored one is refused (PlantStateStore.Save).
interface ISnapshotCoordinator
{
    // completes once the plant is restored (or the restore gave up); at once when the snapshot is off. The sweeper and the plant manager wait for it.
    Task Restored { get; }
    // the "plantSnapshot" block of /api/v1/status and /api/v1/dashboard
    JsonObject Status();
}

sealed class SnapshotCoordinator : ISnapshotCoordinator
{
    readonly GatewayLeader leader;
    readonly IPlantStateStore? store;
    readonly Plant plant;
    readonly SweepGap sweepGap;
    readonly TaskCompletionSource restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly JsonObject state;
    // The epoch whose state holds this process's plant: its own once it wrote one, before that the one it restored from (-1: none).
    long heldEpoch = -1;
    readonly SemaphoreSlim write = new(1, 1);
    string lastStructure = "", lastFull = ""; DateTime lastSave = DateTime.MinValue; long saves, failures;
    // The stored plant could not be read at the start: this process runs on an empty plant (the sweep rebuilds it from the board) and writes none, so that the plant of the term before survives for the next restore
    // instead of being overwritten with an empty one.
    volatile bool loadFailed;

    /// <param name="on">The plant is kept (owner mode, not a suite's second gateway, FACTORY_PLANT_SNAPSHOT not 0); it also needs a store: without a database there is none, and the plant starts empty and is rebuilt from the board.</param>
    public SnapshotCoordinator(bool on, GatewayLeader leader, IPlantStateStore? store, Plant plant, SweepGap sweepGap)
    {
        On = on && store != null; this.leader = leader; this.store = store; this.plant = plant; this.sweepGap = sweepGap;
        if (!On) restored.SetResult();
        state = new JsonObject { ["enabled"] = On };
    }

    public bool On { get; }
    /// <summary>The pause between the tries of a failed read of the stored plant (a test shortens it).</summary>
    internal TimeSpan RetryPause { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>The correction of a restored plant at start (PlantRestoreCorrection); its result is part of the status's restore block.</summary>
    public Func<Task<JsonObject>>? Correct { get; set; }
    /// <summary>Completes when the database's tables exist (the host's upgrade has verified the schema); the restore waits for it, at most two minutes.</summary>
    public Func<CancellationToken, Task>? SchemaReady { get; set; }
    /// <summary>
    /// When the restore may read: the host's upgrade has verified the schema, or this process holds the SQL lock, which it takes only after it applied the scripts (SqlLease.TryAcquireAsync). The Wolverine host (whose
    /// start applies and verifies the schema) begins only after the restore (GatewayLeader.OnActing), so waiting for the host alone would wait out the whole two minutes of the restore and then restore nothing:
    /// a newer process would start with an empty plant, no snapshot, no owner, and a sweep two minutes late.
    /// </summary>
    internal static async Task WaitForTablesAsync(FactorySchemaStatus schema, GatewayLeader leader, CancellationToken ct)
    {
        while (!schema.IsGood && !leader.SqlHeld) await Task.Delay(100, ct);
    }

    public Task Restored => restored.Task;
    public long HeldEpoch => Interlocked.Read(ref heldEpoch);
    public JsonObject Status() { lock (state) return (JsonObject)state.DeepClone(); }
    // the status says the lease is on (set before the restore runs, as before)
    public void MarkLeader() { lock (state) state["leader"] = true; }

    static DateTime? IsoTime(JsonNode? n) => DateTime.TryParse(n?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    // what changes the plant's hand-over (offers, acceptances, owners, drains, bot holders) and everything else (leases seen by the sweep), as text
    internal static string SnapStructure(JsonObject snap) => new JsonObject
    {
        ["a"] = new JsonArray((snap["assignments"] as JsonArray ?? new JsonArray()).Select(x => (JsonNode)new JsonObject { ["id"] = x?["id"]?.DeepClone(), ["w"] = x?["worker"]?.DeepClone(), ["ok"] = x?["accepted"]?.DeepClone() }).ToArray()),
        ["o"] = snap["owners"]?.DeepClone(), ["d"] = snap["drained"]?.DeepClone(), ["b"] = snap["bots"]?.DeepClone()
    }.ToJsonString();
    internal static string SnapFull(JsonObject snap) { var c = (JsonObject)snap.DeepClone(); c.Remove("at"); return c.ToJsonString(); }

    // ---- without the lease (a process with no database to arbitrate, or the lease off: it acts at once) -----------------------------------------------

    // The restore runs before the first sweep. Without the lease (the Sweeper waits for it, at most 60 s): one read, bounded; a failure leaves the plant empty, as every restart did before, and the sweep
    // rebuilds from the board.
    public async Task LoadOnceAsync()
    {
        try { await RestoreAsync("start"); }
        catch (Exception e) { Console.Error.WriteLine($"plant snapshot load: {e.Message}"); }
        finally { restored.TrySetResult(); }
    }

    // Without the lease, at SIGTERM (a release, a scale-in, a replica move) the plant is written before the process ends. The host waits for this callback; the platform's grace period (30 s on Container
    // Apps) bounds it, so the write has 20 s. A snapshot is written only after the restore has run: a process stopped during its start must not overwrite the state it has not read yet with an empty plant.
    public void SaveAtStop()
    {
        try
        {
            if (!restored.Task.IsCompleted || store == null || loadFailed) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"plant-snapshot\",\"skipped\":\"{(loadFailed ? "load-failed" : "not-restored-yet")}\"}}"); return; }
            var snap = plant.Snapshot();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var saved = store.Save(snap, leader.ClaimEpoch, leader.Holder, limit.Token).GetAwaiter().GetResult();
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-snapshot", ["store"] = "sql", ["saved"] = saved, ["assignments"] = (snap["assignments"] as JsonArray)?.Count ?? 0, ["owners"] = (snap["owners"] as JsonObject)?.Count ?? 0 }.ToJsonString());
        }
        catch (Exception e) { try { Console.Error.WriteLine($"plant snapshot save: {e.Message}"); } catch { } }
    }

    // ---- with the lease: the snapshot of every change, the hand-over -------------------------------------------------------------------------------

    public async Task<bool> SaveAsync(bool final, TimeSpan timeout)
    {
        if (store == null) return false;
        if (loadFailed) { Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-snapshot", ["skipped"] = "load-failed", ["final"] = final }.ToJsonString()); return false; }
        // one write at a time, and none that waits longer than it may take: at a stop the final write must not queue behind a periodic one
        if (!await write.WaitAsync(timeout)) return false;
        try
        {
            // a periodic write that waited while the process stopped acting is not made: it would land after the final one
            if (!final && !leader.MayAct) return false;
            var plain = plant.Snapshot(); var ep = leader.ClaimEpoch;
            var snap = (JsonObject)plain.DeepClone(); snap["epoch"] = ep; snap["holder"] = leader.Holder; snap["final"] = final;
            // the rows of the database, in one transaction; a writer of an older epoch than the stored one is refused (it lost the lock)
            int code;
            using (var limit = new CancellationTokenSource(timeout))
            {
                try { code = await store.Save(snap, ep, leader.Holder, limit.Token) ? 0 : 1; }
                catch (Exception e) { Console.Error.WriteLine($"plant state save: {(e is OperationCanceledException ? "timeout" : e.GetType().Name)}"); code = 1; }
            }
            if (code == 0)
            {
                lastSave = DateTime.UtcNow; lastStructure = SnapStructure(plain); lastFull = SnapFull(plain); Interlocked.Exchange(ref heldEpoch, ep); saves++;
            }
            else failures++;
            if (final || code != 0) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-snapshot", ["exit"] = code, ["final"] = final, ["epoch"] = ep, ["assignments"] = (snap["assignments"] as JsonArray)?.Count ?? 0, ["owners"] = (snap["owners"] as JsonObject)?.Count ?? 0 }.ToJsonString());
            lock (state) { state["saves"] = saves; state["saveFailures"] = failures; state["lastSave"] = lastSave == DateTime.MinValue ? null : lastSave.ToString("o"); state["heldEpoch"] = Interlocked.Read(ref heldEpoch); }
            return code == 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"plant snapshot save: {e.Message}"); return false; }
        finally { write.Release(); }
    }

    // a superseded process at its stop: its final snapshot was written when it stood down; one still being written is given its time
    public void WaitForWrite(TimeSpan timeout) { if (write.Wait(timeout)) write.Release(); }

    // a standby that took the lock again writes one of its own epoch at once
    public void ResetCadence() { lastStructure = ""; lastFull = ""; lastSave = DateTime.MinValue; }

    /// <summary>
    /// The restore from the database, before this process acts. The plant the previous term wrote is read at once (the SQL lock fences the terms: a holder that stepped down wrote its last state before it let the
    /// lock go, and a dead one wrote its last periodic one), restored whatever its age within a day, and corrected (Correct: the items whose saga completed, the owner the claim says). A database that holds no plant
    /// yet (the first start) restores nothing; one that cannot be read leaves the plant empty and the sweep rebuilds it from the board. The gap, from the stored state to this start, is handed to the first full
    /// sweep, which renews the claims that ran out meanwhile (a hand-over between live processes leaves a gap of seconds, which renews nothing).
    /// </summary>
    public async Task RestoreAsync(string via)
    {
        if (!On) { restored.TrySetResult(); return; }
        try
        {
            PlantStateRead? stored = null; Exception? failed = null;
            // (the tables are the schema's: the read waits for the host's upgrade of the database where that happens in this process; a read that fails is tried again, a few times, before the plant starts empty)
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (SchemaReady != null) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)); await SchemaReady(cts.Token); }
                    stored = await store!.Load(); failed = null; break;
                }
                catch (Exception e) { failed = e; if (attempt < 3) await Task.Delay(RetryPause); }
            }
            if (failed != null)
            {
                loadFailed = true;
                if (via == "reacquired") plant.ClearForRestore();
                Console.Error.WriteLine($"plant state load: {failed.GetType().Name}");
                lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = 0, ["owners"] = 0, ["skipped"] = "load-failed", ["how"] = "sql", ["via"] = via };
                Interlocked.Exchange(ref heldEpoch, -1);
                return;
            }
            if (stored == null)
            {
                loadFailed = false;
                lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = 0, ["owners"] = 0, ["skipped"] = "none", ["how"] = "sql", ["via"] = via };
                Interlocked.Exchange(ref heldEpoch, -1);
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-restore", ["how"] = "sql", ["skipped"] = "none", ["epoch"] = leader.Epoch }.ToJsonString());
                return;
            }
            loadFailed = false;
            if (via == "reacquired") plant.ClearForRestore();   // the plant of this process's earlier term is older than the stored one: the stored one replaces it, it is not merged into it
            var (na, no, skipped) = plant.Restore(stored.Snapshot, 60 * 24);
            Interlocked.Exchange(ref heldEpoch, skipped == "" ? stored.Epoch : -1);
            var corrected = skipped == "" ? await CorrectAsync() : null;
            var final = stored.Snapshot["final"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
            var gapSince = IsoTime(stored.Snapshot["at"]) ?? stored.SavedAt.UtcDateTime;
            if ((DateTime.UtcNow - gapSince).TotalMinutes <= leader.Settings.GapRenewMaxMinutes) sweepGap.Set(gapSince); else gapSince = default;
            lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["how"] = "sql", ["via"] = via, ["snapshotAt"] = stored.Snapshot["at"]?.ToString(),
                ["snapshotEpoch"] = stored.Epoch, ["final"] = final, ["gapSince"] = gapSince == default ? null : gapSince.ToString("o"), ["corrected"] = corrected };
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-restore", ["how"] = "sql", ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["epoch"] = leader.Epoch, ["corrected"] = corrected?.DeepClone() }.ToJsonString());
        }
        catch (Exception e) { Console.Error.WriteLine($"plant restore: {e.Message}"); }
        finally { restored.TrySetResult(); }
    }

    async Task<JsonObject?> CorrectAsync()
    {
        if (Correct == null) return null;
        try { return await Correct().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (Exception e) { Console.Error.WriteLine($"plant restore correction: {e.GetType().Name}"); return new JsonObject { ["error"] = e.GetType().Name }; }
    }

    // the snapshot of every change: a change of the hand-over at most every plantSnapshotDebounceSeconds (30), any other change at most every
    // plantSnapshotRefreshMinutes (10); the first right after the restore, with this process's epoch
    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, leader.Settings.SnapshotDebounceSeconds)), ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (!leader.MayAct || !restored.Task.IsCompleted) continue;
                var snap = plant.Snapshot(); var since = (DateTime.UtcNow - lastSave).TotalSeconds;
                if ((since >= leader.Settings.SnapshotDebounceSeconds && SnapStructure(snap) != lastStructure) || (since >= leader.Settings.SnapshotRefreshMinutes * 60 && SnapFull(snap) != lastFull))
                    await SaveAsync(false, TimeSpan.FromSeconds(20));
            }
            catch (Exception e) { Console.Error.WriteLine($"plant snapshot loop: {e.Message}"); }
        }
    }
}
