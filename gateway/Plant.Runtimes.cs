using System.Text.Json.Nodes;

// Routing to runtimes (docs/design/cloud-worker-runtimes.md 4.4). An own-item offer with no worker: label pin goes first, as today; then the item's
// runtime: label, then the runtimes.routing rules, decide its runtime; a connected local worker with a free slot still takes it while
// runtimes.preferConnected is true; a runtime at maxConcurrent, over its daily budget or unhealthy is skipped (the item goes the local way). For a
// runtime, the adapter creates the worker: the plant reserves a name and a nonce (RuntimeManager.Begin), offers the item to that name at once with
// the runtime's window (ackSeconds, which covers the container start), and starts the execution after its lock. The worker's registration
// replays the offer (Register). An item whose phase already lost runtimes.caeJob.maxFailuresPerPhase executions is parked runtime-failed.
sealed partial class Plant
{
    public RuntimeManager? Runtimes { get; set; }
    // the api mode's adapter (Cursor Cloud Agents, gateway/Runtimes/CursorCloudRuntime.cs): items reach it by the runtime:cursor-cloud label
    public RuntimeManager? Cursor { get; set; }
    // the Copilot cloud agent (gateway/Runtimes/CopilotCloudRuntime.cs): items reach it by the runtime:copilot-cloud label
    public RuntimeManager? Copilot { get; set; }
    // the Octopus runbook runtime (gateway/Runtimes/OctopusRunbookRuntime.cs): items reach it by the runtime:octopus label or a routing rule
    public RuntimeManager? Octopus { get; set; }
    // the items a Copilot task owns until merged and master green (CopilotCloudRuntime.Owns): only that task is offered them, whatever the label,
    // the routing or a connected session says
    public Func<int, bool>? CopilotOwns { get; set; }
    bool CopilotOwned(int item) => Copilot != null && CopilotOwns?.Invoke(item) == true;
    // the items a Cursor agent owns until in production (CursorCloudRuntime.Owns): only that agent's follow-up runs are offered them
    public Func<int, bool>? CursorOwns { get; set; }
    bool CursorOwned(int item) => Cursor != null && CursorOwns?.Invoke(item) == true;
    string? OwnerRuntime(int item) => CopilotOwned(item) ? "copilot-cloud" : CursorOwned(item) ? "cursor-cloud" : null;
    JsonObject runtimeLabels = new(), itemProfiles = new();
    readonly List<(RuntimeManager Rm, RuntimeManager.Ticket T)> pendingProvisions = new();

    // the adapter that runs a worker name, or null for a session that connected by itself
    RuntimeManager? RuntimeOf(string name) => name == "" ? null : Runtimes?.IsRuntimeWorker(name) == true ? Runtimes : Cursor?.IsRuntimeWorker(name) == true ? Cursor : Copilot?.IsRuntimeWorker(name) == true ? Copilot : Octopus?.IsRuntimeWorker(name) == true ? Octopus : null;
    RuntimeManager? RuntimeFor(string kind) => kind == "cae-job" ? Runtimes : kind == "cursor-cloud" ? Cursor : kind == "copilot-cloud" ? Copilot : kind == "octopus" ? Octopus : null;
    readonly HashSet<(int, string)> parkQueued = new();

    // The CI release's candidates (CopilotCiRelease.cs): the items labelled runtime:copilot-cloud (the sweep reads only this gateway's env scope)
    // and the items the Copilot adapter ran a task for. The rule verb checks scope, state and pull request head again.
    public int[] CopilotItems()
    {
        if (Copilot == null) return Array.Empty<int>();
        var s = Copilot.ItemsSeen();
        lock (gate) foreach (var kv in runtimeLabels) if (kv.Value?.ToString() == "copilot-cloud" && int.TryParse(kv.Key, out var i)) s.Add(i);
        return s.Order().ToArray();
    }

    bool RuntimeAlive(string name) => (Cursor?.Alive(name) ?? false) || (Copilot?.Alive(name) ?? false);
    double AckFor(string name) => RuntimeOf(name)?.AckSeconds ?? ackSeconds;
    bool RuntimeWorker(WorkerInfo w) => w.Runtime != "local";

    // Under gate, for an unpinned own-item assignment. Returns (Handled: the assignment was offered to a new runtime worker or parked, Pin: the
    // runtime worker that already runs for the item, or null for the usual choice).
    (bool Handled, string? Pin) RouteRuntime(Assignment a)
    {
        var any = Runtimes ?? Cursor ?? Copilot ?? Octopus;
        if (any == null || a.Kind != "own-item" || !((Runtimes?.On ?? false) || (Cursor?.On ?? false) || (Copilot?.On ?? false) || (Octopus?.On ?? false))) return (false, null);
        var label = runtimeLabels[a.Item.ToString()]?.ToString() ?? "";
        var ownerRt = OwnerRuntime(a.Item); var owned = ownerRt != null;
        // W9.10j: an item of a saga that holds a slot of the scheduler starts its session where the slot is (the runtime the scheduler granted), not where the plant's routing would put it; an item a Copilot task or Cursor agent owns stays with its owner
        var ledger = !owned && a.Slot.Length > 0 && a.SlotRuntime.Length > 0;
        var runtime = ownerRt ?? (ledger ? a.SlotRuntime : any.Settings.RouteFor(a.Column, label));
        if (runtime == "local") return (false, null);
        var rm = RuntimeFor(runtime);
        if (rm == null) { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = "runtime-unknown"; return (true, null); }   // a kind this gateway has no adapter for
        var existing = rm.ExistingFor(a.Item);
        if (existing != "") return (false, existing);
        if (rm.Exhausted(a.Item, a.Column))
        {
            if (parkQueued.Add((a.Item, a.Column))) rm.QueuePark(a.Item, a.Column);
            a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = "runtime-failed";
            return (true, null);
        }
        // a connected session first (runtimes.preferConnected), except for an item labelled for the api mode: that label is the operator's choice
        if (rm.Settings.PreferConnected && !owned && !ledger && !(rm.Kind is "cursor-cloud" or "copilot-cloud" && label == rm.Kind) && Choose(a, null).Worker != null) return (false, null);
        var (ok, code) = rm.CanStart(ledger);
        if (!ok && ledger) { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = code; Journal(a.Item, "waiting", new JsonObject { ["reason"] = $"the scheduler granted slot {a.Slot} on {a.SlotRuntime} and that runtime cannot start a worker now ({code}); the offer waits for it" }, ""); return (true, null); }
        if (!ok) { if (label != "" || owned) { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = code; Journal(a.Item, "waiting", new JsonObject { ["reason"] = owned ? $"the item is owned by a {(ownerRt == "cursor-cloud" ? "Cursor agent" : "Copilot task")}; it is resumed once the runtime can start a session ({code})" : $"the item is labelled runtime:{label} and that runtime cannot start a worker now ({code})" }, ""); return (true, null); } return (false, null); }
        var t = rm.Begin(a.Item, a.Column, itemProfiles[a.Item.ToString()]?.ToString() ?? "", slot: ledger ? a.Slot : "", arm: ledger ? a.Arm : "");
        pendingProvisions.Add((rm, t));
        a.Pin = t.Name; a.Worker = t.Name; a.AckBy = DateTime.UtcNow.AddSeconds(rm.AckSeconds); a.WaitingSince = null; a.WaitingCode = "";
        Journal(a.Item, "offered", new JsonObject { ["worker"] = t.Name, ["type"] = "runtime", ["runtime"] = rm.Kind, ["ackSeconds"] = rm.AckSeconds }, "off|" + a.Id + "|" + t.Name);
        ReserveLaunched(a.Item, t.Name);   // the launched line's place, before the worker's acceptance can queue its assigned line
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["plant"] = "runtime-route", ["item"] = a.Item, ["phase"] = a.Column, ["worker"] = t.Name, ["label"] = label }.ToJsonString());
        return (true, null);
    }

    /// <summary>
    /// W9.10j: the offers of the items whose sagas the scheduler granted a slot, planned for those items only and outside a sweep (the saga's session start, Ports/SessionStartPort.cs). The items' runtime labels and model
    /// profiles stay as the last sweep read them: an incremental pass replaces the labels of the items it names, and this one read none.
    /// </summary>
    public void PlanSagaOffers(JsonArray offers, IReadOnlyCollection<int> items)
    {
        JsonObject labels = new(), profiles = new();
        lock (gate)
            foreach (var i in items)
            {
                if (runtimeLabels[i.ToString()] is { } l) labels[i.ToString()] = l.DeepClone();
                if (itemProfiles[i.ToString()] is { } p) profiles[i.ToString()] = p.DeepClone();
            }
        Plan(offers, null, null, null, items, null, DateTime.UtcNow, items, labels, profiles);
    }

    // After the plant's lock: the executions decided in it are started.
    void LaunchProvisions()
    {
        List<(RuntimeManager Rm, RuntimeManager.Ticket T)> go; lock (gate) { go = pendingProvisions.ToList(); pendingProvisions.Clear(); }
        foreach (var (rm, t) in go) _ = Task.Run(() => rm.Provision(t));
    }

    // RuntimeManager.Poll: what the plant knows of a runtime worker.
    public (bool Registered, bool Owns, bool Offered) RuntimeWorkerState(string name)
    {
        lock (gate)
        {
            var known = workers.TryGetValue(name, out var w);
            // a spent session (it held an item and gave it back) is being told to end (session.end after SessionEndGraceSeconds): it counts as
            // busy for a minute past that, so the runtime's no-item stop does not race the session's own end under load
            var ending = known && w!.SessionItems.Count > 0 && (w.SpentIdleSince == null || (DateTime.UtcNow - w.SpentIdleSince.Value).TotalSeconds < SessionEndGraceSeconds + 60);
            return (known && w!.Session > 0, ownerOf.Values.Contains(name), ending || assignments.Values.Any(a => a.Worker == name && !a.Accepted));
        }
    }

    // The execution of a runtime worker ended: everything the name still holds goes back to the plan (its owner is dropped, its assignments are
    // removed), its stream is closed and it is forgotten. Answers whether anything was taken back.
    // The execution was started: the item's journal names it and its page (launched; the accepted and ended lines carry the page too).
    // the gateway's page of a session's transcript (gateway/Transcripts.cs) for a runtime whose container posts its lines, "" for none
    public Func<string, string, string>? SessionPage { get; set; }

    public void RuntimeLaunched(string name, int item, string phase, string kind, string execution, string url)
    {
        var f = new JsonObject { ["worker"] = name, ["runtime"] = kind, ["phase"] = phase, ["task"] = execution, ["url"] = url };
        if (SessionPage?.Invoke(kind, name) is { Length: > 0 } page) f["transcript"] = page;
        var key = "rtl|" + name + "|" + execution;
        lock (gate)
        {   // the place taken when the launch was decided (ReserveLaunched); a launch without one (or whose place lapsed) is queued now
            if (launchSlots.Remove(name, out var slot)) slot.TrySetResult((f, key));
            else Journal(item, "launched", f, key);
        }
    }

    // the page of a runtime worker's execution ("" for none or a connected session)
    public string RuntimeUrlOf(string name) => RuntimeOf(name)?.UrlOf(name) ?? "";
    // the runtime kind of a runtime worker ("" for a connected session)
    public string RuntimeKindOf(string name) => RuntimeOf(name)?.Kind ?? "";

    public bool RuntimeEnded(string name, int item, string outcome, string url = "")
    {
        lock (gate)
        {
            var took = false;
            foreach (var kv in ownerOf.Where(kv => kv.Value == name).ToList()) { DropOwner(kv.Key); took = true; }
            foreach (var k in assignments.Where(kv => kv.Value.Worker == name && !IsQc(kv.Value)).Select(kv => kv.Key).ToList())
            {
                var a = assignments[k]; assignments.Remove(k); took = true;
                Journal(a.Item, "reassigned", new JsonObject { ["reason"] = $"the execution of {name} ended ({outcome})", ["url"] = url, ["opCode"] = "runtime-ended" }, "rtend|" + a.Id + "|" + name);
            }
            if (workers.TryGetValue(name, out var w)) { w.Out?.Writer.TryComplete(); w.Connected = false; workers.Remove(name); }
            SlotFreed(item);
            return took;
        }
    }

    // Plan on slot freed (delivery-speed.md 3.3, 4.1; W0.9). A worker's slot freed (an owner dropped, a runtime execution ended): the items that wait
    // for an owner are asked for at once, as an incremental pass through the hub's sweep request (SweepRequest, set from the EventHub), instead of
    // at the next full sweep. The pass reads the board and plans, so nothing is decided here. It runs outside the sweep, so it asks the leader lease
    // first (MayAct), as the sweep and the tick do. Under gate. Not from inside a plan (a drop there is the plan's own, and would ask for the next).
    public Action<int>? SweepRequest { get; set; }
    bool planning;
    public int SlotFreedRequests { get; private set; }
    void SlotFreed(int freedItem)
    {
        if (SweepRequest == null || planning || MayAct?.Invoke() == false) return;
        foreach (var i in QueuedItems(freedItem).Take(MaxSlotFreedItems)) { SweepRequest(i); SlotFreedRequests++; }
    }
    const int MaxSlotFreedItems = 5;   // a freed slot takes one item; a few spare cover a pin or a hold. More wait for the sweep.
    // the numbers of the items waiting for an owner, in the order Plan would take them (the oldest assignment first among equals), but for the freed item
    List<int> QueuedItems(int except) => assignments.Values.Where(a => a.Kind == "own-item" && !IsQc(a) && !a.Accepted && a.Worker == "" && a.Item != except && !ownerOf.ContainsKey(a.Item))
        .OrderBy(a => a.Pin != "" ? 0 : 1).ThenBy(a => string.Equals(a.Column, IntakeColumn, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(a => a.CreatedAt).Select(a => a.Item).Distinct().ToList();

    public void RuntimeParked(int item, string phase) { lock (gate) parkQueued.Remove((item, phase)); }

    JsonObject RuntimesStatus() => Runtimes?.Status() ?? new JsonObject { ["enabled"] = false };
}
