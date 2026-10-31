using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Workflows.PlantState;

// The plant snapshot (PlantSnapshot.cs, enhancement 5; Leader.cs, issue 94): the restore before the first sweep, the write of every change, the final
// write of a hand-over or a stop, and what /api/v1/status shows of it. Moved out of Program.cs unchanged (C4d): the same rule scripts, arguments,
// timeouts, cadence and log lines. LeadershipService decides when each of these runs.
interface ISnapshotCoordinator
{
    // completes once the plant is restored (or the restore gave up); at once when the snapshot is off. The sweeper and the plant manager wait for it.
    Task Restored { get; }
    // the "plantSnapshot" block of /api/v1/status and /api/v1/dashboard
    JsonObject Status();
}

sealed class SnapshotCoordinator : ISnapshotCoordinator
{
    readonly double maxAgeMinutes;
    readonly GatewayLeader leader;
    readonly Func<string, string, TimeSpan, Task<(int Code, string Output)>> run;
    readonly Plant plant;
    readonly SweepGap sweepGap;
    readonly TaskCompletionSource restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly JsonObject state;
    // The epoch whose snapshot holds this process's plant: its own once it wrote one, before that the one it restores from (-1: none). A graceful stop
    // names it in the released lease, so the successor knows which snapshot to accept.
    long heldEpoch = -1;
    readonly SemaphoreSlim write = new(1, 1);
    string lastStructure = "", lastFull = "", tree = ""; DateTime lastSave = DateTime.MinValue; long saves, failures;

    // on: the plant snapshot is on (owner mode, not a suite's second gateway, FACTORY_PLANT_SNAPSHOT not 0); run: the rule-script call (Runner.RunAsync)
    public SnapshotCoordinator(bool on, double maxAgeMinutes, GatewayLeader leader, Func<string, string, TimeSpan, Task<(int Code, string Output)>> run, Plant plant, SweepGap sweepGap)
    {
        On = on; this.maxAgeMinutes = maxAgeMinutes; this.leader = leader; this.run = run; this.plant = plant; this.sweepGap = sweepGap;
        if (!on) restored.SetResult();
        state = new JsonObject { ["enabled"] = on, ["maxAgeMinutes"] = maxAgeMinutes };
    }

    public bool On { get; }
    /// <summary>
    /// W9.10k: where the engine acts the plant is kept in the database (factory.PlantAssignments and the rows beside it, script 240) instead of the snapshot ref: written on every change and at the stop, read first at start. Null in v1
    /// and in the shadow run (tdd), which keep the ref. A database with nothing in it yet (the first start of this version) still reads the ref once.
    /// </summary>
    public IPlantStateStore? Sql { get; set; }
    /// <summary>W9.10k: the correction of a restored plant at start (PlantRestoreCorrection); its result is part of the status's restore block.</summary>
    public Func<Task<JsonObject>>? Correct { get; set; }
    /// <summary>W9.10k: completes when the database's tables exist (the host's upgrade has verified the schema); the restore from the database waits for it, at most two minutes.</summary>
    public Func<CancellationToken, Task>? SchemaReady { get; set; }
    public Task Restored => restored.Task;
    public long HeldEpoch => Interlocked.Read(ref heldEpoch);
    public JsonObject Status() { lock (state) return (JsonObject)state.DeepClone(); }
    // the status says the lease is on (set before the restore runs, as before)
    public void MarkLeader() { lock (state) state["leader"] = true; }

    static DateTime? IsoTime(JsonNode? n) => DateTime.TryParse(n?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    static long LongOf(JsonNode? n, long fallback) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : long.TryParse(n?.ToString(), out var p) ? p : fallback;
    // what changes the plant's hand-over (offers, acceptances, owners, drains, bot holders) and everything else (leases seen by the sweep), as text
    internal static string SnapStructure(JsonObject snap) => new JsonObject
    {
        ["a"] = new JsonArray((snap["assignments"] as JsonArray ?? new JsonArray()).Select(x => (JsonNode)new JsonObject { ["id"] = x?["id"]?.DeepClone(), ["w"] = x?["worker"]?.DeepClone(), ["ok"] = x?["accepted"]?.DeepClone() }).ToArray()),
        ["o"] = snap["owners"]?.DeepClone(), ["d"] = snap["drained"]?.DeepClone(), ["b"] = snap["bots"]?.DeepClone()
    }.ToJsonString();
    internal static string SnapFull(JsonObject snap) { var c = (JsonObject)snap.DeepClone(); c.Remove("at"); return c.ToJsonString(); }

    // ---- without the leader lease ----------------------------------------------------------------------------------------------------------------

    // The restore runs before the first sweep. Without the leader lease (the Sweeper waits for it, at most 60 s): one read, bounded; a failure leaves the
    // plant empty, as every restart did before, and the sweep rebuilds from the board.
    public async Task LoadOnceAsync()
    {
        try
        {
            if (Sql != null && await RestoreFromSqlAsync(null, "start")) return;
            var (code, output) = await run("plant-snapshot-load", "{}", TimeSpan.FromSeconds(45));
            var j = code == 0 ? JsonNode.Parse(output) : null;
            var found = j?["found"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
            var (na, no, skipped) = code != 0 ? (0, 0, "load-failed") : !found ? (0, 0, "none") : plant.Restore(j?["snapshot"], maxAgeMinutes);
            lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["snapshotAt"] = j?["snapshot"]?["at"]?.ToString() };
            // the snapshot is used once: a later start after an outright kill (no SIGTERM, so no new snapshot) must not bring this plant back
            if (found) { var (cc, _) = await run("plant-snapshot-clear", "{}", TimeSpan.FromSeconds(30)); if (cc != 0) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"plant-snapshot\",\"clear\":\"failed\",\"exit\":{cc}}}"); }
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-restore", ["exit"] = code, ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped }.ToJsonString());
        }
        catch (Exception e) { Console.Error.WriteLine($"plant snapshot load: {e.Message}"); }
        finally { restored.TrySetResult(); }
    }

    // Without the lease, at SIGTERM (a release, a scale-in, a replica move) the plant is written before the process ends. The host waits for this
    // callback; the platform's grace period (30 s on Container Apps) bounds it, so the write has 20 s. A snapshot is written only after the restore has
    // run: a process stopped during its start must not overwrite the snapshot it has not read yet with an empty plant.
    public void SaveAtStop()
    {
        try
        {
            if (!restored.Task.IsCompleted) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"gateway\":\"plant-snapshot\",\"skipped\":\"not-restored-yet\"}}"); return; }
            var snap = plant.Snapshot();
            if (Sql != null)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var saved = Sql.Save(snap, leader.ClaimEpoch, leader.Holder, limit.Token).GetAwaiter().GetResult();
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-snapshot", ["store"] = "sql", ["saved"] = saved, ["assignments"] = (snap["assignments"] as JsonArray)?.Count ?? 0, ["owners"] = (snap["owners"] as JsonObject)?.Count ?? 0 }.ToJsonString());
                return;
            }
            var (code, output) = run("plant-snapshot-save", new JsonObject { ["snapshot"] = snap }.ToJsonString(), TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-snapshot", ["exit"] = code, ["assignments"] = (snap["assignments"] as JsonArray)?.Count ?? 0, ["owners"] = (snap["owners"] as JsonObject)?.Count ?? 0 }.ToJsonString());
        }
        catch (Exception e) { try { Console.Error.WriteLine($"plant snapshot save: {e.Message}"); } catch { } }
    }

    // ---- with the leader lease: the snapshot of every change, the hand-over ------------------------------------------------------------------------

    public async Task<bool> SaveAsync(bool final, TimeSpan timeout)
    {
        // one write at a time, and none that waits longer than it may take: at a stop the final write must not queue behind a periodic one
        if (!await write.WaitAsync(timeout)) return false;
        try
        {
            // a periodic write that waited while the process stopped acting is not made: it would land after the final one
            if (!final && !leader.MayAct) return false;
            var plain = plant.Snapshot(); var ep = leader.Epoch;
            var snap = (JsonObject)plain.DeepClone(); snap["epoch"] = ep; snap["holder"] = leader.Holder; snap["final"] = final;
            // every write is guarded: a snapshot of a later epoch (a process that took over) is never overwritten
            int code; var output = "";
            if (Sql != null)
            {
                // W9.10k: the rows of the database, in one transaction; a writer of an older epoch than the stored one is refused (it lost the lock)
                using var limit = new CancellationTokenSource(timeout);
                try { code = await Sql.Save(snap, leader.ClaimEpoch, leader.Holder, limit.Token) ? 0 : 1; }   // (ClaimEpoch: the SQL lock's term where there is one, so it counts in the database's own numbers)
                catch (Exception e) { Console.Error.WriteLine($"plant state save: {(e is OperationCanceledException ? "timeout" : e.GetType().Name)}"); code = 1; }
            }
            else
            {
                var args = new JsonObject { ["snapshot"] = snap, ["guard_epoch"] = ep };
                if (tree != "") args["tree"] = tree;
                (code, output) = await run("plant-snapshot-save", args.ToJsonString(), timeout);
            }
            if (code == 0)
            {
                if (Sql == null) { try { if (JsonNode.Parse(output)?["tree"]?.ToString() is { Length: > 0 } t) tree = t; } catch { } }
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

    // a standby that took the lease again writes one of its own epoch at once
    public void ResetCadence() { lastStructure = ""; lastFull = ""; lastSave = DateTime.MinValue; }

    // Restores the plant from the predecessor's snapshot, and only from its epoch (or, with no predecessor yet, from a snapshot written before the lease
    // existed, which has none). A predecessor that released the lease or whose lease ran out writes nothing more: one read. One that still held it is
    // being superseded right now: wait up to timing.leaderHandoverSeconds for its final snapshot, then take its latest one. The gap (a predecessor that
    // died, or stopped and released) is handed to the first full sweep, which renews the claims that ran out meanwhile.
    public async Task RestoreFromPredecessorAsync(JsonObject? pred, string via)
    {
        try
        {
            if (Sql != null && On && await RestoreFromSqlAsync(pred, via)) return;
            var now = DateTime.UtcNow;
            var predEpoch = LongOf(pred?["epoch"], 0); var released = pred?["released"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
            var predUntil = IsoTime(pred?["until"]);
            var accept = pred == null ? 0 : released && pred["plant_epoch"] is JsonNode pe ? LongOf(pe, -1) : predEpoch;
            Interlocked.Exchange(ref heldEpoch, pred == null ? 0 : accept);
            string how; JsonNode? snap = null; var final = false; var code = 0;
            if (!On) how = "snapshot-off";
            else if (pred == null || released || predUntil == null || predUntil < now)
            {
                how = pred == null ? "no-predecessor" : released ? "released" : "lapsed";
                (code, var output) = await run("plant-snapshot-load", "{}", TimeSpan.FromSeconds(45));
                var j = code == 0 ? JsonNode.Parse(output) : null;
                if (j?["found"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb) snap = j["snapshot"];
            }
            else
            {
                var args = new JsonObject { ["epoch"] = predEpoch, ["wait_seconds"] = leader.Settings.HandoverSeconds, ["until"] = predUntil.Value.ToString("o") };
                (code, var output) = await run("plant-snapshot-await", args.ToJsonString(), TimeSpan.FromSeconds(leader.Settings.HandoverSeconds + 45));
                var j = code == 0 ? JsonNode.Parse(output) : null;
                snap = j?["snapshot"]; final = j?["final"] is JsonValue fl && fl.TryGetValue<bool>(out var flb) && flb;
                how = final ? "handover" : "handover-timeout";
            }
            var snapEpoch = snap is JsonObject so && so.ContainsKey("epoch") ? LongOf(so["epoch"], -2) : 0;
            var usable = snap is JsonObject && On && accept >= 0 && snapEpoch == accept;
            // a snapshot of the predecessor's own epoch is its last word however old (a quiet plant writes none for hours): the age limit is for the
            // snapshot an outright kill left behind, which the epoch already excludes
            var (na, no, skipped) = code != 0 ? (0, 0, "load-failed") : snap == null ? (0, 0, "none") : !usable ? (0, 0, "other-epoch") : plant.Restore(snap, pred == null ? maxAgeMinutes : 60 * 24 * 365);
            if (!(usable && skipped == "")) Interlocked.Exchange(ref heldEpoch, -1);
            // the gap: from the predecessor's release, or its last renewal when it died or handed nothing over, to the first full sweep
            var gapSince = pred == null ? null : released ? IsoTime(pred["released_at"]) : final ? null : IsoTime(pred["renewed_at"]);
            if (gapSince is { } gs && (DateTime.UtcNow - gs).TotalMinutes <= leader.Settings.GapRenewMaxMinutes) sweepGap.Set(gs);
            lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["how"] = how, ["via"] = via,
                ["snapshotAt"] = snap?["at"]?.ToString(), ["snapshotEpoch"] = snap == null ? null : snapEpoch, ["predecessorEpoch"] = pred == null ? null : predEpoch, ["final"] = final, ["gapSince"] = gapSince?.ToString("o") };
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-restore", ["how"] = how, ["exit"] = code, ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["epoch"] = leader.Epoch, ["predecessorEpoch"] = predEpoch, ["gapSince"] = gapSince?.ToString("o") }.ToJsonString());
        }
        catch (Exception e) { Console.Error.WriteLine($"plant restore: {e.Message}"); }
        finally { restored.TrySetResult(); }
    }

    // W9.10k: the restore from the database. The plant the previous term wrote is read at once (the SQL lock fences the terms: a holder that stepped down wrote its last state before it let the lock go, and a dead one wrote its last
    // periodic one), restored whatever its age within a day, and corrected (Correct: the items whose saga completed, the owner the claim says). Returns false when the database holds no plant yet (the first start of this version),
    // so that the ref is read once; true when it handled the restore, also when the database could not be read (the plant then starts empty, as a failed read of the ref leaves it). The gap, if the predecessor did not hand over
    // (its stored state is not marked final), is the same as for the ref.
    async Task<bool> RestoreFromSqlAsync(JsonObject? pred, string via)
    {
        var handled = false;
        try
        {
            PlantStateRead? stored;
            try
            {
                // (the tables are the schema's: the read waits for the host's upgrade of the database where that happens in this process)
                if (SchemaReady != null) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)); await SchemaReady(cts.Token); }
                stored = await Sql!.Load();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"plant state load: {e.GetType().Name}");
                lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = 0, ["owners"] = 0, ["skipped"] = "load-failed", ["how"] = "sql", ["via"] = via };
                Interlocked.Exchange(ref heldEpoch, -1);
                return handled = true;
            }
            if (stored == null) return false;
            handled = true;
            var (na, no, skipped) = plant.Restore(stored.Snapshot, 60 * 24);
            Interlocked.Exchange(ref heldEpoch, skipped == "" ? stored.Epoch : -1);
            var corrected = skipped == "" ? await CorrectAsync() : null;
            var final = stored.Snapshot["final"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
            var released = pred?["released"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
            var gapSince = pred == null ? null : released ? IsoTime(pred["released_at"]) : final ? null : IsoTime(pred["renewed_at"]);
            if (gapSince is { } gs && (DateTime.UtcNow - gs).TotalMinutes <= leader.Settings.GapRenewMaxMinutes) sweepGap.Set(gs);
            lock (state) state["restore"] = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["how"] = "sql", ["via"] = via, ["snapshotAt"] = stored.Snapshot["at"]?.ToString(),
                ["snapshotEpoch"] = stored.Epoch, ["predecessorEpoch"] = pred == null ? null : LongOf(pred["epoch"], 0), ["final"] = final, ["gapSince"] = gapSince?.ToString("o"), ["corrected"] = corrected };
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "plant-restore", ["how"] = "sql", ["assignments"] = na, ["owners"] = no, ["skipped"] = skipped, ["epoch"] = leader.Epoch, ["corrected"] = corrected?.DeepClone() }.ToJsonString());
            return true;
        }
        catch (Exception e) { Console.Error.WriteLine($"plant restore (sql): {e.Message}"); handled = true; return true; }
        finally { if (handled) restored.TrySetResult(); }
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
                    await SaveAsync(false, TimeSpan.FromSeconds(60));
            }
            catch (Exception e) { Console.Error.WriteLine($"plant snapshot loop: {e.Message}"); }
        }
    }
}
