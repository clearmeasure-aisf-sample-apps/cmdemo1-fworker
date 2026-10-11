using System.Text.Json.Nodes;
using System.Threading.Channels;

// The plant manager: a registry of workers, directed assignment of jobs to them, ownership of items, and routing of triggers to the owner.
// Design: docs/design/plant-manager.md sections 3 to 8.

sealed class WorkerInfo
{
    public string Name = "", Type = "persistent-local";
    public HashSet<string> Caps = new(), Roles = new() { "owner" };
    public int Capacity = 1;
    public bool Draining, Connected;
    public DateTime LastSeen = DateTime.UtcNow;
    public Channel<(long, string)>? Out;
    public List<(long Id, string Json)> Buffer = new();
    public long Next = 1;
    // QC (design pr-qc-environment.md 4.2): environments this session may run at once, the address it declared for URL checks, free memory from its heartbeat
    public int QcCapacity = 1;
    public string QcAddress = "";
    public bool QcProfile;
    public double? MemFreeMb;
}

sealed class Assignment
{
    public string Id = "", Key = "", Column = "", Kind = "own-item", Role = "owner", Mode = "first", Reason = "", Worker = "";
    public int Item, Pr;
    public DateTime CreatedAt, AckBy, AcceptedAt;
    public HashSet<string> Declined = new();
    public bool Accepted;
    public string Sha = "", EnvId = "";            // qc-env only
    public int Attempt;
    public string Only = "";                       // qc-env: the one worker an operator chose (reassign); empty means the usual selection
    public DateTime LastWaiting;
}

sealed class Plant(string secret, Func<int, string, JsonObject, string, Task> journal, double ackSeconds)
{
    readonly object gate = new();
    readonly Dictionary<string, WorkerInfo> workers = new();
    readonly Dictionary<string, Assignment> assignments = new();
    readonly Dictionary<int, string> ownerOf = new(), botOf = new(), lastTrigger = new();
    // A QC job never touches ownerOf/botOf: this map is keyed by the assignment key ("item|qc-env|qc|pr") and holds the worker that accepted.
    readonly Dictionary<string, string> qcOf = new();
    // Assignment ids of QC jobs that were torn down, replaced or released. A path that read the QC records before that and acts on them afterwards
    // (a sweep's Reconcile, a late persist) must not bring such a job back: it would hold the worker's capacity for an environment that is gone.
    readonly HashSet<string> qcDead = new();
    public QcSettings QcCfg { get; set; } = new();
    // Raised (outside the lock) when the tick placed or moved a QC job that nobody asked for at that moment; the hub persists the new worker.
    public Func<Assignment, Task>? OnQcPlaced { get; set; }
    public static bool IsQc(Assignment a) => a.Kind == "qc-env";
    readonly Dictionary<int, List<(long Id, string Json)>> mailboxes = new();
    readonly Dictionary<int, long> mailboxNext = new();
    readonly SemaphoreSlim mailboxSignal = new(0, int.MaxValue);
    long counter;

    static readonly Dictionary<string, int> TypeOrder = new() { ["persistent-local"] = 1, ["claude-cloud"] = 2, ["cursor-cloud"] = 2, ["ephemeral-local"] = 3 };

    // ---- registry ---------------------------------------------------------------------------------------------------------------------
    public (WorkerInfo W, Channel<(long, string)> Ch, List<(long, string)> Replay) Register(string name, string type, IEnumerable<string> caps, int capacity, IEnumerable<string> roles, long after, int qcCapacity = 1, string qcAddress = "", bool qcProfile = false)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(name, out var w)) workers[name] = w = new WorkerInfo { Name = name };
            w.Type = type; w.Caps = new(caps); w.Capacity = capOverride.TryGetValue(name, out var capSet) ? capSet : Math.Max(1, capacity); w.Roles = new(roles.Any() ? roles : new[] { "owner" });
            w.QcCapacity = Math.Clamp(qcCapacity, 1, 2); w.QcAddress = qcAddress; w.QcProfile = qcProfile;
            w.Connected = true; w.LastSeen = DateTime.UtcNow; w.Draining = false;
            w.Out?.Writer.TryComplete();
            w.Out = Channel.CreateUnbounded<(long, string)>();
            var replay = w.Buffer.Where(b => after >= 0 && b.Id > after).ToList();
            // what this name owns, and any assignment still waiting for its answer, is told again after a (re)connect
            var mine = ownerOf.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList();
            if (mine.Count > 0) Push(w, new JsonObject { ["provider"] = "gateway", ["event"] = "ownership.restored", ["items"] = new JsonArray(mine.Select(i => (JsonNode)i).ToArray()) }.ToJsonString(), false);
            foreach (var a in assignments.Values.Where(a => a.Worker == name && !a.Accepted)) Push(w, AssignedJson(a), false);
            return (w, w.Out, replay.Select(r => (r.Id, r.Json)).ToList());
        }
    }

    public void Disconnect(WorkerInfo w, Channel<(long, string)> ch) { lock (gate) { if (w.Out == ch) { w.Connected = false; w.Out = null; } } }
    // The operator's capacity for a worker name: it wins over what the session declares at registration, and it holds across the worker's reconnects
    // (not across a gateway restart: the session's own --capacity applies again then).
    readonly Dictionary<string, int> capOverride = new();
    public bool SetCapacity(string name, int capacity)
    {
        lock (gate)
        {
            capOverride[name] = Math.Clamp(capacity, 1, 20);
            if (workers.TryGetValue(name, out var w)) w.Capacity = capOverride[name];
            return true;
        }
    }

    public bool SetDraining(string name, bool on) { lock (gate) { if (!workers.TryGetValue(name, out var w)) return false; w.Draining = on; return true; } }

    long Push(WorkerInfo w, string json, bool toBuffer = true)
    {
        var id = w.Next++;
        if (toBuffer) { w.Buffer.Add((id, json)); if (w.Buffer.Count > 200) w.Buffer.RemoveAt(0); }
        w.Out?.Writer.TryWrite((id, json));
        return id;
    }

    // ---- ownership and routing ---------------------------------------------------------------------------------------------------------
    public void NoteWebhook(int item, string eventName, string reference) { lock (gate) lastTrigger[item] = $"webhook {eventName} (delivery {(reference.Length > 8 ? reference[..8] : reference)})"; }

    public void Route(int item, JsonObject evt)
    {
        lock (gate)
        {
            var json = evt.ToJsonString();
            if (!mailboxes.TryGetValue(item, out var box)) mailboxes[item] = box = new();
            var id = mailboxNext.TryGetValue(item, out var n) ? n : 1; mailboxNext[item] = id + 1;
            box.Add((id, json)); if (box.Count > 100) box.RemoveAt(0);
            if (ownerOf.TryGetValue(item, out var o) && workers.TryGetValue(o, out var w)) Push(w, json);
            try { mailboxSignal.Release(); } catch { }
        }
    }

    public async Task<(List<(long, string)> Events, bool Timeout)> Wait(int item, long after, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (gate) { if (mailboxes.TryGetValue(item, out var box)) { var ev = box.Where(b => b.Id > after).ToList(); if (ev.Count > 0) return (ev.Select(e => (e.Id, e.Json)).ToList(), false); } }
            var left = end - DateTime.UtcNow; if (left <= TimeSpan.Zero) return (new(), true);
            try { await mailboxSignal.WaitAsync(left > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : left, ct); } catch (OperationCanceledException) { return (new(), true); }
        }
    }

    // ---- planning ----------------------------------------------------------------------------------------------------------------------
    int Owned(string name) => ownerOf.Count(kv => kv.Value == name) + botOf.Count(kv => kv.Value == name) + assignments.Values.Count(a => a.Worker == name && !a.Accepted && !IsQc(a));
    int QcActive(string name) => assignments.Values.Count(a => IsQc(a) && a.Worker == name);

    (WorkerInfo? Worker, string Why) Choose(Assignment a, string? pin)
    {
        var ws = workers.Values.Where(w => w.Connected).ToList();
        if (ws.Count == 0) return (null, "no worker session is subscribed");
        var withRole = ws.Where(w => w.Roles.Contains(a.Role)).ToList();
        if (withRole.Count == 0) return (null, $"no subscribed session has the {a.Role} role");
        var live = withRole.Where(w => !w.Draining && !a.Declined.Contains(w.Name)).ToList();
        if (live.Count == 0) return (null, "every eligible session is draining or has declined");
        if (pin != null) { var p = live.FirstOrDefault(w => w.Name == pin); return p != null && Owned(p.Name) < p.Capacity ? (p, "") : (null, $"the pinned worker '{pin}' is not available"); }
        var free = live.Where(w => Owned(w.Name) < w.Capacity).ToList();
        if (free.Count == 0) return (null, "every eligible session is at capacity");
        return (free.OrderBy(w => TypeOrder.GetValueOrDefault(w.Type, 9)).ThenBy(w => Owned(w.Name)).First(), "");
    }

    string AssignedJson(Assignment a)
    {
        var o = new JsonObject
        {
            ["provider"] = "gateway", ["event"] = "item.assigned", ["item"] = a.Item, ["column"] = a.Column, ["job"] = a.Kind, ["role"] = a.Role, ["mode"] = a.Mode, ["reason"] = a.Reason,
            ["pr"] = a.Pr, ["assignment"] = a.Id, ["ackBySeconds"] = ackSeconds,
            ["token"] = Auth.MintItemToken(secret, a.Item, a.Role, a.Id, a.Worker, IsQc(a) ? TimeSpan.FromHours(QcCfg.TokenHours) : TimeSpan.FromHours(12))
        };
        if (IsQc(a)) { o["sha"] = a.Sha; o["env_id"] = a.EnvId; }
        return o.ToJsonString();
    }

    // ---- QC jobs (design pr-qc-environment.md 4.1, 4.2) --------------------------------------------------------------------------------------
    (WorkerInfo? Worker, string Why) ChooseQc(Assignment a)
    {
        var ws = workers.Values.Where(w => w.Connected).ToList();
        if (ws.Count == 0) return (null, "no worker session is subscribed");
        var withRole = ws.Where(w => w.Roles.Contains("qc") && w.Caps.Contains("dotnet")).ToList();
        if (withRole.Count == 0) return (null, "no connected worker has the qc role and the .NET SDK");
        var live = withRole.Where(w => !w.Draining && !a.Declined.Contains(w.Name) && (a.Only == "" || w.Name == a.Only)).ToList();
        if (live.Count == 0) return (null, "every eligible session is draining or has declined");
        if (assignments.Values.Count(x => IsQc(x) && x.Worker != "" && x != a) >= QcCfg.MaxEnvironments) return (null, "the plant is at its limit of QC environments");
        var free = live.Where(w => QcActive(w.Name) - (a.Worker == w.Name ? 1 : 0) < w.QcCapacity).ToList();
        if (free.Count == 0) return (null, "every eligible session is running its limit of QC environments");
        var mem = free.Where(w => w.MemFreeMb == null || w.MemFreeMb >= QcCfg.MinFreeMb).ToList();
        if (mem.Count == 0) return (null, $"no eligible session reports {QcCfg.MinFreeMb} MB of free memory");
        return (mem.OrderBy(w => QcActive(w.Name)).ThenBy(w => TypeOrder.GetValueOrDefault(w.Type, 9)).First(), "");
    }

    static string NewId(long n) => "a-" + n.ToString("x") + DateTime.UtcNow.Ticks.ToString("x")[^4..];

    // Called by Start, Reset and the restart rebuild. The key never appears in Plan's offers, and Plan's pruning skips kind qc-env, so a started
    // environment is never dropped by a sweep. restoreWorker: the recorded worker keeps the recorded assignment id (its item token still matches).
    public (Assignment A, string Why) PlaceQc(int item, int pr, string sha, string envId, int attempt, string mode, string? restoreId = null, string? restoreWorker = null, bool restoreAccepted = false, string only = "")
    {
        lock (gate)
        {
            var key = $"{item}|qc-env|qc|{pr}";
            if (restoreId != null && qcDead.Contains(restoreId)) return (new Assignment { Id = restoreId, Key = key, Item = item, Pr = pr, Kind = "qc-env", Role = "qc", Sha = sha, EnvId = envId, Attempt = attempt }, "this environment was torn down");
            if (assignments.TryGetValue(key, out var ex))
            {
                if (ex.Sha == sha && ex.Attempt == attempt) return (ex, ex.Worker == "" ? "waiting for a worker" : "");
                qcDead.Add(ex.Id); assignments.Remove(key); qcOf.Remove(key);
            }
            var a = new Assignment { Id = restoreId ?? NewId(Interlocked.Increment(ref counter)), Key = key, Item = item, Pr = pr, Kind = "qc-env", Role = "qc", Mode = mode, Reason = "qc", Sha = sha, EnvId = envId, Attempt = attempt, Only = only, CreatedAt = DateTime.UtcNow };
            assignments[key] = a;
            if (restoreWorker != null)
            {
                a.Worker = restoreWorker;
                if (restoreAccepted) { a.Accepted = true; a.AcceptedAt = DateTime.UtcNow; qcOf[key] = restoreWorker; }
                else { a.AckBy = DateTime.UtcNow.AddSeconds(ackSeconds); if (workers.TryGetValue(restoreWorker, out var rw)) Push(rw, AssignedJson(a), false); }
                return (a, "");
            }
            var (w, why) = ChooseQc(a);
            if (w == null) return (a, why);
            Offer(a, w, null);
            return (a, "");
        }
    }

    // Can an operator move this PR's environment to that worker? (exit, code, reason); exit 0 when it can. The PR's own current worker does not count against capacity.
    public (int Exit, string Code, string Reason) QcReassignCheck(string name, int pr)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(name, out var w)) return (12, "no-such-worker", $"no worker named {name} has connected");
            if (!w.Connected || w.Draining || !w.Roles.Contains("qc") || !w.Caps.Contains("dotnet")) return (10, "worker-unqualified", $"{name} is not connected with the qc role and the .NET SDK, or is draining");
            var own = assignments.Values.Count(x => IsQc(x) && x.Pr == pr && x.Worker == name);
            if (QcActive(name) - own >= w.QcCapacity) return (10, "worker-full", $"{name} is running its limit of QC environments");
            return (0, "", "");
        }
    }

    // Names an operator may pick in the dashboard's Reassign select: connected, qc role, dotnet, not draining, free QC capacity.
    public List<string> QcReassignable(int pr)
    {
        lock (gate) return workers.Values.Where(w => w.Connected && !w.Draining && w.Roles.Contains("qc") && w.Caps.Contains("dotnet")
            && QcActive(w.Name) - assignments.Values.Count(x => IsQc(x) && x.Pr == pr && x.Worker == w.Name) < w.QcCapacity).Select(w => w.Name).OrderBy(n => n).ToList();
    }

    public Assignment? QcAssignment(int pr) { lock (gate) return assignments.Values.FirstOrDefault(a => IsQc(a) && a.Pr == pr); }
    public void DropQc(int pr) { lock (gate) { foreach (var k in assignments.Where(kv => IsQc(kv.Value) && kv.Value.Pr == pr).Select(kv => kv.Key).ToList()) { qcDead.Add(assignments[k].Id); assignments.Remove(k); qcOf.Remove(k); } } }
    // Drops the PR's QC assignment only if it is still this one (a persist that failed must not remove a newer placement).
    public void DropQcIf(int pr, string id) { lock (gate) { foreach (var k in assignments.Where(kv => IsQc(kv.Value) && kv.Value.Pr == pr && kv.Value.Id == id).Select(kv => kv.Key).ToList()) { qcDead.Add(id); assignments.Remove(k); qcOf.Remove(k); } } }
    // The worker holding this environment is lost: a new assignment for another worker (the lost one is excluded) replaces it. Mode "relaunch".
    public (Assignment A, string Why) RelaunchQc(int item, int pr, string sha, string envId, int attempt, string lostWorker)
    {
        lock (gate)
        {
            var key = $"{item}|qc-env|qc|{pr}";
            if (assignments.TryGetValue(key, out var ex)) { qcDead.Add(ex.Id); assignments.Remove(key); qcOf.Remove(key); }
            var a = new Assignment { Id = NewId(Interlocked.Increment(ref counter)), Key = key, Item = item, Pr = pr, Kind = "qc-env", Role = "qc", Mode = "relaunch", Reason = "qc", Sha = sha, EnvId = envId, Attempt = attempt, CreatedAt = DateTime.UtcNow };
            if (lostWorker != "") a.Declined.Add(lostWorker);
            assignments[key] = a;
            var (w, why) = ChooseQc(a);
            if (w == null) return (a, why);
            Offer(a, w, null);
            return (a, "");
        }
    }
    public bool IsConnected(string name) { lock (gate) return workers.TryGetValue(name, out var w) && w.Connected; }
    public string DeclaredAddress(string name) { lock (gate) return workers.TryGetValue(name, out var w) ? w.QcAddress : ""; }
    public void SetMemFree(string name, double mb) { lock (gate) { if (workers.TryGetValue(name, out var w)) w.MemFreeMb = mb; } }
    // A directed event for one worker (qc.reset, qc.teardown).
    public void SendTo(string name, JsonObject evt) { lock (gate) { if (workers.TryGetValue(name, out var w)) Push(w, evt.ToJsonString()); } }

    void Offer(Assignment a, WorkerInfo w, string? why)
    {
        a.Worker = w.Name; a.AckBy = DateTime.UtcNow.AddSeconds(ackSeconds);
        Push(w, AssignedJson(a), false);
        var trig = lastTrigger.TryGetValue(a.Item, out var t) ? t : "the sweep";
        _ = journal(a.Item, "offered", new JsonObject { ["worker"] = w.Name, ["type"] = w.Type, ["ackSeconds"] = ackSeconds }, "off|" + a.Id + "|" + w.Name);
    }

    public void Plan(JsonArray offers, JsonObject? owners, JsonObject? pins)
    {
        lock (gate)
        {
            if (owners != null) foreach (var kv in owners) ownerOf[int.Parse(kv.Key)] = kv.Value!.ToString();
            var wanted = new HashSet<string>();
            foreach (var o in offers)
            {
                var item = o!["item"]!.GetValue<int>(); var kind = o["kind"]?.ToString() ?? "own-item"; var role = kind == "bot-job" ? (o["role"]?.ToString() ?? "inspector") : "owner";
                var key = $"{item}|{kind}|{role}"; wanted.Add(key);
                var pin = pins?[item.ToString()]?.ToString();
                if (assignments.TryGetValue(key, out var ex))
                {
                    if (ex.Accepted && (DateTime.UtcNow - ex.AcceptedAt).TotalSeconds < 20) continue;     // the offer predates the acceptance
                    if (ex.Accepted)
                    {   // accepted, yet the item needs a worker again: the session is gone
                        assignments.Remove(key); if (kind == "own-item") ownerOf.Remove(item); else botOf.Remove(item);
                        _ = journal(item, "reassigned", new JsonObject { ["reason"] = $"the session that owned this item ({ex.Worker}) is gone" }, "gone|" + ex.Id);
                    }
                    else if (ex.Worker != "") continue;                                                     // waiting for an answer
                }
                var a = ex != null && !ex.Accepted ? ex : new Assignment
                {
                    Id = "a-" + Interlocked.Increment(ref counter).ToString("x") + DateTime.UtcNow.Ticks.ToString("x")[^4..], Key = key, Item = item, Column = o["column"]?.ToString() ?? "",
                    Kind = kind, Role = role, Mode = o["mode"]?.ToString() ?? (o["reason"]?.ToString() == "relaunch" ? "resume" : "first"), Reason = o["reason"]?.ToString() ?? "", Pr = o["pr"]?.GetValue<int>() ?? 0, CreatedAt = DateTime.UtcNow
                };
                assignments[key] = a;
                var (w, why) = Choose(a, pin);
                if (w == null) { _ = journal(item, "waiting", new JsonObject { ["reason"] = why }, ""); continue; }
                var trigText = lastTrigger.TryGetValue(item, out var tt) ? tt : "the sweep";
                _ = journal(item, "matched", new JsonObject { ["trigger"] = trigText, ["reason"] = a.Reason, ["column"] = a.Column, ["role"] = a.Kind == "bot-job" ? a.Role : "" }, "m|" + a.Id);
                Offer(a, w, null);
            }
            // an unanswered or unwanted assignment that the sweep no longer asks for is dropped
            foreach (var k in assignments.Keys.Where(k => !wanted.Contains(k) && !assignments[k].Accepted && !IsQc(assignments[k])).ToList()) assignments.Remove(k);
        }
    }

    // Called every couple of seconds: an assignment nobody answered is offered to the next worker.
    public void Tick()
    {
        var placed = new List<Assignment>();
        lock (gate)
        {
            foreach (var a in assignments.Values.Where(a => !a.Accepted && a.Worker != "" && a.AckBy < DateTime.UtcNow).ToList())
            {
                var was = a.Worker; a.Declined.Add(was); a.Worker = "";
                _ = journal(a.Item, "reassigned", new JsonObject { ["reason"] = $"{was} did not confirm within {ackSeconds:0} s" }, "ack|" + a.Id + "|" + was);
                var (w, why) = IsQc(a) ? ChooseQc(a) : Choose(a, null);
                if (w == null) { if (!IsQc(a)) _ = journal(a.Item, "waiting", new JsonObject { ["reason"] = why }, ""); continue; }
                Offer(a, w, null);
                if (IsQc(a)) placed.Add(a);
            }
            // a Start that found no worker keeps its place: it is offered as soon as a qualified worker connects (it is the person's earlier Start, not a new automatic one)
            foreach (var a in assignments.Values.Where(a => IsQc(a) && !a.Accepted && a.Worker == "").ToList())
            {
                var (w, why) = ChooseQc(a);
                if (w == null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - a.CreatedAt).TotalSeconds >= QcCfg.WaitingDelaySeconds && (now - a.LastWaiting).TotalMinutes >= QcCfg.WaitingRepeatMinutes)
                    {
                        a.LastWaiting = now;
                        _ = journal(a.Item, "qc-waiting", new JsonObject { ["pr"] = a.Pr, ["reason"] = why }, "qcw|" + a.Id + "|" + now.Ticks);
                    }
                    continue;
                }
                Offer(a, w, null); placed.Add(a);
            }
        }
        foreach (var a in placed) { try { _ = OnQcPlaced?.Invoke(a); } catch { } }
    }

    // After a verb succeeded: ownership changes, and the triggers that follow from it.
    public void OnVerb(string verb, JsonObject request, Principal who, JsonObject response)
    {
        var item = int.TryParse(request["item"]?.ToString(), out var parsed) ? parsed : who.Item; if (item == 0) return;
        lock (gate)
        {
            switch (verb)
            {
                case "accept" when who.Kind == "item":
                    foreach (var a in assignments.Values.Where(a => a.Id == who.Assignment))
                    {
                        a.Accepted = true; a.AcceptedAt = DateTime.UtcNow;
                        if (IsQc(a)) qcOf[a.Key] = who.Name;            // a QC worker is never the owner of the item
                        else if (a.Kind == "own-item") ownerOf[item] = who.Name; else botOf[item] = who.Name;
                    }
                    break;
                case "release":
                    ownerOf.Remove(item); foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|") && !IsQc(assignments[k])).ToList()) assignments.Remove(k);
                    break;
                case "merge" when response["outcome"]?.ToString() is "merged" or "already-merged":
                    // the owner keeps the item until master is green (item.complete); the open assignments are done
                    foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|") && !IsQc(assignments[k])).ToList()) assignments.Remove(k);
                    Route(item, new JsonObject { ["provider"] = "gateway", ["event"] = "item.merged", ["item"] = item });
                    break;
                case "approve-pr":
                    botOf.Remove(item);
                    foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|bot-job")).ToList()) assignments.Remove(k);
                    var v = response["verdict"]?.ToString();
                    Route(item, new JsonObject { ["provider"] = "gateway", ["event"] = v == "APPROVE" ? "inspector.approved" : v == "ESCALATE" ? "inspector.escalated" : "inspector.changes_requested", ["item"] = item });
                    break;
            }
        }
    }

    // The item is merged and master is green (or the item otherwise ended): the owner is done and its slot is free.
    public void Complete(int item)
    {
        lock (gate) { ownerOf.Remove(item); foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|") && !IsQc(assignments[k])).ToList()) assignments.Remove(k); }
    }

    public JsonObject Status()
    {
        lock (gate)
            return new JsonObject
            {
                ["workers"] = new JsonArray(workers.Values.Select(w => (JsonNode)new JsonObject { ["name"] = w.Name, ["type"] = w.Type, ["connected"] = w.Connected, ["draining"] = w.Draining, ["capacity"] = w.Capacity, ["owned"] = Owned(w.Name), ["qcCapacity"] = w.QcCapacity, ["qcActive"] = QcActive(w.Name), ["qcProfile"] = w.QcProfile, ["qcAddress"] = w.QcAddress, ["roles"] = new JsonArray(w.Roles.Select(r => (JsonNode)r).ToArray()) }).ToArray()),
                ["owners"] = new JsonObject(ownerOf.Select(kv => KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)kv.Value))),
                ["assignments"] = new JsonArray(assignments.Values.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["item"] = a.Item, ["kind"] = a.Kind, ["worker"] = a.Worker, ["accepted"] = a.Accepted, ["pr"] = a.Pr }).ToArray())
            };
    }
}
