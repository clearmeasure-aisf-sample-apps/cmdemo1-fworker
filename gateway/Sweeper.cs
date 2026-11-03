using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Workflows.WorkItem;

sealed class Sweeper(EventHub hub, Plant plant, int seconds, bool ownerMode, Task? restored = null) : BackgroundService
{
    // V3.2: v1's sweep (the rule script sync-board) is gone. This loop is the plant's feed: every `seconds` (180), and at most every MinSeconds (60) when a trigger asks for one, it builds the plant's input for the items that have
    // a saga from the saga's state and the cards' labels (SagaPlantFeed: owners, pins, priorities, runtime labels, parks, holds, the Release Queue) and takes the saga's offers (V2Ownership.OfferNodes). A trigger of one item queues the
    // item: between full passes the queued items get the saga's offers for exactly those items (an incremental pass). The owner of an item is released by the saga (OwnerRelease), not by a trigger of the pass.
    // the leader lease (Leader.cs): only the holder feeds the plant; with the lease on, the first pass waits for the restore however long the hand-over takes
    public Func<bool>? MayAct { get; init; }
    public bool LeaderOn { get; init; }
    public int MinSeconds { get; init; }
    // the items that have a saga. Set only where the engine acts (or never loaded, where v2 is configured and there is no database: then nothing is fed)
    public V2Ownership? V2 { get; init; }
    // W9.10k: the plant's input for the items that have a saga, built from the saga's state and the cards' labels. Set only where the engine acts, with V2.
    public SagaPlantFeed? Feed { get; init; }
    // cutover review: where the engine acts the pass also waits for the adoption of this process's term (AdoptionGate), and reads the saga's table again before the term's first pass. Set only with V2.
    public AdoptionGate? Adoption { get; init; }
    long? adoptedTerm;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // no engine here (uat and prod, until the owner says otherwise): nothing feeds the plant
        if (V2 is null) return;
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        // the plant snapshot is restored before the first plan (PlantSnapshot.cs); without the lease a read that hangs holds the pass back 60 s at most
        if (restored != null) try { await Task.WhenAny(restored, Task.Delay(TimeSpan.FromSeconds(LeaderOn ? 3600 : 60), ct)); } catch (OperationCanceledException) { return; }
        var lastFull = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (MayAct?.Invoke() == false) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { } continue; }
            // not until the adoption of this term is over, and then the saga's table is read again, because the adoption wrote to it. A failed adoption is tried again by its job; this keeps waiting.
            if (Adoption != null)
            {
                var term = Adoption.Settled();
                if (term is null)
                {
                    WarnWaiting("the adoption of this term has not finished; the plant is fed from nothing until it has");
                    try { await Adoption.PokeAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } catch (Exception e) { Console.Error.WriteLine($"adoption poke: {e.GetType().Name}"); }
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
                if (adoptedTerm != term)
                {
                    if (!await OwnershipRead(ct)) { try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
                    adoptedTerm = term;
                }
            }
            // W9.10e: nothing is fed until the saga's table has been read once: an empty set that only means "not read yet" would make the plant drop the owners of items in flight. Items asked for meanwhile stay queued.
            if (V2 is { Loaded: false } && !await OwnershipRead(ct)) { try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { } continue; }
            try
            {
                var now = DateTime.UtcNow; var sinceFull = (now - lastFull).TotalSeconds;
                var (fullAsked, queued) = hub.PendingSweep();
                // a full pass: its interval is up, or one was asked for and the last is MinSeconds old; without owner mode there is no incremental pass (the broadcast offers are the whole list), so a queued item asks for a full one
                var full = sinceFull >= seconds || ((fullAsked || (!ownerMode && queued > 0)) && sinceFull >= MinSeconds);
                if (full)
                {
                    hub.FullSweepTaken(); lastFull = now;
                    await SweepOnce(null, ct);
                }
                else if (ownerMode && queued > 0)
                {
                    var items = hub.TakeSweepItems().Distinct().OrderBy(i => i).ToList();
                    // at most 50 in one pass; the rest stay queued for the next one
                    if (items.Count > 50) { foreach (var i in items.Skip(50)) hub.RequestSweep(i); items = items.Take(50).ToList(); }
                    await SweepOnce(items, ct);
                }
            }
            // only the host's own stop ends the loop: a cancellation from anywhere else (a timeout inside a call) is an error of this pass, not the end of it
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":\"error\",\"type\":{JsonSerializer.Serialize(e.GetType().Name)},\"message\":{JsonSerializer.Serialize(e.Message)}}}"); }
            catch (Exception) { return; }
            try
            {
                // until the next thing due: the interval, an asked-for full pass once MinSeconds have passed; a queued item at once
                var next = lastFull.AddSeconds(seconds);
                var (fa, q) = hub.PendingSweep();
                if (fa || (!ownerMode && q > 0)) { var m = lastFull.AddSeconds(MinSeconds); if (m < next) next = m; }
                var wait = next - DateTime.UtcNow;
                if (ownerMode && q > 0) wait = TimeSpan.Zero;
                if (wait > TimeSpan.Zero) await hub.WaitForSweepSignal(wait, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine($"sweep wait: {e.Message}"); try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { } }
        }
    }

    DateTime ownershipWarned = DateTime.MinValue;
    async Task<bool> OwnershipRead(CancellationToken ct)
    {
        try { if (await V2!.RefreshAsync(ct) && V2.Loaded) return true; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        WarnWaiting(V2!.Unavailable ? "v2 is configured for this environment and there is no database; nothing is fed to the plant" : "the saga table has not been read yet; nothing is fed to the plant until it has");
        return false;
    }

    void WarnWaiting(string reason)
    {
        if (DateTime.UtcNow - ownershipWarned > TimeSpan.FromMinutes(1)) { ownershipWarned = DateTime.UtcNow; Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":\"waiting\",\"reason\":{JsonSerializer.Serialize(reason)}}}"); }
    }

    // a pass over named items: the plant takes the saga's offers for exactly those items (scope), as it always took the offers of an incremental pass
    void PlanV2Offers(List<int> mine)
    {
        // (W9.10j: the same offers as the saga's own session start makes, from the saga's state; the labels of the items stay)
        try { plant.PlanSagaOffers(new JsonArray(V2!.OfferNodes(plant.HasOwner, mine).ToArray()), mine); }
        catch (Exception e) { Console.Error.WriteLine($"v2 offers: {e.GetType().Name}"); }
    }

    // One pass: all the saga's items (items null) or the given items. The plant is fed from SagaPlantFeed and the saga's offers alone.
    async Task SweepOnce(List<int>? items, CancellationToken ct = default)
    {
        await V2!.RefreshAsync(ct);
        if (items != null)
        {
            // the pass named only some items: the plant takes the saga's offers for them (an item whose step just began waits for a session)
            var mine = items.Where(V2.Owns).ToList();
            if (ownerMode && mine.Count > 0) PlanV2Offers(mine);
            return;
        }
        var sweepStarted = DateTime.UtcNow;   // (an owner released at a merge after this is not put back by this pass's plan)
        if (Feed != null) { try { await Feed.RefreshViewsAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"saga feed: {e.GetType().Name}"); } }
        if (MayAct?.Invoke() == false) return;   // this process lost the lease while the saga's state was read: nothing is handed to the plant
        var j = new JsonObject { ["offers"] = new JsonArray() };
        try { Feed?.Apply(j); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"saga feed: {e.GetType().Name}"); }
        // W6.6: the offers for the saga's items come from the saga's state
        var offers = j["offers"]!.AsArray();
        if (ownerMode) foreach (var offer in V2.OfferNodes(plant.HasOwner, null)) offers.Add(offer);
        hub.LastSweep = DateTime.UtcNow;
        hub.NoteSweep(true, 0, false);
        if (ownerMode)
        {
            // the pass's `owned` replaces the plant's owner list (Plant.Plan); a settling item keeps no owner: its owner is released at the merge (OwnerRelease)
            plant.Plan(offers, (j["owners"] as JsonObject ?? new JsonObject()), (j["pins"] as JsonObject ?? new JsonObject()), (j["owned"] as JsonObject ?? new JsonObject()), new List<int>(), (j["priorities"] as JsonObject ?? new JsonObject()), sweepStarted, null, (j["runtimes"] as JsonObject ?? new JsonObject()), (j["profiles"] as JsonObject ?? new JsonObject()));
            // the hold:owner items of a full pass replace the plant's
            plant.SetOwnerHeld((j["holds"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var hi) ? hi : 0).Where(i => i > 0).ToList());
            plant.SetSweepFacts(j["parks"] as JsonArray ?? new JsonArray(), new JsonArray());   // (an empty list clears: the feed adds only what exists)   // issue 99: why each unowned item waits
            plant.SetReleaseQueue(j["release_queue"] as JsonArray ?? new JsonArray());
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":{{\"offers\":{offers.Count},\"actions\":0,\"errors\":0,\"kind\":\"full\",\"items\":-1}}}}");
        }
        else
        {
            var list = new List<string>();
            foreach (var o in offers)
                list.Add(new JsonObject { ["provider"] = "gateway", ["event"] = "work.offered", ["item"] = o!["item"]!.DeepClone(), ["column"] = o["column"]!.DeepClone(), ["reason"] = o["reason"]!.DeepClone() }.ToJsonString());
            hub.SetOffers(list);
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweep\":{{\"offers\":{list.Count},\"actions\":0,\"errors\":0}}}}");
        }
    }
}
