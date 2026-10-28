using System.Text.Json.Nodes;

// The gateway's registrations, one method per area (gateway-composition.md 2 and 3 B, task C5a). Program.cs calls them in this order:
//
//   AddGatewayConfiguration  the configuration read at start (C3)
//   AddGatewayCore           EventHub, the GitHub budget, the Runner, the transcript store and its service
//   AddGatewayLeadership     the leader lease, the plant snapshot, LeadershipService (C4d); with FACTORY_DB_CONNECTION the Wolverine host's services (W2.2)
//   AddGatewayIdentity       revocations, runtime nonces, RevocationService (C4a)
//   AddGatewayRuntimes       the prompt files, the prompt builders, RuntimeRegistry (the runtime adapters and managers) and their settings (C5b)
//   AddGatewayPlant          the plant's settings, IPlantHooks, Plant, Messenger, QcHub, BaseWatch, the operator's releases (C4b)
//   AddGatewayBackground     every hosted service, in start order; after the plant ticker the CI release (CopilotCiReleaseService) and one
//                            owner watch per cloud runtime (CloudOwnerWatch, C5c), last the on-demand manager (its settings, ManagerCopilot and
//                            ManagerCopilotService, C5d) and McpSessionSweeper (C5d)
//   AddGatewayMcp            the MCP server at /mcp
//
// Rules that keep the start what it was before C5a, and that a later step keeps:
// - The order of the calls is the order Program.cs made these objects in: an object made at registration (an instance) is made where it
//   was, so the start-up lines and the configuration reads come in the same order. Objects made by a factory are made when first resolved.
// - Every hosted service is registered in AddGatewayBackground, nowhere else: the host starts them in registration order (and stops them in
//   reverse), which CompositionGraphTests pins. A step that adds one adds it there, at the place its start belongs.
// - Lifetimes: everything here is a singleton. An interface is registered as an alias of the concrete singleton (sp => the concrete), so both
//   resolve to one instance.
// - What a later step needs of an earlier one, or what Program.cs's handlers still capture, is kept on the GatewayComposition.
static class GatewayServiceCollectionExtensions
{
    // The configuration as Program.cs read it, the same instances its locals hold (registered as instances: ValidateOnBuild has nothing to construct).
    public static IServiceCollection AddGatewayConfiguration(this IServiceCollection services, GatewayComposition c)
    {
        services.AddSingleton(c.Qc);
        services.AddSingleton(c.Gateway);
        services.AddSingleton(c.Instance);
        services.AddSingleton(c.Timing);
        services.AddSingleton(c.Revocation);
        services.AddSingleton(c.Engine);
        services.AddSingleton(c.Messaging);
        services.AddSingleton(c.Keeper);
        return services;
    }

    public static IServiceCollection AddGatewayCore(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway;
        services.AddSingleton<EventHub>();
        // the shared GitHub budget (GitHubBudget.cs, issue 92): fed by every rule-script answer, read by the verb route and the sweeper
        var githubBudget = GitHubBudget.Load(gw.ScriptsDir);
        services.AddSingleton(githubBudget);
        var runner = new Runner(gw.Entry) { Budget = githubBudget };
        // session transcripts of runtime workers (Transcripts.cs): stored encrypted beside the plant snapshot; FACTORY_TRANSCRIPTS=0 keeps them in memory only
        var transcriptsDurable = gw.OwnerMode && gw.TokenSecret.Length > 0 && Environment.GetEnvironmentVariable("FACTORY_TEST_SKIP_REVOCATIONS") != "1" && Environment.GetEnvironmentVariable("FACTORY_TRANSCRIPTS") != "0";
        var transcripts = new TranscriptStore(transcriptsDurable ? runner.RunAsync : null, gw.TokenSecret);
        if (int.TryParse(Environment.GetEnvironmentVariable("FACTORY_TRANSCRIPT_SAVE_SECONDS"), out var tss) && tss >= 0) transcripts.SaveSeconds = tss;
        services.AddSingleton(transcripts);
        services.AddSingleton<TranscriptService>();
        services.AddSingleton(runner);
        c.GitHubBudget = githubBudget; c.Runner = runner; c.Transcripts = transcripts;
        return services;
    }

    public static IServiceCollection AddGatewayLeadership(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway;
        // The plant snapshot (PlantSnapshot.cs, enhancement 5): read back before the first sweep. Owner mode only, and not for the suites'
        // second gateways (FACTORY_TEST_SKIP_REVOCATIONS: their GitHub is a port nothing listens on). FACTORY_PLANT_SNAPSHOT=0 switches it off.
        var snapshotOn = gw.OwnerMode && Environment.GetEnvironmentVariable("FACTORY_TEST_SKIP_REVOCATIONS") != "1" && Environment.GetEnvironmentVariable("FACTORY_PLANT_SNAPSHOT") != "0";
        // The leader lease (Leader.cs, issue 94): only the process that holds it acts on the board. Owner mode with a GitHub identity, not the suites' second
        // gateways; FACTORY_LEADER_LEASE=0 switches it off (every process acts, and the snapshot is written at SIGTERM and read once, as before).
        var leaderOn = gw.OwnerMode && Environment.GetEnvironmentVariable("FACTORY_TEST_SKIP_REVOCATIONS") != "1" && Environment.GetEnvironmentVariable("FACTORY_LEADER_LEASE") != "0" && c.Revocation.GitHubConfigured;
        var leaderSettings = LeaderSettings.Load(gw.ScriptsDir, leaderOn);
        services.AddSingleton(leaderSettings);
        var leader = new GatewayLeader(c.Runner, leaderSettings,
            (typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion?.Split('+')[0] ?? "0.0.0");
        services.AddSingleton<ILeadership>(leader);
        // fencing (W0.1): the rule scripts that act on the board run under this process's lease epoch and refuse their writes once it is lost (Runner.InjectLease)
        c.Runner.Lease = () => leader.FenceLease;
        // claims whose lease ran out while no gateway was up: the first full sweep after the restore renews them (Verbs.Sweep.ps1)
        var sweepGap = new SweepGap();
        // the plant snapshot and the lease's start, hand-over and stop (gateway/Leadership/): the restore before the first sweep, the write of every change
        services.AddSingleton(leader);
        var maxAge = c.Timing.PlantSnapshotMaxAgeMinutes;
        services.AddSingleton(sp => new SnapshotCoordinator(snapshotOn, maxAge, leader, sp.GetRequiredService<Runner>().RunAsync, sp.GetRequiredService<Plant>(), sweepGap));
        services.AddSingleton<ISnapshotCoordinator>(sp => sp.GetRequiredService<SnapshotCoordinator>());
        services.AddSingleton<LeadershipService>();
        c.SnapshotOn = snapshotOn; c.LeaderOn = leaderOn; c.Leader = leader; c.SweepGap = sweepGap;
        return services.AddGatewayWolverine(c);   // W2.2: the Wolverine host's services, only with a connection string
    }

    public static IServiceCollection AddGatewayIdentity(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var messaging = c.Messaging; var revocationOptions = c.Revocation;
        var revocations = new Revocations();
        var runtimeNonces = new RuntimeNonces();   // the one-time nonces of runtime workers (Auth.cs, cloud-worker-runtimes.md 2.6)
        // the revoked credentials (gateway/Identity/RevocationService.cs): the read at start and the revocation; its read starts with the host, before the other hosted services
        var revocationsNeeded = c.Instance.OwnerSessions || messaging.On || messaging.OperatorEnabled;
        services.AddSingleton(sp => new RevocationService(revocationOptions, gw.OwnerMode, gw.TokenSecret, messaging.TestHooks, revocationsNeeded,
            revocations, sp.GetRequiredService<Runner>().RunAsync, runtimeNonces, sp.GetRequiredService<Plant>().Revoke));
        services.AddSingleton<IRevocationService>(sp => sp.GetRequiredService<RevocationService>());
        // the generations themselves, for the authentication middleware (gateway/Http/GatewayAuthMiddleware.cs, C6); the same instance the service raises
        services.AddSingleton(revocations);
        c.Revocations = revocations; c.RuntimeNonces = runtimeNonces;
        // the credential rules that depend on the configuration (the environment, auth.legacyKinds, the keeper): for the middleware and the endpoints that mint
        services.AddSingleton(c.Credentials);
        return services;
    }

    // The runtime adapters (gateway/Runtimes/, cloud-worker-runtimes.md 4), their settings, and the prompts the Cursor and Copilot adapters send.
    // Each adapter is on only in owner mode with a token secret and the runtime switched on in the configuration; one start-up line each says
    // whether it is. RuntimeRegistry holds them all (C5b).
    public static IServiceCollection AddGatewayRuntimes(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var ownerMode = gw.OwnerMode; var tokenSecret = gw.TokenSecret; var scriptsDir = gw.ScriptsDir;
        var revocations = c.Revocations; var runtimeNonces = c.RuntimeNonces;
        // the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5), loaded once and registered first: the prompt builders render the
        // bootstrap prompts from them. Loading writes nothing; Program.cs writes their errors where it loaded them before C5b (a broken one keeps /ready at 503)
        var prompts = new PromptStore(Environment.GetEnvironmentVariable("FACTORY_PROMPTS") ?? Path.Combine(scriptsDir, "..", "..", "prompts"));
        services.AddSingleton(prompts);
        var runtimeSettings = RuntimeSettings.Load(scriptsDir);
        if (c.Credentials.HomeClaim != "") runtimeSettings.Launch = new LaunchFields(c.Credentials.HomeClaim, "https://github.com/" + c.Credentials.HomeClaim + ".git", c.Instance.DefaultBranch);
        var runtimeManager = ownerMode && tokenSecret.Length > 0 && runtimeSettings.AnyOn ? new RuntimeManager(runtimeSettings, new CaeJobRuntime(runtimeSettings.CaeJob), runtimeNonces) : null;
        // the api mode, v1 Cursor Cloud Agents (gateway/Runtimes/CursorCloudRuntime.cs, agents-roles-launch.md 10): api.cursor.enabled and a key (CURSOR_API_KEY)
        // and the gateway's public URL; the bootstrap prompt is read from prompts/ (CursorPromptBuilder, gateway/Templates/RuntimePrompts.cs)
        var cursorSettings = CursorCloudSettings.Load(scriptsDir, Environment.GetEnvironmentVariable);
        var cursorPrompts = new CursorPromptBuilder(prompts);
        // the owner record of the task that owns an item (rule verb copilot-owner) and the settle (rule verb copilot-settle), shared by both cloud
        // runtimes as constructor parameters (C5c, gateway/Runtimes/Owner*.cs)
        var ownerVerbs = new CopilotOwnerVerbs(c.Runner.RunAsync);
        var settleClient = new CopilotSettleClient(c.Runner.RunAsync);
        var cursorRuntime = ownerMode && tokenSecret.Length > 0 && cursorSettings.On ? new CursorCloudRuntime(cursorSettings, cursorPrompts, owners: ownerVerbs, settle: settleClient) : null;
        var cursorManager = cursorRuntime != null
            ? new RuntimeManager(runtimeSettings, cursorRuntime, runtimeNonces,
                cursorSettings.Limits((name, item, phase, expires) => Auth.RoleToken(tokenSecret, new RoleClaims(name, "worker", "api", "cursor-cloud-agent", c.Credentials.Environment, expires, item, "", RuntimeRegistry.PhaseSlug(phase), Repo: c.Credentials.HomeClaim), revocations.Of(name))))
            : null;
        // the Copilot cloud agent (gateway/Runtimes/CopilotCloudRuntime.cs, docs/design/copilot-cloud-agent.md): api.copilot.enabled, a token
        // (COPILOT_TASKS_TOKEN) and the gateway's public URL; the task's prompt carries a one-time code for the worker's r1 (copilotCodes)
        var copilotSettings = CopilotCloudSettings.Load(scriptsDir, Environment.GetEnvironmentVariable);
        var copilotCiRelease = new CopilotCiRelease { RefusedRetry = TimeSpan.FromSeconds(Math.Max(10, 5 * copilotSettings.CiReleaseSeconds)) };   // the CI release of the Copilot agent's held workflow runs (gateway/Runtimes/CopilotCiRelease.cs)
        var copilotCodes = new CopilotCodes();
        var copilotPrompts = new CopilotPromptBuilder(prompts, copilotSettings);
        var itemPr = new ItemPrLookup(c.Runner);
        var copilotRuntime = ownerMode && tokenSecret.Length > 0 && copilotSettings.On
            ? new CopilotCloudRuntime(copilotSettings, copilotCodes, copilotPrompts, itemPr: itemPr, owners: ownerVerbs, settle: settleClient) : null;
        var copilotManager = copilotRuntime != null
            ? new RuntimeManager(runtimeSettings, copilotRuntime, runtimeNonces,
                copilotSettings.Limits((name, item, phase, expires) => Auth.RoleToken(tokenSecret, new RoleClaims(name, "worker", "api", "copilot-cloud-agent", c.Credentials.Environment, expires, item, "", RuntimeRegistry.PhaseSlug(phase), Repo: c.Credentials.HomeClaim), revocations.Of(name))))
            : null;
        // the Octopus runbook runtime (gateway/Runtimes/OctopusRunbookRuntime.cs, docs/design/octopus-runbook-runtime.md): runtimes.octopus.enabled and the
        // key of the service account that may run the runbooks of that one project (OCTOPUS_RUNBOOK_API_KEY); off without the key
        var octopusRuntime = ownerMode && tokenSecret.Length > 0 && runtimeSettings.Octopus.On ? new OctopusRunbookRuntime(runtimeSettings.Octopus) : null;
        var octopusManager = octopusRuntime != null ? new RuntimeManager(runtimeSettings, octopusRuntime, runtimeNonces, runtimeSettings.Octopus.Limits(octopusRuntime.Blocked)) : null;
        // what the managers read of their runtime's owners: the settle at a run's end (Development only; Copilot's head is the branch its task
        // reported, Cursor sends none), the owned items and the quota for the status, the quota block for a start
        if (copilotRuntime != null && copilotManager != null)
        {
            copilotManager.Ending = e => settleClient.Ending(e, copilotRuntime.HeadOf);
            copilotManager.Owned = copilotRuntime.OwnedJson;
            copilotManager.Quota = copilotRuntime.QuotaJson;
            copilotSettings.QuotaBlock = copilotRuntime.QuotaBlocked;
        }
        if (cursorRuntime != null && cursorManager != null)
        {
            cursorManager.Ending = e => settleClient.Ending(e, null);
            cursorManager.Owned = cursorRuntime.OwnedJson;
            cursorManager.Quota = cursorRuntime.QuotaJson;
            cursorSettings.QuotaBlock = cursorRuntime.QuotaBlocked;
        }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "octopus", ["enabled"] = octopusManager != null, ["configured"] = runtimeSettings.Octopus.Enabled, ["auth"] = runtimeSettings.Octopus.Auth, ["key"] = runtimeSettings.Octopus.ApiKey != "", ["hostToken"] = runtimeSettings.Octopus.HostToken != "" }.ToJsonString());
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-cloud", ["enabled"] = copilotManager != null, ["configured"] = copilotSettings.Enabled, ["token"] = copilotSettings.Token != "", ["gatewayUrl"] = copilotSettings.GatewayUrl != "" }.ToJsonString());
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-cloud", ["enabled"] = cursorManager != null, ["configured"] = cursorSettings.Enabled, ["key"] = cursorSettings.ApiKey != "", ["gatewayUrl"] = cursorSettings.GatewayUrl != "" }.ToJsonString());
        var registry = new RuntimeRegistry
        {
            CaeJob = runtimeManager, CursorRuntime = cursorRuntime, Cursor = cursorManager, CopilotRuntime = copilotRuntime, Copilot = copilotManager,
            OctopusRuntime = octopusRuntime, Octopus = octopusManager, CopilotCiRelease = copilotCiRelease, CopilotCodes = copilotCodes,
        };
        services.AddSingleton(runtimeSettings);
        services.AddSingleton(cursorSettings);
        services.AddSingleton(copilotSettings);
        services.AddSingleton<ICursorPromptBuilder>(cursorPrompts);
        services.AddSingleton<ICopilotPromptBuilder>(copilotPrompts);
        services.AddSingleton<IItemPrLookup>(itemPr);
        services.AddSingleton(ownerVerbs);
        services.AddSingleton(settleClient);
        services.AddSingleton(registry);
        c.Runtimes = registry; c.CopilotSettings = copilotSettings; c.CursorSettings = cursorSettings;
        return services;
    }

    public static IServiceCollection AddGatewayPlant(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var scriptsDir = gw.ScriptsDir; var timing = c.Timing; var messaging = c.Messaging; var qcSettings = c.Qc;
        // the settings of the plant and the base watch, loaded once and registered as singletons (gateway-composition.md 3 A); the factories below used to
        // load them when they ran, after Build(), and nothing is written between here and there, so the one warning ManagerActionSettings can print keeps its place
        var leaseSettings = LeaseSettings.Load(scriptsDir);
        var hostSettings = HostSettings.Load(scriptsDir);
        var managerActionSettings = ManagerActionSettings.Load(scriptsDir);
        var baseUpdateSettings = BaseUpdateSettings.Load(scriptsDir);
        services.AddSingleton(leaseSettings);
        services.AddSingleton(hostSettings);
        services.AddSingleton(managerActionSettings);
        services.AddSingleton(baseUpdateSettings);
        // owner leases (gateway/Lease.cs): the plant decides, the rule scripts change the claim; a renewal answers the new lease, a take-back is followed by a sweep
        services.AddSingleton<IPlantHooks, PlantRuleHooks>();
        var revocations = c.Revocations; var rts = c.Runtimes; var linkBase = c.LinkBase; var keeper = c.Keeper; var instance = c.Instance;
        services.AddSingleton(sp =>
        {
            var runner = sp.GetRequiredService<Runner>();
            var shadow = sp.GetService<IShadowHistory>();   // W2.3: only in a shadow environment, with the Wolverine host
            var journalSink = c.Engine.V2Acts ? sp.GetService<JournalCommandSink>() : null;   // W3.2: only where v2 acts, with the Wolverine host
            return new Plant(gw.TokenSecret, async (item, ev, fields, key) =>
            {
                // v2 acting: the line is a command in the outbox, written by the C# ledger (no `journal` verb); the chain of the plant only publishes
                if (journalSink != null) { await journalSink.Send(item, ev, fields, key); return; }
                var posted = false;
                try { await runner.RunAsync("journal", new JsonObject { ["item"] = item, ["event"] = ev, ["fields"] = fields, ["key"] = key }.ToJsonString(), TimeSpan.FromSeconds(60)); posted = true; }
                catch (Exception e) { Console.Error.WriteLine($"journal {ev} for #{item} failed: {e.Message}"); }
                shadow?.Journal(item, ev, fields, key, posted);   // after v1's call, best-effort, never throws
            }, timing.AckSeconds, gw.OwnerMode ? sp.GetRequiredService<IPlantHooks>() : null)
            { Home = c.Credentials.HomeClaim, HomeBranch = instance.DefaultBranch, QcCfg = qcSettings, Msg = messaging, Rev = revocations, OneItemPerSession = instance.OneItemPerSession,
                PassUnmergedDigestMinutes = timing.PassUnmergedDigestMinutes, OfferMissLimit = timing.OfferMissLimit, OfferCoolDownMinutes = timing.OfferCoolDownMinutes, SessionEndGraceSeconds = timing.SessionEndGraceSeconds, PinFallbackMinutes = timing.PinFallbackMinutes, SessionRestartMinutes = timing.SessionRestartMinutes, RepeatQuestionLimit = timing.SessionRepeatQuestionLimit, Lease = leaseSettings, IntakeColumn = instance.IntakeColumn, Hosts = hostSettings, MgrActions = managerActionSettings, Keeper = keeper, Runtimes = rts.CaeJob, Cursor = rts.Cursor, Copilot = rts.Copilot, Octopus = rts.Octopus,
                // the items the cloud runtimes' tasks own, and their part of the plant snapshot (C5c: set here, where Program.cs set them after Build())
                CopilotOwns = rts.CopilotRuntime != null ? rts.CopilotRuntime.Owns : null, CopilotOwnersSnapshot = rts.CopilotRuntime != null ? rts.CopilotRuntime.OwnedJson : null, CopilotOwnersRestore = rts.CopilotRuntime != null ? rts.CopilotRuntime.Restore : null,
                CursorOwns = rts.CursorRuntime != null ? rts.CursorRuntime.Owns : null, CursorOwnersSnapshot = rts.CursorRuntime != null ? rts.CursorRuntime.OwnedJson : null, CursorOwnersRestore = rts.CursorRuntime != null ? rts.CursorRuntime.Restore : null,
                SweepRequest = sp.GetRequiredService<EventHub>().RequestSweep, SessionPage = (kind, name) => kind is "octopus" or "cae-job" && linkBase() is { Length: > 0 } lb ? $"{lb}/sessions/{Uri.EscapeDataString(name)}" : "" };
        });
        services.AddSingleton(sp => new Messenger(messaging, sp.GetRequiredService<Plant>()));
        services.AddSingleton(sp => new QcHub(sp.GetRequiredService<Runner>(), sp.GetRequiredService<Plant>(), sp.GetRequiredService<EventHub>(), qcSettings, gw.PublicUrl, gw.TokenSecret));
        // master moved: base.updated to the owners of open branches, and GitHub's update branch where it is clean (BaseUpdate.cs); only the acting gateway
        var leader = c.Leader;
        services.AddSingleton(sp => new BaseWatch(sp.GetRequiredService<Runner>(), sp.GetRequiredService<Plant>(), baseUpdateSettings, gw.OwnerMode) { MayAct = () => leader.MayAct });
        // the operator session's recorded releases (gateway/Operator/OperatorReleaseLoader.cs): read once at start, after the revocations' read has started
        var skipRevocations = c.Revocation.SkipRevocations;
        services.AddSingleton(sp => new OperatorReleaseLoader(messaging.OperatorEnabled, skipRevocations, sp.GetRequiredService<Runner>().RunAsync, sp.GetRequiredService<Plant>().AddRelease));
        services.AddSingleton<IOperatorReleaseLoader>(sp => sp.GetRequiredService<OperatorReleaseLoader>());
        return services;
    }

    // Every hosted service, in the order the host starts them. AddHostedService(sp => the singleton) runs the registered instance.
    public static IServiceCollection AddGatewayBackground(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var leader = c.Leader;
        // the revoked credentials' read starts first: the lease's start and every session credential wait for it
        services.AddHostedService(sp => sp.GetRequiredService<RevocationService>());
        // the operator session's recorded releases, after the revocations' read has started
        services.AddHostedService(sp => sp.GetRequiredService<OperatorReleaseLoader>());
        // the transcripts' save loop (TranscriptService) starts after the revocation read
        services.AddHostedService(sp => sp.GetRequiredService<TranscriptService>());
        // the lease's start (LeadershipService) waits for the revocation read; without the lease, the snapshot's one read
        services.AddHostedService(sp => sp.GetRequiredService<LeadershipService>());
        // W2.2: the Wolverine host, only with a connection string; its node leaves with the lease, so it starts after LeadershipService and stops before it
        if (c.WolverineHost) services.AddHostedService(sp => sp.GetRequiredService<WolverineHostService>());
        if (c.Engine.V2Acts) services.AddHostedService(sp => sp.GetRequiredService<CopilotReconcileService>());   // W3.3
        if (c.Engine.V2Acts)   // W4.2: the Octopus watcher, read only; v1 and shadow promote with the rule scripts
        {
            services.AddSingleton(sp => OctopusWatchService.From(c.Instance, sp.GetRequiredService<Fworker.Contracts.Seams.IEventIngress>(), Environment.GetEnvironmentVariable, () => c.Leader.MayAct));
            services.AddHostedService(sp => sp.GetRequiredService<OctopusWatchService>());
        }
        services.AddHostedService(sp => sp.GetRequiredService<BaseWatch>());
        // a full sweep at most every timing.sweepMinSeconds (60, never more than the interval); triggers between them queue their item for an incremental pass (issue 92)
        var sweepSeconds = gw.SweepSeconds;
        var sweepMinSeconds = (int)Math.Min(sweepSeconds, int.TryParse(Environment.GetEnvironmentVariable("FACTORY_SWEEP_MIN_SECONDS"), out var smin) && smin >= 0 ? smin : c.Timing.SweepMinSeconds);
        var githubBudget = c.GitHubBudget; var leaderOn = c.LeaderOn; var sweepGap = c.SweepGap;
        services.AddHostedService(sp => new Sweeper(sp.GetRequiredService<Runner>(), sp.GetRequiredService<EventHub>(), sp.GetRequiredService<Plant>(), sp.GetRequiredService<QcHub>(), sweepSeconds, gw.OwnerMode, sp.GetRequiredService<BaseWatch>(), sp.GetRequiredService<ISnapshotCoordinator>().Restored) { MinSeconds = sweepMinSeconds, Budget = githubBudget, MayAct = () => leader.MayAct, LeaderOn = leaderOn, Gap = sweepGap });
        // The automatic promotion: a loop of its own after the sweep, its own call (verb auto-promote), timeout and leader gate; it never runs inside the sweep
        var promoteOn = c.Instance.AutoPromoteHere; var promoteInterval = TimeSpan.FromSeconds(c.Timing.PromoteSeconds);
        services.AddHostedService(sp => new AutoPromoter(sp.GetRequiredService<Runner>(), promoteInterval, promoteOn) { MayAct = () => leader.MayAct });
        // one RuntimeLoop per adapter: AddHostedService uses TryAddEnumerable, which keeps only the first registration of an implementation type, so
        // with Cursor and Copilot both on the Copilot loop never ran (never relisted, every start refused runtime-relisting); AddSingleton<IHostedService> adds each
        var transcripts = c.Transcripts;
        foreach (var rmx in c.Runtimes.Managers) services.AddSingleton<IHostedService>(sp =>
        {
            var rp = sp.GetRequiredService<Plant>(); var rr = sp.GetRequiredService<Runner>();
            rmx.Plant = rp;
            if (rmx.Kind is "octopus" or "cae-job") { rmx.SessionStarted = transcripts.Started; rmx.SessionEnded = transcripts.Ended; }
            // the gateway's own hands for the adapter: park an item runtime-failed, end the claim a lost execution left, revoke a failed execution's name
            rmx.Park = async (item, phase) =>
            {
                var (code, _) = await rr.RunAsync("park-item", new JsonObject { ["item"] = item, ["code"] = "runtime-failed", ["phase"] = phase, ["by"] = "the runtime adapter" }.ToJsonString(), TimeSpan.FromSeconds(120));
                if (code != 0) throw new InvalidOperationException($"park-item answered exit {code}");
                rp.Route(item, new JsonObject { ["provider"] = "gateway", ["event"] = "item.parked", ["item"] = item, ["reason"] = "runtime-failed" });
            };
            rmx.ReleaseClaim = async (item, holder) => await rr.RunAsync("claim-item", new JsonObject { ["item"] = item, ["release"] = true, ["force"] = true, ["holder"] = holder }.ToJsonString(), TimeSpan.FromSeconds(120));
            return new RuntimeLoop(rmx) { MayAct = () => leader.MayAct };
        });
        services.AddHostedService(sp => new PlantTicker(sp.GetRequiredService<Plant>(), sp.GetRequiredService<QcHub>(), sp.GetRequiredService<Messenger>(), sp.GetRequiredService<Runner>()) { MayAct = () => leader.MayAct });
        c.SweepMinSeconds = sweepMinSeconds;
        // C5c: the loops Program.cs started after Build(), in that order: the Copilot CI release, the Copilot owner watch, the Cursor owner watch
        services.AddGatewayCloudOwners(c);
        // C5d: the two loops Program.cs started last, after Build(), in that order: the on-demand manager's tick, then the MCP session sweep
        services.AddGatewayManagerCopilot(c);
        services.AddHostedService(sp => new McpSessionSweeper(() => sp.GetRequiredService<WorkerSessions>(), McpSessionSweeper.DefaultInterval));
        // W1.2: the system catalog's refresh, last, and only where the catalog is switched on (catalog.enabled or FACTORY_CATALOG=1); off, the list of hosted services is what it was
        if (c.Catalog.Enabled && c.Instance.CanCompose) services.AddHostedService(sp => sp.GetRequiredService<CatalogRefreshService>());
        return services;
    }

    // The CI release and the owner watches of the cloud runtimes (gateway/Runtimes/Owner*.cs, task C5c), where Program.cs started them: the CI release
    // where the Copilot runtime is on and api.copilot.ciRelease; one CloudOwnerWatch per cloud runtime that is on. The Copilot watch's registration
    // also sets the Copilot credit guard (OverBudget), which parks through the plant; the host makes every hosted service before it starts any.
    static void AddGatewayCloudOwners(this IServiceCollection services, GatewayComposition c)
    {
        var rts = c.Runtimes; var copilotSettings = c.CopilotSettings; var cursorSettings = c.CursorSettings;
        var copilotRuntime = rts.CopilotRuntime; var copilotManager = rts.Copilot; var cursorRuntime = rts.CursorRuntime; var cursorManager = rts.Cursor;
        if (copilotManager != null && copilotSettings.CiRelease)
            services.AddHostedService(sp => new CopilotCiReleaseService(rts.CopilotCiRelease, copilotManager, copilotRuntime, sp.GetRequiredService<Plant>(), sp.GetRequiredService<Runner>(),
                sp.GetRequiredService<ILeadership>(), sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, TimeSpan.FromSeconds(copilotSettings.CiReleaseSeconds)));
        // two registrations of one implementation type: AddSingleton<IHostedService> adds each (AddHostedService would keep only the first, as with RuntimeLoop)
        if (copilotRuntime != null && copilotManager != null)
            services.AddSingleton<IHostedService>(sp =>
            {
                var plant = sp.GetRequiredService<Plant>(); var runner = sp.GetRequiredService<Runner>();
                copilotRuntime.OverBudget = async (item, phase, credits) =>
                {
                    var (code, _) = await runner.RunAsync("park-item", new JsonObject { ["item"] = item, ["code"] = "credit-guard", ["phase"] = phase, ["by"] = $"the Copilot credit guard: one task reported {credits:0} ai_credits, over api.copilot.maxCreditsPerTask {copilotSettings.MaxCreditsPerTask:0}" }.ToJsonString(), TimeSpan.FromSeconds(120));
                    if (code != 0) throw new InvalidOperationException($"park-item answered exit {code}");
                    plant.Route(item, new JsonObject { ["provider"] = "gateway", ["event"] = "item.parked", ["item"] = item, ["reason"] = "credit-guard" });
                };
                return new CloudOwnerWatch(copilotRuntime, copilotManager, sp.GetRequiredService<CopilotOwnerVerbs>(), sp.GetRequiredService<ILeadership>(),
                    sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, new CloudOwnerWatchOptions { IntervalSeconds = copilotSettings.CiReleaseSeconds, MasterFixer = copilotRuntime });
            });
        if (cursorRuntime != null && cursorManager != null)
            services.AddSingleton<IHostedService>(sp => new CloudOwnerWatch(cursorRuntime, cursorManager, sp.GetRequiredService<CopilotOwnerVerbs>(), sp.GetRequiredService<ILeadership>(),
                sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, new CloudOwnerWatchOptions { IntervalSeconds = cursorSettings.OwnerWatchSeconds }));
    }

    // The plant manager on demand (gateway/Runtimes/ManagerCopilot.cs, api.copilot.manager): its settings always, the manager and its tick
    // (ManagerCopilotService) only where Program.cs made them before C5d: the Copilot runtime on, owner mode, manager.enabled and messaging.enabled.
    // Elsewhere no ManagerCopilot is registered, so GetService<ManagerCopilot>() is null where Program.cs's local was null.
    static void AddGatewayManagerCopilot(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var messaging = c.Messaging;
        var copilotRuntime = c.Runtimes.CopilotRuntime; var copilotCodes = c.Runtimes.CopilotCodes; var revocations = c.Revocations;
        services.AddSingleton(sp =>
        {
            var copilotSettings = sp.GetRequiredService<CopilotCloudSettings>();
            return ManagerCopilotSettings.Load(gw.ScriptsDir, Environment.GetEnvironmentVariable, copilotSettings.Environment);
        });
        if (copilotRuntime == null || !gw.OwnerMode || !messaging.ManagerEnabled || !messaging.Enabled) return;
        // made when first resolved: Program.cs resolves it where it constructed it, so its start-up line (copilot-config) keeps its place
        services.AddSingleton(sp =>
        {
            var copilotSettings = sp.GetRequiredService<CopilotCloudSettings>();
            var mgrCopSettings = sp.GetRequiredService<ManagerCopilotSettings>();
            var revocationService = sp.GetRequiredService<RevocationService>();
            var prompts = sp.GetRequiredService<PromptStore>();   // the prompt files AddGatewayRuntimes registered
            var manager = new ManagerCopilot(mgrCopSettings, copilotRuntime, copilotCodes, sp.GetRequiredService<Plant>(),
                (name, expires) => Auth.RoleToken(gw.TokenSecret, new RoleClaims(name, "manager", "api", "copilot-cloud-agent", c.Credentials.Environment, expires, 0, "", "patrol", Repo: c.Credentials.HomeClaim), revocations.Of(name)),
                code =>
                {
                    var bp = prompts.ByPath("bootstrap/copilot-manager.md");
                    var text = bp == null ? "" : prompts.Render(bp, "copilot-cloud-agent")["text"]!.ToString();
                    return text + $"\nYour gateway URL for fworker_connect: {copilotSettings.GatewayUrl}\nYour one-time code for fworker_connect: {code}\n";
                },
                async name => { await revocationService.RevokeAsync(name, "runtime"); });
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["manager"] = "copilot-config", ["enabled"] = mgrCopSettings.Enabled, ["maxPerDay"] = mgrCopSettings.MaxPerDay, ["cooldownMinutes"] = mgrCopSettings.CooldownMinutes, ["maxMinutes"] = mgrCopSettings.MaxMinutes }.ToJsonString());
            return manager;
        });
        services.AddSingleton(sp => new ManagerCopilotService(sp.GetRequiredService<ManagerCopilot>(), sp.GetRequiredService<ILeadership>(),
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, ManagerCopilotService.IntervalFor(sp.GetRequiredService<CopilotCloudSettings>().ListSeconds)));
        services.AddHostedService(sp => sp.GetRequiredService<ManagerCopilotService>());
    }

    // The MCP server at /mcp (gateway/Mcp/, docs/design/agents-roles-launch.md 6): stateless Streamable HTTP; the middleware authenticates it like /api.
    // Its tools (McpTools, which GatewayMcp.Tools held as a static until C8) and the worker sessions are singletons made when first resolved: the sweep
    // of the sessions resolves them at its first pass, the tools at the first call, by when the routes they call in process are mapped (InProcess).
    public static IServiceCollection AddGatewayMcp(this IServiceCollection services, GatewayComposition c)
    {
        services.AddMcpServer(o => o.ServerInfo = new() { Name = "fworker", Version = "1" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler(GatewayMcp.ListTools).WithCallToolHandler(GatewayMcp.CallTool);
        var gw = c.Gateway; var messaging = c.Messaging; var timing = c.Timing;
        // the worker sessions of r1 holders at /mcp (gateway/Mcp/WorkerSessions.cs): registered with the plant at their first call, idle ones end
        services.AddSingleton(sp =>
        {
            var plant = sp.GetRequiredService<Plant>(); var runtimes = sp.GetRequiredService<RuntimeRegistry>();
            var cursorManager = runtimes.Cursor; var copilotManager = runtimes.Copilot;
            return new WorkerSessions(plant)
            {
                IdleMinutes = timing.McpIdleMinutes,
                // a worker session that ends (session_end, idle, revoke, the plant closing its stream) stops what runs it: the Cursor agent is cancelled and deleted
                OnEnded = async (name, reason) =>
                {
                    if (cursorManager != null && cursorManager.IsRuntimeWorker(name)) await cursorManager.Deprovision(name, reason);
                    if (copilotManager != null && copilotManager.IsRuntimeWorker(name)) await copilotManager.Deprovision(name, reason);
                }
            };
        });
        // the tools call the endpoints in process, found through the container's EndpointDataSource (gateway/Mcp/InProcess.cs)
        services.AddSingleton(sp =>
        {
            var plant = sp.GetRequiredService<Plant>(); var revocations = sp.GetRequiredService<Revocations>(); var ownerMode = gw.OwnerMode; var tokenSecret = gw.TokenSecret;
            var managerCopilot = sp.GetService<ManagerCopilot>();   // null where the on-demand manager is not enabled
            return new McpTools(new InProcess(sp.GetRequiredService<EndpointDataSource>(), sp, sp.GetRequiredService<AdmissionService>().Admit), sp.GetRequiredService<WorkerSessions>(),
                t => Auth.VerifyItemToken(tokenSecret, t, revocations, c.Credentials.Home), (name, item) =>
                {
                    if (messaging.Enabled && ownerMode) plant.Touch(name);
                    if (ownerMode && item > 0) { try { plant.NoteProgress(name, new[] { (item, DateTime.UtcNow, "work") }); } catch { } }
                }) { Prompts = sp.GetRequiredService<PromptStore>(), ManagerLive = n => managerCopilot?.Live(n) == true, ManagerEnd = (n, why) => managerCopilot?.End(n, why) ?? Task.CompletedTask };
        });
        return services;
    }
}
