using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

// The plant manager on demand (docs/design/agents-roles-launch.md "The manager as a short Copilot task"): while nobody holds the supervisor slot, the
// gateway watches the digest and, when something needs a judgement, launches ONE short Copilot task for the manager role. The task holds a role token
// of role "manager" (the manager tools of /mcp only), is registered as the supervisor while it runs, and ends at session_end, at its end by itself or
// at api.copilot.manager.maxMinutes. It edits no repository: its base_ref is the default branch and its tools have no file, git or pull request verb.
// Limits: one task at a time, a cooldown between tasks, a daily count, the delivery budget of the manager (messaging 3.9). No token cap.
sealed class ManagerCopilotSettings
{
    public bool Enabled;
    public int MaxPerDay = 6;
    public double CooldownMinutes = 15, MaxMinutes = 10;

    // api.copilot.manager of the instance configuration, then api.copilot.environments.<this environment>.manager over it (an entry may be written
    // ready but off), then FACTORY_COPILOT_MANAGER_ENABLED.
    public static ManagerCopilotSettings Load(string scriptsDir, Func<string, string?> get, string environment)
    {
        var s = new ManagerCopilotSettings();
        var path = get("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? root = null; try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { }
        var c = root?["api"]?["copilot"];
        void Read(JsonNode? m)
        {
            if (m is not JsonObject) return;
            static bool Num(string? t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
            if (m["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb)) s.Enabled = eb;
            if (Num(m["maxPerDay"]?.ToString(), out var d)) s.MaxPerDay = (int)d;
            if (Num(m["cooldownMinutes"]?.ToString(), out var cd)) s.CooldownMinutes = cd;
            if (Num(m["maxMinutes"]?.ToString(), out var mm)) s.MaxMinutes = Math.Max(0.1, mm);
        }
        Read(c?["manager"]);
        Read(c?["environments"]?[environment]?["manager"]);
        if (bool.TryParse(get("FACTORY_COPILOT_MANAGER_ENABLED"), out var en)) s.Enabled = en;
        return s;
    }
}

sealed class ManagerCopilot
{
    readonly ManagerCopilotSettings cfg;
    readonly CopilotCloudRuntime runtime;
    readonly CopilotCodes codes;
    readonly Plant plant;
    readonly Func<string, long, string> mint;       // name, expiry -> the task's role token (role manager)
    readonly Func<string, string> prompt;           // one-time code -> the task's bootstrap text
    readonly Func<string, Task> revoke;             // name -> its credentials are refused from now on
    readonly object gate = new();
    readonly List<DateTime> launchedAt = new();
    readonly Dictionary<string, int> seen = new(StringComparer.Ordinal);   // entries the last launch was for: key -> bucket
    string name = "", taskId = "", url = "", lastStatus = "", lastError = "", lastReason = "";
    bool running; DateTime started, lastEnd, lastLaunch; double minutes; int launches;
    JsonObject? usage;
    Channel<(long, string)>? slot;

    public ManagerCopilot(ManagerCopilotSettings cfg, CopilotCloudRuntime runtime, CopilotCodes codes, Plant plant, Func<string, long, string> mint, Func<string, string> prompt, Func<string, Task> revoke)
    { this.cfg = cfg; this.runtime = runtime; this.codes = codes; this.plant = plant; this.mint = mint; this.prompt = prompt; this.revoke = revoke; }

    public bool Enabled => cfg.Enabled;
    public bool Running { get { lock (gate) return running; } }
    // is this the name of the task that runs now (its credential admits the manager tools only while it does)
    public bool Live(string who) { lock (gate) return running && name == who; }
    // the session link of the task that runs now, for the lines it causes in an item's journal
    public string SessionUrl(string who) { lock (gate) return running && name == who ? url : ""; }

    // The trigger: entries that need a judgement and are new or worse than at the last launch.
    (bool Go, string Why) Pending(DateTime now)
    {
        var (entries, present, budget) = plant.ManagerWork();
        lock (gate)
        {
            foreach (var k in seen.Keys.Where(k => entries.All(e => e.Key != k)).ToList()) seen.Remove(k);
            if (present) return (false, "supervisor-present");
            var fresh = entries.Where(e => !seen.TryGetValue(e.Key, out var b) || e.Bucket > b).ToList();
            if (fresh.Count == 0) return (false, "nothing-pending");
            if (!budget) return (false, "budget");
            return (true, string.Join(",", fresh.Select(e => e.Kind).Distinct()));
        }
    }

    // One pass, by the acting gateway: follow the task that runs, or launch one when the trigger and the limits allow it.
    public async Task Tick(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string? follow = null, who = null;
        lock (gate)
        {
            if (running) { follow = taskId; who = name; }
        }
        if (follow != null) { await Follow(who!, follow, ct); return; }
        if (!cfg.Enabled) return;
        lock (gate)
        {
            launchedAt.RemoveAll(t => (now - t).TotalHours >= 24);
            if (launchedAt.Count >= cfg.MaxPerDay) { lastReason = "daily-cap"; return; }
            if (lastEnd != default && (now - lastEnd).TotalMinutes < cfg.CooldownMinutes) { lastReason = "cooldown"; return; }
        }
        var (go, why) = Pending(now);
        lock (gate) lastReason = go ? "launching" : why;
        if (go) await Launch(why, ct);
    }

    async Task Launch(string why, CancellationToken ct)
    {
        var nm = "mgr-cop-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var (ok, active, _, ch, _) = plant.RegisterSupervisor(nm, "copilot-auto", "", 0);
        if (!ok || ch == null) { lock (gate) { lastError = "supervisor-active:" + active; lastEnd = DateTime.UtcNow; } return; }
        var expires = DateTimeOffset.UtcNow.AddSeconds(cfg.MaxMinutes * 60 + 300).ToUnixTimeSeconds();
        var code = codes.Mint(nm, mint(nm, expires), TimeSpan.FromMinutes(Math.Max(2, cfg.MaxMinutes)));
        var (lok, id, link, err) = await runtime.LaunchRole(prompt(code), ct);
        if (!lok)
        {
            codes.Forget(nm); plant.DisconnectSupervisor(ch); ch.Writer.TryComplete(); plant.SupervisorEnded(nm);
            lock (gate) { lastError = err; lastEnd = DateTime.UtcNow; launchedAt.Add(lastEnd); }
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["manager"] = "copilot-launch-failed", ["error"] = err }.ToJsonString());
            return;
        }
        var (entries, _, _) = plant.ManagerWork();
        lock (gate)
        {
            name = nm; taskId = id; url = link; running = true; started = DateTime.UtcNow; lastLaunch = started; launches++; launchedAt.Add(started);
            usage = null; lastStatus = "Running"; lastError = ""; minutes = 0; slot = ch;
            seen.Clear(); foreach (var e in entries) seen[e.Key] = e.Bucket;
        }
        // the supervisor's stream has no reader: what the plant writes to it (the question batch, the baseline digest) is dropped; the task reads the tools
        _ = Task.Run(async () => { try { await foreach (var _ in ch.Reader.ReadAllAsync()) { } } catch { } });
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["manager"] = "copilot-launched", ["name"] = nm, ["task"] = id, ["why"] = why, ["url"] = link }.ToJsonString());
    }

    async Task Follow(string who, string id, CancellationToken ct)
    {
        string status = "Running"; string link = ""; JsonObject? use = null; var read = false;
        try { (read, status, link, use) = await runtime.ReadRole(id, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"manager copilot: read: {e.GetType().Name}"); }
        bool over;
        lock (gate)
        {
            if (read) { if (link != "") url = link; if (use != null) usage = use; }
            over = (DateTime.UtcNow - started).TotalMinutes > cfg.MaxMinutes;
        }
        if (read && status != "Running") await End(who, status == "Succeeded" ? "task-ended" : "task-" + status.ToLowerInvariant());
        else if (over) await End(who, "deadline");
    }

    // The end of the task: its tools are refused from now on (the name is revoked), its code is spent, the supervisor slot is let go. Idempotent.
    public async Task End(string who, string reason)
    {
        Channel<(long, string)>? ch; string id, link; JsonObject? use;
        lock (gate)
        {
            if (!running || name != who) return;
            running = false; ch = slot; slot = null; lastEnd = DateTime.UtcNow; minutes = Math.Round((lastEnd - started).TotalMinutes, 1); lastReason = reason; lastStatus = reason;
            id = taskId; link = url; use = usage;
        }
        codes.Forget(who);
        if (ch != null) { plant.DisconnectSupervisor(ch); ch.Writer.TryComplete(); }
        plant.SupervisorEnded(who);
        try { await revoke(who); } catch (Exception e) { Console.Error.WriteLine($"manager copilot: revoke: {e.GetType().Name}"); }
        // the usage the sessions report is read once more after the end, for the log and /status
        try { var (rd, _, l2, u2) = await runtime.ReadRole(id, CancellationToken.None); if (rd) { lock (gate) { if (u2 != null) usage = use = u2; if (l2 != "") url = link = l2; } } } catch { }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["manager"] = "copilot-ended", ["name"] = who, ["task"] = id, ["reason"] = reason, ["minutes"] = minutes, ["usage"] = use?.DeepClone(), ["url"] = link }.ToJsonString());
    }

    // The block /status carries under manager.copilot: counts and the task link only.
    public JsonObject Status()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            return new JsonObject
            {
                ["enabled"] = cfg.Enabled, ["present"] = running, ["running"] = running, ["task"] = running || taskId != "" ? taskId : null, ["url"] = url == "" ? null : url,
                ["launches"] = launches, ["today"] = launchedAt.Count(t => (now - t).TotalHours < 24), ["maxPerDay"] = cfg.MaxPerDay, ["cooldownMinutes"] = cfg.CooldownMinutes, ["maxMinutes"] = cfg.MaxMinutes,
                ["last"] = lastLaunch == default ? null : lastLaunch.ToString("o"), ["lastEnd"] = lastEnd == default ? null : lastEnd.ToString("o"),
                ["minutes"] = running ? Math.Round((now - started).TotalMinutes, 1) : minutes, ["status"] = lastStatus, ["usage"] = usage?.DeepClone(), ["next"] = lastReason, ["error"] = lastError == "" ? null : lastError
            };
        }
    }
}
