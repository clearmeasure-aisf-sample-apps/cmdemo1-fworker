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
string By(HttpContext c) => CallerContext.Of(c).By;

// the forwarded headers of a trusted proxy, the test-only cause of a request, then the authentication and admission (gateway/Http/, task C6)
app.UseGatewayPipeline();

if (ownerMode) qc.Map(app);
// the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5): loaded once (AddGatewayRuntimes); a broken one keeps /ready at 503
var prompts = app.Services.GetRequiredService<PromptStore>();
foreach (var pe in prompts.Errors) Console.Error.WriteLine($"prompts: {pe}");
// the endpoint modules (gateway/Endpoints/): health, /_build, status and the dashboard's JSON, metrics, the dashboard page, the plugin, the
// operator's release checks (C7a); worker administration, runtime hosts, the webhook, Copilot, transcripts, item administration, the demo upload
// (C7b); wait, the event streams, presence, messaging, the supervisor, the test hooks (C7c); their guards are endpoint filters (gateway/Http/Filters/)
app.MapGatewayEndpoints();
// the revocation itself is RevocationService.RevokeAsync: a runtime revokes a failed execution's name through it
if (runtimeManager != null) runtimeManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (cursorManager != null) cursorManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };
if (copilotManager != null) copilotManager.Revoke = async n => { copilotCodes.Forget(n); await revocationService.RevokeAsync(n, "runtime"); };
if (octopusManager != null) octopusManager.Revoke = async n => { await revocationService.RevokeAsync(n, "runtime"); };

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
