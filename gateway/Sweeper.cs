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
    // W6.1 (hybrid composition): the items that have a WorkItemSaga. Set only where the engine acts; the sweep's answer says nothing about them to the plant, and an incremental pass does not ask for them
    public V2Ownership? V2 { get; init; }
    // W9.10e: the plans of intake.v2Plans, handed to v1's intake (sync-board intake.v2Plans): it leaves their Todo items to the engine's intake. Used only with V2 set
    public IReadOnlyList<string> V2Plans { get; init; } = [];
    // W9.10g: the plant-wide steps (claim-ref reap, board guard, QC sync, replenish, the keeper's backlog count) are recurring jobs of the engine (PlantStepJobs): the sweep leaves them out (skip_steps) and applies none of
    // their results. Set only where the engine acts, together with the jobs.
    public bool PlantJobs { get; init; }
    // W9.10k: the plant's input for the items that have a saga (owners' leases, parks, pins, priorities, runtime labels, the release queue), built from the saga's state and the cards' labels. Set only where the engine acts, with V2.
    public SagaPlantFeed? Feed { get; init; }
    // cutover review: where the engine acts the sweep also waits for the adoption of this process's term (AdoptionGate), and reads the saga's table again before the term's first pass. Set only with V2.
    public AdoptionGate? Adoption { get; init; }
    long? adoptedTerm;
    // cutover review: v1's intake is off. Set where intake.v2Plans names plans and the engine does not act on them (the kill switch, uat and prod, a v2 environment without a database): v1's intake takes only what the plans leave it where the
    // engine acts, and everywhere else it would take everything, which the configuration of the cutover never meant (intake.enabled is true for the image so that the engine's intake runs). The script has no intake without the block.
    public bool IntakeOff { get; init; }

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
            // cutover review: nor until the adoption of this term is over (v1's pass and the adoption have no per-item lock between them), and then the saga's table is read again, because the adoption wrote to it. A failed adoption is tried again by its job; this keeps waiting.
            if (V2 != null && Adoption != null)
            {
                var term = Adoption.Settled();
                if (term is null)
                {
                    WarnWaiting("the adoption of this term has not finished; v1 acts on no item until it has");
                    try { await Adoption.PokeAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } catch (Exception e) { Console.Error.WriteLine($"adoption poke: {e.GetType().Name}"); }
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
                if (adoptedTerm != term)
                {
                    if (!await OwnershipRead(ct)) { try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
                    adoptedTerm = term;
                }
            }
            // W9.10e: where the engine acts, nothing is swept until the saga's table has been read once: an empty set that only means "not read yet" would let v1 act on an item that has a saga. Items asked for meanwhile stay queued.
            if (V2 is { Loaded: false } && !await OwnershipRead(ct)) { try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
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
                    var unconfirmed = await SweepOnce(null, ct);
                    if (unconfirmed.Count > 0) { confirmItems.UnionWith(unconfirmed); confirmAt = DateTime.UtcNow.AddSeconds(Math.Min(seconds, confirmSeconds)); }
                }
                else if (ownerMode && (queued > 0 || confirmDue))
                {
                    var items = hub.TakeSweepItems();
                    if (confirmDue) { items.AddRange(confirmItems); confirmItems.Clear(); confirmAt = null; }
                    items = items.Distinct().OrderBy(i => i).ToList();
                    // at most 50 in one pass; the rest stay queued for the next one
                    if (items.Count > 50) { foreach (var i in items.Skip(50)) hub.RequestSweep(i); items = items.Take(50).ToList(); }
                    var unconfirmed = await SweepOnce(items, ct);
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

    /// <summary>
    /// W9.10e: what the script is told where the engine acts. `skip_items`: the items that have a saga are not v1's, so the script reads and acts on none of them (settle, merge pickup, the PASS merge, park, demote,
    /// auto-unpark, claim-ref reap). `intake.v2Plans` (only in a pass that has an intake block): the plans whose Todo items the engine's intake takes, which v1's intake leaves in the column. Nothing is added without V2.
    /// </summary>
    internal static void AddV2Arguments(JsonObject args, V2Ownership? v2, IReadOnlyList<string> v2Plans, IReadOnlyCollection<int>? left = null)
    {
        if (v2 is null) return;
        // (left: after a break the items the adoption could not fence; the script leaves them alone like the saga's)
        var skip = v2.Items.Concat(left ?? []).Distinct().OrderBy(i => i).ToList();
        if (skip.Count > 0) args["skip_items"] = new JsonArray(skip.Select(i => (JsonNode)i).ToArray());
        if (v2Plans.Count > 0 && args["intake"] is JsonObject intake) intake["v2Plans"] = new JsonArray(v2Plans.Select(p => (JsonNode)p).ToArray());
    }

    // cutover review: after a break, the items the adoption of the term could not fence (AdoptionJob.Excluded): v1 leaves them alone until a run fences them
    IReadOnlyCollection<int> Left() => Adoption is null ? [] : Adoption.Excluded();

    DateTime ownershipWarned = DateTime.MinValue;
    async Task<bool> OwnershipRead(CancellationToken ct)
    {
        try { if (await V2!.RefreshAsync(ct) && V2.Loaded) return true; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        WarnWaiting(V2!.Unavailable ? "v2 is configured for this environment and there is no database; v1 acts on no item" : "the saga table has not been read yet; v1 acts on no item until it has");
        return false;
    }

    void WarnWaiting(string reason)
    {
        if (DateTime.UtcNow - ownershipWarned > TimeSpan.FromMinutes(1)) { ownershipWarned = DateTime.UtcNow; Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":\"waiting\",\"reason\":{JsonSerializer.Serialize(reason)}}}"); }
    }

    // W6.6: a pass over items that all have a saga: nothing to read for v1, the plant takes the saga's offers for exactly those items (scope), as it takes the offers of an incremental pass
    void PlanV2Offers(List<int> mine)
    {
        // (W9.10j: the same offers as the saga's own session start makes, from the saga's state; the labels of the items stay)
        try { plant.PlanSagaOffers(new JsonArray(V2!.OfferNodes(plant.HasOwner, mine).ToArray()), mine); }
        catch (Exception e) { Console.Error.WriteLine($"v2 offers: {e.GetType().Name}"); }
    }

    // One pass of sync-board: the whole board (items null) or the given items. Returns the items it saw for the first time in their column (unconfirmed).
    readonly HashSet<int> ownerHolds = new(); bool holdsComplete;
    async Task<List<int>> SweepOnce(List<int>? items, CancellationToken ct = default)
    {
        var unconfirmedOut = new List<int>();
        if (V2 != null)
        {
            await V2.RefreshAsync(ct);
            if (items != null)
            {
                var left = Left();
                var mine = items.Where(V2.Owns).ToList();
                items = items.Where(i => !V2.Owns(i) && !left.Contains(i)).ToList();
                // W6.6: the pass named only the saga's items. v1 has nothing to read for them, but the plant still takes the saga's offers for them (an item whose step just began waits for a session)
                if (items.Count == 0) { if (ownerMode && mine.Count > 0) PlanV2Offers(mine); return unconfirmedOut; }
            }
        }
        var low = Budget?.Low().Low == true;
        var args = new JsonObject();
        if (items != null) args["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray());
        if (low) args["low"] = true;   // the budget is below its reserve: no claim-ref reaping, no QC sync (GitHubBudget.cs)
        // free_workers: the auto-unpark of a park caused by session trouble needs a session that could take the item (Verbs.Sweep.ps1)
        if (ownerMode) args["free_workers"] = plant.FreeOwnerSessions();
        // work intake (issue 104): a full sweep may pull a Todo item onto the board when an owner session has a free slot
        if (ownerMode && items == null && !IntakeOff) args["intake"] = new JsonObject { ["freeSlots"] = plant.IntakeSlots() };
        AddV2Arguments(args, V2, V2Plans, Left());
        if (PlantJobs) args["skip_steps"] = new JsonArray(PlantStepJobs.SweepSkips.Select(x => (JsonNode)x).ToArray());
        // replenish backpressure (delivery-speed.md 6): the waiting items of the last plan; absent before the first plan has run (Replenish.ps1 then holds back)
        if (ownerMode && items == null && plant.LastPlanWaiting() is { } lastWaiting) args["replenish_waiting"] = lastWaiting;
        var sweepStarted = DateTime.UtcNow;   // (an owner released at a merge after this is not put back by this sweep's plan)
        // the first full sweep after a gap (no gateway was up): the owners' claims that ran out in it are renewed, not offered again (issue 94)
        // (W9.10g: the gap stays with this pass where the engine acts too: the first full sweep after a restore would otherwise offer a claim that ran out in the gap as a relaunch before a job could renew it)
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
            // Fencing (W0.1, delivery-speed.md 18.1.1): the rule script found the lease lost and stopped its pass (`fenced`), or this process lost it while the
            // script ran. Either way what the pass read is dropped: no plan, no offers, no triggers. The gap and the items go back for the next holder's pass.
            var fencedBy = j["fenced"] as JsonObject;
            if (fencedBy != null || MayAct?.Invoke() == false)
            {
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["sweep"] = "fenced", ["kind"] = kind, ["reason"] = fencedBy != null ? "the rule script found the leader lease lost" : "this process lost the leader lease while the sweep ran", ["fence"] = fencedBy?.DeepClone() }.ToJsonString());
                if (gapTaken is { } gf) Gap?.Set(gf);
                if (items != null) foreach (var i in items) hub.RequestSweep(i);
                return unconfirmedOut;
            }
            // The set was read before the script ran, and the script read the board after: a saga's card reaches a working column only after its row is committed (the projection follows the saga's decisions), so a
            // card the script saw there has a row by now, and the set is read again before the filter drops v1's word about it. Read once at the start only, the sweep offered an item that has a saga to the plant
            // (a v1 offer with no slot, and the capacity of the scheduler counted twice) in the seconds between the saga's start and the next sweep. A read that fails leaves the set as it was.
            if (V2 != null) await V2.RefreshAsync(ct);
            if (V2 != null && (V2.Count > 0 || Left().Count > 0))
            {
                var left = Left();
                SweepV2Filter.Apply(j, i => V2.Owns(i) || left.Contains(i));
                // W9.10k: what the sweep would have said of those items, from the saga's state and the cards' labels (a full pass: an incremental pass names none of them)
                if (items == null && Feed != null)
                {
                    try { await Feed.RefreshViewsAsync(); Feed.Apply(j); }
                    catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"saga feed: {e.GetType().Name}"); }
                }
                // W6.6: the offers for the saga's items come from the saga's state, for the items the pass read (an incremental pass named none of them: it never offers an item it did not read)
                if (ownerMode && j["offers"] is JsonArray planned)
                    foreach (var offer in V2.OfferNodes(plant.HasOwner, items))
                        planned.Add(offer);
            }
            foreach (var x in j["unconfirmed"] as JsonArray ?? new JsonArray()) if (x is JsonValue uv && uv.TryGetValue<int>(out var ui) && ui > 0) unconfirmedOut.Add(ui);
            var acts = j["actions"]!.AsArray();
            // A full sweep whose item loop ran out of its budget (issue 429, `partial`) names the items it read (`viewed`): it is taken like an
            // incremental pass over them, so an item it did not look at keeps its owner, its hold and its facts, and the sweep that follows begins there.
            var partial = items == null && j["partial"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
            var scope = items ?? (partial ? (j["viewed"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var vi) ? vi : 0).Where(i => i > 0).ToList() : null);
            if (partial) kind = "partial";
            if (ownerMode)
            {
                // step 5a (worker-messaging.md 3.9): the sweep's `owned` replaces the plant's owner list, except for items the sweep could not read
                // this time and, in an incremental pass, every item it did not look at. A settling item keeps no owner: its owner was released at
                // item.merged (Plant.Merged).
                var keep = new List<int>();
                foreach (var x in j["errors"] as JsonArray ?? new JsonArray()) if (x?["item"] is JsonValue ev && ev.TryGetValue<int>(out var ei)) keep.Add(ei);
                plant.Plan(j["offers"]!.AsArray(), j["owners"] as JsonObject, j["pins"] as JsonObject, j["owned"] as JsonObject, keep, j["priorities"] as JsonObject, sweepStarted, scope, j["runtimes"] as JsonObject, j["profiles"] as JsonObject);
                if (items == null && !PlantJobs && j["replenish"] is JsonObject rpj) { plant.SetReplenish(rpj); plant.NoteGenerated((rpj["created"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var gi) ? gi : 0)); }
                if (items == null && !PlantJobs && j["design_waiting"] is JsonValue dwv && dwv.TryGetValue<int>(out var dw)) plant.KeeperBacklog(dw);   // a full sweep only: an incremental one reads a few items
                // the hold:owner items of the whole board: a full sweep replaces them, an incremental pass updates its own items; the plant hears
                // them once a full sweep has read the whole board (until then its name actions wait, sweep-pending)
                var holds = (j["holds"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var hi) ? hi : 0).Where(i => i > 0);
                if (scope == null) { ownerHolds.Clear(); holdsComplete = true; } else foreach (var i in scope) ownerHolds.Remove(i);
                ownerHolds.UnionWith(holds);
                if (holdsComplete) plant.SetOwnerHeld(ownerHolds.ToList());
                plant.SetPassUnmerged(j["pass_unmerged"] as JsonArray, scope);
                plant.SetHeld(j["held"] as JsonArray, scope);   // issue 65: items that wait for their dependencies
                if (scope == null) plant.SetSweepFacts(j["parks"] as JsonArray, j["unconfirmed"] as JsonArray);   // issue 99: why each unowned item waits (a full sweep: it read every item)
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
                if (scope == null)
                {
                    plant.SetReleaseQueue(j["release_queue"] switch { JsonArray ra => ra, JsonObject ro => new JsonArray(ro.DeepClone()), _ => null });   // (PowerShell sends a one-item queue as an object)
                    if (!PlantJobs) try { await qc.Reconcile(j["qc"], j["qc_teardowns"], j["qc_approvals"]); } catch (Exception e) { Console.Error.WriteLine($"qc reconcile: {e.Message}"); }   // (W9.10g: the qc-sync job reconciles where the engine acts)
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
                if (baseWatch != null && scope == null && !low && !PlantJobs) _ = Task.Run(baseWatch.AfterSweep);   // (V1.4: where the engine acts the job base-watch-fallback does it)
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
