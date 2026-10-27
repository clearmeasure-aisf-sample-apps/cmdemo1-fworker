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
// every event stream writes a `: ping` comment at this interval, busy or idle: a proxy or the platform's ingress that sees no byte for a while
// closes the connection, and a client then sees a drop it did not cause (enhancement 11). Clients ignore comment lines.
var ssePingSeconds = gw.SsePingSeconds;
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
var revocationService = app.Services.GetRequiredService<RevocationService>();
var githubBudget = composition.GitHubBudget;
var leader = composition.Leader;
var revocations = composition.Revocations;
var runtimeNonces = composition.RuntimeNonces;
var sweepMinSeconds = composition.SweepMinSeconds;
var runtimes = app.Services.GetRequiredService<RuntimeRegistry>();
var runtimeManager = runtimes.CaeJob;
var cursorRuntime = runtimes.CursorRuntime;
var cursorManager = runtimes.Cursor;
var copilotCodes = runtimes.CopilotCodes;
var copilotRuntime = runtimes.CopilotRuntime;
var copilotManager = runtimes.Copilot;
var octopusManager = runtimes.Octopus;
ManagerCopilot? managerCopilot = null;   // the plant manager on demand (gateway/Runtimes/ManagerCopilot.cs), resolved below; null where it is not enabled

// The caller of a request (gateway/Http/CallerContext.cs, task C6): who it is, whom it acts as, its session's link. The handlers below keep these
// names until C7 moves them into endpoint modules.
Principal Who(HttpContext c) => CallerContext.Of(c).Who;
bool OperatorActive(HttpContext c) => CallerContext.Of(c).OperatorActive;
Principal Admitted(HttpContext c) => CallerContext.Of(c).Admitted;
string By(HttpContext c) => CallerContext.Of(c).By;

// the forwarded headers of a trusted proxy, the test-only cause of a request, then the authentication and admission (gateway/Http/, task C6)
app.UseGatewayPipeline();

if (ownerMode) qc.Map(app);
// the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5): loaded once (AddGatewayRuntimes); a broken one keeps /ready at 503
var prompts = app.Services.GetRequiredService<PromptStore>();
foreach (var pe in prompts.Errors) Console.Error.WriteLine($"prompts: {pe}");
// the endpoint modules (gateway/Endpoints/): health, /_build, status and the dashboard's JSON, metrics, the dashboard page, the plugin, the
// operator's release checks (C7a); worker administration, runtime hosts, the webhook, Copilot, transcripts, item administration, the demo upload
// (C7b); their guards are endpoint filters (gateway/Http/Filters/)
app.MapGatewayEndpoints();
// the revocation itself is RevocationService.RevokeAsync: a runtime revokes a failed execution's name through it
if (runtimeManager != null) runtimeManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (cursorManager != null) cursorManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (copilotManager != null) copilotManager.Revoke = async n => { copilotCodes.Forget(n); await revocationService.RevokeAsync(n, "runtime"); };
if (octopusManager != null) octopusManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };

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
// the guard texts live once in gateway/Http/Filters/GatewayGuards.cs; until C7b and C7c move these routes into modules, whose guards are then the
// MessagingOn and SupervisorOn filters
IResult? MessagingOff() => GatewayGuards.MessagingOff(ownerMode, messaging);
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
IResult? SupervisorOff() => GatewayGuards.SupervisorOff(ownerMode, messaging);
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
