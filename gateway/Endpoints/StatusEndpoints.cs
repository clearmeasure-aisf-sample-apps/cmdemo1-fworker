using System.Text.Json.Nodes;

// The gateway's state as /api/v1/status and the dashboard's JSON answer it (task C7a, gateway-composition.md 3 C and D): one block per subsystem,
// each only where its feature is on, so a default deployment answers exactly as before the feature existed. Read-only: it asks each owner for its
// status and changes nothing.
sealed class StatusQuery
{
    readonly IServiceProvider services;
    readonly int sweepMinSeconds;
    readonly GatewayOptions gateway;
    readonly MessagingSettings messaging;
    readonly KeeperSettings keeper;
    readonly BuildInfo build;
    readonly EventHub hub;
    readonly Plant plant;
    readonly Messenger messenger;
    readonly BaseWatch baseWatch;
    readonly RuntimeRegistry runtimes;
    readonly CopilotCloudSettings copilotSettings;
    readonly ISnapshotCoordinator snapshots;
    readonly GatewayLeader leader;
    readonly Runner runner;
    readonly GitHubBudget githubBudget;
    readonly TranscriptStore transcripts;
    readonly RevocationService revocations;
    readonly EngineOptions engine;

    // sweepMinSeconds: the sweep's minimum interval as AddGatewayBackground computed it (GatewayComposition.SweepMinSeconds)
    public StatusQuery(IServiceProvider services, int sweepMinSeconds)
    {
        this.services = services; this.sweepMinSeconds = sweepMinSeconds;
        gateway = services.GetRequiredService<GatewayOptions>(); messaging = services.GetRequiredService<MessagingSettings>(); keeper = services.GetRequiredService<KeeperSettings>();
        build = services.GetRequiredService<BuildInfo>(); hub = services.GetRequiredService<EventHub>(); plant = services.GetRequiredService<Plant>();
        messenger = services.GetRequiredService<Messenger>(); baseWatch = services.GetRequiredService<BaseWatch>(); runtimes = services.GetRequiredService<RuntimeRegistry>();
        copilotSettings = services.GetRequiredService<CopilotCloudSettings>(); snapshots = services.GetRequiredService<ISnapshotCoordinator>(); leader = services.GetRequiredService<GatewayLeader>();
        runner = services.GetRequiredService<Runner>(); githubBudget = services.GetRequiredService<GitHubBudget>(); transcripts = services.GetRequiredService<TranscriptStore>();
        revocations = services.GetRequiredService<RevocationService>(); engine = services.GetRequiredService<EngineOptions>();
    }

    // GET /api/v1/status: counts and states only
    public JsonObject Status()
    {
        var ownerMode = gateway.OwnerMode;
        var o = new JsonObject { ["started"] = build.Started, ["sweepSeconds"] = gateway.SweepSeconds, ["ownerMode"] = ownerMode, ["subscribers"] = hub.SubscriberCount, ["lastEventId"] = hub.LastId,
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
        if (ownerMode) o["revocations"] = revocations.Status();
        // the engine that acts here: shadow (v1 acts, v2 beside it) or v2, or the kill switch FACTORY_V2_OFF while the file names one (W0.0). Plain v1, the
        // default of every environment, adds nothing, so a default deployment answers exactly as before (the C0 pins list the keys); no block means v1
        if (engine.Reported) o["engine"] = engine.Status();
        // the system catalog (W1.2): its snapshot and the mismatch report against instance.json, only where it is switched on (catalog.enabled or
        // FACTORY_CATALOG=1), so a default deployment answers exactly as before. Resolved at the ask: it is registered only where it is enabled
        if (services.GetService<CatalogRefreshService>() is { } catalog) o["catalog"] = catalog.Status();
        // the Wolverine host (W2.2): only where FACTORY_DB_CONNECTION is set, so a default deployment answers exactly as before. Never the connection string
        if (services.GetService<WolverineHostService>() is { } wolverine)
        {
            var wb = wolverine.Status();
            if (services.GetService<IShadowHistory>() is { } shadow) wb["shadowHistory"] = shadow.Status();   // W2.3: counts only
            o["wolverine"] = wb;
        }
        // the shadow run of the delivery train (W5.2): the comparison counts since this process started; the report endpoint reads the stored ones. Only in the shadow run
        if (services.GetService<DeliveryShadowHolder>()?.Shadow is { } deliveryShadow) o["deliveryShadow"] = deliveryShadow.Status();
        return o;
    }

    // GET /api/v1/dashboard: what the operator's page shows. host: the request's host, the page's address when FACTORY_PUBLIC_URL is not set.
    public JsonObject Dashboard(HostString host)
    {
        var ownerMode = gateway.OwnerMode;
        var o = new JsonObject { ["environment"] = gateway.EnvironmentName, ["ownerMode"] = ownerMode, ["url"] = gateway.PublicUrl != "" ? gateway.PublicUrl : $"http://{host}", ["started"] = build.Started,
            ["lastSweep"] = hub.LastSweep, ["plant"] = plant.Status() };
        if (messaging.On) foreach (var kv in MessagingBlocks(true).ToList()) o[kv.Key] = kv.Value?.DeepClone();
        if (messaging.OperatorEnabled) o["operator"] = plant.OperatorStatus();
        if (keeper.Enabled) o["keeper"] = plant.KeeperStatus();
        if (plant.ReplenishStatus() is { } rps) o["replenish"] = rps;
        if (ownerMode) o["baseUpdate"] = baseWatch.Status();
        if (ownerMode) o["releaseQueue"] = plant.ReleaseQueueStatus();
        if (ownerMode) o["hosts"] = plant.HostStatus();
        if (ownerMode) o["lease"] = plant.LeaseStatus();
        if (ownerMode && (runtimes.CaeJob != null || runtimes.Cursor != null || runtimes.Copilot != null || runtimes.Octopus != null)) o["runtimes"] = RuntimesBlock();
        if (ownerMode) o["plantSnapshot"] = snapshots.Status();
        if (ownerMode) o["leader"] = leader.Status();
        return o;
    }

    // The messaging and manager blocks of /status and of the dashboard JSON (worker-messaging.md 7, 8): the plant's states and counts with the
    // Messenger's counters. /status carries counts and states only; the dashboard adds the last digest and the decision log for the operator's page.
    JsonObject MessagingBlocks(bool dashboard)
    {
        var b = plant.MessagingStatus(dashboard); messenger.AddCounters((JsonObject)b["messaging"]!);
        // the on-demand manager (ManagerCopilot.cs): present while its task runs, with the launch counts and the task's link. Resolved at the ask, never
        // earlier: it is registered only where it is enabled, and Program.cs makes it where its start-up line belongs
        if (services.GetService<ManagerCopilot>() is { } managerCopilot && b["manager"] is JsonObject mb)
        {
            var mc = managerCopilot.Status(); mb["copilot"] = mc;
            if (mc["present"]?.GetValue<bool>() == true) mb["present"] = true;
        }
        return b;
    }

    // every runtime adapter in one block: the cae-job block as before (or {enabled:false}), and cursor-cloud, octopus and copilot-cloud beside it when on
    JsonObject RuntimesBlock()
    {
        var o = runtimes.CaeJob?.Status() ?? new JsonObject { ["enabled"] = false };
        if (runtimes.Cursor?.Status()["cursor-cloud"] is { } cb) o["cursor-cloud"] = cb.DeepClone();
        if (runtimes.Octopus?.Status()["octopus"] is JsonObject ob && runtimes.OctopusRuntime != null)
        {
            var oc = (JsonObject)ob.DeepClone();
            foreach (var (k, v) in runtimes.OctopusRuntime.Status()) oc[k] = v?.DeepClone();
            o["octopus"] = oc;
        }
        if (runtimes.Copilot?.Status()["copilot-cloud"] is JsonObject pb)
        {
            var pc = (JsonObject)pb.DeepClone();
            pc["ciRelease"] = runtimes.CopilotCiRelease.Status(copilotSettings.CiRelease, copilotSettings.CiReleaseSeconds);
            o["copilot-cloud"] = pc;
        }
        return o;
    }
}

// GET /api/v1/status (the admin and the active operator session: Admitted) and GET /api/v1/dashboard (the admin only: Who). Both are reachable by a
// replaced process's admin too (GatewayAuthMiddleware lets exactly these two through).
sealed class StatusEndpoints(StatusQuery status) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/status", (HttpContext c) => Results.Text(status.Status().ToJsonString(), "application/json"))
            .Admin(CallerView.Admitted);
        routes.MapGet("/api/v1/dashboard", (HttpContext c) => Results.Text(status.Dashboard(c.Request.Host).ToJsonString(), "application/json"))
            .Admin(CallerView.Who);
    }
}
