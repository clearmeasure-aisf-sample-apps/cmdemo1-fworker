using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using static HttpResultsX;

// The gateway: receives GitHub webhooks, holds every GitHub and Octopus secret, runs the rule scripts (pwsh factory.ps1 --server <verb>), runs the sweep,
// and in owner mode acts as the plant manager: a registry of worker sessions, directed assignment of jobs to them, per-item role-scoped tokens, and
// routing of triggers to the session that owns the item. Two listeners: the webhook port serves only the webhook route (this is the port a tunnel
// exposes); the API port serves the authenticated API and never leaves the machine.

// The configuration (gateway/Configuration/): the environment variables, then the gateway's own reads of the configuration file, each read once.
// The same names, defaults and precedence as before; the locals keep their names so the rest of this file reads them as it did.
var gw = GatewayOptions.FromEnvironment(Environment.GetEnvironmentVariable);
var scriptsDir = gw.ScriptsDir;
var entry = gw.Entry;
var apiToken = gw.ApiToken;
var webhookSecret = gw.WebhookSecret;
// every event stream writes a `: ping` comment at this interval, busy or idle: a proxy or the platform's ingress that sees no byte for a while
// closes the connection, and a client then sees a drop it did not cause (enhancement 11). Clients ignore comment lines.
var ssePingSeconds = gw.SsePingSeconds;
var sweepSeconds = gw.SweepSeconds;
var ownerMode = gw.OwnerMode;
var environmentName = gw.EnvironmentName;
var publicUrl = gw.PublicUrl;
var tokenSecret = gw.TokenSecret;
var instanceConfig = InstanceConfig.Load(InstanceConfig.PathFor(scriptsDir, Environment.GetEnvironmentVariable), Environment.GetEnvironmentVariable);
var timing = TimingOptions.From(instanceConfig);
var revocationOptions = RevocationOptions.FromEnvironment(Environment.GetEnvironmentVariable);
// the role token r1 (Auth.cs, agents-roles-launch.md 4): bound to this environment; auth.legacyKinds false refuses the per-role kinds
Auth.Environment = environmentName.Trim().ToLowerInvariant();
Auth.LegacyKinds = instanceConfig.LegacyKinds;
// The accept window of an offer: FACTORY_ASSIGN_ACK_SECONDS, else FACTORY_TIMING_assignAckSeconds, else timing.assignAckSeconds of the config, else 120.
var ackSeconds = timing.AckSeconds;
// plant.oneItemPerSession (docs/launcher.md): FACTORY_ONE_ITEM_PER_SESSION (1 or 0), else the config, else on.
var oneItemPerSession = instanceConfig.OneItemPerSession;
gw.ValidateOwnerMode();
var qcSettings = QcSettings.Load(scriptsDir, publicUrl, ownerMode);
var messaging = MessagingSettings.Load(scriptsDir, ownerMode);
// the gateway's own secrets, so that a message carrying one verbatim is refused (worker-messaging.md 5.4); short development values are left out
messaging.SecretLiterals = new[] { apiToken, tokenSecret }.Where(x => x.Length >= 16).ToArray();
// the backlog keeper session (Keeper.cs, docs/design/backlog-keeper.md): off by default; needs messaging.enabled; its names are reserved
var keeper = KeeperSettings.Load(scriptsDir, messaging.Enabled);
Auth.Keeper = keeper;
if (keeper.Enabled) messaging.Reserved.UnionWith(keeper.Names);

var builder = WebApplication.CreateBuilder(args);
// the gateway's own address for the links it writes into the journal: FACTORY_PUBLIC_URL, else the address the operator or a container last used
// (gateway/Http/PublicBase.cs, noted by the authentication middleware)
var publicBase = new PublicBase(publicUrl);

// The composition (gateway/Composition/, gateway-composition.md 3 B): the host, then one registration method per area, in the order the objects
// were made before C5a (GatewayServiceCollectionExtensions.cs says what each holds and which later task changes it).
var composition = new GatewayComposition(gw, instanceConfig, timing, revocationOptions, qcSettings, messaging, keeper, publicBase.LinkBase);
builder.AddGatewayHosting(composition);
builder.Services
    .AddGatewayConfiguration(composition)
    .AddGatewayCore(composition)
    .AddGatewayLeadership(composition)
    .AddGatewayIdentity(composition)
    .AddGatewayRuntimes(composition)
    .AddGatewayPlant(composition)
    .AddGatewayBackground(composition)
    .AddGatewayMcp()
    .AddGatewayHttp(publicBase);
var app = builder.Build();
// the stopping callbacks in their order (transcript flush, "stopping" line, hand-over) and the plant's wait for the snapshot
app.UseGatewayLifetime();

// What the handlers below still capture, until C6 and C7 move them into services and endpoint modules: the container's singletons, and what
// the composition made outside it (the revocation generations and the nonces, read by handlers until C7).
var hub = app.Services.GetRequiredService<EventHub>();
var runner = app.Services.GetRequiredService<Runner>();
var plant = app.Services.GetRequiredService<Plant>();
var snapshots = app.Services.GetRequiredService<ISnapshotCoordinator>();
var qc = app.Services.GetRequiredService<QcHub>();
var messenger = app.Services.GetRequiredService<Messenger>();
var baseWatch = app.Services.GetRequiredService<BaseWatch>();
var revocationService = app.Services.GetRequiredService<RevocationService>();
var githubBudget = composition.GitHubBudget;
var transcripts = composition.Transcripts;
var leader = composition.Leader;
var revocations = composition.Revocations;
var runtimeNonces = composition.RuntimeNonces;
var sweepMinSeconds = composition.SweepMinSeconds;
var runtimes = app.Services.GetRequiredService<RuntimeRegistry>();
var runtimeManager = runtimes.CaeJob;
var cursorRuntime = runtimes.CursorRuntime;
var cursorManager = runtimes.Cursor;
var copilotSettings = app.Services.GetRequiredService<CopilotCloudSettings>();
var copilotCiRelease = runtimes.CopilotCiRelease;
var copilotCodes = runtimes.CopilotCodes;
var copilotRuntime = runtimes.CopilotRuntime;
var copilotManager = runtimes.Copilot;
var octopusRuntime = runtimes.OctopusRuntime;
var octopusManager = runtimes.Octopus;
ManagerCopilot? managerCopilot = null;   // the plant manager on demand (gateway/Runtimes/ManagerCopilot.cs), resolved below; null where it is not enabled

var seenDeliveries = new ConcurrentDictionary<string, DateTime>();
var started = DateTime.UtcNow;
string PluginRoot() => Path.GetFullPath(Path.Combine(scriptsDir, ".."));
IEnumerable<string> PluginFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(f => { var r = Path.GetRelativePath(root, f).Replace('\\', '/'); return r != ".plugin-version" && r != ".plugin-managed" && !r.Contains("/bin/") && !r.Contains("/obj/"); })
    .OrderBy(f => f, StringComparer.Ordinal);
string? pluginHashCache = null;
string PluginHash()
{
    if (pluginHashCache != null) return pluginHashCache;
    var root = PluginRoot(); var sb = new StringBuilder();
    foreach (var f in PluginFiles(root)) sb.Append(Path.GetRelativePath(root, f).Replace('\\', '/')).Append('\n').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
    return pluginHashCache = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
}

// The caller of a request (gateway/Http/CallerContext.cs, task C6): who it is, whom it acts as, its session's link. The handlers below keep these
// names until C7 moves them into endpoint modules.
Principal Who(HttpContext c) => CallerContext.Of(c).Who;
bool OperatorActive(HttpContext c) => CallerContext.Of(c).OperatorActive;
Principal Admitted(HttpContext c) => CallerContext.Of(c).Admitted;
string By(HttpContext c) => CallerContext.Of(c).By;

// the forwarded headers of a trusted proxy, the test-only cause of a request, then the authentication and admission (gateway/Http/, task C6)
app.UseGatewayPipeline();

if (ownerMode) qc.Map(app);
app.MapGet("/health", () => Results.Text("ok"));
// liveness for the kit's hourly Health report (it asks every app for /alive, then /): the process answers, nothing else is checked
app.MapGet("/alive", () => Results.Text("ok"));
// readiness: the revoked credentials are read (at once where there is nothing to read), so a session credential is answered without waiting
// (503 says what the start waits for, how many reads have failed and since when: {"ready":false,"waitingFor":"revocations","attempts":N,"since":"..."})
// the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5): loaded once (AddGatewayRuntimes); a broken one keeps /ready at 503
var prompts = app.Services.GetRequiredService<PromptStore>();
foreach (var pe in prompts.Errors) Console.Error.WriteLine($"prompts: {pe}");
app.MapGet("/ready", () => leader.Replaced ? Results.Json(new { ready = false, waitingFor = "nothing", replaced = true, reason = "a newer gateway process holds the lease" }, statusCode: 503)
    : prompts.Errors.Count > 0 ? Results.Json(new { ready = false, waitingFor = "prompts", errors = prompts.Errors }, statusCode: 503)
    : revocationService.IsLoaded ? Results.Text("ok") : Results.Json(new { ready = false, waitingFor = "revocations", attempts = revocationService.FailedAttempts, since = revocationService.Since.ToString("o") }, statusCode: 503));
app.MapGet("/", () => Results.Redirect("/dashboard"));
WorkerSessions? mcpSessionsRef = null;
var buildVersion = (typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
// commit: the source revision built into the informational version ("<version>+<sha>"), a full 40-hex commit or null; a hint for
// tools that compare releases (the Octopus release stays the authority: a reused pull request image carries the commit its pull
// request build was given, not master's)
var buildCommit = (typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion is { } iv && iv.Split('+') is [_, var rev] && System.Text.RegularExpressions.Regex.IsMatch(rev, "^[0-9a-f]{40}$") ? rev : null;
app.MapGet("/_build", () => Results.Json(new { name = "factory-worker-gateway", version = buildVersion, commit = buildCommit, pluginHash = PluginHash(), promptsHash = prompts.Hash, started, runtime = Environment.Version.ToString(), ownerMode, environment = environmentName }));
app.MapGet("/api/v1/status", (HttpContext c) =>
{
    if (Admitted(c).Kind != "admin") return Results.StatusCode(403);
    var o = new JsonObject { ["started"] = started, ["sweepSeconds"] = sweepSeconds, ["ownerMode"] = ownerMode, ["subscribers"] = hub.SubscriberCount, ["lastEventId"] = hub.LastId,
        ["pendingOffers"] = hub.PendingCount, ["lastSweep"] = hub.LastSweep, ["plant"] = plant.Status() };
    // the messaging and manager blocks (worker-messaging.md 8) only while either feature is on; a default deployment answers exactly as before
    if (messaging.On) foreach (var kv in MessagingBlocks(false).ToList()) o[kv.Key] = kv.Value?.DeepClone();
    if (messaging.OperatorEnabled) o["operator"] = plant.OperatorStatus();
    if (keeper.Enabled) o["keeper"] = plant.KeeperStatus();
    if (plant.ReplenishStatus() is { } rps) o["replenish"] = rps;
    if (ownerMode) o["baseUpdate"] = baseWatch.Status();
    if (ownerMode) o["releaseQueue"] = plant.ReleaseQueueStatus();
    if (ownerMode) o["hosts"] = plant.HostStatus();
    if (ownerMode) o["runtimes"] = RuntimesBlock();
    if (ownerMode) o["lease"] = plant.LeaseStatus();
    if (ownerMode && messaging.ManagerEnabled) o["managerActions"] = plant.ManagerActionsStatus(false);
    if (ownerMode) o["plantSnapshot"] = snapshots.Status();
    if (ownerMode) o["leader"] = leader.Status();
    // the rule scripts running and waiting, the processes and memory (gateway/RunnerGate.cs, issue 89)
    o["runner"] = runner.Status();
    // the GitHub budget and the sweep's passes (issue 92)
    o["github"] = githubBudget.Status();
    o["sweep"] = hub.SweepStatus(sweepMinSeconds);
    // the session transcripts: sessions held, how many have lines not yet saved to GitHub, saves and failures
    o["transcripts"] = transcripts.Status();
    if (ownerMode) o["revocations"] = revocationService.Status();
    return Results.Text(o.ToJsonString(), "application/json");
});
// the switch of the gateway's own back-merges (baseUpdate.autoUpdateBranch) at run time, operator token only; until the next restart
app.MapPost("/api/v1/base-update", (HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Results.StatusCode(403);
    if (bool.TryParse(c.Request.Query["autoUpdateBranch"].ToString(), out var on)) baseWatch.AutoUpdateBranch = on;
    return Results.Text(baseWatch.Status().ToJsonString(), "application/json");
});

// Cycle-time metrics (enhancement 13), operator token and the active operator session (read-only): computed by the rule script `metrics` from the
// items' journal comments and state comments on GitHub, so they survive a restart. since: an ISO date or 30m, 24h, 7d (default 7d, at most 90d);
// max: items read, newest first (default 100, at most 300); fresh=1 skips the cache. One computation reads every item's comments: answers are cached
// per since|max for metricsCacheSeconds and computed one at a time. A failed computation (any exit but 0 and 2, a timeout included) is cached too,
// for metricsFailSeconds, for every caller except the operator token's fresh=1: a caller that retries cannot make the gateway read GitHub again and
// again. The operator session may ask only since=24h or 7d (none is read as 7d, the script's default), with no max and no fresh: its answers come
// from two cache entries, so it causes at most one computation per entry in 120 s while they succeed, and one in 30 s while they fail.
var metricsCache = new Dictionary<string, (DateTime At, int Code, string Json)>();
var metricsGate = new SemaphoreSlim(1, 1);
const int metricsCacheSeconds = 120, metricsFailSeconds = 30;
static bool MetricsFresh((DateTime At, int Code, string Json) e) => (DateTime.UtcNow - e.At).TotalSeconds < (e.Code is 0 or 2 ? metricsCacheSeconds : metricsFailSeconds);
static int MetricsStatus(int code) => code == 0 ? 200 : code == 2 ? 400 : code == 20 ? 503 : 502;
app.MapGet("/api/v1/metrics", async (HttpContext c) =>
{
    // the operator, and the active supervisor for its daily summary (docs/design/plant-manager.md 10e): a step of its hourly budget, like a read verb
    var mw = Admitted(c);
    if (mw.Kind == "manager")
    {
        if (!ownerMode || !messaging.ManagerEnabled || plant.ActiveSupervisor().Name != mw.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
        // the supervisor reads the cached answers of two windows only: it never forces a recomputation over every item's comments
        if (c.Request.Query["fresh"].ToString() == "1" || c.Request.Query["max"].ToString() != "" || c.Request.Query["since"].ToString() is not ("" or "24h" or "7d"))
            return Fail(400, "usage", "the supervisor reads since=24h or since=7d (no fresh, no max)", 2);
        if (plant.SupervisorStep(false) is { Ok: false } noStep) return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
    }
    else if (mw.Kind == "keeper")
    {   // the backlog keeper: the cached answers of two windows (the median time in Development, for the oversized-item policy)
        if (!ownerMode || plant.ActiveKeeper() != mw.Name) return Fail(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
        if (c.Request.Query["fresh"].ToString() == "1" || c.Request.Query["max"].ToString() != "" || c.Request.Query["since"].ToString() is not ("" or "24h" or "7d"))
            return Fail(400, "usage", "the backlog keeper reads since=24h or since=7d (no fresh, no max)", 2);
    }
    else if (mw.Kind != "admin") return Results.StatusCode(403);
    var since = c.Request.Query["since"].ToString().Trim(); var maxText = c.Request.Query["max"].ToString().Trim();
    if (OperatorActive(c))
    {
        if (since is not ("" or "24h" or "7d") || maxText != "" || c.Request.Query.ContainsKey("fresh"))
            return Results.Json(new { ok = false, code = "usage", reason = "the operator session reads the metrics with since=24h or 7d (or none), no max and no fresh: answers come from a 120 s cache and a miss computes them", exit = 2 }, statusCode: 400);
        if (since == "") since = "7d";   // the script's default: one cache entry for both spellings
    }
    if (since.Length > 40 || maxText.Length > 4 || (maxText != "" && !int.TryParse(maxText, out _))) return Results.Json(new { ok = false, code = "usage", reason = "since is an ISO date or 30m, 24h, 7d; max a number", exit = 2 }, statusCode: 400);
    var key = since + "|" + maxText;
    var fresh = c.Request.Query["fresh"].ToString() == "1";
    // a cached answer does not wait behind a computation that is running
    lock (metricsCache) if (!fresh && metricsCache.TryGetValue(key, out var hit0) && MetricsFresh(hit0)) return Results.Text(hit0.Json, "application/json", Encoding.UTF8, MetricsStatus(hit0.Code));
    await metricsGate.WaitAsync(c.RequestAborted);
    try
    {
        lock (metricsCache) if (!fresh && metricsCache.TryGetValue(key, out var hit) && MetricsFresh(hit)) return Results.Text(hit.Json, "application/json", Encoding.UTF8, MetricsStatus(hit.Code));
        var args = new JsonObject(); if (since != "") args["since"] = since; if (maxText != "") args["max"] = int.Parse(maxText);
        var (code, output) = await runner.RunAsync("metrics", args.ToJsonString(), TimeSpan.FromMinutes(3));
        lock (metricsCache)
        {
            foreach (var k in metricsCache.Where(kv => !MetricsFresh(kv.Value)).Select(kv => kv.Key).ToList()) metricsCache.Remove(k);
            // a failed fresh=1 of the operator token never replaces a good entry
            if (!fresh || code is 0 or 2 || !metricsCache.ContainsKey(key)) metricsCache[key] = (DateTime.UtcNow, code, output);
        }
        return Results.Text(output, "application/json", Encoding.UTF8, MetricsStatus(code));
    }
    finally { metricsGate.Release(); }
});

app.MapGet("/dashboard", () => Results.Text(DashboardPage.Html, "text/html; charset=utf-8"));
// The plugin (skills, agents, scripts) as a tar.gz for a worker that cannot reach the source: a hand-made cloud session pulls exactly the
// version this gateway runs. Needs a worker credential, the supervisor's credential (its connect prompt downloads the same plugin: the scripts and
// the factory-supervisor skill) or the operator token; nothing outside the plugin directory is included. The archive
// carries .plugin-version (content hash) and .plugin-managed (marks a downloaded copy, the only kind `factory.ps1 plugin-sync` ever updates).
app.MapGet("/api/v1/plugin", (HttpContext c) =>
{
    var who = Who(c);
    if (who.Kind is not ("admin" or "worker" or "manager" or "operator" or "keeper")) return Results.StatusCode(403);
    var root = PluginRoot();
    if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "scripts", "factory.ps1"))) return Results.NotFound();
    var ms = new MemoryStream();
    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
    using (var tw = new System.Formats.Tar.TarWriter(gz, leaveOpen: false))
    {
        foreach (var f in PluginFiles(root))
            tw.WriteEntry(f, Path.GetRelativePath(root, f).Replace('\\', '/'));
        void Add(string name, string text) => tw.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        Add(".plugin-version", JsonSerializer.Serialize(new { hash = PluginHash(), gatewayVersion = buildVersion }));
        Add(".plugin-managed", $"downloaded from this gateway on {DateTime.UtcNow:o}; factory.ps1 plugin-sync may replace it\n");
    }
    ms.Position = 0;
    return Results.File(ms, "application/gzip", "factory-worker-plugin.tar.gz");
});

// The messaging and manager blocks of /status and of the dashboard JSON (worker-messaging.md 7, 8): the plant's states and counts with the
// Messenger's counters. /status carries counts and states only; the dashboard adds the last digest and the decision log for the operator's page.
// The operator SESSION credential is admitted only at the endpoints that call Admitted or OperatorActive (gateway/Http/CallerContext.cs says how).
JsonObject MessagingBlocks(bool dashboard)
{
    var b = plant.MessagingStatus(dashboard); messenger.AddCounters((JsonObject)b["messaging"]!);
    // the on-demand manager (ManagerCopilot.cs): present while its task runs, with the launch counts and the task's link
    if (managerCopilot != null && b["manager"] is JsonObject mb)
    {
        var mc = managerCopilot.Status(); mb["copilot"] = mc;
        if (mc["present"]?.GetValue<bool>() == true) mb["present"] = true;
    }
    return b;
}
app.MapGet("/api/v1/dashboard", (HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Results.StatusCode(403);
    var o = new JsonObject { ["environment"] = environmentName, ["ownerMode"] = ownerMode, ["url"] = publicUrl != "" ? publicUrl : $"http://{c.Request.Host}", ["started"] = started,
        ["lastSweep"] = hub.LastSweep, ["plant"] = plant.Status() };
    if (messaging.On) foreach (var kv in MessagingBlocks(true).ToList()) o[kv.Key] = kv.Value?.DeepClone();
    if (messaging.OperatorEnabled) o["operator"] = plant.OperatorStatus();
    if (keeper.Enabled) o["keeper"] = plant.KeeperStatus();
    if (plant.ReplenishStatus() is { } rps) o["replenish"] = rps;
    if (ownerMode) o["baseUpdate"] = baseWatch.Status();
    if (ownerMode) o["releaseQueue"] = plant.ReleaseQueueStatus();
    if (ownerMode) o["hosts"] = plant.HostStatus();
    if (ownerMode) o["lease"] = plant.LeaseStatus();
    if (ownerMode && (runtimeManager != null || cursorManager != null || copilotManager != null || octopusManager != null)) o["runtimes"] = RuntimesBlock();
    if (ownerMode) o["plantSnapshot"] = snapshots.Status();
    if (ownerMode) o["leader"] = leader.Status();
    return Results.Text(o.ToJsonString(), "application/json");
});
// POST /release-queue/promotion-request { "release": "1.2.3" } (enhancement 9): the dashboard's button. Operator token only. It records the request
// and announces it on the operator stream; it never promotes: uat and prod belong to the owner and the cmdemo1 session.
app.MapPost("/api/v1/release-queue/promotion-request", async (HttpContext c) =>
{
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (Who(c).Kind != "admin") return Fail(403, "forbidden", "only the operator asks for a promotion", 30);
    var (text, tooLarge) = await ReadBody(c, 4096);
    JsonObject body; try { body = tooLarge || string.IsNullOrWhiteSpace(text) ? new JsonObject() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is { \"release\": \"<version>\" } or empty", 2); }
    var (status, res) = plant.RequestPromotion(body["release"]?.ToString(), "operator");
    if (status == 200 && res["already"]?.GetValue<bool>() == false)
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "promotion-requested", ["release"] = res["release"]!.DeepClone(), ["environment"] = "uat", ["by"] = By(c) }.ToJsonString());
    return Results.Text(res.ToJsonString(), "application/json", Encoding.UTF8, status);
});
// A worker credential is issued here once per session name; the operator pastes it into the connect prompt. It is never stored by the gateway.
// role=manager issues the supervisor's credential (worker-messaging.md 4.6): only while manager.enabled, and only for a name in manager.allowedNames.
app.MapPost("/api/v1/workers/{name}/credential", (string name, HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Results.StatusCode(403);
    var role = c.Request.Query["role"].ToString() is { Length: > 0 } r ? r : "worker";
    if (!Auth.ValidName(name) || tokenSecret.Length == 0 || role is not ("worker" or "manager" or "operator" or "keeper"))
        return Results.Json(new { ok = false, code = "usage", reason = "a valid name, a role of worker, manager or operator and FACTORY_TOKEN_SECRET are required", exit = 2 }, statusCode: 400);
    if (role == "keeper")
    {   // the backlog keeper's credential (Keeper.cs): only while keeper.enabled, and only for a name in keeper.allowedNames
        if (!keeper.Enabled) return Results.Json(new { ok = false, code = "keeper-disabled", reason = "the backlog keeper is not enabled on this gateway (keeper.enabled)", exit = 30 }, statusCode: 403);
        if (!keeper.Names.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in keeper.allowedNames", exit = 10 }, statusCode: 409);
        return Results.Json(new { ok = true, name, token = Auth.KeeperToken(tokenSecret, name, keeper.TokenGeneration, revocations.Of(name)) });
    }
    if (role == "operator")
    {   // the factory operator session's credential: only while operator.enabled, and only for a name in operator.allowedNames
        if (!messaging.OperatorEnabled) return Results.Json(new { ok = false, code = "operator-disabled", reason = "the operator session is not enabled on this gateway (operator.enabled)", exit = 30 }, statusCode: 403);
        if (!messaging.OperatorNames.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in operator.allowedNames", exit = 10 }, statusCode: 409);
        return Results.Json(new { ok = true, name, token = Auth.OperatorToken(tokenSecret, name, messaging.OperatorTokenGeneration, revocations.Of(name)) });
    }
    if (role == "manager")
    {
        if (!messaging.ManagerEnabled) return Results.Json(new { ok = false, code = "manager-disabled", reason = "the supervisor is not enabled on this gateway (manager.enabled)", exit = 30 }, statusCode: 403);
        if (!messaging.ManagerNames.Contains(name)) return Results.Json(new { ok = false, code = "name-not-allowed", reason = "the name is not in manager.allowedNames", exit = 10 }, statusCode: 409);
        return Results.Json(new { ok = true, name, token = Auth.ManagerToken(tokenSecret, name, messaging.TokenGeneration, revocations.Of(name)) });
    }
    if (messaging.Reserved.Contains(name)) return Results.Json(new { ok = false, code = "name-reserved", reason = "the name is reserved for the operator, the gateway or the supervisor", exit = 10 }, statusCode: 409);
    return Results.Json(new { ok = true, name, token = Auth.WorkerToken(tokenSecret, name, revocations.Of(name)) });
});
// Revoke (operator only): every credential minted so far for this name (worker, supervisor, item tokens) is refused from now on, on every endpoint;
// its open stream ends; a credential minted afterwards works. The new generation is written to GitHub before it takes effect here, so it holds
// across a restart. A name that never connected is revoked all the same (a credential may have been minted for it).
app.MapPost("/api/v1/workers/{name}/revoke", async (string name, HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Fail(403, "forbidden", "only the operator revokes a name's credentials", 30);
    if (!ownerMode || tokenSecret.Length == 0) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (!Auth.ValidName(name)) return Fail(400, "usage", "the name may use letters, digits, - and _ only (40 at most)", 2);
    if (revocationService.Off) return Fail(403, "revocations-off", "this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot revoke", 30);
    if (revocationService.SkipReason != "") return Fail(409, "revocations-unavailable", $"revocations are not read on this gateway ({revocationService.SkipReason}): it cannot revoke", 30);
    if (!revocationService.IsLoaded) return Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the revoked credentials; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
    var (rok, gen, closed) = await revocationService.RevokeAsync(name, "operator");
    if (!rok) return Results.Json(new { ok = false, code = "not-recorded", reason = "the revocation could not be written to GitHub, so nothing was revoked; try again", retryAfterSeconds = 15, exit = 20 }, statusCode: 503);
    // the execution of a runtime worker is stopped with its name (cloud-worker-runtimes.md 5)
    if (runtimeManager != null && runtimeManager.IsRuntimeWorker(name)) _ = Task.Run(() => runtimeManager.Deprovision(name, "revoked"));
    if (cursorManager != null && cursorManager.IsRuntimeWorker(name)) _ = Task.Run(() => cursorManager.Deprovision(name, "revoked"));
    if (copilotManager != null && copilotManager.IsRuntimeWorker(name)) _ = Task.Run(() => copilotManager.Deprovision(name, "revoked"));
    if (octopusManager != null && octopusManager.IsRuntimeWorker(name)) _ = Task.Run(() => octopusManager.Deprovision(name, "revoked"));
    _ = Task.Run(() => mcpSessionsRef?.End(name, "revoked") ?? Task.CompletedTask);
    return Results.Json(new { ok = true, name, generation = gen, streamClosed = closed, exit = 0 });
});
// both adapters in one block: the cae-job block as before (or {enabled:false}), and cursor-cloud beside it when it is on
JsonObject RuntimesBlock()
{
    var o = runtimeManager?.Status() ?? new JsonObject { ["enabled"] = false };
    if (cursorManager?.Status()["cursor-cloud"] is { } cb) o["cursor-cloud"] = cb.DeepClone();
    if (octopusManager?.Status()["octopus"] is JsonObject ob && octopusRuntime != null)
    {
        var oc = (JsonObject)ob.DeepClone();
        foreach (var (k, v) in octopusRuntime.Status()) oc[k] = v?.DeepClone();
        o["octopus"] = oc;
    }
    if (copilotManager?.Status()["copilot-cloud"] is JsonObject pb)
    {
        var pc = (JsonObject)pb.DeepClone();
        pc["ciRelease"] = copilotCiRelease.Status(copilotSettings.CiRelease, copilotSettings.CiReleaseSeconds);
        o["copilot-cloud"] = pc;
    }
    return o;
}
// The operator marks a runtime healthy again after it was marked unhealthy (cloud-worker-runtimes.md 2.11): operator token only.
app.MapPost("/api/v1/runtimes/{kind}/reset", (string kind, HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Fail(403, "forbidden", "only the operator resets a runtime", 30);
    var rk = runtimes.ByKind(kind);
    if (rk == null) return Fail(404, "no-runtime", "no such runtime is switched on", 12);
    rk.ResetHealth();
    return Results.Json(new { ok = true, runtime = kind, exit = 0 });
});
// the revocation itself is RevocationService.RevokeAsync: a runtime revokes a failed execution's name through it
if (runtimeManager != null) runtimeManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (cursorManager != null) cursorManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (copilotManager != null) copilotManager.Revoke = async n => { copilotCodes.Forget(n); await revocationService.RevokeAsync(n, "runtime"); };
if (octopusManager != null) octopusManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
// The reconcile loop of the Octopus runbook runtime (docs/design/octopus-runbook-runtime.md 6): the scheduled runbook "Reconcile hosts" asks, per host,
// which workers should run there and reports the fw-* containers it found. Authenticated by the host token (FACTORY_OCTOPUS_HOST_TOKEN, compared in
// constant time) only: never the operator token, never a worker credential. The answers name workers, never a credential.
string HostBearer(HttpContext c) { var a = c.Request.Headers.Authorization.ToString(); return a.StartsWith("Bearer ", StringComparison.Ordinal) ? a[7..].Trim() : ""; }
app.MapGet("/api/v1/runtime/octopus/desired", async (HttpContext c) =>
{
    if (octopusRuntime == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
    if (!octopusRuntime.HostTokenOk(HostBearer(c))) return Fail(401, "unauthorized", "missing or wrong host token", 30);
    var host = c.Request.Query["host"].ToString().Trim();
    if (host.Length is 0 or > 200) return Fail(400, "usage", "?host=<machine id or name> is required", 2);
    JsonObject? d; try { d = await octopusRuntime.Desired(host, c.RequestAborted); } catch { return Fail(503, "not-ready", "the runtime has not read back its workers yet; remove nothing and try again", 20); }
    return d == null ? Fail(404, "unknown-host", "the host is not a deployment target of the runtime", 12) : Results.Text(d.ToJsonString(), "application/json");
});
app.MapPost("/api/v1/runtime/octopus/report", async (HttpContext c) =>
{
    if (octopusRuntime == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
    if (!octopusRuntime.HostTokenOk(HostBearer(c))) return Fail(401, "unauthorized", "missing or wrong host token", 30);
    var (text, tooLarge) = await ReadBody(c, 65536);
    JsonObject body; try { body = tooLarge ? throw new FormatException() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is { \"host\": \"<machine>\", \"containers\": [{ \"name\", \"item\", \"state\", \"startedAt\" }] }", 2); }
    var host = body["host"]?.ToString().Trim() ?? "";
    if (host.Length is 0 or > 200 || body["containers"] is not JsonArray cs) return Fail(400, "usage", "host and containers are required", 2);
    var rr = await octopusRuntime.Report(host, cs, c.RequestAborted);
    return rr == null ? Fail(404, "unknown-host", "the host is not a deployment target of the runtime", 12) : Results.Text(rr.ToJsonString(), "application/json");
});

app.MapPost("/webhooks/github", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream(); await ctx.Request.Body.CopyToAsync(ms);
    var raw = ms.ToArray();
    var sig = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
    var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), raw)).ToLowerInvariant();
    if (!FixedEquals(sig, expected)) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"webhook\":\"bad-signature\"}}"); return Results.StatusCode(401); }
    var ev = ctx.Request.Headers["X-GitHub-Event"].ToString();
    var delivery = ctx.Request.Headers["X-GitHub-Delivery"].ToString();
    if (ev == "ping") return Results.Ok("pong");
    foreach (var k in seenDeliveries.Where(p => p.Value < DateTime.UtcNow.AddMinutes(-10)).Select(p => p.Key).ToList()) seenDeliveries.TryRemove(k, out _);
    if (delivery != "" && !seenDeliveries.TryAdd(delivery, DateTime.UtcNow)) return Results.Accepted();
    if (ev is not ("issues" or "workflow_run" or "projects_v2_item" or "pull_request" or "pull_request_review")) return Results.Accepted();
    var input = new JsonObject { ["event"] = ev, ["delivery"] = delivery, ["payload"] = JsonNode.Parse(raw) };
    var (code, output) = await runner.RunAsync("ingest-event", input.ToJsonString(), TimeSpan.FromSeconds(60));
    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"webhook\":\"{ev}\",\"delivery\":\"{delivery}\",\"exit\":{code}}}");
    if (code == 0 && JsonNode.Parse(output)?["events"] is JsonArray events)
    {
        foreach (var e in events)
        {
            if (e is not JsonObject eo) continue;
            hub.Publish(eo.ToJsonString());
            // master moved (a pull request merged into the default branch), or an item's branch got a new head: the back-merge trigger (BaseUpdate.cs)
            if (eo["event"]?.ToString() == "base.merged") baseWatch.OnMerged(eo["pr"] is JsonValue bp && bp.TryGetValue<int>(out var bpn) ? bpn : 0);
            if (eo["event"]?.ToString() is "pr.head_changed" or "pr.opened" && eo["item"] is JsonValue hv && hv.TryGetValue<int>(out var hn)) baseWatch.NoteHeadChange(hn);
            if (ownerMode && eo["item"] is JsonNode itn && itn.GetValueKind() == JsonValueKind.Number)
            {
                var item = itn.GetValue<int>();
                plant.NoteWebhook(item, eo["event"]?.ToString() ?? ev, eo["ref"]?.ToString() ?? delivery);
                plant.Route(item, (JsonObject)JsonNode.Parse(eo.ToJsonString())!);
                // a review of the PR: ask GitHub what it says now and wake the owner with qc.approved when a listed person approved the current head
                if (eo["event"]?.ToString() == "pr.reviewed") { var evo = (JsonObject)JsonNode.Parse(eo.ToJsonString())!; _ = Task.Run(async () => { try { await qc.OnReview(evo); } catch (Exception ex) { Console.Error.WriteLine($"qc review: {ex.Message}"); } }); }
            }
        }
        // each event's item gets an incremental pass; an event of no item (master moved, a build of master) asks for a full sweep (issue 92)
        // a pull request event also asks for a full sweep (still at most every sweepMinSeconds): the QC offers and teardowns are made only there
        foreach (var e in events) if (e is JsonObject eo2)
        {
            if (eo2["item"] is JsonValue iv2 && iv2.TryGetValue<int>(out var ii2) && ii2 > 0) hub.RequestSweep(ii2); else hub.RequestSweep();
            if (ownerMode && eo2["event"]?.ToString() is "pr.opened" or "pr.head_changed" or "pr.closed" or "pr.merged") hub.RequestSweep();
        }
    }
    return Results.Accepted();
});

app.MapPost("/api/v1/verbs/{verb}", async (string verb, HttpContext ctx) =>
{
    if (verb.Any(ch => !(char.IsAsciiLetterLower(ch) || ch == '-'))) return Results.BadRequest(new { ok = false, code = "bad-verb", exit = 2 });
    string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
    JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Results.BadRequest(new { ok = false, code = "bad-json", exit = 2 }); }
    var who = Who(ctx);
    var (authorized, refusal) = Auth.AuthorizeVerb(who, verb, body);
    if (authorized == null)
    {
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":{JsonSerializer.Serialize(refusal)}}}");
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "forbidden", reason = refusal, exit = 30 }), "application/json", Encoding.UTF8, 403);
    }
    if (verb == "git-credential" && who.Kind == "item" && !(ownerMode && plant.OwnsAccepted(who.Name, who.Item)))
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "not-owner", reason = $"this session does not own item #{who.Item} through an accepted assignment: no git credential", exit = 10 }), "application/json", Encoding.UTF8, 409);
    // the rule scripts of a revocation run only inside POST /workers/{name}/revoke, which keeps the stored generation and this process in step
    if (verb is "revoke-worker" or "revocations")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/workers/{name}/revoke", exit = 2 }), "application/json", Encoding.UTF8, 400);
    // likewise the release records of the operator session: only POST /api/v1/operator/record-release keeps the stored refs and this process in step
    if (verb is "plant-snapshot-save" or "plant-snapshot-load" or "plant-snapshot-clear" or "plant-snapshot-await" or "leader-lease" or "transcript-save" or "transcript-load" or "transcript-prune")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the plant snapshot is written and read by the gateway itself, at its stop and its start", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "operator-release" or "operator-releases")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/operator/record-release", exit = 2 }), "application/json", Encoding.UTF8, 400);
    // a person releases a park early only through POST /api/v1/items/{n}/unpark (operator token, with a reason)
    if (verb is "ci-release")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway releases the Copilot agent's held workflow runs by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "park-item")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway parks an item runtime-failed by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "copilot-owner")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway records a Copilot task's ownership of an item by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "copilot-settle")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway settles a Copilot Development task by itself, at its launch and its end", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "unpark-item")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/items/{n}/unpark (client: factory.ps1 unpark --item N --reason \"...\")", exit = 2 }), "application/json", Encoding.UTF8, 400);
    if (verb is "resettle-item")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/items/{n}/resettle", exit = 2 }), "application/json", Encoding.UTF8, 400);
    // the demo video arrives as a file: only POST /api/v1/demo/{item} streams it to the place the rule script reads it from
    if (verb is "publish-demo")
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)", exit = 2 }), "application/json", Encoding.UTF8, 400);
    // the backlog keeper (Keeper.cs): only the name that holds the slot; the day's limit of new items before a verb that creates one; a pin
    // "next-free" resolved to the worker the plan would offer an unpinned item to
    if (who.Kind == "keeper")
    {
        if (!ownerMode || plant.ActiveKeeper() != who.Name) return Fail(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
        // flags are JSON booleans and children a JSON array: the rule verb reads exactly what was counted here
        foreach (var bf in new[] { "follow_up", "unpark", "remove", "chain" })
            if (authorized[bf] is { } bv && bv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return Fail(400, "usage", $"{bf} is true or false", 2);
        if (authorized["children"] is { } cv && cv is not JsonArray) return Fail(400, "usage", "children is a JSON array", 2);
        var need = verb switch
        {
            "create-item" => 1,
            "split-item" => authorized["children"] is JsonArray ka ? ka.Count : 2,
            "amend-criteria" => authorized["follow_up"] is JsonValue fu && fu.TryGetValue<bool>(out var fb) && fb ? 1 : 0,
            _ => 0
        };
        if (need > 0 && plant.KeeperItemsLeft() < need)
            return Results.Json(new { ok = false, verb, code = "daily-item-limit", reason = $"the backlog keeper may create {keeper.MaxNewItemsPerDay} new items in 24 hours; this would need {need} and {plant.KeeperItemsLeft()} are left", retryAfterSeconds = 3600, exit = 20 }, statusCode: 429);
        if (verb == "create-item" && authorized["pin"]?.ToString() == "next-free")
        {
            var free = plant.NextFreeWorker();
            if (free == "") return Fail(409, "no-free-worker", "no connected worker has a free slot for a pin of next-free: create it without a pin, or name the worker", 10);
            authorized["pin"] = free;
        }
    }
    // every verb call of the supervisor is a step of its hourly budget (worker-messaging.md 3.9); at the budget its read verbs answer 429 step-budget
    // (only the active supervisor spends steps: another supervisor name's credential is refused before anything is counted)
    if (ownerMode && who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name)
    {
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"not-supervisor\"}}");
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "not-supervisor", reason = "this credential is not the active supervisor: register with subscribe --own --supervisor first", exit = 30 }), "application/json", Encoding.UTF8, 403);
    }
    if (ownerMode && who.Kind == "manager" && plant.SupervisorStep(false) is { Ok: false } noStep)
    {
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"step-budget\"}}");
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }), "application/json", Encoding.UTF8, 429);
    }
    // the plant manager's flow verb (docs/design/plant-manager.md 10e, Manager.cs): one action per call, by the active supervisor or the operator token
    // (Auth.cs refuses every other credential). Checked here, before anything changes: the mode (advise turns every action off), the action, the policy
    // code, the reason, and the limit per hour over every item. A name action (drain, resume, restart-session) is the plant's and is answered here; an
    // item action runs as the rule verb, which checks the owner's hold and the limit per item on the item itself.
    object? mgrTicket = null;
    if (verb == "manager-action")
    {
        if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
        var mAction = authorized["action"]?.ToString() ?? ""; var mPolicy = authorized["policy"]?.ToString() ?? ""; var mWorker = authorized["worker"]?.ToString() ?? "";
        var mBy = who.Kind == "manager" ? who.Name : "operator"; authorized["by"] = mBy;
        // the Copilot session of the on-demand manager (ManagerCopilot.cs) goes into the journal line it causes
        var mSession = who.Kind == "manager" ? managerCopilot?.SessionUrl(who.Name) ?? "" : "";
        if (mSession != "") authorized["session"] = mSession; else authorized.Remove("session");
        // one line, no HTML comment markers (the journal's machine-read marker), no mention: as the rule verb cleans it, for the name actions too
        var mReason = authorized["reason"] is JsonValue mrv && mrv.TryGetValue<string>(out var mrs) ? System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(mrs.Replace("://", " "), @"[<>@`\[\]!]", ""), @"\s+", " ").Trim() : "";
        IResult MgrNo(int status, string code, string reason, int exit, int retry = 0)
        {
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = Auth.ValidTag(mAction) ? mAction : "", ["by"] = mBy, ["refused"] = code }.ToJsonString());
            return Results.Text(retry > 0 ? JsonSerializer.Serialize(new { ok = false, verb, code, reason, retryAfterSeconds = retry, exit }) : JsonSerializer.Serialize(new { ok = false, verb, code, reason, exit }), "application/json", Encoding.UTF8, status);
        }
        if (!Auth.ManagerItemActions.Contains(mAction) && !Auth.ManagerNameActions.Contains(mAction))
            return MgrNo(400, "usage", $"action is one of {string.Join(", ", Auth.ManagerItemActions.Concat(Auth.ManagerNameActions))}", 2);
        if (!plant.MgrActions.Policies.Contains(mPolicy)) return MgrNo(400, "unknown-policy", $"the policy code is one of manager.actions.policies: {string.Join(", ", plant.MgrActions.Policies)}", 2);
        if (mReason.Length > 300 || (authorized["reason"] is { } rn && rn.GetValueKind() != JsonValueKind.String)) return MgrNo(400, "usage", "reason is text of at most 300 characters", 2);
        if (mReason != "" && messenger.LooksSecret(mReason)) return MgrNo(409, "secret-in-message", "the reason looks like a credential or token; it goes into a journal comment and never carries a secret", 10);
        if (plant.ManagerMode() == "advise") return MgrNo(409, "advise-mode", "the plant manager is in advise mode (manager.mode, set by the owner): it changes nothing; report to the operator instead", 10);
        if (Auth.ManagerNameActions.Contains(mAction))
        {
            if (!Auth.ValidName(mWorker) || messaging.Reserved.Contains(mWorker)) return MgrNo(400, "usage", "worker is the name of a worker session", 2);
            if (mAction is "restart-session" or "drain" && plant.ManagerRuntimeOwned(0, mWorker)) return MgrNo(409, "runtime-owner", "that session is a runtime task that is still running; the runtime manager ends it past its deadline", 10);
            var (nt, nra, ncd) = plant.ManagerActionBegin(mAction, 0, mWorker, mPolicy, mBy);
            if (nt == null && ncd == "name-action-limit") return MgrNo(409, "name-action-limit", $"the plant manager drained or restarted {mWorker} {plant.MgrActions.PerItemPerDay} times in 24 hours: leave it to the operator", 10, nra);
            if (nt == null) return MgrNo(429, "action-limit", $"the plant manager's limit of {plant.MgrActions.PerHour} actions an hour is reached; leave the rest to the operator", 20, nra);
            (int Exit, string Code, string Reason, List<int> Items) nr;
            if (mAction == "restart-session") nr = plant.ManagerRestart(mWorker, mPolicy);
            else { var dr = plant.ManagerDrain(mWorker, mAction == "drain"); nr = (dr.Exit, dr.Code, dr.Reason, new List<int>()); }
            var (nx, ncode, nreason, items) = nr;
            plant.ManagerActionDone(nt, nx == 0, ncode, mWorker);
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = mAction, ["worker"] = mWorker, ["policy"] = mPolicy, ["by"] = mBy, ["exit"] = nx }.ToJsonString());
            if (nx != 0) return Results.Text(nx == 20 ? JsonSerializer.Serialize(new { ok = false, verb, action = mAction, code = ncode, reason = nreason, retryAfterSeconds = 60, exit = nx }) : JsonSerializer.Serialize(new { ok = false, verb, action = mAction, code = ncode, reason = nreason, exit = nx }), "application/json", Encoding.UTF8, nx == 12 ? 404 : nx == 20 ? 503 : 409);
            if (mAction == "restart-session")
                foreach (var oi in items)
                    plant.JournalManagerAction(oi, new JsonObject { ["action"] = mAction, ["policy"] = mPolicy, ["by"] = mBy, ["reason"] = mReason, ["session"] = mSession, ["detail"] = $"restarted the session of **{mWorker}**, which owns this item; its next session resumes it" }, $"restart|{mWorker}|{DateTime.UtcNow.Ticks}");
            hub.RequestSweep();
            var mstate = plant.IsDraining(mWorker);
            return Results.Text(JsonSerializer.Serialize(new { ok = true, verb, action = mAction, worker = mWorker, policy = mPolicy, draining = mstate, items, exit = 0 }), "application/json");
        }
        if (!(authorized["item"] is JsonValue miv && miv.TryGetValue<int>(out var mItem) && mItem > 0)) return MgrNo(400, "usage", "item is a work item number", 2);
        // a pin names a session this gateway knows (registered since its start): a typo would hold the item for nobody
        if (mAction == "pin" && !(Auth.ValidName(mWorker) && plant.KnowsWorker(mWorker))) return MgrNo(404, "no-such-worker", "pin names a worker session that has registered with this gateway", 12);
        authorized["reason"] = mReason;
        // a runtime task (Copilot, Cursor, CAE job) that still runs is neither stalled nor disconnected: the manager leaves its owner alone
        if (mAction is "release-owner" or "requeue" && plant.ManagerRuntimeOwned(mItem, "")) return MgrNo(409, "runtime-owner", "the owner of this item is a runtime task that is still running; the runtime manager ends it past its deadline", 10);
        var (it0, ira, _) = plant.ManagerActionBegin(mAction, mItem, mWorker, mPolicy, mBy);
        if (it0 == null) return MgrNo(429, "action-limit", $"the plant manager's limit of {plant.MgrActions.PerHour} actions an hour is reached; leave the rest to the operator", 20, ira);
        mgrTicket = it0;
    }
    // plant data for the digest (worker-messaging.md 3.9): a verb called with an item token is that session's activity on that item
    if (ownerMode && messaging.On && who.Kind == "item") plant.NoteActivity(who.Name, who.Item, true);
    // text that a session writes into an item because of a message is marked by the gateway, not by the model (worker-messaging.md 3.8)
    if (ownerMode && messaging.On && verb is "append-body" or "comment" && DerivedText(verb, authorized, who) is { } derivedRefusal) return derivedRefusal;
    if (ownerMode && QcHub.Handled.Contains(verb))
    {   // the verbs that touch the in-memory half of a QC environment (and Start, Reset, Teardown, which place jobs)
        var (qs, qj) = await qc.HandleVerb(verb, authorized, who);
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"item\":\"{authorized["item"]}\",\"holder\":\"{authorized["holder"]}\",\"caller\":\"{who.Kind}:{who.Role}\",\"status\":{qs}}}");
        return Results.Text(qj, "application/json", Encoding.UTF8, qs);
    }
    // below the reserve of the GitHub budget only the essential verbs run (GitHubBudget.cs, issue 92): the rest wait for the reset
    if (!GitHubBudget.EssentialVerbs.Contains(verb) && githubBudget.Low() is { Low: true } gl)
    {
        githubBudget.CountVerbRefusal();
        // the owner is at work and is told to wait: that is no quiet item (gateway/Lease.cs)
        if (ownerMode && who.Kind == "item" && who.Role == "owner") { try { plant.NoteItemCall(who.Name, who.Item, ""); } catch { } }
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"github-budget\",\"resource\":\"{gl.Resource}\"}}");
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({gl.Resource}) is below its reserve; only accept, claim-item, release and my-items run until the reset", retryAfterSeconds = Math.Max(1, (int)(gl.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = gl.ResetAt.ToString("o"), exit = 20 }), "application/json", Encoding.UTF8, 503);
    }
    var timeout = TimeSpan.FromSeconds(verb is "wait-for-checks" ? 140 : 580);
    // issue 30: only the offer that stands for this session now can be accepted (a late accept of a withdrawn offer is refused, exit 11)
    var accepting = false;
    if (ownerMode && verb == "accept" && who.Kind == "item")
    {
        var (ax, acode, areason) = plant.BeginAccept(who);
        if (ax != 0)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"accept\",\"item\":\"{who.Item}\",\"caller\":\"{who.Kind}:{who.Role}\",\"refused\":\"{acode}\"}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = acode, reason = areason, exit = ax }), "application/json", Encoding.UTF8, 409);
        }
        accepting = true;
        // a runtime worker's acceptance carries its execution's page (a Copilot task's session), for the item's journal
        if (!(authorized["url"] is JsonValue) && plant.RuntimeUrlOf(who.Name) is { Length: > 0 } ru) authorized["url"] = ru;
    }
    if (ownerMode && who.Role == "qc" && verb is "accept" or "qc-progress" or "qc-fail" or "qc-stopped") qc.Arrived(who);
    // the AI session that calls (a worker, role, manager or keeper credential): the rule scripts end every comment they write for it with a link
    // to that session (Board.ps1 Add-Comment). Never a token: the page of the gateway, or the runtime's own page of the agent or task.
    authorized.Remove("_session");
    if (CallerContext.Of(ctx).SessionLinkOf(who) is { } sl) authorized["_session"] = sl;
    var (code, output) = await runner.RunAsync(verb, authorized.ToJsonString(), timeout);
    if (mgrTicket != null)
    {   // the plant's part of an item action of the plant manager, after its rule script changed the item (Manager.cs)
        JsonObject? mo = null; try { mo = JsonNode.Parse(output) as JsonObject; } catch { }
        plant.ManagerActionDone(mgrTicket, code == 0, code == 0 ? "" : mo?["code"]?.ToString() ?? $"exit-{code}");
        // the journal line of an action can be dropped (the item's journal is full, or GitHub refused it): the gateway's log says so
        if (code == 0 && mo?["journaled"] is JsonValue jv && jv.TryGetValue<bool>(out var jb) && !jb)
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = authorized["action"]?.ToString(), ["item"] = authorized["item"]?.DeepClone(), ["journaled"] = false }.ToJsonString());
        if (code == 0 && mo != null && int.TryParse(authorized["item"]?.ToString(), out var mi))
        {
            try
            {
                switch (authorized["action"]?.ToString())
                {
                    case "unpark":
                        if (mo["reoffer"] is JsonValue rov && rov.TryGetValue<bool>(out var rob) && rob) plant.OnVerb("release", new JsonObject { ["item"] = mi }, who, new JsonObject());
                        plant.ClearParks(new[] { mi });
                        break;
                    case "release-owner": if (mo["holder"]?.ToString() is { } mh && Auth.ValidName(mh)) plant.ManagerReleased(mi, mh); break;
                    case "requeue": plant.ManagerRequeued(mi); break;
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"plant hook manager-action: {e.Message}"); }
            hub.RequestSweep();
        }
    }
    var item = authorized["item"]?.ToString() ?? ""; var holder = authorized["holder"]?.ToString() ?? "";
    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"item\":\"{item}\",\"holder\":\"{holder}\",\"caller\":\"{who.Kind}:{who.Role}\",\"exit\":{code}}}");
    if (string.IsNullOrWhiteSpace(output)) output = JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = code == 0 ? 20 : code });
    var status = code switch { 0 => 200, 21 => 202, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };
    // a gate refusal of one code on one item, for the digest's refusals ring (worker-messaging.md 3.9)
    if (ownerMode && messaging.On && code is 10 or 11 && who.Kind == "item") { try { plant.NoteRefusal(who.Name, who.Item, JsonNode.Parse(output)?["code"]?.ToString() ?? ""); } catch { } }
    // a verb that failed for an item (the gateway or GitHub could not serve it, or the rule script crashed), for the digest's verb-errors (3.9)
    if (ownerMode && messaging.On && status is 503 or 500 && who.Kind == "item") { string failed = ""; try { failed = JsonNode.Parse(output)?["code"]?.ToString() ?? ""; } catch { } plant.NoteVerbError(who.Name, who.Item, failed, code); }
    if (ownerMode && keeper.Enabled && verb == "complete" && code == 0 && int.TryParse(authorized["item"]?.ToString(), out var kpi)) { try { if (JsonNode.Parse(output)?["parked"] is JsonValue kpv && kpv.TryGetValue<bool>(out var kpb) && kpb) plant.KeeperPark(kpi, "worker-failure", authorized["column"]?.ToString() ?? ""); } catch { } }
    if (ownerMode && keeper.Enabled && who.Kind == "keeper" && code == 0) { try { var ko = JsonNode.Parse(output); plant.KeeperCreated((ko?["created"] as JsonArray)?.Count ?? (ko?["follow_up"] is JsonValue fv && fv.TryGetValue<int>(out _) ? 1 : 0)); } catch { } }
    if (ownerMode && messaging.OperatorEnabled) { try { plant.OperatorVerbEvent(verb, code, who, authorized, output); } catch { } }
    if (ownerMode && code == 0) { try { plant.OnVerb(verb, authorized, who, (JsonNode.Parse(output) as JsonObject) ?? new JsonObject()); } catch (Exception e) { Console.Error.WriteLine($"plant hook {verb}: {e.Message}"); } }
    if (accepting) plant.EndAccept(who.Assignment);
    // issue 31: a session that calls a verb successfully is answering: its count of lapsed offers starts again. Issue 100: not the accept its stream
    // client makes by itself (accepted_by client): a blocked session's client accepts every offer, and that is no sign that the session works
    if (ownerMode && code == 0 && who.Kind is "item" or "worker" && !(verb == "accept" && authorized["accepted_by"]?.ToString() == "client")) plant.NoteAnswered(who.Name);
    // any call of the owner for its item is its activity and renews the lease when due (gateway/Lease.cs); after OnVerb, which records an accept
    if (ownerMode && who.Kind == "item" && who.Role == "owner") { try { JsonObject? vr = null; try { vr = JsonNode.Parse(output) as JsonObject; } catch { } plant.NoteItemCall(who.Name, who.Item, verb, authorized, vr, code == 0); } catch (Exception e) { Console.Error.WriteLine($"lease hook {verb}: {e.Message}"); } }
    if (ownerMode && code == 0 && (verb is "qc-progress" or "qc-fail" or "qc-stopped" || (verb == "accept" && who.Role == "qc"))) { try { await qc.AfterVerb(verb, authorized, who, (JsonNode.Parse(output) as JsonObject) ?? new JsonObject()); } catch (Exception e) { Console.Error.WriteLine($"qc hook {verb}: {e.Message}"); } }
    if ((code == 0 || code == 21) && verb is "complete" or "validation-verdict" or "claim-item" or "accept" or "settle" or "merge" or "approve-pr") hub.RequestSweep(int.TryParse(item, out var swItem) ? swItem : 0);
    return Results.Text(output, "application/json", Encoding.UTF8, status);
});

// A runtime worker's one-time nonce (cloud-worker-runtimes.md 2.6) is redeemed here for its w2, once. No Authorization header: the nonce is the
// credential. Body {"nonce": "n1.<hex>"}. Every refusal is the same 401 for the caller (unknown, spent, expired); the log says which.
// The Copilot cloud agent's exchange (docs/design/copilot-cloud-agent.md part 3): the bootstrap token of the work-orders repository's Copilot MCP
// configuration (b1, api.copilot.bootstrapGeneration) and a task's one-time code give that task's r1, once. Neither alone grants anything: the
// b1 authenticates no other endpoint, and a code is worth nothing without it. Answers the same 401 for a bad token and a bad code.
app.MapPost("/api/v1/copilot/exchange", async (HttpContext ctx) =>
{
    if (!Auth.VerifyCopilotBootstrap(tokenSecret, ctx.Request.Headers.Authorization.ToString(), copilotSettings.BootstrapGeneration))
        return Results.Json(new { ok = false, code = "unauthorized", reason = "missing or void bootstrap token", exit = 30 }, statusCode: 401);
    var (text, tooLarge) = await ReadBody(ctx, 1000);
    string? code = null; if (!tooLarge) try { code = (JsonNode.Parse(text) as JsonObject)?["code"]?.ToString(); } catch { }
    if (copilotCodes.Redeem(code) is not { } got)
    {
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "exchange-refused" }.ToJsonString());
        return Results.Json(new { ok = false, code = "unauthorized", reason = "unknown, spent or expired code", exit = 30 }, statusCode: 401);
    }
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "exchanged", ["worker"] = got.Name }.ToJsonString());
    return Results.Json(new { ok = true, name = got.Name, token = got.Token, exit = 0 });
});
// The bootstrap token itself (operator only), for the repository's Agents secret COPILOT_MCP_FWORKER_BOOTSTRAP_TOKEN; never stored here.
app.MapPost("/api/v1/copilot/bootstrap-token", (HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Fail(403, "forbidden", "only the operator mints the Copilot bootstrap token", 30);
    if (!ownerMode || tokenSecret.Length == 0) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    return Results.Json(new { ok = true, generation = copilotSettings.BootstrapGeneration, token = Auth.CopilotBootstrapToken(tokenSecret, copilotSettings.BootstrapGeneration), exit = 0 });
});
// fworker-mcp (gateway/Mcp/fworker-mcp.mjs): the stdio MCP proxy the Copilot agent runs, served anonymously (it holds no secret; the repository is public)
app.MapGet("/fworker-mcp.mjs", () =>
{
    using var st = typeof(CopilotCodes).Assembly.GetManifestResourceStream("fworker-mcp.mjs");
    if (st == null) return Results.NotFound();
    using var rd = new StreamReader(st);
    return Results.Text(rd.ReadToEnd(), "text/javascript", Encoding.UTF8);
});
// The transcript of a runtime worker's session (Transcripts.cs, runner/fwrunner.sh): the container posts its redacted lines with its own credential
// (w2 of a worker, r1 of a manager patrol); a credential posts to its own name only. {lines:[...]}: at most 500 lines and 256 KiB a batch.
app.MapPost("/api/v1/runtime/transcript", async (HttpContext ctx) =>
{
    var who = Who(ctx);
    if (who.Kind is not ("worker" or "manager") || !TranscriptStore.ValidName(who.Name)) return Fail(403, "forbidden", "a runtime session posts its own transcript with its own credential", 30);
    var buf = new byte[256 * 1024 + 1]; var n = 0; int r0;
    while (n < buf.Length && (r0 = await ctx.Request.Body.ReadAsync(buf.AsMemory(n, buf.Length - n))) > 0) n += r0;
    if (n > 256 * 1024) return Fail(413, "too-large", "at most 256 KiB a batch", 2);
    List<string> lines = [];
    try { if ((JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, n)) as JsonObject)?["lines"] is JsonArray a) lines = a.Select(x => x?.ToString() ?? "").ToList(); } catch { }
    if (lines.Count == 0) return Fail(400, "usage", "the body is {\"lines\": [\"...\"]}", 2);
    if (lines.Count > transcripts.MaxBatchLines) return Fail(413, "too-large", $"at most {transcripts.MaxBatchLines} lines a batch", 2);
    var (taken, total) = await transcripts.Append(who.Name, lines);
    if (total < 0) return Results.Json(new { ok = false, code = "busy", reason = "the transcript store is full of unsaved sessions; try again", retryAfterSeconds = 30, exit = 20 }, statusCode: 503);
    return Results.Json(new { ok = true, taken, total, exit = 0 });
});
// The operator reads a session's transcript: after=N answers the lines from number N on (the page polls with it)
app.MapGet("/api/v1/sessions/{name}", async (HttpContext ctx, string name) =>
{
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "session transcripts are read with the operator token", 30);
    if (!TranscriptStore.ValidName(name)) return Fail(400, "usage", "not a session name", 2);
    var after = int.TryParse(ctx.Request.Query["after"], out var af) && af > 0 ? af : 0;
    var o = await transcripts.Read(name, after);
    if (o == null) return Fail(404, "not-found", "no transcript of this session (none was posted, or it is older than the retention)", 12);
    o["exit"] = 0;
    return Results.Text(o.ToJsonString(), "application/json");
});
// The page of a session: the operator token from #token=<value> or this tab's connection to the dashboard; live while the session runs
app.MapGet("/sessions/{name}", (string name) => TranscriptStore.ValidName(name) ? Results.Text(SessionPage.Html, "text/html; charset=utf-8") : Results.NotFound());

app.MapPost("/api/v1/runtime/redeem", async (HttpContext ctx) =>
{
    if (!revocationService.IsLoaded)
    {
        await Task.WhenAny(revocationService.Loaded, Task.Delay(TimeSpan.FromSeconds(20), ctx.RequestAborted));
        if (!revocationService.IsLoaded) return Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the revoked credentials; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
    }
    string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
    if (text.Length > 1000) return Results.Json(new { ok = false, code = "usage", reason = "the body is {\"nonce\": \"...\"}", exit = 2 }, statusCode: 400);
    string? nonce = null; try { nonce = (JsonNode.Parse(text) as JsonObject)?["nonce"]?.ToString(); } catch { }
    var (name, rt, expires, error) = runtimeNonces.Redeem(nonce);
    if (error != null || tokenSecret.Length == 0)
    {
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "redeem-refused", ["code"] = error ?? "no-token-secret" }.ToJsonString());
        return Results.Json(new { ok = false, code = "unauthorized", reason = "unknown, spent or expired nonce", exit = 30 }, statusCode: 401);
    }
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "redeemed", ["worker"] = name, ["kind"] = rt, ["expires"] = expires }.ToJsonString());
    return Results.Json(new { ok = true, name, runtime = rt, expires, token = Auth.WorkerToken2(tokenSecret, name, rt, expires, revocations.Of(name)), exit = 0 });
});

// The plant manager's actions of the last 24 hours, its mode and its limits (Manager.cs): the operator, and the active supervisor (a step of its budget).
app.MapGet("/api/v1/manager/actions", (HttpContext ctx) =>
{
    var who = Who(ctx);
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (who.Kind == "manager")
    {
        if (!messaging.ManagerEnabled || plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
        if (plant.SupervisorStep(false) is { Ok: false } noStep) return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
    }
    else if (who.Kind != "admin") return Fail(403, "forbidden", "the plant manager's actions are read by the supervisor and the operator", 30);
    var o = plant.ManagerActionsStatus(true); o["ok"] = true; o["exit"] = 0;
    return Results.Text(o.ToJsonString(), "application/json");
});
// The owner's switch of the plant manager (manager.mode): act, or advise (every action of manager-action is refused advise-mode). The operator token
// only, never the operator session or the supervisor; in memory, and an advise survives a graceful restart in the plant snapshot (an act does not:
// after a restart manager.mode of the configuration applies again).
app.MapPost("/api/v1/manager/mode", (HttpContext ctx) =>
{
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator sets the plant manager's mode", 30);
    var value = ctx.Request.Query["value"].ToString();
    if (value is not ("act" or "advise")) return Fail(400, "usage", "value is act or advise", 2);
    plant.SetManagerMode(value);
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerMode"] = value, ["by"] = "operator" }.ToJsonString());
    return Results.Json(new { ok = true, mode = value, exit = 0 });
});

// Release a park early (operator token only): after the owner relaxed or reworded a criterion, a person need not wait out timing.parkHours. The rule
// verb unpark-item removes the park from the item's state comment with the gateway's own GitHub author and journals the reason; the sweep that
// this call requests then offers the item again in its current column. Body: {"reason": "..."} (required, at most 500 characters).
app.MapPost("/api/v1/items/{n:int}/unpark", async (int n, HttpContext ctx) =>
{
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator releases a park", 30);
    string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
    JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Fail(400, "bad-json", "the body is JSON: {\"reason\": \"...\"}", 2); }
    var reason = body["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs.Trim() : "";
    if (reason.Length == 0 || reason.Length > 500) return Fail(400, "usage", "a reason of 1 to 500 characters is required", 2);
    var args = new JsonObject { ["item"] = n, ["reason"] = reason, ["holder"] = "operator", ["by"] = "the operator" };
    var (code, output) = await runner.RunAsync("unpark-item", args.ToJsonString(), TimeSpan.FromSeconds(120));
    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"unpark-item\",\"item\":\"{n}\",\"caller\":\"admin\",\"exit\":{code}}}");
    if (string.IsNullOrWhiteSpace(output)) output = JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = code == 0 ? 20 : code });
    if (code == 0)
    {   // no live claim: the session that held the item before the park is not its owner any more; the plant forgets that assignment (as for a
        // release), so the sweep this call requests offers the item afresh instead of first waiting to find that session gone
        try { if (ownerMode && JsonNode.Parse(output)?["reoffer"] is JsonValue ro && ro.TryGetValue<bool>(out var reoffer) && reoffer) plant.OnVerb("release", new JsonObject { ["item"] = n }, Who(ctx), new JsonObject()); }
        catch (Exception e) { Console.Error.WriteLine($"plant hook unpark-item: {e.Message}"); }
        if (ownerMode) plant.ClearParks(new[] { n });   // issue 82: the digest no longer reports a park that was released
        hub.RequestSweep(n);
    }
    var status = code switch { 0 => 200, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };
    return Results.Text(output, "application/json", Encoding.UTF8, status);
});

// Settle a merged item again (operator token only, issue 114): for an item an older gateway finished or stranded without a release, after a person
// reopened it and moved its card. The rule verb resettle-item sets the wait from the merge record and journals the reason; never a hand-off.
app.MapPost("/api/v1/items/{n:int}/resettle", async (int n, HttpContext ctx) =>
{
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator settles an item again", 30);
    string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
    JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Fail(400, "bad-json", "the body is JSON: {\"reason\": \"...\"}", 2); }
    var reason = body["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs.Trim() : "";
    if (reason.Length == 0 || reason.Length > 500) return Fail(400, "usage", "a reason of 1 to 500 characters is required", 2);
    var (code, output) = await runner.RunAsync("resettle-item", new JsonObject { ["item"] = n, ["reason"] = reason, ["holder"] = "operator", ["by"] = "the operator" }.ToJsonString(), TimeSpan.FromSeconds(120));
    Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"resettle-item\",\"item\":\"{n}\",\"caller\":\"admin\",\"exit\":{code}}}");
    if (string.IsNullOrWhiteSpace(output)) output = JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = code == 0 ? 20 : code });
    if (code == 0) hub.RequestSweep();
    var status = code switch { 0 => 200, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };
    return Results.Text(output, "application/json", Encoding.UTF8, status);
});

// The demo video of a `video` item (issue #42): the owner sends the MP4 as the raw request body with its item token; the gateway streams it to a
// temp file (never into memory, a log or the verb's JSON) and runs the rule verb publish-demo, which creates release demo-<item> if missing and
// uploads or replaces the asset with the gateway's GitHub credential. Not POST /api/v1/verbs: that route reads the body as text and pipes it to
// the rule script as JSON, and Kestrel caps a body at about 30 MB by default. The right to publish goes with the right to record the verdict
// (validation-verdict of the owner role) or an explicit publish-demo in the role's verb list. Not held under the plant's gate; bounded by a timeout.
const long DemoMaxBytes = 100L * 1024 * 1024;
// at most two uploads at once (disk: 2 x 100 MB, two runner slots); a third is refused busy, not queued
var demoGate = new SemaphoreSlim(2, 2);
app.MapPost("/api/v1/demo/{item:int}", async (int item, HttpContext ctx) =>
{
    IResult No(int status, string code, string reason, int exit) => Results.Text(JsonSerializer.Serialize(new { ok = false, verb = "publish-demo", code, reason, exit }), "application/json", Encoding.UTF8, status);
    var who = Who(ctx);
    var probe = new JsonObject { ["item"] = item };
    var (authorized, refusal) = Auth.AuthorizeVerb(who, "publish-demo", probe);
    if (authorized == null && who.Kind == "item") (authorized, refusal) = Auth.AuthorizeVerb(who, "validation-verdict", new JsonObject { ["item"] = item });
    if (authorized == null)
    {
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"publish-demo\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":{JsonSerializer.Serialize(refusal)}}}");
        return No(403, "forbidden", refusal ?? "forbidden", 30);
    }
    if (ctx.Request.ContentLength is > DemoMaxBytes) return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2);
    if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } lim) lim.MaxRequestBodySize = DemoMaxBytes + 1;
    if (!await demoGate.WaitAsync(0)) return Results.Text(JsonSerializer.Serialize(new { ok = false, verb = "publish-demo", code = "busy", reason = "another demo upload is running; retry", retryAfterSeconds = 30, exit = 20 }), "application/json", Encoding.UTF8, 429);
    var tmp = Path.Combine(Path.GetTempPath(), $"demo-{item}-{Guid.NewGuid():N}.mp4");
    try
    {
        try { if (new DriveInfo(Path.GetTempPath()).AvailableFreeSpace < 2 * DemoMaxBytes) return No(507, "no-space", "the gateway has too little free space for a video now; retry later", 20); } catch (Exception) { }
        long size = 0; var head = new byte[12]; var headLen = 0;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
        {
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            await using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            var buf = new byte[81920]; int n;
            try
            {
                while ((n = await ctx.Request.Body.ReadAsync(buf, cts.Token)) > 0)
                {
                    size += n; if (size > DemoMaxBytes) return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2);
                    if (headLen < head.Length) { var k = Math.Min(head.Length - headLen, n); Array.Copy(buf, 0, head, headLen, k); headLen += k; }
                    await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
                }
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2); }
            catch (OperationCanceledException) { return No(408, "upload-timeout", "the video did not arrive within 10 minutes", 20); }
        }
        // an MP4 (ISO base media file) starts with a box whose type, bytes 4 to 7, is "ftyp"
        if (size == 0) return No(400, "empty", "the request body holds no video: send the MP4 file as the body", 2);
        if (headLen < 8 || Encoding.ASCII.GetString(head, 4, 4) != "ftyp") return No(415, "not-mp4", "the file is not an MP4 (its header has no ftyp box)", 2);
        authorized["file"] = tmp; authorized["size"] = size;
        if (ownerMode && who.Kind == "item") plant.NoteActivity(who.Name, who.Item, true);
        var (code, output) = await runner.RunAsync("publish-demo", authorized.ToJsonString(), TimeSpan.FromMinutes(10));
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"publish-demo\",\"item\":\"{item}\",\"holder\":\"{authorized["holder"]}\",\"caller\":\"{who.Kind}:{who.Role}\",\"bytes\":{size},\"exit\":{code}}}");
        if (string.IsNullOrWhiteSpace(output)) output = JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = code == 0 ? 20 : code });
        var status = code switch { 0 => 200, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, 30 => 403, _ => 500 };
        return Results.Text(output, "application/json", Encoding.UTF8, status);
    }
    finally { try { File.Delete(tmp); } catch { } demoGate.Release(); }
});

// from_message on append-body and comment (worker-messaging.md 3.8): the id must be a message the caller sent or received; the text loses backticks
// and HTML comment markers, is capped at 500 characters and becomes one marked, quoted block before the rule script runs. Without from_message the
// literal marker is refused, so a session can neither forge a block nor close one early. Returns the refusal, or null when the call goes on.
IResult? DerivedText(string verb, JsonObject body, Principal who)
{
    var field = verb == "append-body" ? "content" : "body";
    IResult No(int status, string code, string reason, int exit)
    {
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"{code}\"}}");
        return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code, reason, exit }), "application/json", Encoding.UTF8, status);
    }
    var text = body[field] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
    if (!body.ContainsKey("from_message") || body["from_message"] is null)
        return text != null && text.Contains("aisf:assumption", StringComparison.OrdinalIgnoreCase)
            ? No(409, "reserved-marker", "the text carries the marker aisf:assumption, which only the gateway writes: text that comes from a message goes in with --from-message <id>", 10) : null;
    var mid = body["from_message"]!.ToString();
    if (who.Kind != "item" || text == null || !System.Text.RegularExpressions.Regex.IsMatch(mid, @"^m-[0-9a-f]{8}-\d{4,}\z"))
        return No(400, "usage", "from_message is the id of a message this session sent or received, given with the session's item token and the text", 2);
    var (known, source, from, inReplyTo) = plant.MessageParty(mid, who.Name);
    if (!known) return No(404, "no-such-message", "from_message names no message that this session sent or received in this run of the gateway", 12);
    var clean = text.Replace("`", "").Replace("\r", "");
    while (clean.Contains("<!--") || clean.Contains("-->")) clean = clean.Replace("<!--", "").Replace("-->", "");
    var capped = new StringBuilder(); var count = 0;
    foreach (var rune in clean.Trim().EnumerateRunes()) { if (count++ >= 500) break; capped.Append(rune.ToString()); }
    clean = capped.ToString().Trim();
    if (clean.Length == 0) return No(400, "usage", "there is no text left to write: send the assumption or the decision in your own words", 2);
    // the block names the message whose text it carries and that message's sender; for an answer also the question it answers
    body[field] = $"<!-- aisf:assumption source={source} from={from}{(inReplyTo == "" ? "" : " in_reply_to=" + inReplyTo)} -->\n" + string.Join("\n", clean.Split('\n').Select(l => "> " + l)) + "\n<!-- /aisf:assumption -->";
    body.Remove("from_message");
    return null;
}

// Triggers for one item: a long-poll that costs no tokens while idle, for sessions that cannot hold a stream open (headless and cloud sessions).
app.MapGet("/api/v1/wait", async (HttpContext ctx) =>
{
    var who = Who(ctx);
    if (!int.TryParse(ctx.Request.Query["item"], out var item)) return Results.Text("{\"ok\":false,\"code\":\"usage\",\"reason\":\"item is required\",\"exit\":2}", "application/json", Encoding.UTF8, 400);
    if (who.Kind == "item" && who.Item != item) return Results.Text(JsonSerializer.Serialize(new { ok = false, code = "forbidden", reason = $"this token is for item #{who.Item} only", exit = 30 }), "application/json", Encoding.UTF8, 403);
    if (who.Kind == "worker") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"waiting for an item's triggers needs the item token\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
    if (who.Kind == "manager") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"a supervisor session cannot wait for an item's triggers\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
    if (who.Kind == "operator") return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"an operator session cannot wait for an item's triggers\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
    // then an allow-list (after the kinds above, which keep their own reasons): the operator token and the item's own token (a role token waits through next_event at /mcp)
    if (who.Kind is not ("admin" or "item")) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"waiting for an item's triggers needs the item token\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
    // an owner that waits in wait-event makes no verb call for hours: its poll is its activity on the item (worker-messaging.md 3.9)
    if (ownerMode && messaging.On && who.Kind == "item") plant.NoteActivity(who.Name, item);
    if (ownerMode && who.Kind == "item" && who.Role == "owner") plant.NoteItemCall(who.Name, item, "");
    long after = long.TryParse(ctx.Request.Query["after"], out var a) ? a : 0;
    var secs = Math.Clamp(double.TryParse(ctx.Request.Query["timeout"], out var t) ? t : 60, 1, 120);
    if (ctx.Request.Query["messages"].ToString() == "1")
    {   // the item's triggers and the caller's own new messages (worker-messaging.md 3.1, 3.5): wakes on either, and only for the item token of the session
        if (MessagingOff() is { } off) return off;
        if (who.Kind != "item" || who.Role is not ("owner" or "inspector")) return AsResult(Messenger.Plain("forbidden", "waiting for messages needs the item token of an owner or inspector session"));
        if (!(messaging.WorkerAllowed(who.Name) || (messaging.ManagerEnabled && messaging.AskerAllowed(who.Name)))) return AsResult(Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers"));
        plant.NotePoll(who.Name);
        var afterMsg = long.TryParse(ctx.Request.Query["afterMsg"], out var am) && am >= 0 ? am : 0;
        var epochQ = ctx.Request.Query["epoch"].ToString();
        // a cursor of another epoch is void (3.5): the caller's seqs started again, so nothing may be skipped
        if (epochQ != "" && epochQ != plant.Epoch) { afterMsg = 0; plant.NoteStaleEpoch(who.Name, epochQ); }
        var (evs, msgs, lastMsg, noneArrived) = await plant.WaitAny(item, after, who.Name, afterMsg, TimeSpan.FromSeconds(secs), ctx.RequestAborted);
        var changed = epochQ != "" && epochQ != plant.Epoch;
        var both = new JsonObject
        {
            ["ok"] = true, ["item"] = item, ["timeout"] = noneArrived, ["last"] = evs.Count > 0 ? evs[^1].Item1 : after,
            ["events"] = new JsonArray(evs.Select(e => JsonNode.Parse(e.Item2)).ToArray()),
            ["messages"] = new JsonArray(msgs.ToArray()), ["lastMsg"] = lastMsg == 0 ? afterMsg : lastMsg, ["epoch"] = plant.Epoch, ["epochChanged"] = changed
        };
        return Results.Text(both.ToJsonString(), "application/json");
    }
    var (events, timedOut) = await plant.Wait(item, after, TimeSpan.FromSeconds(secs), ctx.RequestAborted);
    var res = new JsonObject { ["ok"] = true, ["item"] = item, ["timeout"] = timedOut, ["last"] = events.Count > 0 ? events[^1].Item1 : after, ["events"] = new JsonArray(events.Select(e => JsonNode.Parse(e.Item2)).ToArray()) };
    return Results.Text(res.ToJsonString(), "application/json");
});

app.MapPost("/api/v1/workers/{name}/{action}", (string name, string action, HttpContext ctx) =>
{
    var who = Who(ctx);
    // the operator session acts here as the operator does for exactly four actions: a session's messaging mode, its capacity, drain and resume
    // (owner decision 2026-10-14). It never revokes or mints here (those are routes of their own, admin token only).
    var opMode = action is "messaging" or "capacity" or "drain" or "resume" && OperatorActive(ctx);
    var asOperator = who.Kind == "admin" || opMode;
    if (!(asOperator || (who.Kind == "worker" && who.Name == name))) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"a worker may only change itself\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
    if (action == "capacity")
    {   // the operator (or the active operator session): how many items this worker name may own at once
        if (!asOperator) return Results.Text("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"only the operator sets a worker's capacity\",\"exit\":30}", "application/json", Encoding.UTF8, 403);
        if (opMode && (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name))) return Fail(400, "usage", "the name is a worker session's", 2);
        // a name that has not registered since the gateway started gets no override from the session (the overrides are kept per name, so they
        // must not grow without bound)
        if (opMode && !plant.IsWorker(name)) return Results.Text("{\"ok\":false,\"code\":\"not-found\",\"reason\":\"no such worker\",\"exit\":12}", "application/json", Encoding.UTF8, 404);
        if (!int.TryParse(ctx.Request.Query["value"], out var cv) || cv < 1 || cv > 20) return Results.Text("{\"ok\":false,\"code\":\"usage\",\"reason\":\"value must be 1 to 20\",\"exit\":2}", "application/json", Encoding.UTF8, 400);
        plant.SetCapacity(name, cv);
        if (opMode) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["worker"] = "capacity", ["name"] = name, ["capacity"] = Math.Clamp(cv, 1, 20), ["by"] = By(ctx) }.ToJsonString());
        return Results.Text(JsonSerializer.Serialize(new { ok = true, name, capacity = Math.Clamp(cv, 1, 20) }), "application/json");
    }
    if (action == "messaging" && messaging.Configured && ownerMode)
    {   // operator only (worker-messaging.md 4.5): a worker acting on its own name is refused here, so a muted session cannot set itself back to on
        if (who.Kind != "admin" && !opMode) return Fail(403, "forbidden", "only the operator sets a session's messaging mode", 30);
        var mode = ctx.Request.Query["value"].ToString();
        if (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name) || !plant.SetMessagingMode(name, mode)) return Fail(400, "usage", "the name is a worker session's and value is on, mute, inbound-off or off", 2);
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "mode", ["name"] = name, ["mode"] = mode, ["by"] = By(ctx) }.ToJsonString());
        return Results.Json(new { ok = true, name, messaging = mode, exit = 0 });
    }
    if (action is not ("drain" or "resume")) return Results.NotFound();
    if (opMode && (!Auth.ValidName(name) || messaging.ManagerNames.Contains(name) || messaging.OperatorNames.Contains(name))) return Fail(400, "usage", "the name is a worker session's", 2);
    // the operator session's drain is the operator's: it belongs to the name and holds across its reconnects and new sessions (issue 80)
    if (!plant.SetDraining(name, action == "drain", asOperator)) return Results.Text("{\"ok\":false,\"code\":\"not-found\",\"reason\":\"no such worker\",\"exit\":12}", "application/json", Encoding.UTF8, 404);
    if (opMode) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["worker"] = action, ["name"] = name, ["by"] = By(ctx) }.ToJsonString());
    return Results.Text(JsonSerializer.Serialize(new { ok = true, name, draining = plant.IsDraining(name) }), "application/json");
});

app.MapGet("/api/v1/events", async (HttpContext ctx) =>
{
    var who = Who(ctx);
    var name = ctx.Request.Query["name"].ToString();
    var lastEventId = ctx.Request.Headers["Last-Event-ID"].ToString();
    long after = long.TryParse(lastEventId, out var l) ? l : -1;
    if (name != "")
    {   // a worker registers by opening its stream
        if (!ownerMode) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"not-owner-mode\",\"reason\":\"the gateway is not running in owner mode\",\"exit\":30}"); return; }
        if (!Auth.ValidName(name)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"usage\",\"reason\":\"the worker name may use letters, digits, - and _ only\",\"exit\":2}"); return; }
        // with messaging or the supervisor on the id is "<epoch>.<seq>"; another epoch, a bare number or no header is no valid cursor and replays all (3.5)
        if (messaging.On) { after = ParseCursor(lastEventId, plant.Epoch); if ((who.Kind is "worker" or "manager") && who.Name == name) plant.NoteStaleEpoch(name, lastEventId); }
        if (messaging.OperatorEnabled && ctx.Request.Query["operator"].ToString() == "1") { await OperatorStream(ctx, who, name, after); return; }
        if (keeper.Enabled && ctx.Request.Query["keeper"].ToString() == "1") { await KeeperStream(ctx, who, name, after); return; }
        if (who.Kind == "keeper") { await Refuse(ctx, 403, "forbidden", "the backlog keeper opens only its own stream (keeper=1)"); return; }
        if (messaging.ManagerEnabled && ctx.Request.Query["supervisor"].ToString() == "1") { await SupervisorStream(ctx, who, name, after); return; }
        if (who.Kind == "manager") { await Refuse(ctx, 403, "forbidden", "a supervisor session does not open a worker stream"); return; }
        if (!(who.Kind == "admin" || (who.Kind == "worker" && who.Name == name))) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"register with the credential issued for this worker name\",\"exit\":30}"); return; }
        if (messaging.Reserved.Contains(name)) { await Refuse(ctx, 409, "name-reserved", "the name is reserved for the operator, the gateway or the supervisor", 10); return; }
        var caps = ctx.Request.Query["capabilities"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var roles = ctx.Request.Query["roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var type = string.IsNullOrEmpty(ctx.Request.Query["type"]) ? "persistent-local" : ctx.Request.Query["type"].ToString();
        var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
        // the runtime is the credential's (cloud-worker-runtimes.md 4.1): a w2 names it, a w1 or the admin token is local; a claim that differs is refused
        var runtime = who.Kind == "worker" && who.Runtime != "" ? who.Runtime : "local";
        var claimedRuntime = ctx.Request.Query["runtime"].ToString();
        if (claimedRuntime != "" && claimedRuntime != runtime) { await Refuse(ctx, 403, "runtime-not-trusted", $"this credential registers runtime '{runtime}' only: a runtime is taken from the credential the gateway minted, never from the claim"); return; }
        if (!Auth.ValidRegistration(type, roles, caps, model, effort))
        { await Refuse(ctx, 400, "usage", "type, roles, capabilities (10 at most), model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
        ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
        var (w, ch, replay) = plant.Register(name, type, caps, int.TryParse(ctx.Request.Query["capacity"], out var cap) ? cap : 1, roles, after,
            int.TryParse(ctx.Request.Query["qc_capacity"], out var qcap) ? qcap : 1, ctx.Request.Query["qc_address"].ToString(), ctx.Request.Query["qc_profile"].ToString() is "1" or "true",
            messagingClient: ctx.Request.Query["messaging"].ToString() == "1", model: model, effort: effort,
            // a stream opened without a cursor is a new session of this name (a reconnect of the same client sends its last id): one item per session
            newSession: lastEventId == "",
            // a client that reconnects on stream.replaced with reconnect (issue 94); an older one is ended without it, and reconnects as after any drop
            handover: ctx.Request.Query["handover"].ToString() == "1", runtime: runtime);
        plant.SetHost(name, ctx.Request.Query["host"].ToString(), effort);
        // a new session can take a work item that waited as session-spent: plan now instead of at the next sweep (issue 72)
        if (lastEventId == "") hub.RequestSweep();
        var registered = new JsonObject { ["provider"] = "gateway", ["event"] = "worker.registered", ["name"] = name };
        if (messaging.On) { registered["epoch"] = plant.Epoch; registered["messaging"] = messaging.Enabled; }
        // one peers.snapshot per registration (3.2), only for a client that registered with messaging=1 under an allow-listed name
        var snapshot = messaging.Enabled && ctx.Request.Query["messaging"].ToString() == "1" && messaging.WorkerAllowed(name) ? plant.PeersSnapshot(name) : null;
        try { await Pump(ctx, ch, replay, registered.ToJsonString(), snapshot); }
        catch (OperationCanceledException) { }
        finally { plant.Disconnect(w, ch); }
        return;
    }
    if (who.Kind != "admin") { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"ok\":false,\"code\":\"forbidden\",\"reason\":\"the broadcast stream is for the operator\",\"exit\":30}"); return; }
    ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    var (sub, legacyReplay, pending) = hub.Subscribe(after);
    try
    {
        foreach (var (id, json) in legacyReplay) await ctx.Response.WriteAsync($"id: {id}\ndata: {json}\n\n");
        foreach (var json in pending) await ctx.Response.WriteAsync($"data: {json}\n\n");
        await ctx.Response.Body.FlushAsync();
        var lastPing = DateTime.UtcNow;
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); cts.CancelAfter(PingDue(lastPing));
            try { var (id, json) = await sub.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync($"id: {id}\ndata: {json}\n\n"); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
            if (PingDue(lastPing) == TimeSpan.Zero) { await ctx.Response.WriteAsync(": ping\n\n"); lastPing = DateTime.UtcNow; }
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { }
    finally { hub.Unsubscribe(sub); }
});

// POST /build-slot (gateway/Hosts.cs): a private-build slot of the caller's host, so the sessions of one computer do not build all at once.
// Body { "action": "acquire" | "release", "ttlSeconds": S }. The caller is an owner's item token (the item is the token's) or a worker credential.
// Exit 0 granted or released; exit 21 build-slot-busy with retryAfterSeconds (HTTP 429).
app.MapPost("/api/v1/build-slot", async (HttpContext ctx) =>
{
    var who = Who(ctx);
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (!(who.Kind == "worker" || (who.Kind == "item" && who.Role == "owner"))) return Fail(403, "forbidden", "a build slot is asked for with an owner's item token or a worker credential", 30);
    var (text, tooLarge) = await ReadBody(ctx, 4 * 1024);
    if (tooLarge) return Fail(413, "too-large", "the request body is over 4 KB", 2);
    JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? throw new FormatException()); } catch { return Fail(400, "usage", "the body is a JSON object: { \"action\": \"acquire\" | \"release\", \"ttlSeconds\": S }", 2); }
    var action = body["action"]?.ToString() ?? "acquire";
    if (action is not ("acquire" or "release")) return Fail(400, "usage", "action is acquire or release", 2);
    double? ttl = null;
    if (body["ttlSeconds"] is JsonNode tn) { if (!double.TryParse(tn.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) || !double.IsFinite(tv) || tv <= 0) return Fail(400, "usage", "ttlSeconds is a positive number", 2); ttl = tv; }
    var item = who.Kind == "item" ? who.Item : (int.TryParse(body["item"]?.ToString(), out var bi) && bi > 0 ? bi : 0);
    var r = plant.BuildSlotRequest(who.Name, item, action, ttl);
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["buildSlot"] = action, ["name"] = who.Name, ["item"] = item, ["ok"] = r["ok"]?.DeepClone() }.ToJsonString());
    return Results.Text(r.ToJsonString(), "application/json", Encoding.UTF8, r["ok"]?.GetValue<bool>() == true ? 200 : 429);
});

// POST /presence (worker-messaging.md 3.1, 3.2): the heartbeat of a session's own credential, with an optional activity summary. The stream client
// sends one every 30 s; it is what keeps a session online, because a write into its stream proves nothing about the client.
app.MapPost("/api/v1/presence", async (HttpContext ctx) =>
{
    var who = Who(ctx);
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (!messaging.Enabled) return Fail(403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled)", 30);
    // the operator session's heartbeat: the request itself stamped it (the middleware), and it has no summary and no directory entry
    if (who.Kind == "keeper") return plant.ActiveKeeper() == who.Name ? Results.Json(new { ok = true, presence = "online", epoch = plant.Epoch, exit = 0 }) : Fail(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
    if (who.Kind == "operator") return OperatorActive(ctx) ? Results.Json(new { ok = true, presence = "online", epoch = plant.Epoch, exit = 0 }) : Fail(403, "not-operator", "this credential is not the active operator session: register with subscribe --own --operator first", 30);
    if (who.Kind is not ("worker" or "item" or "manager")) return Fail(403, "forbidden", "presence is sent with a session's own credential", 30);
    if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return Fail(403, "role-cannot-send", "only the item tokens of an owner or an inspector session have presence (a QC token is pull request code's credential)", 30);
    if (who.Kind != "manager" && !messaging.WorkerAllowed(who.Name)) return Fail(403, "not-allowed", "this name is not in messaging.allowedWorkers", 30);
    if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
    var (text, tooLarge) = await ReadBody(ctx, 16 * 1024);
    if (tooLarge) return Fail(413, "too-large", "the request body is over 16 KB", 2);
    JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : Messenger.ParseObject(text); } catch { return Fail(400, "usage", "the body is a JSON object: {} or { \"summary\": \"...\" }", 2); }
    foreach (var kv in body)
    {
        if (kv.Key is "from" or "sender" or "holder" or "fromKind" or "role") return Fail(403, "forged-sender", "the sender comes from the credential; a body never names it", 30);
        if (kv.Key is not ("summary" or "progress")) return Fail(400, "usage", $"unknown field '{kv.Key}'", 2);
    }
    // local progress of owned items (gateway/Lease.cs rule 3): [{ "item": N, "ageSeconds": S, "source": "commit|files|process|marker|work" }], 20 at most;
    // the age (by the client's clock) and not a time, so a client whose clock is off still reports what is recent
    var progress = new List<(int Item, DateTime At, string Source)>();
    if (body["progress"] is JsonNode pn)
    {
        if (who.Kind == "manager") return Fail(400, "usage", "the supervisor owns no item: progress is sent by an owner's client", 2);
        if (pn is not JsonArray pa || pa.Count > 20) return Fail(400, "usage", "progress is an array of at most 20 { item, ageSeconds, source }", 2);
        foreach (var e in pa)
        {
            if (e is not JsonObject eo || !int.TryParse(eo["item"]?.ToString(), out var pi) || pi <= 0
                || !double.TryParse(eo["ageSeconds"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var age) || !double.IsFinite(age) || age < 0 || age > 86400
                || eo["source"]?.ToString() is not ("commit" or "files" or "process" or "marker" or "work"))
                return Fail(400, "usage", "each progress entry is { \"item\": N, \"ageSeconds\": 0 to 86400, \"source\": \"commit|files|process|marker|work\" }", 2);
            progress.Add((pi, DateTime.UtcNow.AddSeconds(-age), eo["source"]!.ToString()));
        }
    }
    string? summary = null;
    if (body["summary"] is JsonNode sn) { if (sn.GetValueKind() != System.Text.Json.JsonValueKind.String) return Fail(400, "usage", "summary is text", 2); summary = sn.GetValue<string>(); if (summary.Length > 140) return Fail(413, "too-large", "summary is 140 characters at most", 2); }
    // the summary is shown to every peer (3.2), so it passes the same credential check as a message (5.4)
    if (summary != null && messenger.LooksSecret(summary)) return Fail(409, "secret-in-message", "the summary looks like a credential or token; summaries never carry secrets (worker-messaging.md 5.4)", 10);
    var (presence, retry) = plant.Heartbeat(who.Name, summary);
    if (retry > 0) messenger.CountRefusal("rate-limited");
    if (retry > 0) return Results.Json(new { ok = false, code = "rate-limited", reason = "the summary changes at most once per 10 seconds", retryAfterSeconds = retry, exit = 20 }, statusCode: 429);
    // a summary that names owned items ("#N") renews their leases, and local progress counts as the owner's activity (gateway/Lease.cs)
    if (who.Kind is "worker" or "item") plant.NoteHeartbeat(who.Name);
    if (progress.Count > 0) return Results.Json(new { ok = true, presence, epoch = plant.Epoch, progress = plant.NoteProgress(who.Name, progress), exit = 0 });
    return Results.Json(new { ok = true, presence, epoch = plant.Epoch, exit = 0 });
});

// ---- messages (worker-messaging.md 3.1 to 3.7): send, state, inbox, acknowledgement, and the operator's traffic log. They are not /verbs: a worker
// credential may not call verbs, and a message never travels the path that decides items. The sender is the credential, never the body (4.2). ----
IResult? MessagingOff()
{
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (!messaging.Enabled) return Fail(403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled)", 30);
    return null;
}
static IResult AsResult(MsgResult r) => Results.Text(r.Body.ToJsonString(), "application/json", Encoding.UTF8, r.Status);
// Who has an inbox, and so may read it, acknowledge it and ask for the state of what it sent: a worker, the item token of an owner or an inspector,
// the supervisor. A name outside both lists has none; a QC item token never does (4.1).
MsgResult? InboxRefusal(Principal who)
{
    if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return Messenger.Plain("forbidden", $"an item token of role '{who.Role}' has no inbox");
    if (who.Kind is not ("worker" or "item" or "manager")) return Messenger.Plain("forbidden", "an inbox belongs to a session credential: a worker, an item token or the supervisor");
    if (who.Kind != "manager" && !(messaging.WorkerAllowed(who.Name) || (messaging.ManagerEnabled && messaging.AskerAllowed(who.Name)))) return Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers");
    // only the supervisor that holds the slot has an inbox (4.1, 4.6): another supervisor name's credential reads, acknowledges and stamps nothing
    if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Messenger.Plain("not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first");
    return null;
}

// GET /peers (3.2): the directory. A worker or the item token of an owner or inspector session sees the allow-listed sessions; the operator sees
// all; the supervisor is refused (the digest replaces it), as is a QC item token.
app.MapGet("/api/v1/peers", (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var who = Admitted(ctx);
    if (who.Kind == "manager") return AsResult(Messenger.Plain("forbidden", "the supervisor does not read the directory; the digest replaces it"));
    if (who.Kind == "item" && who.Role is not ("owner" or "inspector")) return AsResult(Messenger.Plain("forbidden", $"an item token of role '{who.Role}' cannot read the directory"));
    if (who.Kind is not ("admin" or "worker" or "item")) return AsResult(Messenger.Plain("forbidden", "the directory is read with a session credential or by the operator"));
    if (who.Kind != "admin" && !messaging.WorkerAllowed(who.Name)) return AsResult(Messenger.Plain("not-allowed", "this name is not in messaging.allowedWorkers"));
    var d = plant.Directory(who.Kind == "admin" ? null : who.Name, who.Kind == "item" && who.Role == "inspector", ctx.Request.Query["all"].ToString() == "1");
    return Results.Text(d.ToJsonString(), "application/json");
});

app.MapPost("/api/v1/messages", async (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
    return AsResult(await messenger.SendAsync(Admitted(ctx), tooLarge ? "" : text, tooLarge));
});

// GET /messages: the traffic log and the ring of refusals and flags, for the operator only (never a worker, never the supervisor)
app.MapGet("/api/v1/messages", (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    if (Admitted(ctx).Kind != "admin") return Fail(403, "forbidden", "the traffic log is for the operator only", 30);
    var q = ctx.Request.Query;
    var worker = q["worker"].ToString();
    int? item = int.TryParse(q["item"], out var it) ? it : null;
    var limit = int.TryParse(q["limit"], out var lm) ? Math.Clamp(lm, 1, 1000) : 200;
    var to = q["to"].ToString();
    if (to != "" && !Auth.ValidName(to)) return Fail(400, "usage", "to is a session name or operator", 2);
    return Results.Text(messenger.TrafficLog(worker == "" ? null : worker, item, limit, q["ring"].ToString() == "refused", to == "" ? null : to, q["unread"].ToString() == "1").ToJsonString(), "application/json");
});

// POST /messages/read: the operator marks notes addressed to it read (the operator has no inbox to acknowledge; this is its counterpart)
app.MapPost("/api/v1/messages/read", async (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var who = Admitted(ctx);
    if (who.Kind != "admin") return Fail(403, "forbidden", "only the operator marks the notes addressed to the operator read", 30);
    var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
    return AsResult(tooLarge ? messenger.TooLarge(who, "read") : messenger.MarkRead(text));
});

app.MapGet("/api/v1/messages/{id}", async (string id, HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var who = Who(ctx);
    if (who.Kind != "admin" && InboxRefusal(who) is { } no) return AsResult(no);
    var wait = double.TryParse(ctx.Request.Query["wait"], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w) ? w : 0;
    return AsResult(await messenger.StatusAsync(who, id, wait, ctx.RequestAborted));
});

app.MapPost("/api/v1/messages/ack", async (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var who = Who(ctx);
    if (who.Kind == "admin") return AsResult(Messenger.Plain("forbidden", "the operator has no inbox to acknowledge"));
    if (InboxRefusal(who) is { } no) return AsResult(no);
    var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
    return AsResult(tooLarge ? messenger.TooLarge(who, "ack") : messenger.Ack(who, text));
});

app.MapGet("/api/v1/inbox", async (HttpContext ctx) =>
{
    if (MessagingOff() is { } off) return off;
    var who = Who(ctx);
    if (who.Kind == "admin") return AsResult(Messenger.Plain("forbidden", "the operator has no inbox; the traffic log is GET /api/v1/messages"));
    if (InboxRefusal(who) is { } no) return AsResult(no);
    var q = ctx.Request.Query;
    long after = 0; if (q["after"].ToString() is { Length: > 0 } aq && (!long.TryParse(aq, NumberStyles.None, CultureInfo.InvariantCulture, out after))) return Fail(400, "usage", "after is a sequence number", 2);
    var timeout = double.TryParse(q["timeout"], NumberStyles.Float, CultureInfo.InvariantCulture, out var tq) && double.IsFinite(tq) ? tq : 0;
    var limit = int.TryParse(q["limit"], out var lq) ? lq : 50;
    var include = q["include"].ToString();
    if (include != "" && include != "delivered") return Fail(400, "usage", "include is delivered", 2);
    var idq = q["id"].ToString();
    var epoch = q["epoch"].ToString();
    if (epoch != "") plant.NoteStaleEpoch(who.Name, epoch);
    return AsResult(await messenger.InboxAsync(who.Name, after, epoch == "" ? null : epoch, timeout, limit, include == "delivered", idq == "" ? null : idq, ctx.RequestAborted));
});

// ---- questions (worker-messaging.md 3.1, 3.8): the supervisor's own queue and the operator's view of it, and the operator's answer -----------------
IResult? SupervisorOff()
{
    if (MessagingOff() is { } off) return off;
    return messaging.ManagerEnabled ? null : Fail(403, "manager-disabled", "the supervisor is not enabled on this gateway (manager.enabled)", 30);
}
app.MapGet("/api/v1/questions", (HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    var who = Admitted(ctx);
    if (who.Kind is not ("admin" or "manager")) return Fail(403, "forbidden", "the questions are read by the supervisor and the operator", 30);
    if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
    var state = ctx.Request.Query["state"].ToString();
    if (state is not ("" or "open" or "all")) return Fail(400, "usage", "state is open or all", 2);
    return Results.Text(plant.Questions(state == "all", who.Kind == "admin").ToJsonString(), "application/json");
});
app.MapPost("/api/v1/questions/{id}/answer", async (string id, HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    var who = Admitted(ctx);
    if (who.Kind != "admin") return Fail(403, "forbidden", "a question is answered here by the operator only; the supervisor answers with a message of type answer", 30);
    var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
    return AsResult(tooLarge ? messenger.TooLarge(who, "operator-note") : messenger.OperatorAnswer(id, text, who.Name));
});

// ---- the patrol and the supervisor's budgets (worker-messaging.md 3.1, 3.9) -------------------------------------------------------------------
// GET /digest: the digest of this moment for the active supervisor (after a restart or a compaction; the routine never polls it) and the operator.
app.MapGet("/api/v1/digest", (HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    var who = Admitted(ctx);
    if (who.Kind is not ("admin" or "manager")) return Fail(403, "forbidden", "the digest is read by the supervisor and the operator", 30);
    if (who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
    // for the supervisor it is a step of its hourly budget (3.9), like its read verbs
    if (who.Kind == "manager" && plant.SupervisorStep(false) is { Ok: false } noStep)
        return Results.Json(new { ok = false, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }, statusCode: 429);
    return Results.Text(new JsonObject { ["ok"] = true, ["digest"] = plant.DigestNow(), ["exit"] = 0 }.ToJsonString(), "application/json");
});
app.MapPost("/api/v1/supervisor/patrol-ack", async (HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    var who = Who(ctx);
    if (who.Kind != "manager" || plant.ActiveSupervisor().Name != who.Name) return Fail(403, "not-supervisor", "a patrol is acknowledged by the active supervisor only", 30);
    var (text, tooLarge) = await ReadBody(ctx, messaging.MaxBodyBytes);
    return AsResult(tooLarge ? messenger.TooLarge(who, "patrol-ack") : messenger.PatrolAck(who, text));
});
// the operator's two controls of the supervisor's cost: the patrol interval (5 to 240 minutes, in memory) and extra deliveries for one hour
app.MapPost("/api/v1/supervisor/patrol", (HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator sets the patrol interval", 30);
    if (!double.TryParse(ctx.Request.Query["minutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || !double.IsFinite(minutes) || minutes < 5 || minutes > 240)
        return Fail(400, "usage", "minutes is 5 to 240", 2);
    plant.SetPatrolMinutes(minutes);
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "patrol-interval", ["minutes"] = minutes, ["by"] = "operator" }.ToJsonString());
    return Results.Json(new { ok = true, patrolMinutes = minutes, exit = 0 });
});
app.MapPost("/api/v1/supervisor/budget", (HttpContext ctx) =>
{
    if (SupervisorOff() is { } off) return off;
    if (Admitted(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator grants deliveries", 30);
    if (!int.TryParse(ctx.Request.Query["extra"], NumberStyles.None, CultureInfo.InvariantCulture, out var extra) || extra < 1 || extra > 100) return Fail(400, "usage", "extra is 1 to 100 deliveries", 2);
    // the operator session may grant at most 12 deliveries in an hour, all its grants together: the person's grants are not capped
    if (OperatorActive(ctx) && !plant.OperatorGrant(extra)) return Results.Json(new { ok = false, code = "grant-limit", reason = "the operator session grants at most 12 extra deliveries in an hour; more needs the operator", retryAfterSeconds = 600, exit = 20 }, statusCode: 429);
    var total = plant.GrantDeliveries(extra);
    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = "budget-granted", ["extra"] = extra, ["by"] = By(ctx) }.ToJsonString());
    return Results.Json(new { ok = true, extra, extraThisHour = total, exit = 0 });
});

// POST /messaging/enable and /messaging/disable (worker-messaging.md 4.5): the operator's switch for the whole gateway, in memory. Only a gateway
// that was loaded with messaging on has the switch; elsewhere both answer messaging-disabled, as every messaging endpoint does there.
app.MapPost("/api/v1/messaging/{action}", (string action, HttpContext ctx) =>
{
    if (action is not ("enable" or "disable")) return Results.NotFound();
    if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
    if (!messaging.Configured) return Fail(403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled); the switch exists only where it is", 30);
    if (Who(ctx).Kind != "admin") return Fail(403, "forbidden", "only the operator switches messaging", 30);
    var changed = plant.SetMessagingEnabled(action == "enable");
    if (changed) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["message"] = action == "enable" ? "messaging-enabled" : "messaging-disabled", ["by"] = "operator" }.ToJsonString());
    return Results.Json(new { ok = true, enabled = messaging.Enabled, changed, exit = 0 });
});

// ---- the operator session's two enforced checks (Operator.cs, docs/design/operator-session.md). The release records are refs in GitHub, written
// by the rule scripts; they are read once at start (gateway/Operator/OperatorReleaseLoader.cs), and until that read succeeded the check answers
// 503 (it never answers "allowed" on a count it does not know). -----------------------------------------------------------------------------
var releaseLoader = app.Services.GetRequiredService<IOperatorReleaseLoader>();
var releaseGate = new SemaphoreSlim(1, 1);
IResult? OperatorOff(HttpContext ctx)
{
    if (!messaging.OperatorEnabled) return Fail(403, "operator-disabled", "the operator session is not enabled on this gateway (operator.enabled)", 30);
    if (MessagingOff() is { } off) return off;
    if (Admitted(ctx).Kind != "admin") return Fail(403, "forbidden", "this check belongs to the active operator session and the operator", 30);
    if (!releaseLoader.IsLoaded) return Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the recorded releases; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
    return null;
}
// GET /operator/release-check: 200 when the plant is quiet and the day's limit is not reached; otherwise 409 not-quiet (with every reason) or daily-limit.
app.MapGet("/api/v1/operator/release-check", (HttpContext ctx) =>
{
    if (OperatorOff(ctx) is { } off) return off;
    var r = plant.ReleaseCheck();
    return Results.Text(r.ToJsonString(), "application/json", Encoding.UTF8, r["ok"]!.GetValue<bool>() ? 200 : 409);
});
// POST /operator/record-release { "pr": N, "version": "1.0.N" }: one self-merged release, counted against operator.maxSelfMergesPerDay. A release
// is always recorded, also beyond the limit (the record is the truth; the next release-check refuses). Idempotent per pull request.
app.MapPost("/api/v1/operator/record-release", async (HttpContext ctx) =>
{
    if (OperatorOff(ctx) is { } off) return off;
    if (revocationService.Off) return Fail(403, "releases-off", "this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot record a release", 30);
    var (text, tooLarge) = await ReadBody(ctx, 4096);
    JsonObject body; try { body = tooLarge ? new JsonObject() : Messenger.ParseObject(text); } catch { body = new JsonObject(); }
    if (!(body["pr"] is JsonValue pv && pv.TryGetValue<int>(out var pr) && pr >= 1 && pr <= 999_999_999) || body["version"]?.ToString() is not { } version || !Plant.ValidVersion(version) || body.Count != 2)
        return Fail(400, "usage", "the body is { \"pr\": <pull request number>, \"version\": \"<the version /_build shows, digits and dots>\" }", 2);
    var now = DateTime.UtcNow;
    // its own gate (a flood of records must never delay a revoke) and a cap on the writes of one day
    await releaseGate.WaitAsync();
    try
    {
        if (!plant.HasRelease(pr))
        {
            if (plant.ReleaseRecordsToday() >= Plant.ReleaseWritesPerDay) return Results.Json(new { ok = false, code = "record-limit", reason = $"{Plant.ReleaseWritesPerDay} releases are recorded today; no more are written", retryAfterSeconds = 3600, exit = 20 }, statusCode: 429);
            var (code, _) = await runner.RunAsync("operator-release", new JsonObject { ["day"] = OperatorReleaseLoader.Day(now), ["pr"] = pr, ["version"] = version }.ToJsonString(), TimeSpan.FromSeconds(120));
            if (code != 0) return Results.Json(new { ok = false, code = "not-recorded", reason = "the release could not be written to GitHub, so it is not counted; try again", retryAfterSeconds = 15, exit = 20 }, statusCode: 503);
            plant.AddRelease(now, pr, version);
            Console.WriteLine(new JsonObject { ["at"] = now.ToString("o"), ["operatorRelease"] = pr, ["version"] = version, ["by"] = By(ctx) }.ToJsonString());
        }
    }
    finally { releaseGate.Release(); }
    var st = plant.OperatorStatus();
    return Results.Json(new { ok = true, pr, version, releasesToday = st["releasesToday"]!.GetValue<int>(), limit = st["limit"]!.GetValue<int>(), exit = 0 });
});

// Test seam of the Tier 1 suites (messaging.testHooks, operator token only): puts a message into a name's inbox, or a trigger into its Buffer,
// answers a question, and sets a messaging mode, so the store, the replay merge, the envelope and the question transport can be tested before the
// supervisor's and the operator's endpoints exist (steps 5 and 6). Not mapped unless the config asks for it.
if (messaging.Enabled && messaging.TestHooks)
{
    async Task<(JsonObject? Body, IResult? Error)> SeamBody(HttpContext ctx, string shape)
    {
        if (Who(ctx).Kind != "admin") return (null, Results.StatusCode(403));
        var (text, tooLarge) = await ReadBody(ctx, 64 * 1024);
        if (tooLarge) return (null, Fail(413, "too-large", "the request body is over 64 KB", 2));
        try { return (Messenger.ParseObject(text), null); }
        catch { return (null, Fail(400, "usage", shape, 2)); }
    }

    // a runtime worker's nonce, as the adapter mints one at provisioning: {"name", "runtime", "tokenSeconds", "ttlSeconds"}
    // the octopus runtime's start of a worker with an explicit role (the overflow rule; the plant starts own-item workers only)
    app.MapPost("/api/v1/_test/octopus-provision", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with item, phase and role");
        if (b == null) return err!;
        if (octopusManager == null) return Fail(404, "no-runtime", "the octopus runtime is not switched on", 12);
        var t = octopusManager.Begin(int.TryParse(b["item"]?.ToString(), out var ti) ? ti : 0, b["phase"]?.ToString() ?? "", "", b["role"]?.ToString() ?? "");
        await octopusManager.Provision(t);
        return Results.Json(new { ok = true, name = t.Name, execution = octopusManager.ExecutionOf(t.Name) });
    });
    app.MapPost("/api/v1/_test/runtime-nonce", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name and runtime");
        if (b == null) return err!;
        var nm = b["name"]?.ToString() ?? ""; var rk = b["runtime"]?.ToString() ?? "";
        if (!Auth.ValidName(nm) || !Auth.ValidRuntime(rk)) return Fail(400, "usage", "a valid worker name and a runtime other than local", 2);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)(double.TryParse(b["tokenSeconds"]?.ToString(), out var ts) ? ts : 3600);
        return Results.Json(new { ok = true, nonce = runtimeNonces.Mint(nm, rk, exp, TimeSpan.FromSeconds(double.TryParse(b["ttlSeconds"]?.ToString(), out var tt) ? tt : 600)) });
    });
    // an r1 for a test (agents-roles-launch.md 4): only the adapters mint one in a deployed gateway
    app.MapPost("/api/v1/_test/role-token", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, role and mode");
        if (b == null) return err!;
        var nm = b["name"]?.ToString() ?? ""; var rl = b["role"]?.ToString() ?? ""; var md = b["mode"]?.ToString() ?? "manual";
        if (!Auth.ValidName(nm) || !Auth.Roles.Contains(rl) || !Auth.Modes.Contains(md) || tokenSecret.Length == 0) return Fail(400, "usage", "a valid name, a known role and mode, and a token secret", 2);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)(double.TryParse(b["tokenSeconds"]?.ToString(), out var ts) && double.IsFinite(ts) ? Math.Clamp(ts, -86400, 86400) : 3600);
        var claims = new RoleClaims(nm, rl, md, b["agent"]?.ToString() ?? "", b["env"]?.ToString() ?? Auth.Environment, exp,
            int.TryParse(b["item"]?.ToString(), out var ri) ? ri : 0, b["assignment"]?.ToString() ?? "", b["phase"]?.ToString() ?? "");
        return Results.Json(new { ok = true, token = Auth.RoleToken(tokenSecret, claims, revocations.Of(nm)) });
    });
    app.MapPost("/api/v1/_test/deliver", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with to and text, trigger or assigned");
        if (b == null) return err!;
        // an item trigger as the webhooks and the sweep route it: into the item's mailbox (what GET /wait reads) and to the owner's stream if there is one
        if (b["routeItem"] is JsonNode ri && int.TryParse(ri.ToString(), out var routeItem))
        {   // with park: an item.parked trigger with that reason, as the sweep routes one
            var routed = new JsonObject { ["provider"] = "gateway", ["event"] = b["park"] != null ? "item.parked" : b["event"]?.ToString() is { Length: > 0 } routedName ? routedName : "test.item-trigger", ["item"] = routeItem, ["note"] = b["note"]?.ToString() ?? "" };
            if (b["park"] != null) routed["reason"] = b["park"]!.ToString();
            plant.Route(routeItem, routed); return Results.Json(new { ok = true, exit = 0 });
        }
        var to = b["to"]?.ToString() ?? "";
        if (!Auth.ValidName(to)) return Fail(400, "usage", "to is a session name", 2);
        if (b["trigger"] is JsonNode tg) { plant.SendTo(to, new JsonObject { ["provider"] = "gateway", ["event"] = "test.trigger", ["note"] = tg.ToString() }); return Results.Json(new { ok = true, exit = 0 }); }
        // an item.assigned in the Buffer, as a replay would show it (no real assignment, no real token): for the client's replayed-event rule
        // With job (bot-job or qc-env) the event carries a real item token of that role, so a scenario can see which credential a spawned process gets.
        if (b["assigned"] is JsonNode asg && int.TryParse(asg.ToString(), out var asgItem))
        {
            var job = b["job"]?.ToString() ?? "own-item";
            if (job is not ("own-item" or "bot-job" or "qc-env")) return Fail(400, "usage", "job is own-item, bot-job or qc-env", 2);
            var jobRole = job == "bot-job" ? "inspector" : job == "qc-env" ? "qc" : "owner";
            var asgEvt = new JsonObject { ["provider"] = "gateway", ["event"] = "item.assigned", ["item"] = asgItem, ["job"] = job, ["role"] = jobRole, ["mode"] = "first", ["assignment"] = "a-test",
                ["token"] = b["job"] == null || b["tokenless"]?.ToString() == "true" ? "test-placeholder" : Auth.MintItemToken(tokenSecret, asgItem, jobRole, "a-test", to, TimeSpan.FromHours(1), revocations.Of(to)) };
            if (job == "qc-env") { asgEvt["pr"] = 0; asgEvt["sha"] = "0000000"; asgEvt["env_id"] = "qc-test"; }
            plant.SendTo(to, asgEvt); return Results.Json(new { ok = true, exit = 0 });
        }
        // a message as the gateway would deliver it; the optional fields let a scenario play the supervisor or a peer (the real send endpoint cannot)
        var fromKind = b["fromKind"]?.ToString() ?? "operator";
        if (fromKind is not ("operator" or "supervisor" or "worker")) return Fail(400, "usage", "fromKind is operator, supervisor or worker", 2);
        var from = b["from"]?.ToString() ?? (fromKind == "operator" ? "operator" : fromKind == "supervisor" ? "supervisor" : "peer");
        if (!Auth.ValidName(from)) return Fail(400, "usage", "from is a session name", 2);
        var evt = new JsonObject
        {
            ["provider"] = "peer", ["relay"] = "gateway", ["event"] = "message.received", ["type"] = b["type"]?.ToString() ?? (fromKind == "operator" ? "operator-note" : "note"),
            ["from"] = from, ["fromKind"] = fromKind, ["to"] = to, ["kind"] = "direct", ["thread"] = null,
            ["item"] = b["item"] is JsonValue iv && iv.TryGetValue<int>(out var itemN) ? itemN : null, ["inReplyTo"] = b["inReplyTo"]?.ToString(),
            ["subject"] = b["subject"]?.ToString() ?? "", ["text"] = b["text"]?.ToString() ?? "", ["sentAt"] = DateTime.UtcNow.ToString("o"),
            ["flags"] = b["flags"] is JsonArray fl ? new JsonArray(fl.Select(f => (JsonNode)f!.ToString()).ToArray()) : new JsonArray()
        };
        if (b["fromModel"]?.ToString() is { Length: > 0 } fm) evt["fromModel"] = fm;
        var (id, seq, pushed) = plant.Deliver(to, evt);
        return Results.Json(new { ok = true, id, seq, pushed, exit = 0 });
    });

    // the answer to an open question, as the supervisor or the operator would give it (step 5 adds the real endpoints on the same plant method)
    app.MapPost("/api/v1/_test/answer", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with id and text");
        if (b == null) return err!;
        var fromKind = b["fromKind"]?.ToString() ?? "supervisor";
        if (fromKind is not ("operator" or "supervisor")) return Fail(400, "usage", "fromKind is operator or supervisor", 2);
        var evt = new JsonObject
        {
            ["provider"] = "peer", ["relay"] = "gateway", ["event"] = "message.received", ["type"] = fromKind == "operator" ? "operator-note" : "answer",
            ["from"] = fromKind == "operator" ? "operator" : "supervisor", ["fromKind"] = fromKind, ["kind"] = "direct", ["thread"] = null, ["subject"] = "",
            ["text"] = b["text"]?.ToString() ?? "", ["sentAt"] = DateTime.UtcNow.ToString("o"), ["flags"] = new JsonArray()
        };
        if (b["model"]?.ToString() is { Length: > 0 } model) evt["fromModel"] = model;
        var (ok, code, reason, answerId, _, _, _) = plant.AnswerQuestion(b["id"]?.ToString() ?? "", evt);
        return ok ? Results.Json(new { ok = true, id = answerId, exit = 0 }) : Fail(409, code, reason, 10);
    });

    // one sweep's `owned` and the items it must keep (not read this time), as the Sweeper hands them to Plan
    app.MapPost("/api/v1/_test/plan", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with owned and keep");
        if (b == null) return err!;
        plant.Plan(b["offers"]?.DeepClone() as JsonArray ?? new JsonArray(), b["owners"]?.DeepClone() as JsonObject, b["pins"]?.DeepClone() as JsonObject, b["owned"]?.DeepClone() as JsonObject ?? new JsonObject(), (b["keep"] as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<int>()).ToList(), b["priorities"]?.DeepClone() as JsonObject,
            DateTime.TryParse(b["sweepStarted"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var seamStart) ? seamStart : null);
        return Results.Json(new { ok = true, exit = 0 });
    });

    // one sweep's `release_queue`, as the Sweeper hands it to the plant: { "queue": [{ "item": N, "release": "1.0.N", "since": "<iso>" }] }
    app.MapPost("/api/v1/_test/release-queue", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with queue");
        if (b == null) return err!;
        plant.SetReleaseQueue(b["queue"]?.DeepClone() as JsonArray ?? new JsonArray());
        return Results.Json(new { ok = true, exit = 0 });
    });

    // a gate refusal of one code on one item, as the /verbs handler records one of a verb that answered exit 10 or 11 (the digest's refusals)
    app.MapPost("/api/v1/_test/refusal", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, item and code");
        if (b == null) return err!;
        var rn = b["name"]?.ToString() ?? "";
        if (!Auth.ValidName(rn) || !(b["item"] is JsonValue fv && fv.TryGetValue<int>(out var fItem))) return Fail(400, "usage", "name is a session name and item a work item number", 2);
        plant.NoteRefusal(rn, fItem, b["code"]?.ToString() ?? "");
        return Results.Json(new { ok = true, exit = 0 });
    });

    // a fact of the reported conditions (3.9), as the verbs handler records it: a failed verb, a recorded review round, a refused merge
    app.MapPost("/api/v1/_test/fact", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with fact (verb-error, review or merge-refused) and item");
        if (b == null) return err!;
        if (!(b["item"] is JsonValue fv && fv.TryGetValue<int>(out var fItem) && fItem >= 1)) return Fail(400, "usage", "item is a work item number", 2);
        int Num(string k, int d) => b[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : d;
        switch (b["fact"]?.ToString())
        {
            case "verb-error" when Auth.ValidName(b["name"]?.ToString() ?? ""): plant.NoteVerbError(b["name"]!.ToString(), fItem, b["code"]?.ToString() ?? "", Num("exit", 20)); break;
            case "review": plant.NoteReview(fItem, b["column"]?.ToString() ?? "", b["verdict"]?.ToString() ?? "FINDINGS", Num("round", 1), Num("max", 3)); break;
            case "merge-refused": plant.NoteMergeRefused(fItem, Num("roundTrip", 1)); break;
            default: return Fail(400, "usage", "fact is verb-error (with name), review or merge-refused", 2);
        }
        return Results.Json(new { ok = true, exit = 0 });
    });

    // the plant data the digest will be computed from (3.9, step 5a): owners as the sweep lists them, activity, waiting codes, refusals, parks
    app.MapGet("/api/v1/_test/plant-data", (HttpContext ctx) => Who(ctx).Kind != "admin" ? Results.StatusCode(403) : Results.Text(plant.PlantData().ToJsonString(), "application/json"));

    // issue 105: the plant snapshot as a graceful stop writes it, and its restore as the next start runs it (the digest's memory is part of it)
    app.MapGet("/api/v1/_test/plant-snapshot", (HttpContext ctx) => Who(ctx).Kind != "admin" ? Results.StatusCode(403) : Results.Text(plant.Snapshot().ToJsonString(), "application/json"));
    app.MapPost("/api/v1/_test/plant-restore", async (HttpContext ctx) =>
    {
        if (Who(ctx).Kind != "admin") return Results.StatusCode(403);
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        JsonNode? snap; try { snap = JsonNode.Parse(text); } catch { return Fail(400, "bad-json", "the body is a plant snapshot", 2); }
        var (na, no, skipped) = plant.Restore(snap, timing.PlantSnapshotMaxAgeMinutes);
        return Results.Json(new { ok = true, assignments = na, owners = no, skipped, digest = plant.DigestReportedCount() });
    });

    // how many entries each bounded store holds, with its cap (the stress test and the limit scenarios read it); counts only, never a text or a name
    app.MapGet("/api/v1/_test/stores", (HttpContext ctx) =>
    {
        if (Who(ctx).Kind != "admin") return Results.StatusCode(403);
        var o = plant.StoreStats(); messenger.AddStoreStats(o);
        return Results.Text(o.ToJsonString(), "application/json");
    });

    // who owns, inspects and runs the QC of an item, as accepted assignments would record it (independence, broadcast and thread scenarios)
    app.MapPost("/api/v1/_test/roles", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with item and owner, inspector or qc");
        if (b == null) return err!;
        if (!(b["item"] is JsonValue rv && rv.TryGetValue<int>(out var rItem) && rItem >= 1)) return Fail(400, "usage", "item is a work item number", 2);
        string? Name(string k) => b.ContainsKey(k) ? b[k]?.ToString() ?? "" : null;
        var (ro, ri, rq) = (Name("owner"), Name("inspector"), Name("qc"));
        foreach (var n in new[] { ro, ri, rq }) if (n is { Length: > 0 } && !Auth.ValidName(n)) return Fail(400, "usage", "owner, inspector and qc are session names", 2);
        plant.TestSetRoles(rItem, ro, ri, rq, b["sweepable"]?.ToString() == "true");
        return Results.Json(new { ok = true, exit = 0 });
    });

    // a verb call of an item token that has not answered yet, as the middleware counts one (the operator session's quiet-plant check): start
    // returns a ticket, end takes it back
    app.MapPost("/api/v1/_test/inflight", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name, item and verb, or with end");
        if (b == null) return err!;
        if (b["end"] is JsonValue ev && ev.TryGetValue<long>(out var ticket)) { plant.VerbEnded(ticket); return Results.Json(new { ok = true, exit = 0 }); }
        var fn = b["name"]?.ToString() ?? "";
        if (!Auth.ValidName(fn) || !(b["item"] is JsonValue fi && fi.TryGetValue<int>(out var fItem2))) return Fail(400, "usage", "name is a session name and item a work item number", 2);
        return Results.Json(new { ok = true, ticket = plant.VerbStarted(fn, fItem2, b["verb"]?.ToString() ?? ""), exit = 0 });
    });

    app.MapPost("/api/v1/_test/mode", async (HttpContext ctx) =>
    {
        var (b, err) = await SeamBody(ctx, "the body is a JSON object with name and mode");
        if (b == null) return err!;
        var name = b["name"]?.ToString() ?? "";
        return Auth.ValidName(name) && plant.SetMessagingMode(name, b["mode"]?.ToString() ?? "") ? Results.Json(new { ok = true, name, mode = plant.MessagingMode(name), exit = 0 }) : Fail(400, "usage", "name is a session name and mode is on, mute, inbound-off or off", 2);
    });
}

// One worker or supervisor stream: the replay snapshot, the registration line, then the channel until it ends. An entry with id 0 (stream.replaced)
// carries no id line, so it never becomes a client's cursor.
async Task Pump(HttpContext ctx, Channel<(long, string)> ch, List<(long, string)> replay, string registered, string? snapshot = null)
{
    foreach (var (id, json) in replay) await ctx.Response.WriteAsync($"id: {Sid(id)}\ndata: {json}\n\n");
    await ctx.Response.WriteAsync($"data: {registered}\n\n");
    if (snapshot != null) await ctx.Response.WriteAsync($"data: {snapshot}\n\n");
    await ctx.Response.Body.FlushAsync();
    var lastPing = DateTime.UtcNow;
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); cts.CancelAfter(PingDue(lastPing));
        try { var (id, json) = await ch.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync(id == 0 ? $"data: {json}\n\n" : $"id: {Sid(id)}\ndata: {json}\n\n"); }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
        catch (ChannelClosedException) { break; }
        if (PingDue(lastPing) == TimeSpan.Zero) { await ctx.Response.WriteAsync(": ping\n\n"); lastPing = DateTime.UtcNow; }
        await ctx.Response.Body.FlushAsync();
    }
}

// GET /events?name=N&supervisor=1&model=&effort= with the m1. credential of that name (worker-messaging.md 4.6): the supervisor's slot, never a worker.
async Task SupervisorStream(HttpContext ctx, Principal who, string name, long after)
{
    if (who.Kind != "manager" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the supervisor credential issued for this name registers as the supervisor"); return; }
    var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
    if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
    // the model requirement (4.7): flagged by default, refused only when manager.enforceModel is on; declared, never verified
    if (messaging.EnforceModel && messaging.ModelCheck(model, effort) != "ok")
    { await Refuse(ctx, 403, "supervisor-model-too-weak", $"this gateway enforces the supervisor's model: the {messaging.RequiredModelTier} tier at effort {messaging.RequiredEffort} or higher, declared with --model and --effort"); return; }
    var (ok, active, since, ch, replay) = plant.RegisterSupervisor(name, model, effort, after);
    if (ok) plant.NoteHandover(name, ctx.Request.Query["handover"].ToString() == "1");
    if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "supervisor-active", reason = "another supervisor session is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
    ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "supervisor.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.ModelCheck(model, effort) }.ToJsonString()); }
    catch (OperationCanceledException) { }
    finally { plant.DisconnectSupervisor(ch!); }
}

// GET /events?name=N&keeper=1&model=&effort= with the k1. credential of that name: the backlog keeper's slot (Keeper.cs), never a worker.
async Task KeeperStream(HttpContext ctx, Principal who, string name, long after)
{
    if (who.Kind != "keeper" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the keeper credential issued for this name registers as the backlog keeper"); return; }
    var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
    if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
    var (ok, active, since, ch, replay) = plant.RegisterKeeper(name, model, effort, after);
    if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "keeper-active", reason = "another backlog keeper is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
    plant.KeeperRaiseOpen();
    ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "keeper.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.ModelCheckFor(model, effort, keeper.RequiredModelTier, keeper.RequiredEffort) }.ToJsonString()); }
    catch (OperationCanceledException) { }
    finally { plant.DisconnectKeeper(ch!); }
}

// GET /events?name=N&operator=1&model=&effort= with the o1. credential of that name: the operator session's slot (Operator.cs), never a worker.
async Task OperatorStream(HttpContext ctx, Principal who, string name, long after)
{
    if (who.Kind != "operator" || who.Name != name) { await Refuse(ctx, 403, "forbidden", "only the operator session credential issued for this name registers as the operator session"); return; }
    if (!messaging.Enabled) { await Refuse(ctx, 403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled)"); return; }
    var model = ctx.Request.Query["model"].ToString(); var effort = ctx.Request.Query["effort"].ToString();
    if (!Auth.ValidRegistration("persistent-local", Array.Empty<string>(), Array.Empty<string>(), model, effort)) { await Refuse(ctx, 400, "usage", "model and effort must use the registration values (worker-messaging.md 2.2)", 2); return; }
    var (ok, active, since, ch, replay) = plant.RegisterOperator(name, model, effort, after);
    if (!ok) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "operator-active", reason = "another operator session is active and was heard within timing.mgrTakeoverSeconds", name = active, since, exit = 40 }); return; }
    ctx.Response.Headers.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    try { await Pump(ctx, ch!, replay, new JsonObject { ["provider"] = "gateway", ["event"] = "operator.registered", ["name"] = name, ["epoch"] = plant.Epoch, ["modelCheck"] = messaging.OperatorModelCheck(model, effort) }.ToJsonString()); }
    catch (OperationCanceledException) { }
    finally { plant.DisconnectOperator(ch!); }
}

// how long until the next `: ping` of a stream is due (zero: now)
TimeSpan PingDue(DateTime lastPing) { var left = lastPing.AddSeconds(ssePingSeconds) - DateTime.UtcNow; return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
string Sid(long id) => messaging.On ? $"{plant.Epoch}.{id}" : id.ToString();

app.MapMcp("/mcp");
// the worker sessions of r1 holders at /mcp (gateway/Mcp/WorkerSessions.cs): registered with the plant at their first call, idle ones end
var mcpSessions = new WorkerSessions(plant) { IdleMinutes = timing.McpIdleMinutes };
mcpSessionsRef = mcpSessions;
// a worker session that ends (session_end, idle, revoke, the plant closing its stream) stops what runs it: the Cursor agent is cancelled and deleted
mcpSessions.OnEnded = async (name, reason) =>
{
    if (cursorManager != null && cursorManager.IsRuntimeWorker(name)) await cursorManager.Deprovision(name, reason);
    if (copilotManager != null && copilotManager.IsRuntimeWorker(name)) await copilotManager.Deprovision(name, reason);
};
// the Copilot CI release, the owner records and the settle of the cloud runtimes, and their owner watches: gateway/Runtimes/Owner*.cs (C5c), registered by
// AddGatewayRuntimes and AddGatewayBackground
// The plant manager on demand (api.copilot.manager, docs/design/agents-roles-launch.md): registered where it is enabled and ticked by
// ManagerCopilotService (AddGatewayBackground); resolved here, where it was made before C5d, so its start-up line keeps its place. Its bootstrap
// text reads the prompt files from the container (registered by AddGatewayRuntimes since C5b).
managerCopilot = app.Services.GetService<ManagerCopilot>();
// the tools call the endpoints in process, found through the container's EndpointDataSource (gateway/Mcp/InProcess.cs)
GatewayMcp.Tools = new McpTools(new InProcess(app.Services.GetRequiredService<EndpointDataSource>(), app.Services, app.Services.GetRequiredService<AdmissionService>().Admit), mcpSessions, t => Auth.VerifyItemToken(tokenSecret, t, revocations), (name, item) =>
{
    if (messaging.Enabled && ownerMode) plant.Touch(name);
    if (ownerMode && item > 0) { try { plant.NoteProgress(name, new[] { (item, DateTime.UtcNow, "work") }); } catch { } }
}) { Prompts = prompts, ManagerLive = n => managerCopilot?.Live(n) == true, ManagerEnd = (n, why) => managerCopilot?.End(n, why) ?? Task.CompletedTask };
// swept every 30 s by McpSessionSweeper (AddGatewayBackground), handed over until a later step registers the worker sessions
composition.McpSessions = mcpSessions;
app.Run();
