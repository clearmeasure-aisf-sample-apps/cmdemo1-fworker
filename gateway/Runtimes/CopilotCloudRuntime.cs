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
    public double MaxCreditsPerTask = 0;
    // a session refused for the seat's quota stops all starts for quotaRetryMinutes; the next start after that is the probe (it fails in seconds,
    // spending nothing, while the quota is still exhausted)
    public double QuotaRetryMinutes = 30;
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
            s.MaxCreditsPerTask = D("maxCreditsPerTask", s.MaxCreditsPerTask); s.QuotaRetryMinutes = D("quotaRetryMinutes", 30);
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

    public Func<string?>? QuotaBlock;
    public RuntimeLimits Limits(Func<string, int, string, long, string> mint) => new()
    {
        Kind = "copilot-cloud", Prefix = "cop", IsOn = () => On, Blocked = () => QuotaBlock?.Invoke(), MaxConcurrent = MaxConcurrent, MaxExecutionsPerDay = MaxExecutionsPerDay, AckSeconds = AckSeconds,
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

// The owner of an item (docs/design/copilot-cloud-agent.md, "One task, resumed", Jeffrey 2026-10-21): ONE agent task owns the item from its first
// acceptance until its pull request is merged and master is green. Its first session opens the item's draft pull request; every later phase,
// fix-forward and rework is a follow-up session of the same task, started by a @copilot comment on that pull request (the agent tasks API has no
// follow-up endpoint; GitHub starts a new session on the same pull request and branch, with memory of the earlier ones). Between sessions no
// session runs and no credit is spent: the gateway holds the item for the task and watches CI, the merge and master. The durable record is
// the item state's copilot_task (rule verb copilot-owner); this is the process's copy.
sealed class CopilotOwner
{
    public int Item, Pr, Sessions;
    public string TaskId = "", Url = "", Branch = "", Phase = "", WaitingOn = "", MasterFixFor = "";
    public DateTime Since = DateTime.UtcNow, Updated = DateTime.UtcNow;
    public DateTime? LastResume;
    public bool Done;
    public List<string> Previous = new();
    public JsonObject Json() => new()
    {
        ["item"] = Item, ["task"] = TaskId, ["url"] = Url, ["branch"] = Branch, ["pr"] = Pr, ["phase"] = Phase, ["waitingOn"] = WaitingOn, ["sessions"] = Sessions,
        ["since"] = Since.ToString("o"), ["lastResume"] = LastResume?.ToString("o"), ["previousTasks"] = new JsonArray(Previous.Select(x => (JsonNode)x).ToArray())
    };
}

sealed class CopilotCloudRuntime : IWorkerRuntime, IRuntimeDetail, IRuntimeWork
{
    // one execution: a session of a task (Session 0 is the task's first; Id is the task id for it and "<task>#<n>" for a follow-up)
    sealed class Task_ { public string Id = "", TaskId = "", Url = "", Worker = "", Phase = "", State = "queued", Status = "Running", Via = "task", BaseSha = ""; public int Item, Session, Pr; public bool Guarded; public DateTime Started = DateTime.UtcNow; public DateTime? Ended; public JsonNode? Usage, Artifacts; public JsonArray? Fields; }

    readonly CopilotCloudSettings cfg;
    readonly HttpClient http;
    readonly CopilotCodes codes;
    // item, phase, one-time code, the item's open pull request, why (null: a new task; else a follow-up session, with what changed) -> the prompt
    readonly Func<int, string, string, ItemPr?, string?, string> prompt;
    readonly Func<int, CancellationToken, Task<ItemPr?>>? itemPr;   // the item's open pull request (number, head branch), null when it has none
    readonly Dictionary<string, Task_> tasks = new(StringComparer.Ordinal);   // by worker name
    readonly Dictionary<int, CopilotOwner> owners = new();
    // The gateway owns the completion of Development (rule verb copilot-settle): (item, stage launch | end, the task's branch) -> its action:
    // launch (start the task), awaiting (the item's pull request waits for its checks in the gateway: no task), none
    public Func<int, string, string?, CancellationToken, Task<string>>? Settle { get; set; }
    // the credit guard (api.copilot.maxCreditsPerTask): parks the item (item, phase, the credits the task reported)
    public Func<int, string, double, Task>? OverBudget { get; set; }
    // the durable record of the owner (rule verb copilot-owner): load (null: none), save (set)
    public Func<int, CancellationToken, Task<CopilotOwner?>>? LoadOwner { get; set; }
    public Func<CopilotOwner, Task>? SaveOwner { get; set; }
    // what changed since the last session (the rework or the red checks the item state records), for a follow-up's prompt
    public Func<int, CancellationToken, Task<string>>? WhyResume { get; set; }

    // The seat's quota (Copilot answers a session "You have exceeded your monthly quota"): no start or resume until the retry time; owned items
    // stay owned and wait. Cleared by the first session that runs.
    DateTime? quotaSince, quotaRetryAt; string quotaMessage = "";
    public const string QuotaOwnerAction = "raise the Copilot AI-credit budget / quota for cm-ai-ops-bot (the seat that runs the Copilot cloud agent)";
    public static bool IsQuotaError(string? m) => m != null && (m.Contains("exceeded your monthly quota", StringComparison.OrdinalIgnoreCase) || m.Contains("insufficient premium", StringComparison.OrdinalIgnoreCase) || m.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase));
    public string? QuotaBlocked() { lock (tasks) return quotaRetryAt is { } r && DateTime.UtcNow < r ? "quota-exhausted" : null; }
    public JsonObject QuotaJson() { lock (tasks) { var o = new JsonObject { ["exhausted"] = quotaSince != null, ["since"] = quotaSince?.ToString("o"), ["retryAt"] = quotaRetryAt?.ToString("o"), ["message"] = quotaMessage }; if (quotaSince != null) o["ownerAction"] = QuotaOwnerAction; return o; } }

    public CopilotCloudRuntime(CopilotCloudSettings cfg, CopilotCodes codes, Func<int, string, string, ItemPr?, string?, string> prompt, HttpClient? http = null, Func<int, CancellationToken, Task<ItemPr?>>? itemPr = null)
    { this.cfg = cfg; this.codes = codes; this.prompt = prompt; this.itemPr = itemPr; this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }; }

    public string Kind => "copilot-cloud";
    public int MaxConcurrent => cfg.MaxConcurrent;
    string TasksPath => $"/agents/repos/{cfg.Repository}/tasks";

    public JsonObject? Detail(string workerName)
    {
        lock (tasks)
            return tasks.TryGetValue(workerName, out var t) ? new JsonObject { ["task"] = t.TaskId, ["session"] = t.Session, ["via"] = t.Via, ["state"] = t.State, ["usage"] = t.Usage?.DeepClone(), ["artifacts"] = t.Artifacts?.DeepClone(), ["taskFields"] = t.Fields?.DeepClone(),
                ["url"] = t.Url != "" ? t.Url : owners.TryGetValue(t.Item, out var o) && o.TaskId == t.TaskId ? o.Url : null, ["minutes"] = Math.Round(((t.Ended ?? DateTime.UtcNow) - t.Started).TotalMinutes, 1) } : null;
    }

    // ---- the owners -----------------------------------------------------------------------------------------------------------------------------
    public bool Owns(int item) { lock (tasks) return owners.TryGetValue(item, out var o) && !o.Done; }
    public CopilotOwner? OwnerOf(int item) { lock (tasks) return owners.TryGetValue(item, out var o) && !o.Done ? o : null; }
    public List<CopilotOwner> Owners() { lock (tasks) return owners.Values.Where(o => !o.Done).OrderBy(o => o.Item).ToList(); }
    public bool Running(int item) { lock (tasks) return tasks.Values.Any(t => t.Item == item && t.Ended == null); }
    // the owners of a plant snapshot (written by a stopping gateway): only plausible values; the item state stays the truth
    public void Restore(JsonArray a)
    {
        foreach (var n in a)
        {
            if (n is not JsonObject j || !int.TryParse(j["item"]?.ToString(), out var item) || item <= 0 || item > 9_999_999) continue;
            var id = j["task"]?.ToString() ?? ""; if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9-]{1,80}\z")) continue;
            var br = j["branch"]?.ToString() ?? ""; if (!System.Text.RegularExpressions.Regex.IsMatch(br, @"^(copilot/[A-Za-z0-9._/-]{1,150})?\z")) br = "";
            var url = j["url"]?.ToString() ?? ""; if (!url.StartsWith("https://github.com/", StringComparison.Ordinal) || url.Length > 300) url = "";
            var o = new CopilotOwner { Item = item, TaskId = id, Branch = br, Url = url, Pr = int.TryParse(j["pr"]?.ToString(), out var p) && p > 0 ? p : 0, Phase = j["phase"]?.ToString() ?? "", WaitingOn = "restored",
                Sessions = int.TryParse(j["sessions"]?.ToString(), out var ss) && ss > 0 ? ss : 1 };
            if (DateTime.TryParse(j["since"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since)) o.Since = since;
            Adopt(o);
        }
    }
    public void Adopt(CopilotOwner o) { lock (tasks) if (!owners.TryGetValue(o.Item, out var cur) || cur.Done) owners[o.Item] = o; }
    public void Waiting(int item, string column, string waitingOn) { lock (tasks) if (owners.TryGetValue(item, out var o)) { if (column != "") o.Phase = column; o.WaitingOn = waitingOn; o.Updated = DateTime.UtcNow; } }
    // the item's pull request as the rule verb resolved it (copilot-owner status: the open one of the owner task's branch)
    public void SetPr(int item, int pr) { lock (tasks) if (pr > 0 && owners.TryGetValue(item, out var o) && o.Pr != pr) { o.Pr = pr; o.Updated = DateTime.UtcNow; } }
    public void Close(int item) { lock (tasks) if (owners.TryGetValue(item, out var o)) { o.Done = true; o.WaitingOn = "done"; owners.Remove(item); } }
    public JsonArray OwnedJson() { lock (tasks) return new JsonArray(owners.Values.Where(o => !o.Done).OrderBy(o => o.Item).Select(o => { var j = o.Json(); if (tasks.Values.Any(t => t.Item == o.Item && t.Ended == null)) j["waitingOn"] = "session"; return (JsonNode)j; }).ToArray()); }

    async Task<CopilotOwner?> Owner(int item, CancellationToken ct)
    {
        lock (tasks) if (owners.TryGetValue(item, out var o) && !o.Done) return o;
        if (LoadOwner == null) return null;
        CopilotOwner? l = null; try { l = await LoadOwner(item, ct); } catch (Exception e) { Console.Error.WriteLine($"copilot: owner of #{item}: {e.GetType().Name}"); }
        if (l == null) return null;
        lock (tasks) { if (owners.TryGetValue(item, out var o2) && !o2.Done) return o2; owners[item] = l; return l; }
    }

    async Task Save(CopilotOwner o) { if (SaveOwner != null) { try { await SaveOwner(o); } catch (Exception e) { Console.Error.WriteLine($"copilot: owner record of #{o.Item}: {e.GetType().Name}"); } } }

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

    // A NEW task of an item that already has its open pull request (an item an earlier gateway ran one task per phase for) starts from that pull
    // request's head branch: Copilot makes a new copilot/* branch from it and opens its draft against it; pr_open then makes that draft the
    // item's pull request and closes the superseded one (Verbs.Pr.ps1). From then on that task is the owner and is resumed on its pull request.
    // Without one: the default branch (Functional Testing: the branch Development reported).
    string BaseRef(int item, string phase, ItemPr? pr) => pr is { Branch.Length: > 0 } ? pr.Branch
        : phase.Equals("Functional Testing", StringComparison.OrdinalIgnoreCase) ? HeadOf(item) ?? cfg.DefaultBranch : cfg.DefaultBranch;
    static bool IsDevelopment(string phase) => phase.Equals("Development", StringComparison.OrdinalIgnoreCase);
    static bool IsTechnicalDesign(string phase) => phase.Equals("Technical Design", StringComparison.OrdinalIgnoreCase);

    // The owner task's first session opens the item's draft pull request right away (create_pull_request, every phase): every later session is a
    // follow-up on that pull request, so it keeps one task, one branch and one pull request (a Functional Testing task of an item that already has
    // its pull request asks for none: it changes nothing).
    public JsonObject TaskBody(ProvisionRequest r, string code, ItemPr? pr = null)
    {
        var o = new JsonObject { ["prompt"] = prompt(r.Item, r.Phase, code, pr, null), ["base_ref"] = BaseRef(r.Item, r.Phase, pr), ["create_pull_request"] = !(pr != null && !IsDevelopment(r.Phase)) };
        if (cfg.Model != "") o["model"] = cfg.Model;
        return o;
    }

    public async Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct)
    {
        // the r1 (r.Nonce) stays in the gateway: the task gets a one-time code for it, valid for the acknowledgement window
        // a fresh code for every session (a follow-up included); it outlives the registration window by 30 min: the agent environment's setup
        // (SDK, Playwright browsers) runs before the agent connects
        var code = codes.Mint(r.WorkerName, r.Nonce, TimeSpan.FromSeconds(Math.Max(cfg.AckSeconds, 60) + 1800));
        // Development of an item that already has its pull request with changes and no rework asked for: no new session, the gateway waits for
        // its checks (#395 was relaunched in mode first after its green pull request and opened a duplicate draft for 25B ai_credits)
        if (IsDevelopment(r.Phase) && Settle != null)
        {
            string act; try { act = await Settle(r.Item, "launch", null, ct); } catch (Exception e) { act = "launch"; Console.Error.WriteLine($"copilot: settle of #{r.Item} before launch: {e.GetType().Name}"); }
            if (act == "awaiting") { codes.Forget(r.WorkerName); return new(false, "", "settled"); }
        }
        // the item's pull request (none in Technical Design) and its owner record are read side by side: each is a rule-script run, and the
        // registration window counts from the offer
        async Task<ItemPr?> ReadPr()
        {
            if (itemPr == null || IsTechnicalDesign(r.Phase)) return null;
            try { return await itemPr(r.Item, ct); } catch (Exception e) { Console.Error.WriteLine($"copilot: pull request of #{r.Item}: {e.GetType().Name}"); return null; }
        }
        var prTask = ReadPr(); var ownTask = Owner(r.Item, ct);
        var pr = await prTask; var own = await ownTask;
        // an item an earlier gateway ran one task per phase for, with its pull request open: the task whose branch that pull request is on
        // becomes the owner and is resumed there (no new task, no stacked draft against the item's branch)
        if (own == null && pr is { Branch.Length: > 0 } && await TaskOfBranch(pr.Branch, ct) is { } adopted)
        {
            own = new CopilotOwner { Item = r.Item, TaskId = adopted.Id, Url = adopted.Url, Branch = pr.Branch, Pr = pr.Number, Phase = r.Phase, Sessions = adopted.Sessions, WaitingOn = "adopted" };
            lock (tasks) { if (owners.TryGetValue(r.Item, out var o0) && !o0.Done) own = o0; else owners[r.Item] = own; }
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "owner-adopted", ["item"] = r.Item, ["task"] = own.TaskId, ["pr"] = pr.Number }.ToJsonString());
        }
        if (own != null)
        {
            // the owner is resumed on its pull request: the item's open one, else the one on the task's branch
            var target = pr?.Number ?? 0;
            if (target == 0 && own.Branch != "") target = await PrOfBranch(own.Branch, ct);
            if (target == 0 && own.Pr > 0) target = own.Pr;
            if (target > 0)
            {
                var why = ""; if (WhyResume != null) { try { why = await WhyResume(r.Item, ct); } catch (Exception e) { Console.Error.WriteLine($"copilot: why of #{r.Item}: {e.GetType().Name}"); } }
                var res = await Resume(own, r.WorkerName, r.Item, r.Phase, target, prompt(r.Item, r.Phase, code, pr ?? new ItemPr(target, own.Branch), why), ct);
                if (!res.Ok) codes.Forget(r.WorkerName);
                return res;
            }
            // no pull request to resume on (the first session never opened its draft): a new task, on the owner's branch when it has one; the
            // item's owner becomes that task (the old id is kept as previous)
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "owner-no-pr", ["item"] = r.Item, ["task"] = own.TaskId }.ToJsonString());
        }
        int s; JsonNode? j;
        var body = TaskBody(r, code, pr);
        if (own != null && pr == null && own.Branch != "") { body["head_ref"] = own.Branch; body["base_ref"] = cfg.DefaultBranch; }
        try { (s, j) = await Send(HttpMethod.Post, TasksPath, body, ct); }
        catch (Exception e) { codes.Forget(r.WorkerName); return new(false, "", "launch-" + e.GetType().Name); }
        var id = j?["id"]?.ToString() ?? "";
        if (s is not (200 or 201 or 202) || id == "") { codes.Forget(r.WorkerName); return new(false, "", s is 200 or 201 or 202 ? "launch-no-id" : $"launch-{s}"); }
        CopilotOwner no; var url = TaskUrl(j?["html_url"]?.ToString(), id);
        lock (tasks)
        {
            tasks[r.WorkerName] = new Task_ { Id = id, TaskId = id, Url = url, Worker = r.WorkerName, Item = r.Item, Phase = r.Phase, State = j?["state"]?.ToString() ?? "queued" };
            no = new CopilotOwner { Item = r.Item, TaskId = id, Url = url, Branch = HeadRef(j) ?? (pr?.Branch ?? ""), Pr = pr?.Number ?? 0, Phase = r.Phase, Sessions = 1, LastResume = DateTime.UtcNow, WaitingOn = "session" };
            if (own != null) { no.Previous.AddRange(own.Previous); no.Previous.Add(own.TaskId); no.Since = own.Since; }
            owners[r.Item] = no;
        }
        _ = Task.Run(() => Save(no));
        return new(true, id, "", url);
    }

    // A follow-up session of the owner task: a @copilot comment on its pull request, by the identity that holds the seat (GitHub answers only a
    // writer's mention). The execution is that task's next session: "<task>#<n>".
    async Task<ProvisionResult> Resume(CopilotOwner own, string worker, int item, string phase, int pr, string text, CancellationToken ct)
    {
        var k = own.Sessions;
        // the pull request's head when the session is asked for: its end is judged by whether it pushed (IRuntimeWork.Pushed)
        var baseSha = await HeadShaOf(pr, ct);
        try
        {
            var (ts, tj) = await Send(HttpMethod.Get, $"{TasksPath}/{Uri.EscapeDataString(own.TaskId)}", null, ct);
            if (ts == 200 && tj?["sessions"] is JsonArray ss) k = Math.Max(k, ss.Count);
        }
        catch (Exception e) { Console.Error.WriteLine($"copilot: read of task {own.TaskId}: {e.GetType().Name}"); }
        int s;
        try { (s, _) = await Send(HttpMethod.Post, $"/repos/{cfg.Repository}/issues/{pr}/comments", new JsonObject { ["body"] = "@copilot " + text }, ct); }
        catch (Exception e) { return new(false, "", "resume-" + e.GetType().Name); }
        if (s is not (200 or 201)) return new(false, "", $"resume-{s}");
        var id = $"{own.TaskId}#{k}"; var url = own.Url != "" ? own.Url : TaskUrl(null, own.TaskId);
        lock (tasks)
        {
            tasks[worker] = new Task_ { Id = id, TaskId = own.TaskId, Url = url, Session = k, Via = "comment", Pr = pr, Worker = worker, Item = item, Phase = phase, BaseSha = baseSha ?? "" };
            own.Sessions = k + 1; own.Pr = pr; own.Phase = phase; own.LastResume = DateTime.UtcNow; own.WaitingOn = "session"; own.Updated = DateTime.UtcNow;
        }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "resume", ["item"] = item, ["phase"] = phase, ["task"] = own.TaskId, ["session"] = k, ["pr"] = pr }.ToJsonString());
        _ = Task.Run(() => Save(own));
        return new(true, id, "", url);
    }

    // A follow-up of the owner task that is no phase of the board (a red master after the merge): no worker, no tool of the gateway; the session
    // fixes forward on a new pull request against the default branch. Counted by the caller against the daily cap.
    public async Task<bool> ResumeDirect(int item, int pr, string text, CancellationToken ct)
    {
        var own = OwnerOf(item); if (own == null || pr <= 0) return false;
        var (s, _) = await Send(HttpMethod.Post, $"/repos/{cfg.Repository}/issues/{pr}/comments", new JsonObject { ["body"] = "@copilot " + text }, ct);
        if (s is not (200 or 201)) return false;
        lock (tasks) { own.Sessions++; own.LastResume = DateTime.UtcNow; own.Updated = DateTime.UtcNow; }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "resume", ["item"] = item, ["phase"] = "master-fix", ["task"] = own.TaskId, ["pr"] = pr }.ToJsonString());
        return true;
    }

    // the head commit of a pull request (null: unreadable)
    async Task<string?> HeadShaOf(int pr, CancellationToken ct)
    {
        try { var (s, j) = await Send(HttpMethod.Get, $"/repos/{cfg.Repository}/pulls/{pr}", null, ct); return s == 200 && j?["head"]?["sha"]?.ToString() is { Length: > 0 } h ? h : null; }
        catch (Exception e) { Console.Error.WriteLine($"copilot: head of #{pr}: {e.GetType().Name}"); return null; }
    }

    // IRuntimeWork: a follow-up session (a @copilot comment on the item's pull request), and whether that pull request's head moved since it was
    // asked for (null: not known)
    public bool FollowUp(string worker) { lock (tasks) return tasks.TryGetValue(worker, out var t) && t.Via == "comment"; }
    public async Task<bool?> Pushed(string worker, CancellationToken ct)
    {
        Task_? t; lock (tasks) tasks.TryGetValue(worker, out t);
        if (t == null || t.Via != "comment" || t.Pr <= 0 || t.BaseSha == "") return null;
        var h = await HeadShaOf(t.Pr, ct);
        return h == null ? null : h != t.BaseSha;
    }

    // the newest task of the repository whose branch artifact is this branch (the tasks the seat's identity can list), with its session count
    async Task<(string Id, string Url, int Sessions)?> TaskOfBranch(string branch, CancellationToken ct)
    {
        try
        {
            var (s, j) = await Send(HttpMethod.Get, $"{TasksPath}?per_page=100", null, ct);
            if (s != 200) return null;
            foreach (var t in ((j?["tasks"] as JsonArray) ?? new JsonArray()).OrderByDescending(x => x?["created_at"]?.ToString() ?? "", StringComparer.Ordinal))
                if (t?["id"]?.ToString() is { Length: > 0 } id && HeadRef(t) == branch)
                    return (id, HtmlUrl(t), int.TryParse(t["session_count"]?.ToString(), out var sc) && sc > 0 ? sc : 1);
        }
        catch (Exception e) { Console.Error.WriteLine($"copilot: tasks of {branch}: {e.GetType().Name}"); }
        return null;
    }

    // the open pull request on a branch (0: none)
    async Task<int> PrOfBranch(string branch, CancellationToken ct)
    {
        try
        {
            var owner = cfg.Repository.Split('/')[0];
            var (s, j) = await Send(HttpMethod.Get, $"/repos/{cfg.Repository}/pulls?state=open&head={Uri.EscapeDataString(owner + ":" + branch)}", null, ct);
            if (s == 200 && j is JsonArray a) foreach (var p in a) if (p?["number"] is JsonValue nv && nv.TryGetValue<int>(out var n) && p["head"]?["ref"]?.ToString() == branch) return n;
        }
        catch (Exception e) { Console.Error.WriteLine($"copilot: pull request of {branch}: {e.GetType().Name}"); }
        return 0;
    }

    // A task for a role that is not a worker (the on-demand manager, ManagerCopilot.cs): its prompt and the default branch as base_ref, no pull
    // request by Copilot, no model unless api.copilot.model names one. It is not in the worker table: the runtime manager never lists it.
    public async Task<(bool Ok, string Id, string Url, string Error)> LaunchRole(string promptText, CancellationToken ct)
    {
        var body = new JsonObject { ["prompt"] = promptText, ["base_ref"] = cfg.DefaultBranch, ["create_pull_request"] = false };
        if (cfg.Model != "") body["model"] = cfg.Model;
        int s; JsonNode? j;
        try { (s, j) = await Send(HttpMethod.Post, TasksPath, body, ct); }
        catch (Exception e) { return (false, "", "", "launch-" + e.GetType().Name); }
        var id = j?["id"]?.ToString() ?? "";
        if (s is not (200 or 201 or 202) || id == "") return (false, "", "", s is 200 or 201 or 202 ? "launch-no-id" : $"launch-{s}");
        return (true, id, j?["html_url"]?.ToString() ?? "", "");
    }

    // The state of such a task: Running, Succeeded, Failed or Stopped as for a worker's, the session link (html_url) and the usage its sessions report.
    public async Task<(bool Ok, string Status, string Url, JsonObject? Usage)> ReadRole(string id, CancellationToken ct)
    {
        var (s, j) = await Send(HttpMethod.Get, $"{TasksPath}/{Uri.EscapeDataString(id)}", null, ct);
        if (s != 200) return (false, "Running", "", null);
        var st = j?["state"]?.ToString() ?? "";
        var status = st switch { "completed" or "idle" => "Succeeded", "failed" or "timed_out" => "Failed", "cancelled" => "Stopped", _ => "Running" };
        return (true, status, j?["html_url"]?.ToString() ?? "", Usage(j?["sessions"]));
    }

    // The agent tasks API has no cancel: the end is recorded here, the code is spent, and the manager revokes the name (RevokeOnEnd), so the
    // r1 the session holds is refused at once. Idempotent. The owner stays: the task is resumed for the next phase.
    public Task Deprovision(string workerName, string reason, CancellationToken ct)
    {
        codes.Forget(workerName);
        lock (tasks) if (tasks.TryGetValue(workerName, out var t) && t.Ended == null) { t.Ended = DateTime.UtcNow; if (t.Status == "Running") t.Status = "Stopped"; }
        return Task.CompletedTask;
    }

    // the sessions this process started, with Container Apps job states: queued, in_progress and waiting_for_user are Running; completed and idle
    // Succeeded; failed and timed_out Failed; cancelled Stopped. A session past its deadline is reported Failed. A follow-up whose session has not
    // appeared yet is queued.
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
                    var (s, j) = await Send(HttpMethod.Get, $"{TasksPath}/{Uri.EscapeDataString(t.TaskId)}", null, ct);
                    if (s != 200) throw new HttpRequestException($"read of task answered {s}");
                    var sessions = ((j?["sessions"] as JsonArray) ?? new JsonArray()).Where(x => x != null).OrderBy(x => x!["created_at"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
                    var mine = t.Session < sessions.Count ? sessions[t.Session] : null;
                    // a follow-up session not listed yet is queued; the task's first session reads as the task when the API lists none
                    var st = mine?["state"]?.ToString() ?? (t.Session == 0 ? j?["state"]?.ToString() ?? "" : "queued");
                    var err = mine?["error"]?["message"]?.ToString();
                    var quota = st is "failed" && IsQuotaError(err);
                    lock (tasks)
                    {
                        if (quota)
                        {
                            if (quotaSince == null) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-quota-exhausted", ["item"] = t.Item, ["task"] = t.TaskId }.ToJsonString());
                            quotaSince ??= DateTime.UtcNow; quotaRetryAt = DateTime.UtcNow.AddMinutes(cfg.QuotaRetryMinutes); quotaMessage = (err ?? "").Length > 200 ? err![..200] : err ?? "";
                        }
                        else if (quotaSince != null && st is "in_progress" or "completed" && t.Started > quotaSince)
                        {
                            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-quota-back", ["item"] = t.Item }.ToJsonString());
                            quotaSince = null; quotaRetryAt = null; quotaMessage = "";
                        }
                    }
                    var usage = Usage(mine != null ? new JsonArray(mine.DeepClone()) : t.Session == 0 ? j?["sessions"] : null);
                    var taskUsage = Usage(j?["sessions"]);
                    // the credit guard: a task over api.copilot.maxCreditsPerTask (all its sessions) ends here (the API has no cancel: its r1 is
                    // revoked with the end) and its item is parked before the end is settled, so no resume and no settle follows
                    var credits = taskUsage?["ai_credits"] is JsonValue cv && cv.TryGetValue<double>(out var cd) ? cd : 0;
                    if (cfg.MaxCreditsPerTask > 0 && credits > cfg.MaxCreditsPerTask && !t.Guarded)
                    {
                        lock (tasks) { t.Guarded = true; t.Usage = usage; }
                        codes.Forget(t.Worker);
                        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-credit-guard", ["worker"] = t.Worker, ["item"] = t.Item, ["phase"] = t.Phase, ["task"] = t.Id, ["ai_credits"] = credits, ["max"] = cfg.MaxCreditsPerTask }.ToJsonString());
                        if (OverBudget != null) { try { await OverBudget(t.Item, t.Phase, credits); } catch (Exception e) { Console.Error.WriteLine($"copilot: credit-guard park of #{t.Item}: {e.GetType().Name}"); } }
                        lock (tasks) if (t.Ended == null) { t.Status = "Failed"; t.Ended = DateTime.UtcNow; }
                    }
                    CopilotOwner? changed = null;
                    lock (tasks)
                    {
                        t.State = st; t.Artifacts = j?["artifacts"]?.DeepClone(); if (usage != null) t.Usage = usage;
                        var hr = HeadRef(j);
                        if (IsDevelopment(t.Phase) && hr != null) heads[t.Item] = hr;
                        if (hr != null && owners.TryGetValue(t.Item, out var ow) && ow.TaskId == t.TaskId && ow.Branch != hr) { ow.Branch = hr; changed = ow; }
                        if (owners.TryGetValue(t.Item, out var ow2) && ow2.TaskId == t.TaskId && HtmlUrl(j) is { Length: > 0 } h0 && TaskUrl(h0, t.TaskId) is var hu && ow2.Url != hu) { ow2.Url = hu; changed = ow2; }
                        t.Fields ??= new JsonArray((j as JsonObject)?.Select(kv => (JsonNode)kv.Key).ToArray() ?? Array.Empty<JsonNode>());
                        if (t.Ended == null && !t.Guarded)
                        {
                            t.Status = quota ? "Blocked" : st switch { "completed" or "idle" => "Succeeded", "failed" or "timed_out" => "Failed", "cancelled" => "Stopped", _ => "Running" };
                            if (t.Status != "Running") t.Ended = DateTime.UtcNow;
                        }
                        else if (t.Usage == null) t.Usage = new JsonObject { ["reported"] = false };
                    }
                    if (changed != null) { var ch = changed; _ = Task.Run(() => Save(ch)); }
                    if (t.Ended != null && st is not ("queued" or "in_progress")) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-usage", ["worker"] = t.Worker, ["item"] = t.Item, ["phase"] = t.Phase, ["task"] = t.Id, ["state"] = st,
                        ["minutes"] = Math.Round(((t.Ended ?? DateTime.UtcNow) - t.Started).TotalMinutes, 1), ["usage"] = t.Usage?.DeepClone() }.ToJsonString());
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"copilot: read of {t.Worker}: {e.GetType().Name}"); }
            list.Add(new RuntimeInstance(t.Id, t.Worker, t.Item, t.Phase, t.Status, t.Started, t.Ended));
        }
        return list;
    }

    // The task's page (its live session, after the end its transcript): the API's html_url when it is an https URL without credentials, query or
    // fragment; else built from the repository and the task id. Never carries a token.
    public string TaskUrl(string? htmlUrl, string id)
    {
        if (Uri.TryCreate(htmlUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" && u.UserInfo == "" && u.Query == "" && u.Fragment == "" && htmlUrl!.Length <= 300) return u.AbsoluteUri;
        return $"https://github.com/{cfg.Repository}/tasks/{Uri.EscapeDataString(id)}";
    }
    static string HtmlUrl(JsonNode? task) => task?["html_url"]?.ToString() is { } u && u.StartsWith("https://", StringComparison.Ordinal) && u.Length <= 300 ? u : "";

    // the branch of the task's artifacts ({type: branch, data: {head_ref}}), only a copilot/ branch
    public static string? HeadRef(JsonNode? task)
    {
        foreach (var a in (task?["artifacts"] as JsonArray) ?? new JsonArray())
            if (a?["type"]?.ToString() == "branch" && a["data"]?["head_ref"]?.ToString() is { } h && h.StartsWith("copilot/", StringComparison.Ordinal) && h.Length <= 200) return h;
        return null;
    }

    // the usage the sessions report ({type: premium_requests | ai_credits, amount}), summed by type; null when none reports one
    static JsonObject? Usage(JsonNode? sessions)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in (sessions as JsonArray) ?? new JsonArray())
            if (s?["usage"] is JsonObject u && u["type"]?.ToString() is { Length: > 0 } ty && double.TryParse(u["amount"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                sums[ty] = sums.GetValueOrDefault(ty) + a;
        if (sums.Count == 0) return null;
        var o = new JsonObject(); foreach (var (k, v) in sums) o[k] = v; return o;
    }
}
