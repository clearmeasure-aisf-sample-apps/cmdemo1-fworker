using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

// The api launch mode, v1: Cursor Cloud Agents (docs/design/agents-roles-launch.md 10). One agent per worker: POST /v1/agents with the bootstrap
// prompt, the work-orders repository at the phase's starting branch, autoCreatePR false (the gateway's pr_open opens the pull request) and one MCP
// server, the gateway's public /mcp, with the worker's r1 in its Authorization header. The run is followed with GET /v1/agents/{id}; the agent
// is cancelled and deleted at session_end, at the deadline (api.cursor.maxMinutes) and on revoke, so the r1 Cursor stored goes with it.
// The API key (CURSOR_API_KEY; api.cursor.apiKey only for a local gateway, never in a committed file) never leaves this class; nothing here
// logs a request body. A process start deletes the fworker-* agents a former process left (their workers went with it).
sealed class CursorCloudSettings
{
    public bool Enabled;
    public int MaxConcurrent = 1, MaxExecutionsPerDay = 10, MaxFailuresPerPhase = 2, MaxLaunchesPerPhase = 3, UnhealthyAfterFailures = 2;
    public double MaxMinutes = 60;
    public double AckSeconds = 900, ListSeconds = 30, IdleGraceSeconds = 60;
    // Cursor's usage of an agent reads all zeros for a while after its run ends (live runs 371, 372 of 2026-10-17): the delete waits for a
    // non-zero read, re-reading after UsageRetrySeconds and doubling, at most UsageWaitSeconds after the end
    public double UsageRetrySeconds = 10, UsageWaitSeconds = 300;
    // the owner watch (Program.cs StartOwnerWatch): what each owned item waits on, read at most every 3 x this
    public double OwnerWatchSeconds = 120;
    // Model "auto": Cursor's Auto model (owner decision 2026-10-17, cost first); the adapter resolves it against GET /v1/models once and never
    // falls back to the account's default (which may be a premium model): a launch with a model Cursor refuses fails visibly instead
    public string Environment = "tdd", OptimizeFor = "cost";
    public string ApiUrl = "https://api.cursor.com", ApiKey = "", GatewayUrl = "", Model = "auto",
        Repository = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders", DefaultBranch = "master";
    public double QuotaRetryMinutes = 30;
    public Func<string?>? QuotaBlock;
    public bool On => Enabled && MaxConcurrent > 0 && ApiKey != "" && GatewayUrl != "";

    // api.cursor of the instance configuration, then the variables of the gateway app: CURSOR_API_KEY (the key), FACTORY_CURSOR_ENABLED,
    // FACTORY_CURSOR_GATEWAY_URL (default FACTORY_PUBLIC_URL), FACTORY_CURSOR_MAX_CONCURRENT, FACTORY_CURSOR_MAX_MINUTES.
    public static CursorCloudSettings Load(string scriptsDir, Func<string, string?> get)
    {
        var s = new CursorCloudSettings();
        var path = get("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? root = null; try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { }
        var c = root?["api"]?["cursor"];
        if (c is JsonObject)
        {
            static bool Num(string? t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
            double D(string k, double d) => Num(c[k]?.ToString(), out var v) ? v : d;
            string S(string k, string d) => c[k] is JsonValue sv && sv.TryGetValue<string>(out var str) ? str : d;
            s.Enabled = c["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
            s.MaxConcurrent = (int)D("maxConcurrent", 1); s.MaxExecutionsPerDay = (int)D("maxExecutionsPerDay", 10); s.MaxMinutes = Math.Max(0.1, D("maxMinutes", 60));
            s.MaxFailuresPerPhase = Math.Max(1, (int)D("maxFailuresPerPhase", 2)); s.MaxLaunchesPerPhase = (int)D("maxLaunchesPerPhase", 3); s.UnhealthyAfterFailures = Math.Max(1, (int)D("unhealthyAfterFailures", 2));
            s.AckSeconds = D("ackSeconds", 900); s.ListSeconds = Math.Max(1, D("listSeconds", 30)); s.IdleGraceSeconds = D("idleGraceSeconds", 60);
            s.UsageRetrySeconds = Math.Max(0.5, D("usageRetrySeconds", 10)); s.UsageWaitSeconds = D("usageWaitSeconds", 300); s.OwnerWatchSeconds = Math.Max(1, D("ownerWatchSeconds", 120)); s.QuotaRetryMinutes = D("quotaRetryMinutes", 30);
            s.ApiUrl = S("apiUrl", s.ApiUrl).TrimEnd('/'); s.ApiKey = S("apiKey", ""); s.GatewayUrl = S("gatewayUrl", "").TrimEnd('/'); s.Model = S("model", "auto").Trim(); if (s.Model == "") s.Model = "auto";
            var of = S("optimizeFor", "cost"); s.OptimizeFor = of is "cost" or "balanced" or "intelligence" ? of : "cost";
            s.Repository = S("repository", s.Repository); s.DefaultBranch = S("defaultBranch", s.DefaultBranch);
        }
        string? V(string n) => get(n) is { } v && v.Trim() != "" ? v.Trim() : null;
        s.Environment = (V("FACTORY_ENVIRONMENT") ?? "tdd").ToLowerInvariant();
        // api.cursor.environments: when it names any, the mode is on only in those, each with its gateway's public URL (one image runs everywhere)
        if (c?["environments"] is JsonObject { Count: > 0 } envs)
        {
            if (envs[s.Environment] is JsonObject me) { if (me["gatewayUrl"] is JsonValue gv && gv.TryGetValue<string>(out var g) && g != "") s.GatewayUrl = g.TrimEnd('/'); }
            else s.Enabled = false;
        }
        if (V("CURSOR_API_KEY") is { } key) s.ApiKey = key;
        if (bool.TryParse(V("FACTORY_CURSOR_ENABLED"), out var en)) s.Enabled = en;
        if (s.GatewayUrl == "") s.GatewayUrl = (V("FACTORY_CURSOR_GATEWAY_URL") ?? V("FACTORY_PUBLIC_URL") ?? "").TrimEnd('/');
        else if (V("FACTORY_CURSOR_GATEWAY_URL") is { } gu) s.GatewayUrl = gu.TrimEnd('/');
        if (int.TryParse(V("FACTORY_CURSOR_MAX_CONCURRENT"), NumberStyles.None, CultureInfo.InvariantCulture, out var mc)) s.MaxConcurrent = mc;
        if (double.TryParse(V("FACTORY_CURSOR_MAX_MINUTES"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && double.IsFinite(mm) && mm > 0) s.MaxMinutes = mm;
        return s;
    }

    public RuntimeLimits Limits(Func<string, int, string, long, string> mint) => new()
    {
        Kind = "cursor-cloud", Prefix = "cur", IsOn = () => On, Blocked = () => QuotaBlock?.Invoke(), MaxConcurrent = MaxConcurrent, MaxExecutionsPerDay = MaxExecutionsPerDay, AckSeconds = AckSeconds,
        MaxFailuresPerPhase = MaxFailuresPerPhase, MaxLaunchesPerPhase = MaxLaunchesPerPhase, UnhealthyAfterFailures = UnhealthyAfterFailures, ListSeconds = ListSeconds,
        // the r1 expires 15 minutes after the deadline (agents-roles-launch.md 4)
        TokenSeconds = (long)(MaxMinutes * 60) + 900, MintCredential = mint, RevokeOnEnd = true
    };
}

// The owner of an item (Jeffrey 2026-10-21, as for Copilot: CopilotOwner): ONE Cursor agent owns the item from its first launch until the change is
// in production. Every later phase, fix-forward and rework is a follow-up run of that agent (POST /v1/agents/{id}/runs, with a fresh r1 in its
// mcpServers), so the agent keeps its conversation and workspace. Between runs no run is active and the gateway watches CI, the merge and the
// release; the agent is deleted only when the ownership closes. The durable record is the item state's copilot_task with runtime cursor-cloud.
sealed class CursorCloudRuntime : IWorkerRuntime, IRuntimeDetail, IItemOwners
{
    sealed class Agent { public string Id = "", RunId = "", Worker = "", Phase = ""; public int Item; public bool FollowUp, Kept; public string Url = ""; public DateTime Started = DateTime.UtcNow; public string Status = "Running"; public DateTime? Ended, IdleSince; public bool ModelLogged, Cancelled; public DateTime? NextUsageRead; public double UsageBackoff; public int UsageReads; public JsonNode? Model, ReportedModel, Usage, UsageShape; public JsonArray? Fields, RunFields; public int UsageStatus; }

    readonly CursorCloudSettings cfg;
    readonly HttpClient http;
    readonly ICursorPromptBuilder prompt;   // item, phase, why (null: a new agent; else a follow-up run, with what changed) -> the text
    readonly Dictionary<int, CopilotOwner> owners = new();
    // the durable record of the owner (rule verb copilot-owner, runtime cursor-cloud): load (null: none), save
    public Func<int, CancellationToken, Task<CopilotOwner?>>? LoadOwner { get; set; }
    public Func<CopilotOwner, Task>? SaveOwner { get; set; }
    public Func<int, CancellationToken, Task<string>>? WhyResume { get; set; }
    // the gateway's completion of Development (rule verb copilot-settle) before a launch: "awaiting" means the item's pull request waits for its checks
    public Func<int, string, string?, CancellationToken, Task<string>>? Settle { get; set; }
    readonly Dictionary<string, Agent> agents = new(StringComparer.Ordinal);   // by worker name
    bool swept;   // the agents a former process left behind were looked for (first List)
    JsonObject? model;   // the resolved model selection (Model "auto" resolved against GET /v1/models)
    // agent names carry the environment, so a key shared by two gateways never lets one delete the other's agents
    string Prefix => $"fworker-{cfg.Environment}-";

    // owners and settle (C5c, gateway/Runtimes/Owner*.cs): the same owner record as Copilot's (runtime cursor-cloud) and the same settle
    public CursorCloudRuntime(CursorCloudSettings cfg, ICursorPromptBuilder prompt, HttpClient? http = null, CopilotOwnerVerbs? owners = null, CopilotSettleClient? settle = null)
    {
        this.cfg = cfg; this.prompt = prompt; this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (settle != null) Settle = (item, stage, head, _) => settle.Settle(item, stage, head);
        if (owners != null)
        {
            LoadOwner = (item, _) => owners.Load(item, cursor: true);
            SaveOwner = async o => await owners.Save(o, "cursor-cloud");
            WhyResume = (item, _) => owners.WhyResume(item);
        }
    }

    public string Kind => "cursor-cloud";

    // The team's prepaid balance / usage limit (Cursor answers a launch 400 usage_limit_exceeded, or a 402): no start until the retry time; the next
    // start after that is the probe. A refusal is no failure of the item and no launch; a launch that succeeds clears it.
    DateTime? quotaSince, quotaRetryAt; string quotaMessage = "";
    public const string QuotaOwnerAction = "buy Cursor team credits / enable usage (the team that owns the Cursor API key)";
    public static bool IsQuotaError(int status, string? text)
    {
        if (status == 402) return true;
        if (status is < 400 or >= 500 or 429 or 404 or 410 || string.IsNullOrEmpty(text)) return false;
        foreach (var k in new[] { "usage_limit_exceeded", "payment_required", "insufficient_credits", "insufficient_funds", "prepaid balance", "additional usage is off", "spend_limit" })
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    public string? QuotaBlocked() { lock (agents) return quotaRetryAt is { } r && DateTime.UtcNow < r ? "quota-exhausted" : null; }
    public JsonObject QuotaJson() { lock (agents) { var o = new JsonObject { ["exhausted"] = quotaSince != null, ["since"] = quotaSince?.ToString("o"), ["retryAt"] = quotaRetryAt?.ToString("o"), ["message"] = quotaMessage }; if (quotaSince != null) o["ownerAction"] = QuotaOwnerAction; return o; } }
    void QuotaHit(int item, string text)
    {
        string msg; try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? ""; } catch { msg = ""; }
        if (msg == "") msg = Excerpt(text);
        lock (agents)
        {
            if (quotaSince == null) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-quota-exhausted", ["item"] = item, ["message"] = msg.Length > 200 ? msg[..200] : msg }.ToJsonString());
            quotaSince ??= DateTime.UtcNow; quotaRetryAt = DateTime.UtcNow.AddMinutes(cfg.QuotaRetryMinutes); quotaMessage = msg.Length > 200 ? msg[..200] : msg;
        }
    }
    void QuotaClear()
    {
        lock (agents) { if (quotaSince == null) return; quotaSince = null; quotaRetryAt = null; quotaMessage = ""; }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-quota-back" }.ToJsonString());
    }

    // ---- the owners -----------------------------------------------------------------------------------------------------------------------------
    public bool Owns(int item) { lock (agents) return owners.TryGetValue(item, out var o) && !o.Done; }
    public CopilotOwner? OwnerOf(int item) { lock (agents) return owners.TryGetValue(item, out var o) && !o.Done ? o : null; }
    public List<CopilotOwner> Owners() { lock (agents) return owners.Values.Where(o => !o.Done).OrderBy(o => o.Item).ToList(); }
    public bool Running(int item) { lock (agents) return agents.Values.Any(a => a.Item == item && a.Ended == null); }
    public void Adopt(CopilotOwner o) { lock (agents) if (!owners.TryGetValue(o.Item, out var cur) || cur.Done) owners[o.Item] = o; }
    public void Waiting(int item, string column, string waitingOn) { lock (agents) if (owners.TryGetValue(item, out var o)) { if (column != "") o.Phase = column; o.WaitingOn = waitingOn; o.Updated = DateTime.UtcNow; } }
    public void SetPr(int item, int pr, string branch = "") { lock (agents) if (owners.TryGetValue(item, out var o)) { if (pr > 0) o.Pr = pr; if (System.Text.RegularExpressions.Regex.IsMatch(branch, @"^[A-Za-z0-9._/-]{1,200}\z")) o.Branch = branch; o.Updated = DateTime.UtcNow; } }
    public JsonArray OwnedJson() { lock (agents) return new JsonArray(owners.Values.Where(o => !o.Done).OrderBy(o => o.Item).Select(o => { var j = o.Json(); j["agent"] = o.TaskId; if (agents.Values.Any(a => a.Item == o.Item && a.Ended == null)) j["waitingOn"] = "run"; return (JsonNode)j; }).ToArray()); }
    // the ownership ends (in production, or the item closed). The agent is never deleted (owner 2026-10-21): its transcript stays readable at its url
    public Task CloseOwner(int item)
    {
        CopilotOwner? o; lock (agents) { owners.TryGetValue(item, out o); if (o != null) { o.Done = true; o.WaitingOn = "done"; owners.Remove(item); } }
        if (o != null) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-owner-closed", ["item"] = item, ["agent"] = o.TaskId, ["url"] = o.Url }.ToJsonString());
        return Task.CompletedTask;
    }
    // the owners of a plant snapshot: only plausible values; the item state stays the truth
    public void Restore(JsonArray a)
    {
        foreach (var n in a)
        {
            if (n is not JsonObject j || !int.TryParse(j["item"]?.ToString(), out var item) || item <= 0 || item > 9_999_999) continue;
            var id = j["task"]?.ToString() ?? ""; if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9-]{1,80}\z")) continue;
            var br = j["branch"]?.ToString() ?? ""; if (!System.Text.RegularExpressions.Regex.IsMatch(br, @"^[A-Za-z0-9._/-]{0,200}\z")) br = "";
            Adopt(new CopilotOwner { Item = item, TaskId = id, Branch = br, Pr = int.TryParse(j["pr"]?.ToString(), out var p) && p > 0 ? p : 0, Phase = j["phase"]?.ToString() ?? "", WaitingOn = "restored", Sessions = int.TryParse(j["sessions"]?.ToString(), out var ss) && ss > 0 ? ss : 1 });
        }
    }
    async Task<CopilotOwner?> Owner(int item, CancellationToken ct)
    {
        lock (agents) if (owners.TryGetValue(item, out var o) && !o.Done) return o;
        if (LoadOwner == null) return null;
        CopilotOwner? l = null; try { l = await LoadOwner(item, ct); } catch (Exception e) { Console.Error.WriteLine($"cursor: owner of #{item}: {e.GetType().Name}"); }
        if (l == null) return null;
        lock (agents) { if (owners.TryGetValue(item, out var o2) && !o2.Done) return o2; owners[item] = l; return l; }
    }
    async Task Save(CopilotOwner o) { if (SaveOwner != null) { try { await SaveOwner(o); } catch (Exception e) { Console.Error.WriteLine($"cursor: owner record of #{o.Item}: {e.GetType().Name}"); } } }
    bool OwnedAgent(Agent a) { lock (agents) return owners.TryGetValue(a.Item, out var o) && !o.Done && o.TaskId == a.Id; }
    // the agent's page as Cursor's agents API answers it (create or get: its url field), never built here; "" when Cursor gives none
    static string UrlOf(JsonNode? j) => (j?["agent"]?["url"] ?? j?["url"])?.ToString() is { } u && u.StartsWith("https://", StringComparison.Ordinal) && u.Length <= 300 && !u.Any(char.IsWhiteSpace) ? u : "";
    async Task<string> ReadUrl(string id, CancellationToken ct)
    {
        try { var (s, j) = await Send(HttpMethod.Get, $"/v1/agents/{Uri.EscapeDataString(id)}", null, ct); return s == 200 ? UrlOf(j) : ""; }
        catch { return ""; }
    }

    public JsonObject? Detail(string workerName)
    {
        lock (agents)
            return agents.TryGetValue(workerName, out var a) ? new JsonObject { ["agent"] = a.Id, ["run"] = a.RunId, ["url"] = a.Url, ["followUp"] = a.FollowUp, ["released"] = a.Kept, ["model"] = a.Model?.DeepClone(), ["reportedModel"] = a.ReportedModel?.DeepClone(), ["usage"] = a.Usage?.DeepClone(), ["usageStatus"] = a.UsageStatus, ["usageShape"] = a.UsageShape?.DeepClone(), ["agentFields"] = a.Fields?.DeepClone(), ["runFields"] = a.RunFields?.DeepClone() } : null;
    }
    double IdleGraceSeconds => cfg.IdleGraceSeconds;
    public int MaxConcurrent => cfg.MaxConcurrent;

    async Task<(int Status, JsonNode? Body)> Send(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        var (s, j, _) = await SendText(m, path, body, ct);
        return (s, j);
    }

    async Task<(int Status, JsonNode? Body, string Text)> SendText(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, cfg.ApiUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? j = null; try { if (text.Length > 0) j = JsonNode.Parse(text); } catch { }
        return ((int)resp.StatusCode, j, text);
    }

    // the answer of a failed call, for the log and /status: at most 1 KB on one line, the key masked should Cursor ever echo it
    string Excerpt(string text)
    {
        var t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cfg.ApiKey.Length > 0) t = t.Replace(cfg.ApiKey, "***");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"Bearer\s+\S+", "Bearer ***");   // an r1 Cursor may echo from mcpServers
        return t.Length > 1024 ? t[..1024] + "..." : t;
    }

    // The branch an agent starts on: the default branch for Technical Design and a first Development pass (the agent creates
    // factory-worker/N-development), the item's branch for Functional Testing.
    string StartingRef(int item, string phase) => phase.Equals("Functional Testing", StringComparison.OrdinalIgnoreCase) ? $"factory-worker/{item}-development" : cfg.DefaultBranch;

    JsonArray Mcp(string nonce) => new(new JsonObject
    {
        ["name"] = "fworker", ["type"] = "http", ["url"] = cfg.GatewayUrl + "/mcp",
        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + nonce }
    });

    // the agent's name carries the environment and the item ("fworker-tdd-i<item>-<worker>"): a restarted gateway finds the owner agents it keeps
    public JsonObject LaunchBody(ProvisionRequest r) => new()
    {
        ["name"] = Prefix + "i" + r.Item + "-" + r.WorkerName,
        ["prompt"] = new JsonObject { ["text"] = prompt.Build(r.Item, r.Phase, null) },
        ["repos"] = new JsonArray(new JsonObject { ["url"] = cfg.Repository, ["startingRef"] = StartingRef(r.Item, r.Phase) }),
        ["autoCreatePR"] = false,
        ["mcpServers"] = new JsonArray(new JsonObject
        {
            ["name"] = "fworker", ["type"] = "http", ["url"] = cfg.GatewayUrl + "/mcp",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + r.Nonce }
        })
    };

    public async Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct)
    {
        // Development of an item whose pull request already carries the change and no rework is asked for: no run, the gateway waits for its checks
        if (r.Phase.Equals("Development", StringComparison.OrdinalIgnoreCase) && Settle != null)
        {
            string act; try { act = await Settle(r.Item, "launch", null, ct); } catch (Exception e) { act = "launch"; Console.Error.WriteLine($"cursor: settle of #{r.Item} before launch: {e.GetType().Name}"); }
            if (act == "awaiting") return new(false, "", "settled");
        }
        var own = await Owner(r.Item, ct);
        if (own != null)
        {
            var fu = await FollowUp(own, r, ct);
            if (fu != null) return fu;
            // the owner agent is gone (deleted or archived at Cursor): a new agent becomes the owner, the old id is kept as previous
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-owner-gone", ["item"] = r.Item, ["agent"] = own.TaskId }.ToJsonString());
        }
        var body = LaunchBody(r);
        JsonObject? sel; try { sel = await Model(ct); } catch (Exception e) { return new(false, "", "models-" + e.GetType().Name); }
        if (sel == null) return new(false, "", "model-unavailable");
        body["model"] = sel;
        int s; JsonNode? j; string text;
        try { (s, j, text) = await SendText(HttpMethod.Post, "/v1/agents", body, ct); }
        catch (Exception e) { return new(false, "", "launch-" + e.GetType().Name); }
        var id = j?["agent"]?["id"]?.ToString() ?? j?["id"]?.ToString() ?? "";
        // a launch that failed may still have created the agent (a timeout, an odd answer): it is looked up by its name and deleted
        if (IsQuotaError(s, text)) { QuotaHit(r.Item, text); return new(false, "", "quota"); }
        if (s is not (200 or 201) || id == "") { return new(false, "", s is 200 or 201 ? "launch-no-id" : $"launch-{s}: {Excerpt(text)}"); }
        QuotaClear();
        var url = UrlOf(j); if (url == "") url = await ReadUrl(id, ct);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-agent-url", ["item"] = r.Item, ["agent"] = id, ["url"] = url }.ToJsonString());
        CopilotOwner no;
        lock (agents)
        {
            agents[r.WorkerName] = new Agent { Url = url, Model = sel.DeepClone(), Id = id, RunId = j?["run"]?["id"]?.ToString() ?? j?["agent"]?["latestRunId"]?.ToString() ?? "", Worker = r.WorkerName, Item = r.Item, Phase = r.Phase };
            no = new CopilotOwner { Item = r.Item, TaskId = id, Url = url, Branch = own?.Branch ?? "", Pr = own?.Pr ?? 0, Phase = r.Phase, Sessions = 1, LastResume = DateTime.UtcNow, WaitingOn = "run" };
            if (own != null) { no.Previous.AddRange(own.Previous); no.Previous.Add(own.TaskId); no.Since = own.Since; }
            owners[r.Item] = no;
        }
        _ = Task.Run(() => Save(no));
        return new(true, id, "", url);
    }

    // A follow-up run of the owner agent: the next phase's prompt and a fresh r1 in its MCP configuration (the previous run's was revoked at its
    // end). Null: the agent cannot take a run any more (gone or archived); a new agent is launched instead.
    async Task<ProvisionResult?> FollowUp(CopilotOwner own, ProvisionRequest r, CancellationToken ct)
    {
        var why = ""; if (WhyResume != null) { try { why = await WhyResume(r.Item, ct); } catch (Exception e) { Console.Error.WriteLine($"cursor: why of #{r.Item}: {e.GetType().Name}"); } }
        var body = new JsonObject { ["prompt"] = new JsonObject { ["text"] = prompt.Build(r.Item, r.Phase, why) }, ["mcpServers"] = Mcp(r.Nonce) };
        int s; JsonNode? j; string text;
        try { (s, j, text) = await SendText(HttpMethod.Post, $"/v1/agents/{Uri.EscapeDataString(own.TaskId)}/runs", body, ct); }
        catch (Exception e) { return new(false, "", "followup-" + e.GetType().Name); }
        if (IsQuotaError(s, text)) { QuotaHit(r.Item, text); return new(false, "", "quota"); }
        if (s is 404 or 410) return null;
        if (s is 400 or 409 && text.Contains("archiv", StringComparison.OrdinalIgnoreCase)) return null;
        var run = j?["run"]?["id"]?.ToString() ?? j?["id"]?.ToString() ?? "";
        if (s is not (200 or 201 or 202) || run == "") return new(false, "", s is 200 or 201 or 202 ? "followup-no-id" : $"followup-{s}: {Excerpt(text)}");
        QuotaClear();
        var id = $"{own.TaskId}#{own.Sessions}";
        lock (agents)
        {
            agents[r.WorkerName] = new Agent { Id = own.TaskId, RunId = run, Worker = r.WorkerName, Item = r.Item, Phase = r.Phase, FollowUp = true, Model = model?.DeepClone() };
            own.Sessions++; own.Phase = r.Phase; own.LastResume = DateTime.UtcNow; own.WaitingOn = "run"; own.Updated = DateTime.UtcNow;
        }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-followup", ["item"] = r.Item, ["phase"] = r.Phase, ["agent"] = own.TaskId, ["run"] = run, ["session"] = own.Sessions - 1 }.ToJsonString());
        _ = Task.Run(() => Save(own));
        if (own.Url == "") { var u = await ReadUrl(own.TaskId, ct); if (u != "") lock (agents) own.Url = u; }
        lock (agents) agents[r.WorkerName].Url = own.Url;
        return new(true, id, "", own.Url);
    }

    // The end of a run: its usage is read once and logged, then the run is cancelled when it did not finish by itself. The agent is NEVER deleted
    // (owner 2026-10-21): its transcript stays readable at its url, and an owner agent takes the item's next run as a follow-up. The run's r1 is
    // revoked by the manager (RevokeOnEnd), so the MCP configuration Cursor keeps calls no tool any more. Idempotent.
    public async Task Deprovision(string workerName, string reason, CancellationToken ct)
    {
        Agent? a; lock (agents) agents.TryGetValue(workerName, out a);
        if (a == null) return;
        bool finished;
        lock (agents) { if (a.Kept) return; a.Kept = true; a.Ended ??= DateTime.UtcNow; finished = a.Status == "Succeeded"; if (a.Status == "Running") a.Status = "Stopped"; }
        await LogUsage(a, ct, force: true);
        if (!finished) await Cancel(a, ct);
    }

    // The model of a launch (owner decision 2026-10-17: Auto with the cost preference). Model "auto" is resolved against GET /v1/models once per
    // process: the Cursor Router "auto-smart" with params [{id: optimize_for, value: cost}] where the catalog offers it (Cursor SDK docs,
    // cursor.com/docs/sdk/python: "Always pass optimize_for"), else the plain "auto" (which carries no cost preference). Never the account's
    // default: with neither in the catalog the launch is refused (model-unavailable) rather than run on a model nobody chose.
    async Task<JsonObject?> Model(CancellationToken ct)
    {
        if (model != null) return (JsonObject)model.DeepClone();
        if (!cfg.Model.Equals("auto", StringComparison.OrdinalIgnoreCase)) return (JsonObject)(model = new JsonObject { ["id"] = cfg.Model }).DeepClone();
        var (s, j) = await Send(HttpMethod.Get, "/v1/models", null, ct);
        var arr = (j?["items"] ?? j?["models"] ?? j?["data"] ?? j) as JsonArray ?? new JsonArray();
        var ids = arr.Select(x => x is JsonValue ? x.ToString() : x?["id"]?.ToString() ?? "").Where(x => x != "").ToList();
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-models", ["status"] = s, ["ids"] = new JsonArray(ids.Take(80).Select(x => (JsonNode)x).ToArray()) }.ToJsonString());
        if (s != 200) return null;
        if (ids.Contains("auto-smart"))
            model = new JsonObject { ["id"] = "auto-smart", ["params"] = new JsonArray(new JsonObject { ["id"] = "optimize_for", ["value"] = cfg.OptimizeFor }) };
        else if (ids.Contains("auto")) model = new JsonObject { ["id"] = "auto" };
        else return null;
        return (JsonObject)model.DeepClone();
    }

    // the agents of this environment at Cursor (GET /v1/agents, every page), as (id, name); throws when a page cannot be read
    async Task<List<(string Id, string Name, DateTime? Created)>> Remote(CancellationToken ct)
    {
        var all = new List<(string, string, DateTime?)>(); string? next = null;
        for (var page = 0; page < 20; page++)
        {
            var (s, j) = await Send(HttpMethod.Get, "/v1/agents?limit=100" + (next != null ? "&cursor=" + Uri.EscapeDataString(next) : ""), null, ct);
            if (s != 200) throw new HttpRequestException($"list of agents answered {s}");
            var arr = (j?["items"] ?? j?["agents"] ?? j?["data"]) as JsonArray ?? new JsonArray();
            all.AddRange(arr.Select(x => (x?["id"]?.ToString() ?? "", x?["name"]?.ToString() ?? "",
                DateTime.TryParse(x?["createdAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var cd) ? cd : (DateTime?)null)).Where(x => x.Item1 != ""));
            next = j?["nextCursor"]?.ToString(); if (string.IsNullOrEmpty(next)) break;
        }
        return all;
    }

    // an agent a former process left that is still an item's owner (its name carries the item; the item state records its id): adopted, not deleted
    async Task<bool> KeepOwnerAgent(string id, string name, CancellationToken ct)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name[Prefix.Length..], @"^i(\d{1,7})-");
        if (!m.Success) return false;
        var o = await Owner(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), ct);
        return o != null && o.TaskId == id;
    }

    async Task Cancel(Agent a, CancellationToken ct)
    {
        string id, run; bool done; lock (agents) { id = a.Id; run = a.RunId; done = a.Cancelled; if (run != "") a.Cancelled = true; }
        if (run != "" && !done) { try { await Send(HttpMethod.Post, $"/v1/agents/{Uri.EscapeDataString(id)}/runs/{Uri.EscapeDataString(run)}/cancel", null, ct); } catch { } }
    }

    static bool HasUsage(Agent a) => a.Usage?["totalTokens"] is JsonValue v && v.TryGetValue<double>(out var t) && t > 0;

    // the tokens an agent used (GET /v1/agents/{id}/usage), logged once it reads non-zero (or for the last read), for the cycle metrics
    async Task LogUsage(Agent a, CancellationToken ct, bool force = false)
    {
        try
        {
            var (s, j) = await Send(HttpMethod.Get, $"/v1/agents/{Uri.EscapeDataString(a.Id)}/usage", null, ct);
            lock (agents)
            {
                a.UsageReads++; a.UsageStatus = s;
                var o = j as JsonObject; if (s == 200) a.Usage = (o?["totalUsage"] ?? o?["usage"])?.DeepClone();
                // the shape of the answer (keys, and the token counts of each run), never anything else: totalUsage read zero on live runs
                a.UsageShape = new JsonObject { ["keys"] = new JsonArray((o?.Select(kv => (JsonNode)kv.Key).ToArray()) ?? Array.Empty<JsonNode>()),
                    ["runs"] = new JsonArray(((o?["runs"] as JsonArray) ?? new JsonArray()).Take(10).Select(x => (JsonNode)new JsonObject { ["keys"] = new JsonArray(((x as JsonObject)?.Select(kv => (JsonNode)kv.Key).ToArray()) ?? Array.Empty<JsonNode>()),
                        ["usage"] = ((x as JsonObject)?["usage"] ?? (x as JsonObject)?["totalUsage"])?.DeepClone() }).ToArray()) };
            }
            if (s == 200 && (force || HasUsage(a) || (DateTime.UtcNow - (a.Ended ?? DateTime.UtcNow)).TotalSeconds >= cfg.UsageWaitSeconds)) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-usage", ["worker"] = a.Worker, ["item"] = a.Item, ["phase"] = a.Phase, ["agent"] = a.Id, ["run"] = a.RunId, ["followUp"] = a.FollowUp,
                ["minutes"] = Math.Round(((a.Ended ?? DateTime.UtcNow) - a.Started).TotalMinutes, 1), ["reads"] = a.UsageReads, ["usage"] = j?["totalUsage"]?.DeepClone() }.ToJsonString());
        }
        catch { }
    }


    // The agents this process started, with Container Apps job states: ACTIVE is Running; IDLE (its turn ended), ARCHIVED or a deleted agent
    // ended; a run in ERROR or EXPIRED failed. An agent past its deadline is cancelled and deleted here and reported Failed.
    public async Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct)
    {
        // the owner agents a former process left (fworker-<environment>-i<item>-* names, recorded in the item state) are adopted; no agent is deleted
        if (!swept)
        {
            try
            {
                HashSet<string> mine; lock (agents) mine = agents.Values.Select(x => x.Id).ToHashSet();
                // only agents of the last 14 days: agents are never deleted, and each check is a rule-script run
                var since = DateTime.UtcNow.AddDays(-14);
                foreach (var (id, n, created) in await Remote(ct))
                    if (n.StartsWith(Prefix) && !mine.Contains(id) && (created == null || created > since) && await KeepOwnerAgent(id, n, ct)) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-owner-adopted", ["agent"] = id }.ToJsonString());
                swept = true;
            }
            catch (Exception e) { Console.Error.WriteLine($"cursor: owner sweep: {e.GetType().Name}"); }
        }
        List<Agent> all; lock (agents) { foreach (var k in agents.Where(kv => kv.Value.Kept && kv.Value.Ended < DateTime.UtcNow.AddHours(-2)).Select(kv => kv.Key).ToList()) agents.Remove(k); all = agents.Values.ToList(); }
        var list = new List<RuntimeInstance>();
        foreach (var a in all)
        {
            try
            {
                if (a.Ended == null && (DateTime.UtcNow - a.Started).TotalMinutes > cfg.MaxMinutes)
                {   // the deadline: a failure of the item's phase; the delete is retried below until it is done
                    lock (agents) { a.Status = "Failed"; a.Ended = DateTime.UtcNow; }
                }
                else if (a.Ended == null)
                {
                    var (s, j) = await Send(HttpMethod.Get, $"/v1/agents/{Uri.EscapeDataString(a.Id)}", null, ct);
                    if (s is not (200 or 404)) throw new HttpRequestException($"read of agent answered {s}");
                    var st = s == 404 ? "ARCHIVED" : j?["agent"]?["status"]?.ToString() ?? j?["status"]?.ToString() ?? "";
                    // the model Cursor reports for the agent, once: the check that the launch ran on the model asked for
                    // the field names of the answers (never their values), once: what Cursor's agent and run objects carry
                    lock (agents) a.Fields ??= new JsonArray(((j?["agent"] ?? j) as JsonObject)?.Select(kv => (JsonNode)kv.Key).ToArray() ?? Array.Empty<JsonNode>());
                    if (!a.ModelLogged && (j?["agent"]?["model"] ?? j?["model"] ?? j?["agent"]?["modelId"]) is { } rm) { a.ModelLogged = true; lock (agents) a.ReportedModel = rm.DeepClone(); Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-agent-model", ["agent"] = a.Id, ["model"] = rm.DeepClone() }.ToJsonString()); }
                    // this execution's run: a follow-up knows its own; a first launch takes the agent's latest
                    var runId = a.FollowUp && a.RunId != "" ? a.RunId : j?["agent"]?["latestRunId"]?.ToString() ?? j?["latestRunId"]?.ToString() ?? "";
                    var runStatus = "";
                    if (runId != "") { try { var (rs, rj) = await Send(HttpMethod.Get, $"/v1/agents/{Uri.EscapeDataString(a.Id)}/runs/{Uri.EscapeDataString(runId)}", null, ct); if (rs == 200) { runStatus = rj?["run"]?["status"]?.ToString() ?? rj?["status"]?.ToString() ?? ""; var ro = (rj?["run"] ?? rj) as JsonObject; lock (agents) { a.RunFields ??= new JsonArray(ro?.Select(kv => (JsonNode)kv.Key).ToArray() ?? Array.Empty<JsonNode>()); if (a.ReportedModel == null && (ro?["model"] ?? ro?["modelId"]) is { } rmm) a.ReportedModel = rmm.DeepClone(); } } } catch { } }
                    lock (agents)
                    {
                        if (runId != "" && !a.FollowUp) a.RunId = runId;
                        // IDLE (the turn ended) counts as the end once it lasted a minute: a short pause between turns is no end
                        if (st == "IDLE") a.IdleSince ??= DateTime.UtcNow; else a.IdleSince = null;
                        var idleOver = a.IdleSince is { } isn && (DateTime.UtcNow - isn).TotalSeconds >= IdleGraceSeconds;
                        // a follow-up's own run decides (the agent may read IDLE for a moment before the run starts)
                        if (a.FollowUp && runStatus != "") a.Status = runStatus switch { "ERROR" or "EXPIRED" => "Failed", "FINISHED" => "Succeeded", "CANCELLED" => "Stopped", _ => st == "ARCHIVED" ? "Stopped" : "Running" };
                        else a.Status = runStatus is "ERROR" or "EXPIRED" ? "Failed" : st switch { "ACTIVE" => "Running", "IDLE" => idleOver ? "Succeeded" : "Running", "ARCHIVED" => "Stopped", _ => "Running" };
                        if (a.Status != "Running") a.Ended ??= DateTime.UtcNow;
                    }
                }
                // an agent that ended (by itself, at the deadline, or a delete that failed before) is deleted, again at every reading until it is gone
                if (a.Ended != null && !a.Kept) { try { await Deprovision(a.Worker, "ended", ct); } catch (Exception e) { Console.Error.WriteLine($"cursor: end of {a.Worker}: {e.GetType().Name}"); } }
            }
            catch (Exception e) { Console.Error.WriteLine($"cursor: read of {a.Worker}: {e.GetType().Name}"); }
            list.Add(new RuntimeInstance(a.Id, a.Worker, a.Item, a.Phase, a.Status, a.Started, a.Ended));
        }
        return list;
    }
}
