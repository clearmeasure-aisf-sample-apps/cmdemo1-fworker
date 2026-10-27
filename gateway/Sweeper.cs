using System.Text.Json;
using System.Text.Json.Nodes;

sealed class Sweeper(Runner runner, EventHub hub, Plant plant, QcHub qc, int seconds, bool ownerMode, BaseWatch? baseWatch = null, Task? restored = null) : BackgroundService
{
    // the leader lease (Leader.cs): only the holder sweeps; with the lease on, the first sweep waits for the restore however long the hand-over takes
    public Func<bool>? MayAct { get; init; }
    public bool LeaderOn { get; init; }
    public SweepGap? Gap { get; init; }
    readonly HashSet<(int, string)> flagged = new();
    // issue 92: a full sweep (sync-board over the whole board, about 50 REST calls) runs every `seconds` (180), and at most every MinSeconds (60) when
    // a trigger asks for one. A trigger of one item (a webhook, a verb, a take-back, an unpark) queues the item: between full sweeps the queued items
    // get an incremental pass (sync-board with items: the same rules for those items only, no claim-ref reaping, no QC sync). Before, every trigger
    // started a full sweep 2 s later, so a busy plant swept about once a minute.
    public int MinSeconds { get; init; }
    public GitHubBudget? Budget { get; init; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        // the plant snapshot is restored before the first plan (PlantSnapshot.cs); without the lease a read that hangs holds the sweep back 60 s at most
        if (restored != null) try { await Task.WhenAny(restored, Task.Delay(TimeSpan.FromSeconds(LeaderOn ? 3600 : 60), ct)); } catch (OperationCanceledException) { return; }
        // An item new in a working column is offered by the second sweep that sees it there (timing.sweepSeenCount, so a card dragged across the
        // board starts nothing). That second look must not cost a whole interval: after a sweep that saw such an item it gets a pass of its own
        // after FACTORY_SWEEP_CONFIRM_SECONDS (20). Before, a new item waited up to two intervals (6 minutes at 180 s) for its first offer.
        var confirmSeconds = int.TryParse(Environment.GetEnvironmentVariable("FACTORY_SWEEP_CONFIRM_SECONDS"), out var cs0) && cs0 > 0 ? cs0 : 20;
        var lastFull = DateTime.MinValue; DateTime? confirmAt = null; var confirmItems = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            if (MayAct?.Invoke() == false) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { } continue; }
            try
            {
                var now = DateTime.UtcNow; var sinceFull = (now - lastFull).TotalSeconds;
                var (fullAsked, queued) = hub.PendingSweep();
                var confirmDue = confirmAt is { } ca && ca <= now;
                // a full sweep: its interval is up, or one was asked for and the last is MinSeconds old; without owner mode there is no incremental
                // pass (the broadcast offers are the whole list), so a queued item or a second look asks for a full one
                var full = sinceFull >= seconds || ((fullAsked || (!ownerMode && (queued > 0 || confirmDue))) && sinceFull >= MinSeconds);
                if (full)
                {
                    hub.FullSweepTaken(); confirmItems.Clear(); confirmAt = null; lastFull = now;
                    var unconfirmed = await SweepOnce(null);
                    if (unconfirmed.Count > 0) { confirmItems.UnionWith(unconfirmed); confirmAt = DateTime.UtcNow.AddSeconds(Math.Min(seconds, confirmSeconds)); }
                }
                else if (ownerMode && (queued > 0 || confirmDue))
                {
                    var items = hub.TakeSweepItems();
                    if (confirmDue) { items.AddRange(confirmItems); confirmItems.Clear(); confirmAt = null; }
                    items = items.Distinct().OrderBy(i => i).ToList();
                    // at most 50 in one pass; the rest stay queued for the next one
                    if (items.Count > 50) { foreach (var i in items.Skip(50)) hub.RequestSweep(i); items = items.Take(50).ToList(); }
                    var unconfirmed = await SweepOnce(items);
                    if (unconfirmed.Count > 0) { confirmItems.UnionWith(unconfirmed); confirmAt ??= DateTime.UtcNow.AddSeconds(Math.Min(seconds, confirmSeconds)); }
                }
            }
            // only the host's own stop ends the loop: a cancellation from anywhere else (a timeout inside a call) is an error of this sweep, not the end of sweeping
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":\"error\",\"type\":{JsonSerializer.Serialize(e.GetType().Name)},\"message\":{JsonSerializer.Serialize(e.Message)}}}"); }
            catch (Exception) { return; }
            try
            {
                // until the next thing due: the interval, an asked-for full sweep once MinSeconds have passed, the second look; a queued item at once
                var next = lastFull.AddSeconds(seconds);
                var (fa, q) = hub.PendingSweep();
                if (fa || (!ownerMode && q > 0)) { var m = lastFull.AddSeconds(MinSeconds); if (m < next) next = m; }
                // (without owner mode the second look is a full sweep, so it waits for MinSeconds as well)
                if (confirmAt is { } c2) { var c3 = !ownerMode && c2 < lastFull.AddSeconds(MinSeconds) ? lastFull.AddSeconds(MinSeconds) : c2; if (c3 < next) next = c3; }
                var wait = next - DateTime.UtcNow;
                if (ownerMode && q > 0) wait = TimeSpan.Zero;
                if (wait > TimeSpan.Zero) await hub.WaitForSweepSignal(wait, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine($"sweep wait: {e.Message}"); try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { } }
        }
    }

    // One pass of sync-board: the whole board (items null) or the given items. Returns the items it saw for the first time in their column (unconfirmed).
    readonly HashSet<int> ownerHolds = new(); bool holdsComplete;
    async Task<List<int>> SweepOnce(List<int>? items)
    {
        var unconfirmedOut = new List<int>();
        var low = Budget?.Low().Low == true;
        var args = new JsonObject();
        if (items != null) args["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray());
        if (low) args["low"] = true;   // the budget is below its reserve: no claim-ref reaping, no QC sync (GitHubBudget.cs)
        // free_workers: the auto-unpark of a park caused by session trouble needs a session that could take the item (Verbs.Sweep.ps1)
        if (ownerMode) args["free_workers"] = plant.FreeOwnerSessions();
        // work intake (issue 104): a full sweep may pull a Todo item onto the board when an owner session has a free slot
        if (ownerMode && items == null) args["intake"] = new JsonObject { ["freeSlots"] = plant.IntakeSlots() };
        var sweepStarted = DateTime.UtcNow;   // (an owner released at a merge after this is not put back by this sweep's plan)
        // the first full sweep after a gap (no gateway was up): the owners' claims that ran out in it are renewed, not offered again (issue 94)
        var gapTaken = items == null ? Gap?.Take() : null;
        if (gapTaken is { } gap) args["gap"] = new JsonObject { ["since"] = gap.ToString("o"), ["until"] = DateTime.UtcNow.ToString("o") };
        int code; string output;
        try { (code, output) = await runner.RunAsync("sync-board", args.ToJsonString(), TimeSpan.FromSeconds(280)); }
        catch { if (gapTaken is { } g1) Gap?.Set(g1); throw; }
        // a sweep that failed renewed nothing: the next one gets the gap again
        if (code != 0 && gapTaken is { } g0) Gap?.Set(g0);
        if (items == null) hub.LastSweep = DateTime.UtcNow;
        hub.NoteSweep(items == null, items?.Count ?? 0, low); if (low) Budget?.CountLowSweep();
        var kind = items == null ? "full" : "incremental";
        if (code == 0)
        {
            var j = JsonNode.Parse(output)!;
            foreach (var x in j["unconfirmed"] as JsonArray ?? new JsonArray()) if (x is JsonValue uv && uv.TryGetValue<int>(out var ui) && ui > 0) unconfirmedOut.Add(ui);
            var acts = j["actions"]!.AsArray();
            if (ownerMode)
            {
                // step 5a (worker-messaging.md 3.9): the sweep's `owned` replaces the plant's owner list, except for items the sweep could not read
                // this time and, in an incremental pass, every item it did not look at. A settling item keeps no owner: its owner was released at
                // item.merged (Plant.Merged).
                var keep = new List<int>();
                foreach (var x in j["errors"] as JsonArray ?? new JsonArray()) if (x?["item"] is JsonValue ev && ev.TryGetValue<int>(out var ei)) keep.Add(ei);
                plant.Plan(j["offers"]!.AsArray(), j["owners"] as JsonObject, j["pins"] as JsonObject, j["owned"] as JsonObject, keep, j["priorities"] as JsonObject, sweepStarted, items, j["runtimes"] as JsonObject, j["profiles"] as JsonObject);
                if (items == null && j["replenish"] is JsonObject rpj) plant.SetReplenish(rpj);
                if (items == null && j["design_waiting"] is JsonValue dwv && dwv.TryGetValue<int>(out var dw)) plant.KeeperBacklog(dw);   // a full sweep only: an incremental one reads a few items
                // the hold:owner items of the whole board: a full sweep replaces them, an incremental pass updates its own items; the plant hears
                // them once a full sweep has read the whole board (until then its name actions wait, sweep-pending)
                var holds = (j["holds"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var hi) ? hi : 0).Where(i => i > 0);
                if (items == null) { ownerHolds.Clear(); holdsComplete = true; } else foreach (var i in items) ownerHolds.Remove(i);
                ownerHolds.UnionWith(holds);
                if (holdsComplete) plant.SetOwnerHeld(ownerHolds.ToList());
                plant.SetPassUnmerged(j["pass_unmerged"] as JsonArray, items);
                plant.SetHeld(j["held"] as JsonArray, items);   // issue 65: items that wait for their dependencies
                if (items == null) plant.SetSweepFacts(j["parks"] as JsonArray, j["unconfirmed"] as JsonArray);   // issue 99: why each unowned item waits (a full sweep: it read every item)
                if (j["unparked"] is JsonArray up && up.Count > 0) plant.ClearParks(up.Select(x => int.TryParse(x?.ToString(), out var ui) ? ui : 0));   // issue 82
                // issue 117: what the sweep flags (a Release Queue card without a merge record): one plant event per item and code in this process
                foreach (var f in j["flags"] as JsonArray ?? new JsonArray())
                    if (f is JsonObject fo && fo["item"] is JsonValue fv && fv.TryGetValue<int>(out var fi) && flagged.Add((fi, fo["code"]?.ToString() ?? "")))
                        plant.OperatorPlantEvent(fo["code"]?.ToString() ?? "flagged", fi, "", fo["column"]?.ToString() ?? "");
                // a merge the gateway made after a PASS and the round-trip of one it could not make (Verbs.Merge.ps1 -Gateway)
                foreach (var a in acts)
                    if (a is JsonObject ao && ao["action"]?.ToString() == "pass-merge" && ao["outcome"]?.ToString() == "demoted" && ao["item"] is JsonValue di && di.TryGetValue<int>(out var dItem))
                        plant.NoteMergeRefused(dItem, int.TryParse(ao["roundTrip"]?.ToString(), out var drt) ? drt : 1);
                foreach (var a in acts)   // issue 104: an item the sweep pulled from Todo, for the operator stream
                    if (a is JsonObject io && io["action"]?.ToString() == "intake-pulled" && io["item"] is JsonValue iv && iv.TryGetValue<int>(out var pulled)) plant.OperatorPlantEvent("pulled", pulled, column: io["column"]?.ToString() ?? "");
                // issue 102: a red that only repeats a defect of the base branch is reported once to the operator (it costs the item nothing)
                foreach (var a in acts)
                    if (a is JsonObject mo && mo["item"] is JsonValue mi && mi.TryGetValue<int>(out var mItem)
                        && ((mo["action"]?.ToString() == "awaiting-checks" && mo["result"] is JsonObject mr && mr["outcome"]?.ToString() == "master-defect" && mr["new"] is JsonValue mn && mn.TryGetValue<bool>(out var mNew) && mNew)
                            || (mo["action"]?.ToString() == "pass-merge" && mo["masterDefectNew"] is JsonValue pn && pn.TryGetValue<bool>(out var pNew) && pNew)))
                        plant.OperatorPlantEvent("master-defect", mItem);
                if (items == null)
                {
                    plant.SetReleaseQueue(j["release_queue"] switch { JsonArray ra => ra, JsonObject ro => new JsonArray(ro.DeepClone()), _ => null });   // (PowerShell sends a one-item queue as an object)
                    try { await qc.Reconcile(j["qc"], j["qc_teardowns"], j["qc_approvals"]); } catch (Exception e) { Console.Error.WriteLine($"qc reconcile: {e.Message}"); }
                }
                foreach (var t in j["triggers"]?.AsArray() ?? new JsonArray())
                    if (t is JsonObject to && to["item"] is JsonNode ti)
                    {
                        to["provider"] = "gateway"; plant.Route(ti.GetValue<int>(), (JsonObject)JsonNode.Parse(to.ToJsonString())!);
                        if (to["event"]?.ToString() == "item.complete") plant.Complete(ti.GetValue<int>());
                        if (to["event"]?.ToString() == "item.merged") plant.Merged(ti.GetValue<int>());
                        // issue 65: an item that waits for its dependencies is offered by the next pass once one of them merged or ended, not an interval later
                        if (to["event"]?.ToString() is "item.merged" or "item.complete")
                            foreach (var h in j["held"] as JsonArray ?? new JsonArray()) if (h?["item"] is JsonValue hv && hv.TryGetValue<int>(out var hItem)) hub.RequestSweep(hItem);
                    }
                // the fallback of the back-merge trigger: master's head compared with the last one seen (not awaited: the sweep goes on); not on a
                // low budget, and only after a full sweep
                if (baseWatch != null && items == null && !low) _ = Task.Run(baseWatch.AfterSweep);
                Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":{{\"offers\":{j["offers"]!.AsArray().Count},\"actions\":{acts.Count},\"errors\":{j["errors"]!.AsArray().Count},\"kind\":\"{kind}\",\"items\":{items?.Count ?? -1},\"lowBudget\":{(low ? "true" : "false")},\"elapsedMs\":{(j["elapsed_ms"]?.ToString() ?? "null")},\"steps\":{(j["steps"]?.ToJsonString() ?? "null")},\"skipped\":{(j["skipped"]?.ToJsonString() ?? "[]")}}}}}");
            }
            else
            {
                var offers = new List<string>();
                foreach (var o in j["offers"]!.AsArray())
                    offers.Add(new JsonObject { ["provider"] = "gateway", ["event"] = "work.offered", ["item"] = o!["item"]!.DeepClone(), ["column"] = o["column"]!.DeepClone(), ["reason"] = o["reason"]!.DeepClone() }.ToJsonString());
                hub.SetOffers(offers);
                Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":{{\"offers\":{offers.Count},\"actions\":{acts.Count},\"errors\":{j["errors"]!.AsArray().Count}}}}}");
            }
            foreach (var a in acts) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepAction\":{a!.ToJsonString()}}}");
            foreach (var er in j["errors"]!.AsArray()) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepError\":{er!.ToJsonString()}}}");
        }
        else plant.OperatorPlantEvent("sweep-failed", 0, detail: "exit-" + code);
        // the items of a failed incremental pass are asked for again (the next pass or full sweep takes them)
        if (code != 0 && items != null) foreach (var i in items) hub.RequestSweep(i);
        if (code != 0) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":\"failed\",\"kind\":\"{kind}\",\"exit\":{code},\"out\":{JsonSerializer.Serialize(output.Length > 300 ? output[..300] : output)}}}");
        return unconfirmedOut;
    }
}
