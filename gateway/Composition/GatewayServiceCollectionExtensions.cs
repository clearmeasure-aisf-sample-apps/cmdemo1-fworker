using System.Text.Json.Nodes;

// The gateway's registrations, one method per area (gateway-composition.md 2 and 3 B, task C5a). Program.cs calls them in this order:
//
//   AddGatewayConfiguration  the configuration read at start (C3)
//   AddGatewayCore           EventHub, the GitHub budget, the Runner, the transcript store and its service
//   AddGatewayLeadership     the leader lease, the plant snapshot, LeadershipService (C4d)
//   AddGatewayIdentity       revocations, runtime nonces, RevocationService (C4a)
//   AddGatewayRuntimes       the runtime adapters and managers; their settings      -> C5b: RuntimeRegistry and the prompt builders
//   AddGatewayPlant          the plant's settings, IPlantHooks, Plant, Messenger, QcHub, BaseWatch, the operator's releases (C4b)
//   AddGatewayBackground     every hosted service, in start order                   -> C5c: CopilotCiReleaseService, CloudOwnerWatch;
//                                                                                       C5d: ManagerCopilotService, McpSessionSweeper
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
        // claims whose lease ran out while no gateway was up: the first full sweep after the restore renews them (Verbs.Sweep.ps1)
        var sweepGap = new SweepGap();
        // the plant snapshot and the lease's start, hand-over and stop (gateway/Leadership/): the restore before the first sweep, the write of every change
        services.AddSingleton(leader);
        var maxAge = c.Timing.PlantSnapshotMaxAgeMinutes;
        services.AddSingleton(sp => new SnapshotCoordinator(snapshotOn, maxAge, leader, sp.GetRequiredService<Runner>().RunAsync, sp.GetRequiredService<Plant>(), sweepGap));
        services.AddSingleton<ISnapshotCoordinator>(sp => sp.GetRequiredService<SnapshotCoordinator>());
        services.AddSingleton<LeadershipService>();
        c.SnapshotOn = snapshotOn; c.LeaderOn = leaderOn; c.Leader = leader; c.SweepGap = sweepGap;
        return services;
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
        c.Revocations = revocations; c.RuntimeNonces = runtimeNonces;
        return services;
    }

    // The runtime adapters (gateway/Runtimes/, cloud-worker-runtimes.md 4) and their settings. Each adapter is on only in owner mode with a token
    // secret and the runtime switched on in the configuration; one start-up line each says whether it is. C5b turns this into RuntimeRegistry.
    public static IServiceCollection AddGatewayRuntimes(this IServiceCollection services, GatewayComposition c)
    {
        var gw = c.Gateway; var ownerMode = gw.OwnerMode; var tokenSecret = gw.TokenSecret; var scriptsDir = gw.ScriptsDir;
        var revocations = c.Revocations; var runtimeNonces = c.RuntimeNonces;
        var runtimeSettings = RuntimeSettings.Load(scriptsDir);
        var runtimeManager = ownerMode && tokenSecret.Length > 0 && runtimeSettings.AnyOn ? new RuntimeManager(runtimeSettings, new CaeJobRuntime(runtimeSettings.CaeJob), runtimeNonces) : null;
        // the api mode, v1 Cursor Cloud Agents (gateway/Runtimes/CursorCloudRuntime.cs, agents-roles-launch.md 10): api.cursor.enabled and a key (CURSOR_API_KEY)
        // and the gateway's public URL; the bootstrap prompt is read from prompts/ (Program.cs sets the prompt function once the prompts are loaded)
        var cursorSettings = CursorCloudSettings.Load(scriptsDir, Environment.GetEnvironmentVariable);
        GatewayRuntimes? set = null;
        var cursorRuntime = ownerMode && tokenSecret.Length > 0 && cursorSettings.On ? new CursorCloudRuntime(cursorSettings, (item, phase, why) => set!.CursorPrompt(item, phase, why)) : null;
        var cursorManager = cursorRuntime != null
            ? new RuntimeManager(runtimeSettings, cursorRuntime, runtimeNonces,
                cursorSettings.Limits((name, item, phase, expires) => Auth.RoleToken(tokenSecret, new RoleClaims(name, "worker", "api", "cursor-cloud-agent", Auth.Environment, expires, item, "", GatewayRuntimes.PhaseSlug(phase)), revocations.Of(name))))
            : null;
        // the Copilot cloud agent (gateway/Runtimes/CopilotCloudRuntime.cs, docs/design/copilot-cloud-agent.md): api.copilot.enabled, a token
        // (COPILOT_TASKS_TOKEN) and the gateway's public URL; the task's prompt carries a one-time code for the worker's r1 (copilotCodes)
        var copilotSettings = CopilotCloudSettings.Load(scriptsDir, Environment.GetEnvironmentVariable);
        var copilotCiRelease = new CopilotCiRelease { RefusedRetry = TimeSpan.FromSeconds(Math.Max(10, 5 * copilotSettings.CiReleaseSeconds)) };   // the CI release of the Copilot agent's held workflow runs (gateway/Runtimes/CopilotCiRelease.cs)
        var copilotCodes = new CopilotCodes();
        var copilotRuntime = ownerMode && tokenSecret.Length > 0 && copilotSettings.On
            ? new CopilotCloudRuntime(copilotSettings, copilotCodes, (item, phase, code, pr, why) => set!.CopilotPrompt(item, phase, code, pr, why), itemPr: (item, ct) => set!.CopilotItemPr(item, ct)) : null;
        var copilotManager = copilotRuntime != null
            ? new RuntimeManager(runtimeSettings, copilotRuntime, runtimeNonces,
                copilotSettings.Limits((name, item, phase, expires) => Auth.RoleToken(tokenSecret, new RoleClaims(name, "worker", "api", "copilot-cloud-agent", Auth.Environment, expires, item, "", GatewayRuntimes.PhaseSlug(phase)), revocations.Of(name))))
            : null;
        // the Octopus runbook runtime (gateway/Runtimes/OctopusRunbookRuntime.cs, docs/design/octopus-runbook-runtime.md): runtimes.octopus.enabled and the
        // key of the service account that may run the runbooks of that one project (OCTOPUS_RUNBOOK_API_KEY); off without the key
        var octopusRuntime = ownerMode && tokenSecret.Length > 0 && runtimeSettings.Octopus.On ? new OctopusRunbookRuntime(runtimeSettings.Octopus) : null;
        var octopusManager = octopusRuntime != null ? new RuntimeManager(runtimeSettings, octopusRuntime, runtimeNonces, runtimeSettings.Octopus.Limits(octopusRuntime.Blocked)) : null;
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "octopus", ["enabled"] = octopusManager != null, ["configured"] = runtimeSettings.Octopus.Enabled, ["auth"] = runtimeSettings.Octopus.Auth, ["key"] = runtimeSettings.Octopus.ApiKey != "", ["hostToken"] = runtimeSettings.Octopus.HostToken != "" }.ToJsonString());
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-cloud", ["enabled"] = copilotManager != null, ["configured"] = copilotSettings.Enabled, ["token"] = copilotSettings.Token != "", ["gatewayUrl"] = copilotSettings.GatewayUrl != "" }.ToJsonString());
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "cursor-cloud", ["enabled"] = cursorManager != null, ["configured"] = cursorSettings.Enabled, ["key"] = cursorSettings.ApiKey != "", ["gatewayUrl"] = cursorSettings.GatewayUrl != "" }.ToJsonString());
        set = new GatewayRuntimes
        {
            CaeJob = runtimeManager, CursorRuntime = cursorRuntime, Cursor = cursorManager, CopilotRuntime = copilotRuntime, Copilot = copilotManager,
            OctopusRuntime = octopusRuntime, Octopus = octopusManager, CopilotCiRelease = copilotCiRelease, CopilotCodes = copilotCodes,
        };
        services.AddSingleton(runtimeSettings);
        services.AddSingleton(cursorSettings);
        services.AddSingleton(copilotSettings);
        c.Runtimes = set;
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
            return new Plant(gw.TokenSecret, async (item, ev, fields, key) =>
            {
                try { await runner.RunAsync("journal", new JsonObject { ["item"] = item, ["event"] = ev, ["fields"] = fields, ["key"] = key }.ToJsonString(), TimeSpan.FromSeconds(60)); }
                catch (Exception e) { Console.Error.WriteLine($"journal {ev} for #{item} failed: {e.Message}"); }
            }, timing.AckSeconds, gw.OwnerMode ? sp.GetRequiredService<IPlantHooks>() : null)
            { QcCfg = qcSettings, Msg = messaging, Rev = revocations, OneItemPerSession = instance.OneItemPerSession,
                PassUnmergedDigestMinutes = timing.PassUnmergedDigestMinutes, OfferMissLimit = timing.OfferMissLimit, OfferCoolDownMinutes = timing.OfferCoolDownMinutes, SessionEndGraceSeconds = timing.SessionEndGraceSeconds, PinFallbackMinutes = timing.PinFallbackMinutes, SessionRestartMinutes = timing.SessionRestartMinutes, RepeatQuestionLimit = timing.SessionRepeatQuestionLimit, Lease = leaseSettings, IntakeColumn = instance.IntakeColumn, Hosts = hostSettings, MgrActions = managerActionSettings, Keeper = keeper, Runtimes = rts.CaeJob, Cursor = rts.Cursor, Copilot = rts.Copilot, Octopus = rts.Octopus,
                SessionPage = (kind, name) => kind is "octopus" or "cae-job" && linkBase() is { Length: > 0 } lb ? $"{lb}/sessions/{Uri.EscapeDataString(name)}" : "" };
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
        services.AddHostedService(sp => sp.GetRequiredService<BaseWatch>());
        // a full sweep at most every timing.sweepMinSeconds (60, never more than the interval); triggers between them queue their item for an incremental pass (issue 92)
        var sweepSeconds = gw.SweepSeconds;
        var sweepMinSeconds = (int)Math.Min(sweepSeconds, int.TryParse(Environment.GetEnvironmentVariable("FACTORY_SWEEP_MIN_SECONDS"), out var smin) && smin >= 0 ? smin : c.Timing.SweepMinSeconds);
        var githubBudget = c.GitHubBudget; var leaderOn = c.LeaderOn; var sweepGap = c.SweepGap;
        services.AddHostedService(sp => new Sweeper(sp.GetRequiredService<Runner>(), sp.GetRequiredService<EventHub>(), sp.GetRequiredService<Plant>(), sp.GetRequiredService<QcHub>(), sweepSeconds, gw.OwnerMode, sp.GetRequiredService<BaseWatch>(), sp.GetRequiredService<ISnapshotCoordinator>().Restored) { MinSeconds = sweepMinSeconds, Budget = githubBudget, MayAct = () => leader.MayAct, LeaderOn = leaderOn, Gap = sweepGap });
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
        return services;
    }

    // The MCP server at /mcp (gateway/Mcp/, docs/design/agents-roles-launch.md 6): stateless Streamable HTTP; the middleware authenticates it like /api.
    // Its tools (GatewayMcp.Tools) are set in Program.cs once the prompts and the worker sessions exist (C5d and C8 move them).
    public static IServiceCollection AddGatewayMcp(this IServiceCollection services)
    {
        services.AddMcpServer(o => o.ServerInfo = new() { Name = "fworker", Version = "1" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler(GatewayMcp.ListTools).WithCallToolHandler(GatewayMcp.CallTool);
        return services;
    }
}
