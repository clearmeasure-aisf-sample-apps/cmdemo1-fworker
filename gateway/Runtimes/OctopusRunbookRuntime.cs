using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

// The Octopus runbook runtime (docs/design/octopus-runbook-runtime.md): a worker container is started on a self-hosted machine (an Octopus
// tentacle with target tag runtimes.octopus.hostTag in runtimes.octopus.environment) by a run of the runbook "Start worker" of project
// runtimes.octopus.project. The runbook starts a detached container fw-<name> and exits in seconds: no Octopus task is held open for a worker
// (Octopus Cloud's task cap is shared with the deployments). Only short roles (overflowRoles, never a coding worker) may run "held" on the
// Hosted Ubuntu dynamic worker pool (runbook "Start worker (overflow)"), at most overflowMax at a time. The container's life is reported by
// the scheduled runbook "Reconcile hosts" (POST /api/v1/runtime/octopus/report), which also reads what should run (GET .../desired).
// The gateway signs in either with a key (auth "apiKey", OCTOPUS_RUNBOOK_API_KEY) or, by default when no key is set, with its managed identity
// (auth "oidc"): an Entra token for oidcAudience is exchanged at {serverUrl}/token/v1 for an Octopus access token of the service account
// serviceAccountId (Octopus "OpenID Connect" for service accounts). Either identity may run the runbooks of that one project; the CLI keys of the agents are Octopus sensitive
// variables and never pass through the gateway. The execution's values are no credential beyond the one-time nonce.
sealed class OctopusRunbookSettings
{
    public bool Enabled;
    public string ServerUrl = "https://clearmeasure.octopus.app", SpaceId = "Spaces-355", Project = "cmdemo1-fworker-runtime", Environment = "tdd", HostTag = "fworker-host";
    public string StartRunbook = "Start worker", OverflowRunbook = "Start worker (overflow)", StopRunbook = "Stop worker";
    public string Agent = "codex";                                   // Fworker.Agent: claude, cursor or codex
    public int PerHostCapacity = 2, OverflowMax = 4, MaxConcurrent, MaxExecutionsPerDay, MaxFailuresPerPhase = 2, UnhealthyAfterFailures = 2;
    public double AckSeconds = 600, ListSeconds = 30, ReportGraceSeconds = 90;
    // the roles that may overflow to the Hosted Ubuntu pool; a coding role is refused whatever this list says (CodingRoles)
    public List<string> OverflowRoles = new() { "plant-manager", "supervisor", "qc", "operator" };
    // the short roles the gateway starts through this runtime instead of their default (runtimes.octopus.roles; today "supervisor", the on-demand
    // plant manager of ManagerCopilot.cs). Default empty: nothing is routed here. A coding role is never one of them (CodingRoles).
    public List<string> Roles = new();
    public bool RunsRole(string role) { var r = role.Trim().ToLowerInvariant().Replace(' ', '-'); return r != "" && Roles.Contains(r) && !CodingRoles.Contains(r); }
    public static readonly string[] CodingRoles = { "worker", "development", "coding", "own-item" };
    public static readonly string[] Agents = { "claude", "cursor", "codex" };
    public string ApiKey = "", HostToken = "";
    public string Auth = "oidc", ServiceAccountId = "", OidcAudience = "api://AzureADTokenExchange", ManagedIdentityClientId = "";
    public bool Oidc => Auth == "oidc";
    public bool On => Enabled && MaxConcurrent > 0 && ServerUrl != "" && (Oidc ? ServiceAccountId != "" : ApiKey != "");

    public static OctopusRunbookSettings From(JsonNode? c, Func<string, string?> get)
    {
        var s = new OctopusRunbookSettings();
        if (c is JsonObject)
        {
            static bool Num(string? t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
            double D(string k, double d) => Num(c[k]?.ToString(), out var v) ? v : d;
            string S(string k, string d) => c[k] is JsonValue sv && sv.TryGetValue<string>(out var str) ? str : d;
            s.Enabled = c["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
            s.ServerUrl = S("serverUrl", s.ServerUrl).TrimEnd('/'); s.SpaceId = S("spaceId", s.SpaceId); s.Project = S("project", s.Project);
            s.Environment = S("environment", s.Environment); s.HostTag = S("hostTag", s.HostTag);
            s.StartRunbook = S("startRunbook", s.StartRunbook); s.OverflowRunbook = S("overflowRunbook", s.OverflowRunbook); s.StopRunbook = S("stopRunbook", s.StopRunbook);
            var agent = S("agent", s.Agent).Trim().ToLowerInvariant(); if (Agents.Contains(agent)) s.Agent = agent;
            s.PerHostCapacity = (int)D("perHostCapacity", 2); s.OverflowMax = (int)D("overflowMax", 4);
            s.MaxConcurrent = (int)D("maxConcurrent", 0); s.MaxExecutionsPerDay = (int)D("maxExecutionsPerDay", 0);
            s.MaxFailuresPerPhase = Math.Max(1, (int)D("maxFailuresPerPhase", 2)); s.UnhealthyAfterFailures = Math.Max(1, (int)D("unhealthyAfterFailures", 2));
            s.AckSeconds = D("ackSeconds", 600); s.ListSeconds = Math.Max(1, D("listSeconds", 30)); s.ReportGraceSeconds = D("reportGraceSeconds", 90);
            s.Auth = S("auth", "").Trim().ToLowerInvariant() switch { "apikey" => "apiKey", "oidc" => "oidc", _ => "" };
            s.ServiceAccountId = S("serviceAccountId", "").Trim(); s.OidcAudience = S("oidcAudience", s.OidcAudience).Trim();
            s.ManagedIdentityClientId = S("managedIdentityClientId", "").Trim();
            if (c["overflowRoles"] is JsonArray roles) s.OverflowRoles = roles.Select(x => x?.ToString().Trim().ToLowerInvariant() ?? "").Where(x => x != "").ToList();
            if (c["roles"] is JsonArray rr) s.Roles = rr.Select(x => x?.ToString().Trim().ToLowerInvariant() ?? "").Where(x => x != "").ToList();
        }
        s.ApiKey = get("OCTOPUS_RUNBOOK_API_KEY")?.Trim() ?? "";
        s.HostToken = get("FACTORY_OCTOPUS_HOST_TOKEN")?.Trim() ?? "";
        if (s.Auth == "") s.Auth = s.ApiKey != "" ? "apiKey" : "oidc";
        if (s.ManagedIdentityClientId == "") s.ManagedIdentityClientId = get("FACTORY_RUNTIME_IDENTITY_CLIENT_ID")?.Trim() ?? "";
        return s;
    }

    // one-time nonce (MintCredential null), as cae-job: the container redeems it for its w2
    public RuntimeLimits Limits(Func<string?> blocked) => new()
    {
        Kind = "octopus", Prefix = "oct", IsOn = () => On, Blocked = blocked, MaxConcurrent = MaxConcurrent, MaxExecutionsPerDay = MaxExecutionsPerDay,
        MaxFailuresPerPhase = MaxFailuresPerPhase, UnhealthyAfterFailures = UnhealthyAfterFailures, AckSeconds = AckSeconds, ListSeconds = ListSeconds
    };
}

sealed class OctopusRunbookRuntime : IWorkerRuntime, IRuntimeDetail
{
    sealed class Rec
    {
        public string Name = "", Phase = "", Machine = "", MachineName = "", Mode = "detached", TaskId = "", StopTaskId = "", State = "Queued", Url = "";
        public int Item;
        public DateTime Started = DateTime.UtcNow;
        public DateTime? TaskDone, Ended;
        public bool StopAsked, Adopted;   // Adopted: read back at a restart, not yet seen in a host report
        public string Status = "Processing";
    }
    sealed record Container(string Name, int Item, string State, DateTime? StartedAt);
    sealed record HostReport(DateTime At, Dictionary<string, Container> Containers);
    sealed record Host(string Id, string Name);

    readonly OctopusRunbookSettings cfg;
    readonly HttpClient http;
    readonly object gate = new();
    readonly Dictionary<string, Rec> recs = new(StringComparer.Ordinal);
    readonly Dictionary<string, HostReport> reports = new(StringComparer.OrdinalIgnoreCase);   // by machine id
    readonly Dictionary<string, string> machineNames = new(StringComparer.OrdinalIgnoreCase); // id -> name
    readonly Dictionary<string, Dictionary<string, string>> formIds = new(StringComparer.Ordinal); // runbook id -> variable name -> control id
    string projectId = "", environmentId = "";
    bool rebuilt;
    DateTime noCapacityUntil = DateTime.MinValue;

    readonly Func<CancellationToken, Task<string>> entraToken;
    readonly SemaphoreSlim tokenGate = new(1, 1);
    string accessToken = ""; DateTime accessUntil = DateTime.MinValue;
    int exchanges;

    public OctopusRunbookRuntime(OctopusRunbookSettings cfg, HttpClient? http = null, Func<CancellationToken, Task<string>>? entraToken = null)
    {
        this.cfg = cfg; this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        this.entraToken = entraToken ?? DefaultEntraToken(cfg);
    }

    // The gateway's managed identity (its user-assigned one when managedIdentityClientId or FACTORY_RUNTIME_IDENTITY_CLIENT_ID names it), for the
    // audience the Octopus OIDC identity expects. A test gateway against a loopback Octopus takes FACTORY_TEST_OCTOPUS_ENTRA_TOKEN instead; that
    // variable is ignored for any other server.
    static Func<CancellationToken, Task<string>> DefaultEntraToken(OctopusRunbookSettings cfg)
    {
        var test = Environment.GetEnvironmentVariable("FACTORY_TEST_OCTOPUS_ENTRA_TOKEN");
        if (!string.IsNullOrEmpty(test) && Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out var u) && u.IsLoopback) return _ => Task.FromResult(test);
        var cred = cfg.ManagedIdentityClientId != "" ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(cfg.ManagedIdentityClientId)) : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        var scope = new TokenRequestContext(new[] { cfg.OidcAudience.TrimEnd('/') + "/.default" });
        return async ct => (await cred.GetTokenAsync(scope, ct)).Token;
    }

    // The Octopus access token of the service account, cached until a minute before it expires (or half its life when that is shorter).
    // Token exchange (RFC 8693) at {serverUrl}/token/v1, the token_endpoint of Octopus's /.well-known/openid-configuration. Never logged.
    async Task<string> AccessToken(bool renew, CancellationToken ct)
    {
        await tokenGate.WaitAsync(ct);
        try
        {
            if (!renew && accessToken != "" && DateTime.UtcNow < accessUntil) return accessToken;
            var jwt = await entraToken(ct);
            var body = new JsonObject
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange", ["audience"] = cfg.ServiceAccountId,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt", ["subject_token"] = jwt
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.ServerUrl}/token/v1") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonNode? j = null; try { if (text.Length > 0) j = JsonNode.Parse(text); } catch { }
            var at = j?["access_token"]?.ToString() ?? "";
            if (!resp.IsSuccessStatusCode || at == "") throw new HttpRequestException($"octopus token exchange answered {(int)resp.StatusCode}");
            var life = double.TryParse(j?["expires_in"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) && e > 0 ? e : 300;
            accessToken = at; accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(life - 60, life / 2)));
            exchanges++;
            return at;
        }
        finally { tokenGate.Release(); }
    }

    public string Kind => "octopus";
    public int MaxConcurrent => cfg.MaxConcurrent;
    public OctopusRunbookSettings Settings => cfg;

    // RuntimeLimits.Blocked: after a refusal for capacity no further start is tried until the next reading (the plant queues the item)
    public string? Blocked() { lock (gate) return DateTime.UtcNow < noCapacityUntil ? "runtime-no-capacity" : null; }

    string Space(string suffix) => $"{cfg.ServerUrl}/api/{cfg.SpaceId}{suffix}";

    async Task<(int Status, JsonNode? Body)> Send(HttpMethod m, string url, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(m, url);
            if (cfg.Oidc) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(attempt > 0, ct));
            else req.Headers.Add("X-Octopus-ApiKey", cfg.ApiKey);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct);
            // an access token Octopus no longer takes (revoked, expired early): one new exchange and one retry
            if (cfg.Oidc && attempt == 0 && resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) continue;
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonNode? j = null; try { if (text.Length > 0) j = JsonNode.Parse(text); } catch { }
            return ((int)resp.StatusCode, j);
        }
    }

    async Task<JsonNode> Get(string url, CancellationToken ct)
    {
        var (s, b) = await Send(HttpMethod.Get, url, null, ct);
        if (s != 200 || b == null) throw new HttpRequestException($"octopus GET {new Uri(url).AbsolutePath} answered {s}");
        return b;
    }

    static IEnumerable<JsonNode> Items(JsonNode n) => (n as JsonArray ?? n["Items"] as JsonArray ?? new JsonArray()).Where(x => x != null)!;

    async Task Ids(CancellationToken ct)
    {
        lock (gate) if (projectId != "" && environmentId != "") return;
        var p = Items(await Get(Space("/projects/all"), ct)).FirstOrDefault(x => x["Name"]?.ToString() == cfg.Project) ?? throw new InvalidOperationException("project-not-found");
        var e = Items(await Get(Space("/environments/all"), ct)).FirstOrDefault(x => string.Equals(x["Name"]?.ToString(), cfg.Environment, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("environment-not-found");
        lock (gate) { projectId = p["Id"]!.ToString(); environmentId = e["Id"]!.ToString(); }
    }

    // The records of a previous process are read back once, before anything counts host load, answers desired or judges a report: a
    // reconcile run right after a restart must not see an empty desired list and remove the containers that still work.
    readonly SemaphoreSlim rebuildGate = new(1, 1);
    async Task EnsureRebuilt(CancellationToken ct)
    {
        if (Volatile.Read(ref rebuilt)) return;
        await rebuildGate.WaitAsync(ct);
        try { if (!rebuilt) { await Ids(ct); await Rebuild(ct); Volatile.Write(ref rebuilt, true); } }
        finally { rebuildGate.Release(); }
    }

    // the project's runbooks by name: id and published snapshot (read at every start, a republished runbook is taken at once)
    async Task<Dictionary<string, (string Id, string Snapshot)>> Runbooks(CancellationToken ct)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var r in Items(await Get(Space($"/projects/{projectId}/runbooks?take=100"), ct)))
            d[r["Name"]?.ToString() ?? ""] = (r["Id"]?.ToString() ?? "", r["PublishedRunbookSnapshotId"]?.ToString() ?? "");
        return d;
    }

    // The control ids of the prompted variables (Octopus docs, "Run a runbook with prompted variables"): the preview of a run of the runbook in
    // the environment lists Form.Elements, each with Name (the control id that FormValues takes) and Control.Name (the variable's name).
    async Task<Dictionary<string, string>> Form(string runbookId, CancellationToken ct)
    {
        lock (gate) if (formIds.TryGetValue(runbookId, out var c)) return c;
        var prev = await Get(Space($"/runbooks/{runbookId}/runbookRuns/preview/{environmentId}?includeDisabledSteps=true"), ct);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in prev["Form"]?["Elements"] as JsonArray ?? new JsonArray())
            if (el?["Control"]?["Name"]?.ToString() is { Length: > 0 } vn && el["Name"]?.ToString() is { Length: > 0 } id) d[vn] = id;
        lock (gate) formIds[runbookId] = d;
        return d;
    }

    async Task<List<Host>> Hosts(CancellationToken ct)
    {
        var url = Space($"/machines?environmentIds={Uri.EscapeDataString(environmentId)}&roles={Uri.EscapeDataString(cfg.HostTag)}&take=200");
        var list = new List<Host>();
        foreach (var m in Items(await Get(url, ct)))
        {
            var id = m["Id"]?.ToString() ?? ""; var name = m["Name"]?.ToString() ?? "";
            // the tag is asked of the server; a server that ignores the filter is checked here too, before the machine is known
            if (m["Roles"] is not JsonArray roles || !roles.Any(r => r?.ToString() == cfg.HostTag)) continue;
            if (m["EnvironmentIds"] is not JsonArray envs || !envs.Any(r => r?.ToString() == environmentId)) continue;
            lock (gate) machineNames[id] = name;   // disabled and unhealthy hosts may still report
            if (m["IsDisabled"]?.GetValue<bool>() == true) continue;
            if (m["HealthStatus"]?.ToString() is not ("Healthy" or "HasWarnings")) continue;
            list.Add(new Host(id, name));
        }
        return list;
    }

    static string Slug(string s) => s.Trim().ToLowerInvariant().Replace(' ', '-');
    // only an explicit short role overflows: an own-item worker (no Role, the plant's phase workers) never does, whatever overflowRoles says
    public bool MayOverflow(string role) => role != "" && cfg.OverflowRoles.Contains(Slug(role)) && !OctopusRunbookSettings.CodingRoles.Contains(Slug(role));

    // Under gate: the workers on a host, from this process's records and the host's last report
    int Load(string machineId)
    {
        var names = recs.Values.Where(x => x.Ended == null && x.Mode == "detached" && string.Equals(x.Machine, machineId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToHashSet();
        if (reports.TryGetValue(machineId, out var rep)) foreach (var c in rep.Containers.Values) if (c.State == "running") names.Add(c.Name);
        return names.Count;
    }

    public async Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct)
    {
        await EnsureRebuilt(ct);
        var books = await Runbooks(ct);
        var hosts = await Hosts(ct);
        Host? pick; int overflowInUse;
        lock (gate)
        {
            pick = hosts.Select(h => (h, n: Load(h.Id))).Where(x => x.n < cfg.PerHostCapacity).OrderBy(x => x.n).ThenBy(x => x.h.Name, StringComparer.Ordinal).Select(x => x.h).FirstOrDefault();
            overflowInUse = recs.Values.Count(x => x.Ended == null && x.Mode == "held");
            // reserve the slot before the call, so two starts at once do not pick the same last slot
            recs[r.WorkerName] = new Rec { Name = r.WorkerName, Item = r.Item, Phase = r.Phase, Machine = pick?.Id ?? "", MachineName = pick?.Name ?? "", Mode = pick != null ? "detached" : "held" };
        }
        string book;
        if (pick != null) book = cfg.StartRunbook;
        else if (MayOverflow(r.Role) && overflowInUse < cfg.OverflowMax && books.ContainsKey(cfg.OverflowRunbook)) book = cfg.OverflowRunbook;
        else
        {
            lock (gate) { recs.Remove(r.WorkerName); noCapacityUntil = DateTime.UtcNow.AddSeconds(Math.Max(cfg.ListSeconds, 15)); }
            return new(false, "", "no capacity");
        }
        if (!books.TryGetValue(book, out var rb) || rb.Snapshot == "") { lock (gate) recs.Remove(r.WorkerName); return new(false, "", "runbook-not-published"); }
        var mode = pick != null ? "detached" : "held";
        var values = new Dictionary<string, string>
        {
            ["Fworker.Name"] = r.WorkerName, ["Fworker.Item"] = r.Item.ToString(CultureInfo.InvariantCulture), ["Fworker.Phase"] = r.Phase, ["Fworker.ModelProfile"] = r.ModelProfile,
            ["Fworker.Nonce"] = r.Nonce, ["Fworker.Agent"] = cfg.Agent, ["Fworker.Mode"] = mode,
            // sent only where the runbook prompts for it (Run keeps the form's variables): "" for an own-item worker
            ["Fworker.Role"] = r.Role
        };
        var (status, task) = await Run(rb.Id, rb.Snapshot, pick?.Id, values, ct);
        if (status is not (200 or 201) || task == "") { lock (gate) recs.Remove(r.WorkerName); return new(false, "", $"runbook-run-{status}"); }
        var url = $"{cfg.ServerUrl}/app#/{cfg.SpaceId}/tasks/{task}";
        lock (gate) if (recs.TryGetValue(r.WorkerName, out var rec)) { rec.TaskId = task; rec.Url = url; }
        return new(true, task, "", url);
    }

    async Task<(int Status, string Task)> Run(string runbookId, string snapshot, string? machine, Dictionary<string, string> values, CancellationToken ct)
    {
        var ids = await Form(runbookId, ct);
        var form = new JsonObject();
        foreach (var (k, v) in values) if (ids.TryGetValue(k, out var id)) form[id] = v;
        var body = new JsonObject
        {
            ["RunbookId"] = runbookId, ["RunbookSnapshotId"] = snapshot, ["EnvironmentId"] = environmentId, ["FormValues"] = form,
            ["SpecificMachineIds"] = machine != null ? new JsonArray(machine) : new JsonArray()
        };
        var (s, b) = await Send(HttpMethod.Post, Space("/runbookRuns"), body, ct);
        return (s, b?["TaskId"]?.ToString() ?? "");
    }

    public async Task Deprovision(string workerName, string reason, CancellationToken ct)
    {
        Rec? rec; lock (gate) recs.TryGetValue(workerName, out rec);
        if (rec == null) { await List(ct); lock (gate) recs.TryGetValue(workerName, out rec); }
        if (rec == null || rec.Ended != null) return;
        lock (gate) { if (rec.StopAsked) return; rec.StopAsked = true; }
        if (rec.Mode == "held")
        {
            if (rec.TaskId == "") return;
            var (s, _) = await Send(HttpMethod.Post, $"{cfg.ServerUrl}/api/tasks/{Uri.EscapeDataString(rec.TaskId)}/cancel", null, ct);
            if (s is not (200 or 202 or 204 or 400 or 404 or 409)) { lock (gate) rec.StopAsked = false; throw new HttpRequestException($"cancel of {rec.TaskId} answered {s}"); }
            return;
        }
        await Ids(ct);
        var books = await Runbooks(ct);
        if (!books.TryGetValue(cfg.StopRunbook, out var rb) || rb.Snapshot == "") { lock (gate) rec.StopAsked = false; throw new InvalidOperationException("stop runbook not published"); }
        var (st, task) = await Run(rb.Id, rb.Snapshot, rec.Machine, new Dictionary<string, string>
        {
            ["Fworker.Name"] = rec.Name, ["Fworker.Item"] = rec.Item.ToString(CultureInfo.InvariantCulture), ["Fworker.Phase"] = rec.Phase, ["Fworker.ModelProfile"] = "",
            ["Fworker.Nonce"] = "", ["Fworker.Agent"] = cfg.Agent, ["Fworker.Mode"] = "detached"
        }, ct);
        if (st is not (200 or 201)) { lock (gate) rec.StopAsked = false; throw new HttpRequestException($"stop runbook answered {st}"); }
        lock (gate) rec.StopTaskId = task;
    }

    static string Map(string state) => state switch { "Queued" => "Processing", "Executing" or "Cancelling" => "Running", "Success" => "Succeeded", "Canceled" => "Stopped", "Failed" or "TimedOut" => "Failed", _ => "Unknown" };

    public async Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct)
    {
        await Ids(ct);
        await EnsureRebuilt(ct);
        List<Rec> live; lock (gate) live = recs.Values.Where(x => x.Ended == null && x.TaskId != "").ToList();
        foreach (var rec in live)
        {
            if (rec.TaskDone != null && rec.Mode == "detached") continue;   // the start task ended: its container is read from the reports
            var (s, t) = await Send(HttpMethod.Get, $"{cfg.ServerUrl}/api/tasks/{Uri.EscapeDataString(rec.TaskId)}", null, ct);
            if (s == 404) { lock (gate) { rec.State = "Missing"; rec.TaskDone = DateTime.UtcNow; } continue; }
            if (s != 200) throw new HttpRequestException($"task read answered {s}");
            var state = t?["State"]?.ToString() ?? "";
            lock (gate) { rec.State = state; if (t?["IsCompleted"]?.GetValue<bool>() == true || state is "Success" or "Failed" or "Canceled" or "TimedOut") rec.TaskDone ??= DateTime.UtcNow; }
        }
        var now = DateTime.UtcNow; var list = new List<RuntimeInstance>();
        lock (gate)
        {
            foreach (var rec in recs.Values)
            {
                if (rec.Ended == null)
                {
                    string st;
                    if (rec.Mode == "held") st = rec.State == "Missing" ? "Stopped" : Map(rec.State);
                    else if (rec.State is "Failed" or "TimedOut" or "Canceled" or "Missing") st = "Failed";   // the start itself did not succeed
                    else if (rec.State != "Success") st = "Processing";
                    else
                    {
                        // the container's state as the host reported it after the start task ended; no report since: still starting or running
                        reports.TryGetValue(rec.Machine, out var rep);
                        if (rep != null && rep.Containers.TryGetValue(rec.Name, out var c)) { rec.Adopted = false; st = c.State == "running" ? "Running" : c.State == "exited" ? "Succeeded" : "Stopped"; }
                        // gone from the host; a worker read back at a restart that the host does not report had ended before (it exited by itself)
                        else if (rep != null && rec.TaskDone is { } td && rep.At > td.AddSeconds(cfg.ReportGraceSeconds)) st = rec.Adopted ? "Succeeded" : "Stopped";
                        else st = "Running";
                    }
                    rec.Status = st;
                    if (st is "Succeeded" or "Failed" or "Stopped") rec.Ended = now;
                }
                list.Add(new RuntimeInstance(rec.TaskId, rec.Name, rec.Item, rec.Phase, rec.Status, rec.Started, rec.Ended));
            }
            foreach (var k in recs.Where(kv => kv.Value.Ended < now.AddHours(-1)).Select(kv => kv.Key).ToList()) recs.Remove(k);
        }
        return list;
    }

    // Restart recovery: the runs of the last day of the project's runbooks are read back (task, run, its prompted values); a Start whose
    // worker has no later Stop is a record again. The values are read by the control ids of the runbook's form.
    async Task Rebuild(CancellationToken ct)
    {
        var books = await Runbooks(ct);
        string Id(string n) => books.TryGetValue(n, out var b) ? b.Id : "";
        var startIds = new HashSet<string> { Id(cfg.StartRunbook), Id(cfg.OverflowRunbook) }; startIds.Remove("");
        var stopId = Id(cfg.StopRunbook);
        // every task of the project back to the one-day cutoff, page by page (the scheduled Reconcile hosts runs would push the starts out of one page)
        var tasks = new List<JsonNode>(); var cutoff = DateTime.UtcNow.AddDays(-1);
        for (var skip = 0; skip < 20000; skip += 100)
        {
            var page = Items(await Get(Space($"/tasks?project={Uri.EscapeDataString(projectId)}&skip={skip}&take=100"), ct)).ToList();
            tasks.AddRange(page);
            if (page.Count < 100 || page.All(t => DateTime.TryParse(t["QueueTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var qt) && qt < cutoff)) break;
        }
        var stopped = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Rec>();
        foreach (var t in tasks)   // newest first, as Octopus lists them
        {
            var runId = t["Arguments"]?["RunbookRunId"]?.ToString() ?? "";
            if (runId == "") continue;
            var queued = DateTime.TryParse(t["QueueTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var q) ? q : DateTime.UtcNow;
            if (queued < DateTime.UtcNow.AddDays(-1)) continue;
            var (s, run) = await Send(HttpMethod.Get, Space($"/runbookRuns/{Uri.EscapeDataString(runId)}"), null, ct);
            if (s != 200 || run == null) continue;
            var rbId = run["RunbookId"]?.ToString() ?? "";
            if (rbId != stopId && !startIds.Contains(rbId)) continue;
            var ids = await Form(rbId, ct);
            string V(string n) => ids.TryGetValue(n, out var id) ? run["FormValues"]?[id]?.ToString() ?? "" : "";
            var name = V("Fworker.Name"); if (!Auth.ValidName(name)) continue;
            if (rbId == stopId) { stopped.Add(name); continue; }
            if (stopped.Contains(name) || found.Any(x => x.Name == name)) continue;
            var machine = (run["SpecificMachineIds"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "";
            found.Add(new Rec
            {
                Name = name, Item = int.TryParse(V("Fworker.Item"), out var it) ? it : 0, Phase = V("Fworker.Phase"), Machine = machine, Mode = V("Fworker.Mode") == "held" ? "held" : "detached",
                TaskId = t["Id"]?.ToString() ?? "", State = t["State"]?.ToString() ?? "", Started = queued, Url = $"{cfg.ServerUrl}/app#/{cfg.SpaceId}/tasks/{t["Id"]}",
                TaskDone = t["IsCompleted"]?.GetValue<bool>() == true ? DateTime.UtcNow : null, Adopted = true
            });
        }
        lock (gate) foreach (var r in found) recs.TryAdd(r.Name, r);
    }

    // ---- the reconcile endpoints (Program.cs): the host token is checked there -------------------------------------------------------------
    string? MachineOf(string host)
    {
        if (machineNames.ContainsKey(host)) return machineNames.Keys.First(k => string.Equals(k, host, StringComparison.OrdinalIgnoreCase));
        var byName = machineNames.FirstOrDefault(kv => string.Equals(kv.Value, host, StringComparison.OrdinalIgnoreCase));
        if (byName.Key != null) return byName.Key;
        return null;
    }

    // a deployment target of the hostTag in the environment (by id or name), else null: only those may report or ask
    async Task<string?> Resolve(string host, CancellationToken ct)
    {
        lock (gate) if (MachineOf(host) is { } m) return m;
        await Ids(ct); await Hosts(ct);
        lock (gate) return MachineOf(host);
    }

    // the worker names that should run on the host: started there by this gateway and not asked to stop
    // null: the host is not a known target. Throws when the records could not be read back: the endpoint answers 503 and the reconcile removes nothing.
    public async Task<JsonObject?> Desired(string host, CancellationToken ct)
    {
        await EnsureRebuilt(ct);
        var m = await Resolve(host, ct);
        if (m == null) return null;
        lock (gate)
        {
            var names = recs.Values.Where(x => x.Ended == null && !x.StopAsked && x.Mode == "detached" && string.Equals(x.Machine, m, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
            return new JsonObject { ["host"] = host, ["machine"] = m, ["workers"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) };
        }
    }

    // what runs on the host: a container that exited or is gone ends its execution at the next reading (List)
    // null: the host is not a known target. Before the records were read back the report is kept but names nothing to stop.
    public async Task<JsonObject?> Report(string host, JsonArray containers, CancellationToken ct)
    {
        var ready = true; try { await EnsureRebuilt(ct); } catch { ready = false; }
        string? m; try { m = await Resolve(host, ct); } catch { lock (gate) m = MachineOf(host); }
        if (m == null) return null;
        var d = new Dictionary<string, Container>(StringComparer.Ordinal);
        foreach (var c in containers.Take(200))
        {
            var name = c?["name"]?.ToString() ?? ""; if (name.StartsWith("fw-")) name = name[3..];
            if (!Auth.ValidName(name)) continue;
            var state = (c?["state"]?.ToString() ?? "").Trim().ToLowerInvariant();
            d[name] = new Container(name, int.TryParse(c?["item"]?.ToString(), out var it) ? it : 0, state is "running" or "restarting" or "created" ? "running" : state == "exited" ? "exited" : state == "" ? "unknown" : state,
                DateTime.TryParse(c?["startedAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sa) ? sa : null);
        }
        lock (gate)
        {
            reports[m] = new HostReport(DateTime.UtcNow, d);
            var desired = recs.Values.Where(x => x.Ended == null && !x.StopAsked && x.Mode == "detached" && string.Equals(x.Machine, m, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToHashSet();
            var stop = ready ? d.Values.Where(c => c.State == "running" && !desired.Contains(c.Name)).Select(c => (JsonNode)c.Name).ToArray() : Array.Empty<JsonNode>();
            return new JsonObject { ["ok"] = true, ["machine"] = m, ["accepted"] = d.Count, ["stop"] = new JsonArray(stop) };
        }
    }

    public JsonObject Status()
    {
        lock (gate)
            return new JsonObject
            {
                ["perHostCapacity"] = cfg.PerHostCapacity, ["overflowMax"] = cfg.OverflowMax, ["auth"] = cfg.Auth, ["serviceAccountId"] = cfg.ServiceAccountId, ["exchanges"] = exchanges, ["overflowInUse"] = recs.Values.Count(x => x.Ended == null && x.Mode == "held"),
                ["overflowRoles"] = new JsonArray(cfg.OverflowRoles.Select(x => (JsonNode)x).ToArray()), ["roles"] = new JsonArray(cfg.Roles.Select(x => (JsonNode)x).ToArray()), ["hostTokenSet"] = cfg.HostToken != "",
                ["hosts"] = new JsonArray(reports.Select(kv => (JsonNode)new JsonObject { ["machine"] = kv.Key, ["name"] = machineNames.GetValueOrDefault(kv.Key, ""), ["reportedAt"] = kv.Value.At.ToString("o"), ["containers"] = kv.Value.Containers.Count }).ToArray())
            };
    }

    public JsonObject? Detail(string workerName)
    {
        lock (gate) return recs.TryGetValue(workerName, out var r) ? new JsonObject { ["machine"] = r.Machine, ["machineName"] = r.MachineName, ["mode"] = r.Mode, ["taskState"] = r.State } : null;
    }

    public bool HostTokenOk(string presented) => cfg.HostToken != "" && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(cfg.HostToken));
}
