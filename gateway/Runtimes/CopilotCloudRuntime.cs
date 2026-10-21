using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// The api launch mode, v2: the Copilot cloud agent (docs/design/copilot-cloud-agent.md). One agent task per worker and phase: POST
// /agents/repos/{owner}/{repo}/tasks (GitHub's agent tasks REST API, public preview, X-GitHub-Api-Version 2026-03-10) as the bot user that holds
// the Copilot seat, with the phase's bootstrap prompt (prompts/bootstrap/copilot.md). The prompt carries no credential: it carries a one-time
// code that the agent's fworker-mcp (the stdio proxy the gateway serves at /fworker-mcp.mjs) exchanges, with the repository's bootstrap token,
// for the worker's r1 (POST /api/v1/copilot/exchange). The run is followed with GET .../tasks/{id}. The API has no cancel: a task past the
// deadline (api.copilot.maxMinutes) is reported Failed and its name revoked, so the r1 it holds calls no tool any more.
// The token (COPILOT_TASKS_TOKEN; api.copilot.token only for a local gateway, never in a committed file) never leaves this class; nothing here
// logs a request body.
sealed class CopilotCloudSettings
{
    public bool Enabled;
    public int MaxConcurrent = 1, MaxExecutionsPerDay = 30, MaxFailuresPerPhase = 2, MaxLaunchesPerPhase = 3, UnhealthyAfterFailures = 2, BootstrapGeneration = 1;
    public double MaxMinutes = 60, AckSeconds = 900, ListSeconds = 30;
    // the CI release (CopilotCiRelease.cs): api.copilot.ciRelease, on by default wherever the mode is on; a pass at most every ciReleaseSeconds
    public bool CiRelease = true;
    public double CiReleaseSeconds = 120;
    // the credit guard: a task whose sessions report more ai_credits than this is ended (its r1 revoked) and its item parked credit-guard; 0 = off
    public double MaxCreditsPerTask = 3e9;
    public string Environment = "tdd";
    public string ApiUrl = "https://api.github.com", ApiVersion = "2026-03-10", Token = "", GatewayUrl = "", Model = "",
        Repository = "clearmeasure-aisf-sample-apps/cmdemo1-workorders", DefaultBranch = "master";
    public bool On => Enabled && MaxConcurrent > 0 && Token != "" && GatewayUrl != "";

    // api.copilot of the instance configuration, then the variables of the gateway app: COPILOT_TASKS_TOKEN (the token), FACTORY_COPILOT_ENABLED,
    // FACTORY_COPILOT_GATEWAY_URL (default FACTORY_PUBLIC_URL), FACTORY_COPILOT_MAX_CONCURRENT, FACTORY_COPILOT_MAX_MINUTES.
    public static CopilotCloudSettings Load(string scriptsDir, Func<string, string?> get)
    {
        var s = new CopilotCloudSettings();
        var path = get("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? root = null; try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { }
        var c = root?["api"]?["copilot"];
        if (c is JsonObject)
        {
            static bool Num(string? t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
            double D(string k, double d) => Num(c[k]?.ToString(), out var v) ? v : d;
            string S(string k, string d) => c[k] is JsonValue sv && sv.TryGetValue<string>(out var str) ? str : d;
            s.Enabled = c["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
            s.MaxConcurrent = (int)D("maxConcurrent", 1); s.MaxExecutionsPerDay = (int)D("maxExecutionsPerDay", 30); s.MaxMinutes = Math.Max(0.1, D("maxMinutes", 60));
            s.MaxFailuresPerPhase = Math.Max(1, (int)D("maxFailuresPerPhase", 2)); s.MaxLaunchesPerPhase = (int)D("maxLaunchesPerPhase", 3); s.UnhealthyAfterFailures = Math.Max(1, (int)D("unhealthyAfterFailures", 2));
            s.AckSeconds = D("ackSeconds", 900); s.ListSeconds = Math.Max(1, D("listSeconds", 30)); s.BootstrapGeneration = Math.Max(1, (int)D("bootstrapGeneration", 1));
            s.MaxCreditsPerTask = D("maxCreditsPerTask", s.MaxCreditsPerTask);
            s.ApiUrl = S("apiUrl", s.ApiUrl).TrimEnd('/'); s.ApiVersion = S("apiVersion", s.ApiVersion); s.Token = S("token", "");
            s.GatewayUrl = S("gatewayUrl", "").TrimEnd('/'); s.Model = S("model", "").Trim();
            s.Repository = S("repository", s.Repository).Trim('/'); s.DefaultBranch = S("defaultBranch", s.DefaultBranch);
            if (c["ciRelease"] is JsonValue crv && crv.TryGetValue<bool>(out var crb)) s.CiRelease = crb;
            s.CiReleaseSeconds = Math.Max(1, D("ciReleaseSeconds", 120));
        }
        string? V(string n) => get(n) is { } v && v.Trim() != "" ? v.Trim() : null;
        s.Environment = (V("FACTORY_ENVIRONMENT") ?? "tdd").ToLowerInvariant();
        // api.copilot.environments: when it names any, the mode is on only in those, each with its gateway's public URL (one image runs everywhere)
        if (c?["environments"] is JsonObject { Count: > 0 } envs)
        {
            if (envs[s.Environment] is JsonObject me)
            {
                if (me["gatewayUrl"] is JsonValue gv && gv.TryGetValue<string>(out var g) && g != "") s.GatewayUrl = g.TrimEnd('/');
                // an entry may be written ready but off ("enabled": false): the owner switches it on in the file
                if (me["enabled"] is JsonValue mev && mev.TryGetValue<bool>(out var meb) && !meb) s.Enabled = false;
            }
            else s.Enabled = false;
        }
        if (V("COPILOT_TASKS_TOKEN") is { } key && key != "unset") s.Token = key;
        if (bool.TryParse(V("FACTORY_COPILOT_ENABLED"), out var en)) s.Enabled = en;
        if (s.GatewayUrl == "") s.GatewayUrl = (V("FACTORY_COPILOT_GATEWAY_URL") ?? V("FACTORY_PUBLIC_URL") ?? "").TrimEnd('/');
        else if (V("FACTORY_COPILOT_GATEWAY_URL") is { } gu) s.GatewayUrl = gu.TrimEnd('/');
        if (int.TryParse(V("FACTORY_COPILOT_MAX_CONCURRENT"), NumberStyles.None, CultureInfo.InvariantCulture, out var mc)) s.MaxConcurrent = mc;
        if (double.TryParse(V("FACTORY_COPILOT_MAX_CREDITS_PER_TASK"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mcr) && double.IsFinite(mcr) && mcr >= 0) s.MaxCreditsPerTask = mcr;
        if (double.TryParse(V("FACTORY_COPILOT_MAX_MINUTES"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && double.IsFinite(mm) && mm > 0) s.MaxMinutes = mm;
        return s;
    }

    public RuntimeLimits Limits(Func<string, int, string, long, string> mint) => new()
    {
        Kind = "copilot-cloud", Prefix = "cop", IsOn = () => On, MaxConcurrent = MaxConcurrent, MaxExecutionsPerDay = MaxExecutionsPerDay, AckSeconds = AckSeconds,
        MaxFailuresPerPhase = MaxFailuresPerPhase, MaxLaunchesPerPhase = MaxLaunchesPerPhase, UnhealthyAfterFailures = UnhealthyAfterFailures, ListSeconds = ListSeconds,
        TokenSeconds = (long)(MaxMinutes * 60) + 900, MintCredential = mint, RevokeOnEnd = true
    };
}

// The one-time codes of Copilot tasks: code -> the worker's r1, redeemed once, within the acknowledgement window. Only the hash of a code is kept.
sealed class CopilotCodes
{
    sealed record Entry(string Name, string Token, DateTime Expires);
    readonly Dictionary<string, Entry> live = new(StringComparer.Ordinal);
    readonly object gate = new();
    static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    public string Mint(string name, string token, TimeSpan ttl)
    {
        var code = "c1." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (gate) { Prune(); live[Hash(code)] = new Entry(name, token, DateTime.UtcNow.Add(ttl)); }
        return code;
    }

    // the r1 once; null for an unknown, spent, malformed or expired code
    public (string Name, string Token)? Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 100 || !code.StartsWith("c1.")) return null;
        lock (gate)
        {
            Prune();
            if (!live.Remove(Hash(code), out var e) || e.Expires < DateTime.UtcNow) return null;
            return (e.Name, e.Token);
        }
    }

    public void Forget(string name) { lock (gate) foreach (var k in live.Where(kv => kv.Value.Name == name).Select(kv => kv.Key).ToList()) live.Remove(k); }
    public int Live { get { lock (gate) { Prune(); return live.Count; } } }
    void Prune() { var now = DateTime.UtcNow; foreach (var k in live.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList()) live.Remove(k); }
}

// an item's open pull request: its number and head branch
sealed record ItemPr(int Number, string Branch);

sealed class CopilotCloudRuntime : IWorkerRuntime, IRuntimeDetail
{
    sealed class Task_ { public string Id = "", Worker = "", Phase = "", State = "queued", Status = "Running"; public int Item; public bool Guarded; public DateTime Started = DateTime.UtcNow; public DateTime? Ended; public JsonNode? Usage, Artifacts; public JsonArray? Fields; }

    readonly CopilotCloudSettings cfg;
    readonly HttpClient http;
    readonly CopilotCodes codes;
    readonly Func<int, string, string, ItemPr?, string> prompt;   // item, phase, one-time code, the item's open pull request -> the bootstrap text
    readonly Func<int, CancellationToken, Task<ItemPr?>>? itemPr;   // the item's open pull request (number, head branch), null when it has none
    readonly Dictionary<string, Task_> tasks = new(StringComparer.Ordinal);   // by worker name
    // The gateway owns the completion of Development (rule verb copilot-settle): (item, stage launch | end, the task's branch) -> its action:
    // launch (start the task), awaiting (the item's pull request waits for its checks in the gateway: no task), none
    public Func<int, string, string?, CancellationToken, Task<string>>? Settle { get; set; }
    // the credit guard (api.copilot.maxCreditsPerTask): parks the item (item, phase, the credits the task reported)
    public Func<int, string, double, Task>? OverBudget { get; set; }

    public CopilotCloudRuntime(CopilotCloudSettings cfg, CopilotCodes codes, Func<int, string, string, ItemPr?, string> prompt, HttpClient? http = null, Func<int, CancellationToken, Task<ItemPr?>>? itemPr = null)
    { this.cfg = cfg; this.codes = codes; this.prompt = prompt; this.itemPr = itemPr; this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }; }

    public string Kind => "copilot-cloud";
    public int MaxConcurrent => cfg.MaxConcurrent;
    string TasksPath => $"/agents/repos/{cfg.Repository}/tasks";

    public JsonObject? Detail(string workerName)
    {
        lock (tasks)
            return tasks.TryGetValue(workerName, out var t) ? new JsonObject { ["task"] = t.Id, ["state"] = t.State, ["usage"] = t.Usage?.DeepClone(), ["artifacts"] = t.Artifacts?.DeepClone(), ["taskFields"] = t.Fields?.DeepClone(),
                ["minutes"] = Math.Round(((t.Ended ?? DateTime.UtcNow) - t.Started).TotalMinutes, 1) } : null;
    }

    async Task<(int Status, JsonNode? Body)> Send(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, cfg.ApiUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", cfg.ApiVersion);
        req.Headers.UserAgent.ParseAdd("fworker-gateway");
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? j = null; try { if (text.Length > 0) j = JsonNode.Parse(text); } catch { }
        return ((int)resp.StatusCode, j);
    }

    // The branch each item's Development task reported (its artifacts' head_ref; the coding agent pushes only copilot/* branches)
    readonly Dictionary<int, string> heads = new();
    public string? HeadOf(int item) { lock (tasks) return heads.TryGetValue(item, out var h) ? h : null; }

    // A task of an item that already has its open pull request (fix-forward, rework after a red check or an FT FAIL, Functional Testing) starts
    // from that pull request's head branch, so the agent sees and fixes it. The agent tasks API never pushes to base_ref: Copilot makes a new
    // copilot/* branch from it and opens its draft against it; pr_open then makes that draft the item's pull request (retargeted to the default
    // branch, it carries the old commits) and closes the superseded one (Verbs.Pr.ps1, Close-SupersededPr). Without one: Technical Design and
    // Development start from the default branch; Functional Testing from the branch Development reported.
    string BaseRef(int item, string phase, ItemPr? pr) => pr is { Branch.Length: > 0 } ? pr.Branch
        : phase.Equals("Functional Testing", StringComparison.OrdinalIgnoreCase) ? HeadOf(item) ?? cfg.DefaultBranch : cfg.DefaultBranch;
    static bool IsDevelopment(string phase) => phase.Equals("Development", StringComparison.OrdinalIgnoreCase);
    static bool IsTechnicalDesign(string phase) => phase.Equals("Technical Design", StringComparison.OrdinalIgnoreCase);

    public JsonObject TaskBody(ProvisionRequest r, string code, ItemPr? pr = null)
    {
        var o = new JsonObject { ["prompt"] = prompt(r.Item, r.Phase, code, pr), ["base_ref"] = BaseRef(r.Item, r.Phase, pr), ["create_pull_request"] = IsDevelopment(r.Phase) };   // Development: Copilot opens a draft on its branch, pr_open takes it over
        if (cfg.Model != "") o["model"] = cfg.Model;
        return o;
    }

    public async Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct)
    {
        // the r1 (r.Nonce) stays in the gateway: the task gets a one-time code for it, valid for the acknowledgement window
        var code = codes.Mint(r.WorkerName, r.Nonce, TimeSpan.FromSeconds(Math.Max(cfg.AckSeconds, 60) + 300));
        // Development of an item that already has its pull request with changes and no rework asked for: no new task, the gateway waits for its
        // checks (#395 was relaunched in mode first after its green pull request and opened a duplicate draft for 25B ai_credits)
        if (IsDevelopment(r.Phase) && Settle != null)
        {
            string act; try { act = await Settle(r.Item, "launch", null, ct); } catch (Exception e) { act = "launch"; Console.Error.WriteLine($"copilot: settle of #{r.Item} before launch: {e.GetType().Name}"); }
            if (act == "awaiting") { codes.Forget(r.WorkerName); return new(false, "", "settled"); }
        }
        ItemPr? pr = null;
        if (itemPr != null && !IsTechnicalDesign(r.Phase)) { try { pr = await itemPr(r.Item, ct); } catch (Exception e) { Console.Error.WriteLine($"copilot: pull request of #{r.Item}: {e.GetType().Name}"); } }
        int s; JsonNode? j;
        try { (s, j) = await Send(HttpMethod.Post, TasksPath, TaskBody(r, code, pr), ct); }
        catch (Exception e) { codes.Forget(r.WorkerName); return new(false, "", "launch-" + e.GetType().Name); }
        var id = j?["id"]?.ToString() ?? "";
        if (s is not (200 or 201 or 202) || id == "") { codes.Forget(r.WorkerName); return new(false, "", s is 200 or 201 or 202 ? "launch-no-id" : $"launch-{s}"); }
        lock (tasks) tasks[r.WorkerName] = new Task_ { Id = id, Worker = r.WorkerName, Item = r.Item, Phase = r.Phase, State = j?["state"]?.ToString() ?? "queued" };
        return new(true, id, "");
    }

    // The agent tasks API has no cancel: the end is recorded here, the code is spent, and the manager revokes the name (RevokeOnEnd), so the
    // r1 the task holds is refused at once. Idempotent.
    public Task Deprovision(string workerName, string reason, CancellationToken ct)
    {
        codes.Forget(workerName);
        lock (tasks) if (tasks.TryGetValue(workerName, out var t) && t.Ended == null) { t.Ended = DateTime.UtcNow; if (t.Status == "Running") t.Status = "Stopped"; }
        return Task.CompletedTask;
    }

    // the tasks this process started, with Container Apps job states: queued, in_progress and waiting_for_user are Running; completed and idle
    // Succeeded; failed and timed_out Failed; cancelled Stopped. A task past its deadline is reported Failed.
    public async Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct)
    {
        List<Task_> all; lock (tasks) { foreach (var k in tasks.Where(kv => kv.Value.Ended < DateTime.UtcNow.AddHours(-2)).Select(kv => kv.Key).ToList()) tasks.Remove(k); all = tasks.Values.ToList(); }
        var list = new List<RuntimeInstance>();
        foreach (var t in all)
        {
            try
            {
                if (t.Ended == null && (DateTime.UtcNow - t.Started).TotalMinutes > cfg.MaxMinutes)
                {
                    lock (tasks) { t.Status = "Failed"; t.Ended = DateTime.UtcNow; }
                    codes.Forget(t.Worker);
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-deadline", ["worker"] = t.Worker, ["item"] = t.Item, ["task"] = t.Id }.ToJsonString());
                }
                // read while running, and once more after the end for the usage the sessions report
                if (t.Ended == null || t.Usage == null)
                {
                    var (s, j) = await Send(HttpMethod.Get, $"{TasksPath}/{Uri.EscapeDataString(t.Id)}", null, ct);
                    if (s != 200) throw new HttpRequestException($"read of task answered {s}");
                    var st = j?["state"]?.ToString() ?? "";
                    var usage = Usage(j);
                    // the credit guard: one task over api.copilot.maxCreditsPerTask ends here (the API has no cancel: its r1 is revoked with the end)
                    // and its item is parked before the end is settled, so no relaunch and no settle follows
                    var credits = usage?["ai_credits"] is JsonValue cv && cv.TryGetValue<double>(out var cd) ? cd : 0;
                    if (cfg.MaxCreditsPerTask > 0 && credits > cfg.MaxCreditsPerTask && !t.Guarded)
                    {
                        lock (tasks) { t.Guarded = true; t.Usage = usage; }
                        codes.Forget(t.Worker);
                        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-credit-guard", ["worker"] = t.Worker, ["item"] = t.Item, ["phase"] = t.Phase, ["task"] = t.Id, ["ai_credits"] = credits, ["max"] = cfg.MaxCreditsPerTask }.ToJsonString());
                        if (OverBudget != null) { try { await OverBudget(t.Item, t.Phase, credits); } catch (Exception e) { Console.Error.WriteLine($"copilot: credit-guard park of #{t.Item}: {e.GetType().Name}"); } }
                        // the end is reported only after the park is saved: the settle at the end must see the guard's park
                        lock (tasks) if (t.Ended == null) { t.Status = "Failed"; t.Ended = DateTime.UtcNow; }
                    }
                    lock (tasks)
                    {
                        t.State = st; t.Artifacts = j?["artifacts"]?.DeepClone(); if (usage != null) t.Usage = usage;
                        if (IsDevelopment(t.Phase) && HeadRef(j) is { } hr) heads[t.Item] = hr;
                        t.Fields ??= new JsonArray((j as JsonObject)?.Select(kv => (JsonNode)kv.Key).ToArray() ?? Array.Empty<JsonNode>());
                        if (t.Ended == null && !t.Guarded)
                        {
                            t.Status = st switch { "completed" or "idle" => "Succeeded", "failed" or "timed_out" => "Failed", "cancelled" => "Stopped", _ => "Running" };
                            if (t.Status != "Running") t.Ended = DateTime.UtcNow;
                        }
                        else if (t.Usage == null) t.Usage = new JsonObject { ["reported"] = false };
                    }
                    if (t.Ended != null && st is not ("queued" or "in_progress")) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-usage", ["worker"] = t.Worker, ["item"] = t.Item, ["phase"] = t.Phase, ["task"] = t.Id, ["state"] = st,
                        ["minutes"] = Math.Round(((t.Ended ?? DateTime.UtcNow) - t.Started).TotalMinutes, 1), ["usage"] = t.Usage?.DeepClone() }.ToJsonString());
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"copilot: read of {t.Worker}: {e.GetType().Name}"); }
            list.Add(new RuntimeInstance(t.Id, t.Worker, t.Item, t.Phase, t.Status, t.Started, t.Ended));
        }
        return list;
    }

    // the branch of the task's artifacts ({type: branch, data: {head_ref}}), only a copilot/ branch
    public static string? HeadRef(JsonNode? task)
    {
        foreach (var a in (task?["artifacts"] as JsonArray) ?? new JsonArray())
            if (a?["type"]?.ToString() == "branch" && a["data"]?["head_ref"]?.ToString() is { } h && h.StartsWith("copilot/", StringComparison.Ordinal) && h.Length <= 200) return h;
        return null;
    }

    // the usage the task's sessions report ({type: premium_requests | ai_credits, amount}), summed by type; null when none reports one
    static JsonObject? Usage(JsonNode? task)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in (task?["sessions"] as JsonArray) ?? new JsonArray())
            if (s?["usage"] is JsonObject u && u["type"]?.ToString() is { Length: > 0 } ty && double.TryParse(u["amount"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                sums[ty] = sums.GetValueOrDefault(ty) + a;
        if (sums.Count == 0) return null;
        var o = new JsonObject(); foreach (var (k, v) in sums) o[k] = v; return o;
    }
}
