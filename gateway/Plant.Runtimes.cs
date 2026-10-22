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
    RuntimeManager? RuntimeOf(string name) => name == "" ? null : Runtimes?.IsRuntimeWorker(name) == true ? Runtimes : Cursor?.IsRuntimeWorker(name) == true ? Cursor : Copilot?.IsRuntimeWorker(name) == true ? Copilot : null;
    RuntimeManager? RuntimeFor(string kind) => kind == "cae-job" ? Runtimes : kind == "cursor-cloud" ? Cursor : kind == "copilot-cloud" ? Copilot : null;
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
        var any = Runtimes ?? Cursor ?? Copilot;
        if (any == null || a.Kind != "own-item" || !((Runtimes?.On ?? false) || (Cursor?.On ?? false) || (Copilot?.On ?? false))) return (false, null);
        var label = runtimeLabels[a.Item.ToString()]?.ToString() ?? "";
        var ownerRt = OwnerRuntime(a.Item); var owned = ownerRt != null;
        var runtime = ownerRt ?? any.Settings.RouteFor(a.Column, label);
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
        if (rm.Settings.PreferConnected && !owned && !(rm.Kind is "cursor-cloud" or "copilot-cloud" && label == rm.Kind) && Choose(a, null).Worker != null) return (false, null);
        var (ok, code) = rm.CanStart();
        if (!ok) { if (label != "" || owned) { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = code; Journal(a.Item, "waiting", new JsonObject { ["reason"] = owned ? $"the item is owned by a {(ownerRt == "cursor-cloud" ? "Cursor agent" : "Copilot task")}; it is resumed once the runtime can start a session ({code})" : $"the item is labelled runtime:{label} and that runtime cannot start a worker now ({code})" }, ""); return (true, null); } return (false, null); }
        var t = rm.Begin(a.Item, a.Column, itemProfiles[a.Item.ToString()]?.ToString() ?? "");
        pendingProvisions.Add((rm, t));
        a.Pin = t.Name; a.Worker = t.Name; a.AckBy = DateTime.UtcNow.AddSeconds(rm.AckSeconds); a.AckExtended = false; a.WaitingSince = null; a.WaitingCode = "";
        Journal(a.Item, "offered", new JsonObject { ["worker"] = t.Name, ["type"] = "runtime", ["runtime"] = rm.Kind, ["ackSeconds"] = rm.AckSeconds }, "off|" + a.Id + "|" + t.Name);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["plant"] = "runtime-route", ["item"] = a.Item, ["phase"] = a.Column, ["worker"] = t.Name, ["label"] = label }.ToJsonString());
        return (true, null);
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
    public void RuntimeLaunched(string name, int item, string phase, string kind, string execution, string url)
    {
        Journal(item, "launched", new JsonObject { ["worker"] = name, ["runtime"] = kind, ["phase"] = phase, ["task"] = execution, ["url"] = url }, "rtl|" + name + "|" + execution);
    }

    // the page of a runtime worker's execution ("" for none or a connected session)
    public string RuntimeUrlOf(string name) => RuntimeOf(name)?.UrlOf(name) ?? "";

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
            return took;
        }
    }

    public void RuntimeParked(int item, string phase) { lock (gate) parkQueued.Remove((item, phase)); }

    JsonObject RuntimesStatus() => Runtimes?.Status() ?? new JsonObject { ["enabled"] = false };
}
