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
    .AddGatewayHttp(publicBase)
    .AddGatewayEndpoints(composition);
var app = builder.Build();
// the stopping callbacks in their order (transcript flush, "stopping" line, hand-over) and the plant's wait for the snapshot
app.UseGatewayLifetime();

// What the handlers below still capture, until C6 and C7 move them into services and endpoint modules: the container's singletons, and what
// the composition made outside it (the revocation generations and the nonces, read by handlers until C7).
var hub = app.Services.GetRequiredService<EventHub>();
var runner = app.Services.GetRequiredService<Runner>();
var plant = app.Services.GetRequiredService<Plant>();
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
var copilotCodes = runtimes.CopilotCodes;
var copilotRuntime = runtimes.CopilotRuntime;
var copilotManager = runtimes.Copilot;
var octopusRuntime = runtimes.OctopusRuntime;
var octopusManager = runtimes.Octopus;
ManagerCopilot? managerCopilot = null;   // the plant manager on demand (gateway/Runtimes/ManagerCopilot.cs), resolved below; null where it is not enabled

var seenDeliveries = new ConcurrentDictionary<string, DateTime>();

// The caller of a request (gateway/Http/CallerContext.cs, task C6): who it is, whom it acts as, its session's link. The handlers below keep these
// names until C7 moves them into endpoint modules.
Principal Who(HttpContext c) => CallerContext.Of(c).Who;
bool OperatorActive(HttpContext c) => CallerContext.Of(c).OperatorActive;
string By(HttpContext c) => CallerContext.Of(c).By;

// the forwarded headers of a trusted proxy, the test-only cause of a request, then the authentication and admission (gateway/Http/, task C6)
app.UseGatewayPipeline();

if (ownerMode) qc.Map(app);
// the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5): loaded once (AddGatewayRuntimes); a broken one keeps /ready at 503
var prompts = app.Services.GetRequiredService<PromptStore>();
foreach (var pe in prompts.Errors) Console.Error.WriteLine($"prompts: {pe}");
// the endpoint modules (gateway/Endpoints/, C7a): health, /_build, status and the dashboard's JSON, metrics, the dashboard page, the plugin, the
// operator's release checks; C7c: wait, the event streams, presence, messaging, the supervisor, the test hooks; their guards are endpoint filters
// (gateway/Http/Filters/)
app.MapGatewayEndpoints();
WorkerSessions? mcpSessionsRef = null;
// the switch of the gateway's own back-merges (baseUpdate.autoUpdateBranch) at run time, operator token only; until the next restart
app.MapPost("/api/v1/base-update", (HttpContext c) =>
{
    if (Who(c).Kind != "admin") return Results.StatusCode(403);
    if (bool.TryParse(c.Request.Query["autoUpdateBranch"].ToString(), out var on)) baseWatch.AutoUpdateBranch = on;
    return Results.Text(baseWatch.Status().ToJsonString(), "application/json");
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
