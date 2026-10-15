using System.Globalization;
using System.Text.Json.Nodes;

// The standing back-merge trigger (docs/design/plant-manager.md 6a). When master moves (a pull request merged into the default branch: the
// pull_request closed+merged webhook, debounced; or, as the fallback, the sweep seeing a new head), every owner of an item with an open branch
// that is behind gets `base.updated` and merges master into its branch at once. Where GitHub can do that without a session (no conflict, the
// branch is only behind), the gateway itself calls "update branch" after a delay if the owner has not pushed. Counts for /status and the dashboard.
sealed class BaseUpdateSettings
{
    public bool Enabled = true, AutoUpdateBranch = true;
    public double DebounceSeconds = 20, AutoUpdateDelaySeconds = 120, QuietSeconds = 60, SweepCheckSeconds = 60;

    public static BaseUpdateSettings Load(string scriptsDir)
    {
        var path = Environment.GetEnvironmentVariable("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? root = null; try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { }
        var b = root?["baseUpdate"]; var t = root?["timing"];
        static bool Num(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
        double D(JsonNode? section, string k, double d) => Num(Environment.GetEnvironmentVariable("FACTORY_TIMING_" + k), out var e) ? e : Num(section?[k]?.ToString(), out var v) ? v : d;
        bool B(string k, bool d) => b?[k] is JsonNode n && bool.TryParse(n.ToString(), out var v) ? v : d;
        return new BaseUpdateSettings
        {
            Enabled = B("enabled", true), AutoUpdateBranch = B("autoUpdateBranch", true),
            DebounceSeconds = D(t, "baseUpdateDebounceSeconds", 20), AutoUpdateDelaySeconds = D(b, "autoUpdateDelaySeconds", 120),
            QuietSeconds = D(b, "quietSeconds", 60), SweepCheckSeconds = D(b, "sweepCheckSeconds", 60)
        };
    }
}

sealed class BaseWatch(Runner runner, Plant plant, BaseUpdateSettings cfg, bool ownerMode) : BackgroundService
{
    sealed class PendingUpdate { public int Item, Pr; public string Expected = ""; public DateTime At; public bool Running; }
    readonly object gate = new();
    string known = "";                                   // the last head of the default branch a trigger (or the first look) was computed for
    readonly HashSet<int> mergedPrs = new();              // pull requests of the webhooks waiting for the debounce
    DateTime? firstAt, dueAt;
    DateTime lastCheck = DateTime.MinValue;
    readonly Dictionary<int, int> behind = new();         // per owned item: commits its branch is behind master, as of the last look
    readonly Dictionary<int, DateTime> headChangedAt = new();
    readonly Dictionary<int, PendingUpdate> updates = new();
    readonly HashSet<int> awaiting = new();               // told, behind, not yet back-merged
    readonly SemaphoreSlim running = new(1, 1);
    long triggers, eventsSent, gatewayUpdates, workerBackMerges, gatewayConflicts;
    DateTime? lastTriggerAt;

    public bool AutoUpdateBranch { get { lock (gate) return cfg.AutoUpdateBranch; } set { lock (gate) cfg.AutoUpdateBranch = value; } }

    // a pull request merged into the default branch (webhook): one trigger for a burst, with the latest head; at most 3 debounce windows late
    public void OnMerged(int pr)
    {
        if (!ownerMode || !cfg.Enabled) return;
        lock (gate)
        {
            var now = DateTime.UtcNow; if (pr > 0) mergedPrs.Add(pr); firstAt ??= now;
            var due = now.AddSeconds(cfg.DebounceSeconds); var cap = firstAt.Value.AddSeconds(3 * cfg.DebounceSeconds);
            dueAt = due < cap ? due : cap;
        }
    }
    public void NoteHeadChange(int item) { lock (gate) headChangedAt[item] = DateTime.UtcNow; }

    // after each sweep: the fallback for a missed or absent webhook, and the refresh of the behind counts (at most every sweepCheckSeconds)
    // the back-merge trigger acts on GitHub: only in the process that holds the leader lease (Leader.cs)
    public Func<bool>? MayAct { get; set; }
    public async Task AfterSweep()
    {
        if (!ownerMode || !cfg.Enabled || MayAct?.Invoke() == false) return;
        lock (gate) { if (dueAt != null || (DateTime.UtcNow - lastCheck).TotalSeconds < cfg.SweepCheckSeconds) return; lastCheck = DateTime.UtcNow; }
        await Run(new List<int>(), "sweep");
    }

    async Task Run(List<int> prs, string via)
    {
        await running.WaitAsync();
        try
        {
            var owners = plant.OwnersSnapshot(); string k; lock (gate) k = known;
            var input = new JsonObject { ["known"] = k, ["items"] = new JsonArray(owners.Select(o => (JsonNode)o.Item).ToArray()), ["mergedPrs"] = new JsonArray(prs.Select(p => (JsonNode)p).ToArray()) };
            var (code, output) = await runner.RunAsync("base-watch", input.ToJsonString(), TimeSpan.FromSeconds(120));
            if (code != 0) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"baseUpdate\":\"watch-failed\",\"exit\":{code}}}"); return; }
            var j = JsonNode.Parse(output)!; var head = j["head"]?.ToString() ?? ""; var changed = j["changed"]?.GetValue<bool>() == true;
            var rows = (j["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var sent = new List<(int Item, string Owner, JsonObject Row)>();
            lock (gate)
            {
                if (head == "") return;
                behind.Clear();
                foreach (var r in rows)
                {
                    var n = r["item"]!.GetValue<int>(); var b = r["behind"]?.GetValue<int>() ?? 0; var skip = r["skip"]?.ToString() ?? "";
                    if (skip == "" && b >= 0) behind[n] = b;
                    // an owner that was told and whose branch now has master: a back-merge by the worker (the gateway's own are taken out when it makes them)
                    if (awaiting.Contains(n) && (skip != "" || b == 0)) { awaiting.Remove(n); if (skip == "" && b == 0) workerBackMerges++; }
                }
                if (known == "") { known = head; return; }
                if (!changed) return;
                // the sweep saw master move: debounced like a webhook, so a webhook that is on its way (and the pull request numbers it carries)
                // joins this trigger instead of finding master already seen
                if (via == "sweep") { if (dueAt == null) { firstAt = DateTime.UtcNow; dueAt = firstAt.Value.AddSeconds(cfg.DebounceSeconds); } return; }
                known = head; triggers++; lastTriggerAt = DateTime.UtcNow;
                foreach (var r in rows)
                {
                    var n = r["item"]!.GetValue<int>();
                    if ((r["skip"]?.ToString() ?? "") != "" || (r["behind"]?.GetValue<int>() ?? 0) <= 0) continue;
                    var owner = owners.FirstOrDefault(o => o.Item == n).Owner; if (string.IsNullOrEmpty(owner)) continue;
                    sent.Add((n, owner, r));
                }
            }
            var sha7 = head.Length > 7 ? head[..7] : head;
            foreach (var (n, owner, r) in sent)
            {
                var pr = r["pr"]?.GetValue<int>() ?? 0;
                var evt = new JsonObject
                {
                    ["provider"] = "gateway", ["event"] = "base.updated", ["item"] = n, ["base"] = "master", ["sha"] = head, ["prs"] = new JsonArray(prs.Select(p => (JsonNode)p).ToArray()),
                    ["pr"] = pr, ["branch"] = r["branch"]?.ToString() ?? "", ["behind"] = r["behind"]!.GetValue<int>(), ["overlap"] = r["overlap"]?.DeepClone() ?? new JsonArray(), ["via"] = via, ["at"] = DateTime.UtcNow.ToString("o")
                };
                if (!plant.RouteBaseUpdated(n, evt)) continue;
                lock (gate)
                {
                    eventsSent++; awaiting.Add(n);
                    if (cfg.AutoUpdateBranch && pr > 0 && r["head"]?.ToString() is { Length: > 0 } h) updates[n] = new PendingUpdate { Item = n, Pr = pr, Expected = h, At = DateTime.UtcNow.AddSeconds(cfg.AutoUpdateDelaySeconds) };
                }
                var overlap = string.Join(", ", (r["overlap"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()).Take(10));
                Journal(n, "base-updated", new JsonObject { ["sha7"] = sha7, ["prs"] = string.Join(", ", prs.Select(p => "#" + p)), ["worker"] = owner, ["behind"] = r["behind"]!.GetValue<int>(), ["overlap"] = overlap }, "bu|" + head);
            }
            plant.OperatorPlantEvent("base-updated", 0, detail: $"sent-{sent.Count}-behind-{rows.Count(r => (r["behind"]?.GetValue<int>() ?? 0) > 0)}");
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["baseUpdate"] = "trigger", ["via"] = via, ["sha"] = head, ["prs"] = prs.Count, ["sent"] = sent.Count }.ToJsonString());
        }
        catch (Exception e) { Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["baseUpdate"] = "error", ["message"] = e.Message }.ToJsonString()); }
        finally { running.Release(); }
    }

    void Journal(int item, string ev, JsonObject fields, string key) => _ = Task.Run(async () =>
    {
        try { await runner.RunAsync("journal", new JsonObject { ["item"] = item, ["event"] = ev, ["fields"] = fields, ["key"] = key }.ToJsonString(), TimeSpan.FromSeconds(60)); }
        catch (Exception e) { Console.Error.WriteLine($"journal {ev} for #{item} failed: {e.Message}"); }
    });

    // the gateway's own back-merge: due, switched on, and the head has not changed for quietSeconds (the owner may be pushing)
    async Task UpdateBranch(PendingUpdate u)
    {
        try
        {
            var (code, output) = await runner.RunAsync("base-update-branch", new JsonObject { ["item"] = u.Item, ["pr"] = u.Pr, ["expected"] = u.Expected }.ToJsonString(), TimeSpan.FromSeconds(90));
            var outcome = code == 0 ? JsonNode.Parse(output)?["outcome"]?.ToString() ?? "" : "exit-" + code;
            lock (gate)
            {
                if (outcome == "updated") { gatewayUpdates++; awaiting.Remove(u.Item); behind[u.Item] = 0; }
                if (outcome == "conflict") gatewayConflicts++;
            }
            if (outcome == "updated")
            {
                Journal(u.Item, "branch-updated", new JsonObject { ["pr"] = u.Pr, ["seconds"] = (int)cfg.AutoUpdateDelaySeconds }, "gu|" + u.Expected);
                plant.OperatorPlantEvent("branch-updated", u.Item, detail: "pr-" + u.Pr);
            }
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["baseUpdate"] = "update-branch", ["item"] = u.Item, ["pr"] = u.Pr, ["outcome"] = outcome }.ToJsonString());
        }
        catch (Exception e) { Console.Error.WriteLine($"update-branch #{u.Item}: {e.Message}"); }
        finally { lock (gate) { if (updates.TryGetValue(u.Item, out var cur) && ReferenceEquals(cur, u)) updates.Remove(u.Item); } }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!ownerMode) return;
        while (!ct.IsCancellationRequested)
        {
            if (MayAct?.Invoke() == false) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { } continue; }
            try
            {
                List<int>? prs = null; var due = new List<PendingUpdate>();
                lock (gate)
                {
                    var now = DateTime.UtcNow;
                    if (dueAt is { } d && d <= now) { prs = mergedPrs.OrderBy(x => x).ToList(); mergedPrs.Clear(); dueAt = null; firstAt = null; lastCheck = now; }
                    foreach (var u in updates.Values.Where(u => !u.Running && u.At <= now))
                    {
                        if (!cfg.AutoUpdateBranch) { u.Running = true; due.Add(u); continue; }
                        if (headChangedAt.TryGetValue(u.Item, out var hc) && (now - hc).TotalSeconds < cfg.QuietSeconds) { u.At = hc.AddSeconds(cfg.QuietSeconds); continue; }
                        u.Running = true; due.Add(u);
                    }
                }
                if (prs != null) await Run(prs, prs.Count > 0 ? "webhook" : "sweep-confirmed");
                foreach (var u in due)
                {
                    if (!AutoUpdateBranch) { lock (gate) updates.Remove(u.Item); continue; }
                    _ = Task.Run(() => UpdateBranch(u));
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"base watch: {e.Message}"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
        }
    }

    public JsonObject Status()
    {
        lock (gate)
            return new JsonObject
            {
                ["enabled"] = cfg.Enabled && ownerMode, ["autoUpdateBranch"] = cfg.AutoUpdateBranch, ["head"] = known, ["pending"] = dueAt != null, ["triggers"] = triggers, ["eventsSent"] = eventsSent,
                ["gatewayUpdates"] = gatewayUpdates, ["gatewayConflicts"] = gatewayConflicts, ["workerBackMerges"] = workerBackMerges, ["itemsBehind"] = behind.Count(kv => kv.Value > 0),
                ["lastTriggerAt"] = lastTriggerAt?.ToString("o"), ["behind"] = new JsonObject(behind.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)kv.Value)))
            };
    }
}
