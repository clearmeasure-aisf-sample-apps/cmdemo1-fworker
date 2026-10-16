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
    JsonObject runtimeLabels = new(), itemProfiles = new();
    readonly List<RuntimeManager.Ticket> pendingProvisions = new();
    readonly HashSet<(int, string)> parkQueued = new();

    double AckFor(string name) => Runtimes != null && name != "" && Runtimes.IsRuntimeWorker(name) ? Runtimes.AckSeconds : ackSeconds;
    bool RuntimeWorker(WorkerInfo w) => w.Runtime != "local";

    // Under gate, for an unpinned own-item assignment. Returns (Handled: the assignment was offered to a new runtime worker or parked, Pin: the
    // runtime worker that already runs for the item, or null for the usual choice).
    (bool Handled, string? Pin) RouteRuntime(Assignment a)
    {
        var rm = Runtimes;
        if (rm == null || !rm.On || a.Kind != "own-item") return (false, null);
        var label = runtimeLabels[a.Item.ToString()]?.ToString() ?? "";
        var runtime = rm.Settings.RouteFor(a.Column, label);
        if (runtime == "local") return (false, null);
        if (runtime != "cae-job") { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = "runtime-unknown"; return (true, null); }   // a kind this gateway has no adapter for
        var existing = rm.ExistingFor(a.Item);
        if (existing != "") return (false, existing);
        if (rm.Exhausted(a.Item, a.Column))
        {
            if (parkQueued.Add((a.Item, a.Column))) rm.QueuePark(a.Item, a.Column);
            a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = "runtime-failed";
            return (true, null);
        }
        if (rm.Settings.PreferConnected && Choose(a, null).Worker != null) return (false, null);
        var (ok, code) = rm.CanStart();
        if (!ok) { if (label != "") { a.WaitingSince ??= DateTime.UtcNow; a.WaitingCode = code; Journal(a.Item, "waiting", new JsonObject { ["reason"] = $"the item is labelled runtime:{label} and that runtime cannot start a worker now ({code})" }, ""); return (true, null); } return (false, null); }
        var t = rm.Begin(a.Item, a.Column, itemProfiles[a.Item.ToString()]?.ToString() ?? "");
        pendingProvisions.Add(t);
        a.Pin = t.Name; a.Worker = t.Name; a.AckBy = DateTime.UtcNow.AddSeconds(rm.AckSeconds); a.AckExtended = false; a.WaitingSince = null; a.WaitingCode = "";
        Journal(a.Item, "offered", new JsonObject { ["worker"] = t.Name, ["type"] = "runtime", ["runtime"] = "cae-job", ["ackSeconds"] = rm.AckSeconds }, "off|" + a.Id + "|" + t.Name);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["plant"] = "runtime-route", ["item"] = a.Item, ["phase"] = a.Column, ["worker"] = t.Name, ["label"] = label }.ToJsonString());
        return (true, null);
    }

    // After the plant's lock: the executions decided in it are started.
    void LaunchProvisions()
    {
        List<RuntimeManager.Ticket> go; lock (gate) { go = pendingProvisions.ToList(); pendingProvisions.Clear(); }
        foreach (var t in go) _ = Task.Run(() => Runtimes!.Provision(t));
    }

    // RuntimeManager.Poll: what the plant knows of a runtime worker.
    public (bool Registered, bool Owns, bool Offered) RuntimeWorkerState(string name)
    {
        lock (gate)
            return (workers.TryGetValue(name, out var w) && w.Session > 0, ownerOf.Values.Contains(name),
                assignments.Values.Any(a => a.Worker == name && !a.Accepted));
    }

    // The execution of a runtime worker ended: everything the name still holds goes back to the plan (its owner is dropped, its assignments are
    // removed), its stream is closed and it is forgotten. Answers whether anything was taken back.
    public bool RuntimeEnded(string name, int item, string outcome)
    {
        lock (gate)
        {
            var took = false;
            foreach (var kv in ownerOf.Where(kv => kv.Value == name).ToList()) { DropOwner(kv.Key); took = true; }
            foreach (var k in assignments.Where(kv => kv.Value.Worker == name && !IsQc(kv.Value)).Select(kv => kv.Key).ToList())
            {
                var a = assignments[k]; assignments.Remove(k); took = true;
                Journal(a.Item, "reassigned", new JsonObject { ["reason"] = $"the execution of {name} ended ({outcome})", ["opCode"] = "runtime-ended" }, "rtend|" + a.Id + "|" + name);
            }
            if (workers.TryGetValue(name, out var w)) { w.Out?.Writer.TryComplete(); w.Connected = false; workers.Remove(name); }
            return took;
        }
    }

    public void RuntimeParked(int item, string phase) { lock (gate) parkQueued.Remove((item, phase)); }

    JsonObject RuntimesStatus() => Runtimes?.Status() ?? new JsonObject { ["enabled"] = false };
}
