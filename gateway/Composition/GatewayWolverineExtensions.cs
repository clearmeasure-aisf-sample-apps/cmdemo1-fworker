using Fworker.Contracts.Seams;
using Fworker.Contracts.Catalog;
using Fworker.Integrations.Commands;
using Fworker.Workflows.WorkItem;
// The Wolverine host's registration (task W2.2, docs/design/wolverine-host.md). Nothing is registered without a connection string (FACTORY_DB_CONNECTION):
// no service, no hosted service, no database call, and the gateway is v1 exactly as before. With one, the services below join the graph and
// the hosted service is added by AddGatewayBackground (the only place hosted services are registered), after LeadershipService (the node leaves with the lease, so it starts and stops beside it).
static class GatewayWolverineExtensions
{
    /// <summary>How long a claim of the ingress deduplication is kept: longer than any redelivery of a webhook.</summary>
    public static readonly TimeSpan IngressDedupRetention = TimeSpan.FromDays(7);

    /// <summary>How long the idempotency ledger keeps a success (W9.10o): a key is a request, and the same request asked for again after this long must run. Longer than any redelivery or retry schedule.</summary>
    public static readonly TimeSpan LedgerRetention = TimeSpan.FromDays(30);

    public static IServiceCollection AddGatewayWolverine(this IServiceCollection services, GatewayComposition c)
    {
        // always registered, so that /ready and /api/v1/status can take it; Tracked is false where there is no Wolverine host, and Required (the gate of /ready) only where v2 acts (W9.10o)
        var schemaStatus = new FactorySchemaStatus(required: c.WolverineHost && c.Engine.V2Acts, tracked: c.WolverineHost);
        services.AddSingleton(schemaStatus);
        if (c.Database.Malformed) services.AddSingleton(c.Database);   // (only so that /api/v1/status can say the variable is set and malformed; every other case registers it with the host)
        if (!c.WolverineHost) return services;
        if (!c.Database.Malformed) services.AddSingleton(c.Database);
        BoardWiring.Register(services, c, schemaStatus);   // W9.8: the transcripts in the database, the board verbs (only where the engine acts)
        services.AddSingleton(WolverineHostOptions.From(c.Instance));
        // built after LeadershipService, whose hand-over callbacks it wraps (the order of the callbacks is the order of the stop: hand-over, then the node leaves)
        JournalCommandSink? journalSink = null;
        CopilotReconciler? copilotReconciler = null;
        IngressCorrelations? correlations = null; IngressInboxPort? inboxPort = null;
        ShadowOctopusPort? octopusPort = null; var shadowHolder = new DeliveryShadowHolder();
        var jobs = new RecurringJobRegistry(); RecurringJobSlots? recurring = null;
        V2Ownership? v2Ownership = null; WorkItemParts? workItemParts = null; WorkItemRearm? rearm = null; IPurgeableLedger? purgeable = null;
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<LeadershipService>();
            var host = new WolverineHostService(sp.GetRequiredService<DatabaseOptions>(), sp.GetRequiredService<WolverineHostOptions>(), sp.GetRequiredService<GatewayLeader>(), sp.GetRequiredService<FactorySchemaStatus>());
            if (c.Engine.V2Acts)
            {
                // W3.3: the Copilot launch runs under a reservation, settled by CopilotReconcileService (a hosted service of AddGatewayBackground)
                // with the database the host needs, the reservations and the idempotency ledger are in SQL and survive the kill the host is built for (#481)
                // W9.10c: the claims of the ledger name this process and the epoch of the lease it holds
                var claimNode = ClaimOwner.ThisProcess().Node; var claimLeader = sp.GetRequiredService<GatewayLeader>();
                var stores = new SqlCommandStores(WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds), owner: () => new ClaimOwner(claimNode, claimLeader.ClaimEpoch));
                var reservations = stores.Reservations;
                purgeable = stores.Ledger as IPurgeableLedger;
                copilotReconciler = new CopilotReconciler(reservations, new CopilotTaskLookup(sp.GetRequiredService<RuntimeRegistry>()), owners: new CopilotOwnerRecorder(sp.GetRequiredService<RuntimeRegistry>()));
                RuntimeCommandWiring.Attach(host, sp.GetRequiredService<RuntimeRegistry>(), new CopilotLaunchPort(sp.GetRequiredService<Fworker.Contracts.Seams.ICommandPort>(), reservations, copilotReconciler), stores.Ledger,
                    (runtime, execution, item) => { _ = correlations?.FromRuntime(runtime, execution, item); });   // W4.1: the task or agent id finds its item later
                journalSink = JournalCommandWiring.Attach(host, c, ledger: stores.Ledger, locks: sp.GetRequiredService<IItemLock>());   // W3.2: the plant's journal lines become commands
                // W6.0, W6.1: the WorkItemSaga of the hybrid composition: v1's plant still offers the sessions, the saga decides the items it has started, and v1's sweep leaves those items alone (V2Ownership)
                // W6.6: the docs plan's steps run in it; the board it moves cards on is the catalog's, read when the first card moves
                // W7.3: a fix-forward round of the feature plan reaches the owner session as the trigger v1's sweep sends (the plant is resolved when the trigger is sent, not here: building the plant needs the host)
                var workItems = WorkItemWiring.Make(c, WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds), boardRef: () => sp.GetService<ISystemCatalog>()?.Current.Board,
                    route: (item, trigger) => { if (sp.GetService<Plant>() is not { } plant) return false; plant.Route(item, trigger); return true; });
                WorkItemWiring.Attach(host, workItems);
                workItemParts = workItems;
                v2Ownership = workItems.Ownership;
                // W7.3: a session of an item the saga owns that is gone ends its slot in the scheduler and tells the saga (the plant's own word for it, the journal's session-gone line)
                if (journalSink is not null && c.Instance.Home is { } sessionHome)
                    journalSink.OnSessionGone = async (item, key) =>
                    {
                        if (workItems.Ownership?.Owns(item) != true) return;
                        var id = new Fworker.Contracts.Ids.ItemId(sessionHome, item);
                        var at = DateTimeOffset.UtcNow;
                        // (a host that is not up takes neither: the slot then ends at its time limit, which the scheduler tells the saga as a lost session)
                        if (!await host.PublishAsync(new Fworker.Workflows.Capacity.SessionEnded(id, $"ended|{key}", "")) || !await host.PublishAsync(new SessionLost(id, 0, $"gone|{key}", "", "the session that held the item is gone", at)))
                            throw new InvalidOperationException("the Wolverine host did not take the end of the session");
                    };
                // W7.0, W7.1: the CapacityScheduler, the slot ledger of the sessions the v2 engine starts; it shares the runtimes' limits with the plant's manager (no flag: it exists where the engine acts)
                CapacityWiring.Attach(host, CapacityWiring.Make(c, WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds), CapacityWiring.OutsideOf(sp.GetRequiredService<RuntimeRegistry>()), CapacityWiring.OnOf(sp.GetRequiredService<RuntimeRegistry>())));
                CapacityWiring.ConnectHostReports(sp.GetRequiredService<RuntimeRegistry>().OctopusRuntime, host);   // W7.4: the host reports (metrics, running containers) are the scheduler's HostReported
                if (c.Instance.Home is { } home)
                {
                    // W4.1: webhook events into the durable inbox, deduplicated, with the correlation map (also written by the verbs and the runtimes)
                    var cs = WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds);
                    // (#485: the rows carry the generation of the item's open instance, which the WorkItemSaga's store keeps)
                    var map = new Fworker.Integrations.Correlation.GenerationStampingMap(new Fworker.Integrations.Correlation.SqlCorrelationMap(cs), new Fworker.Integrations.Correlation.SqlItemGenerations(cs));
                    var dedup = new Fworker.Integrations.Correlation.SqlIngressDedup(cs);
                    IngressInboxWiring.Attach(host, new Fworker.Integrations.Correlation.IngressInboxProcessor(dedup, map, home));
                    // W9.10h: what a person does on GitHub (close, reopen, move the card, a hold label) reaches the saga from the same events; the card projection's echo is consulted first
                    IngressInboxSagaWiring.Attach(host, new SagaIngress(workItems.Store, home, workItems.Docs.Settings, workItems.Docs.Versions!, workItems.Docs.Echo!, workItems.Docs.Issues, workItems.Docs.Facts));
                    // W5.0: ItemMerged, derived from the merge webhook and the board reconciler's poll (v1 emits nothing); needs GH_TOKEN or the author App to read the pull request and the item
                    var mergedDeriver = ItemMergedWiring.Make(c, home, cs, Environment.GetEnvironmentVariable);
                    if (mergedDeriver is { } merged) ItemMergedWiring.Attach(host, merged, new Fworker.Workflows.Delivery.DeliveredToTable(() => ItemMergedWiring.Open(cs)));
                    // W9.4: the delivery train acts: it promotes (PromoteRelease, AnswerSignOff are executed against Octopus) and the watcher's events reach it. W9.10p: for tdd only; v1's auto-promote loop keeps uat and prod (AddGatewayBackground, AutoPromoter.Runs) and the train's shadow of them is the holder's
                    if (mergedDeriver is not null) octopusPort = DeliveryActingWiring.Attach(host, c, () => sp.GetService<ISystemCatalog>(), cs, Environment.GetEnvironmentVariable, () => c.Leader.MayAct, stores.Ledger, shadowHolder);
                    // W6.6: the intake of docs items (off unless intake is enabled and names the docs plan in intake.v2Plans); the plant's facts are read when the job runs
                    if (workItems.Intake is { Enabled: true } && workItems.Intake.V2Plans.Count > 0)
                    {
                        var intake = new Fworker.Workflows.Intake.WorkItemIntake(workItems.Store, workItems.Intake, async (m, _) => { if (!await host.PublishAsync(m)) throw new InvalidOperationException("the Wolverine host is not up"); });
                        var docsIntake = workItems.Docs.IntakeJob(intake, home, workItems.Docs.Settings.Columns.Values.ToList(), () => sp.GetRequiredService<Plant>().IntakeSlots(),
                            () => workItems.Ownership?.Offers.Any(o => o.Column == workItems.Docs.Settings.ColumnOf(Fworker.Workflows.WorkItem.StepKind.Design) && !sp.GetRequiredService<Plant>().HasOwner(o.Item)) == true,
                            () => c.Leader.MayAct, () => c.GitHubBudget.Low().Low);
                        if (docsIntake is not null)
                            jobs.Add(RecurringJobSlots.DocsIntake, async ct => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["docsIntake"] = await docsIntake.Run(ct) }.ToJsonString()));
                    }
                    // W7.3: the watcher of CI alongside Functional Testing (every minute, on the leader), for the feature items; the checks are read through the rule script's own verbs, so its CI rule stays the script's
                    if (workItems.Docs.Settings.CarriesFeature)
                    {
                        var runner = sp.GetRequiredService<Runner>();
                        var watcher = new FeatureChecksJob(workItems.Store, home, new PullRequestChecks(async (verb, stdin, timeout) => await runner.RunAsync(verb, stdin, timeout)), m => host.PublishAsync(m), () => c.Leader.MayAct, () => c.GitHubBudget.Low().Low);
                        jobs.Add(RecurringJobSlots.FeatureChecks, async ct => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["featureChecks"] = await watcher.Run(ct) }.ToJsonString()));
                    }
                    // W9.10f: the sweep's item steps for the items the saga owns (every minute, on the leader): master's CI for merged code items (the settle's master-green wait and its parks), the settle's re-arm, a PASS whose pull request
                    // was closed, an inspector's hold that a person approved. It reads GitHub with the delivery verbs' identity and kit; the owner that waited out a hold is released through the verb router
                    var kitHost = new Lazy<DeliveryVerbHost>(() => DeliveryVerbHost.Make(sp), LazyThreadSafetyMode.PublicationOnly);
                    var stepReads = new Fworker.Workflows.Delivery.ItemStepReads(new DeliveryKitSource(() => kitHost.Value));
                    var itemSteps = new ItemStepsJob(workItems.Store, home, stepReads, stepReads, m => host.PublishAsync(m), () => c.Leader.MayAct, () => c.GitHubBudget.Low().Low, masterWatch: workItems.Docs.Settings.CarriesFeature);
                    jobs.Add(RecurringJobSlots.ItemSteps, async ct => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["itemSteps"] = await itemSteps.Run(ct) }.ToJsonString()));
                    // W9.10j: the saga starts the session of the slot the scheduler granted (the offer is made from the saga's state, on the slot's runtime), and the reaper tells the scheduler which items are in flight so that
                    // what it keeps of an item whose saga never cleared it is dropped
                    if (workItems.Ownership is { } sessionOwnership) SessionStartWiring.Attach(host, new SessionStartPort(sessionOwnership, () => sp.GetService<Plant>()));
                    var reaper = new CapacityReaper(workItems.Store, m => host.PublishAsync(m), () => c.Leader.MayAct);
                    jobs.Add(RecurringJobSlots.CapacityReap, async ct => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["capacityReap"] = await reaper.Run(ct) }.ToJsonString()));
                    ItemStepsWiring.Attach(host, new OwnerRelease(() => sp.GetService<Fworker.Contracts.Seams.IVerbRouter>(), (item, holder) => sp.GetService<Plant>()?.ManagerReleased(item, holder)));
                    // W9.10o: the repair of the window between a saga's save and its outbox (the timer or command that was never sent), and the purge of the old successes of the idempotency ledger
                    if (workItems.Store is IWorkItemAges ages)
                    {
                        var repair = rearm = new WorkItemRearm(ages, workItems.Docs.Settings.Policy, m => host.PublishAsync(m));
                        jobs.Add(RecurringJobSlots.SagaRearm, async ct =>
                        {
                            var report = await repair.Run(ct);
                            if (report.Timers + report.Commands + report.Failed > 0) Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["sagaRearm"] = new System.Text.Json.Nodes.JsonObject { ["open"] = report.Open, ["timers"] = report.Timers, ["commands"] = report.Commands, ["failed"] = report.Failed } }.ToJsonString());
                        });
                    }
                    if (purgeable is { } ledgerToPurge) jobs.Add(RecurringJobSlots.LedgerPurge, ct => Task.Run(() => { var n = ledgerToPurge.PurgeSuccesses(LedgerRetention, DateTimeOffset.UtcNow); if (n > 0) Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["ledgerPurge"] = n }.ToJsonString()); }, ct));
                    correlations = new IngressCorrelations(map, home);
                    inboxPort = new IngressInboxPort(m => host.PublishAsync(m));
                    // W4.1 follow-up (#485): the claims of the deduplication do not live for ever
                    jobs.Add(RecurringJobSlots.IngressDedupPurge, _ => { dedup.Purge(IngressDedupRetention); return Task.CompletedTask; });
                    // W4.3: the board reconciler publishes what the board shows into the inbox as source "poll", the message a webhook sends
                    if (BoardReconcilerJob.Make(c, sp.GetRequiredService<ISystemCatalog>(), inboxPort, Environment.GetEnvironmentVariable) is { } reconcile) jobs.Add(RecurringJobSlots.BoardReconciler, reconcile);
                }
                // W8.1: the IncidentSaga of the watched environments of the application (incidents.environments; tdd). It listens to the Octopus watcher's deployment events and to the hotfix item, takes the health reads of its
                // job, and holds the delivery train (W9.9 attaches the train to an acting host; until then the hold and the rollback request have no route)
                if (sp.GetService<ISystemCatalog>() is { } incidentCatalog
                    && IncidentWiring.Make(c, incidentCatalog, WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds), Environment.GetEnvironmentVariable, m => host.PublishAsync(m)) is { } incidents)
                {
                    IncidentWiring.Attach(host, incidents);
                    jobs.Add(RecurringJobSlots.IncidentHealth, async ct => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["incidentHealth"] = await incidents.Health.Run(ct) }.ToJsonString()));
                    octopusPort = new ShadowOctopusPort(ev => ev is Fworker.Contracts.Seams.OctopusDeploymentChanged ? host.PublishAsync(ev) : Task.FromResult(true), enabled: true);   // (the other Octopus events concern the delivery train, which is not attached here yet)
                }
                if (c.Catalog.Enabled) jobs.Add(RecurringJobSlots.CatalogReread, async ct => { await sp.GetRequiredService<ISystemCatalog>().RefreshAsync(ct); });   // W4.3 (the loader keeps the last good snapshot on a failed read)
                recurring = RecurringJobSlots.Attach(host, jobs);   // W4.0 slots, filled by W4.3
            }
            else if (c.Engine.ShadowRuns && c.Instance.Home is { } shadowHome)
            {
                // W5.2: the delivery train in the shadow run. It sees the merge webhook (the inbox, W4.1; ItemMerged, W5.0) and the Octopus watcher's events (W4.2); its commands are only recorded.
                // The board reconciler stays out (v1 reads the board itself); a merge the webhook missed is found by the train's own reads.
                var cs = WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds);
                var map = new Fworker.Integrations.Correlation.SqlCorrelationMap(cs);
                var dedup = new Fworker.Integrations.Correlation.SqlIngressDedup(cs);
                IngressInboxWiring.Attach(host, new Fworker.Integrations.Correlation.IngressInboxProcessor(dedup, map, shadowHome));
                inboxPort = new IngressInboxPort(m => host.PublishAsync(m));
                var schemaStatus = sp.GetRequiredService<FactorySchemaStatus>();
                shadowHolder.Housekeeping = () => { if (schemaStatus.IsGood) dedup.Purge(IngressDedupRetention); };   // (the table is the scripts': not before the schema is good; no recurring jobs in the shadow run: the shadow's tick calls this once an hour)
                var parts = sp.GetService<Fworker.Contracts.Catalog.ISystemCatalog>() is { } catalog ? DeliveryShadowWiring.Make(c, catalog, cs, Environment.GetEnvironmentVariable) : null;
                var merged = parts is null ? null : ItemMergedWiring.Make(c, shadowHome, cs, Environment.GetEnvironmentVariable);
                if (parts is not null && merged is not null)
                {
                    ItemMergedWiring.Attach(host, merged, parts.Delivered);
                    DeliveryShadowWiring.Attach(host, parts);
                    shadowHolder.Shadow = parts.Shadow;
                }
                octopusPort = new ShadowOctopusPort(ev => { if (ev is OctopusReplayBegan) shadowHolder.Shadow?.ReplayBegan(); return DeliveryShadowWiring.Publish(host, ev); }, shadowHolder.Shadow is not null);
            }
            return host;
        });
        if (c.Engine.V2Acts) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return new CopilotReconcileService(copilotReconciler!, sp.GetRequiredService<FactorySchemaStatus>()); });
        // W3.2: the plant's journal delegate publishes through this (AddGatewayPlant); resolving it builds the host first, which makes the sink
        if (c.Engine.V2Acts) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return journalSink!; });
        // W4.1: resolving these builds the host first, which makes them (null without a home repository)
        if (c.Engine.V2Acts)
        {
            if (c.Instance.Home != null)
            {
                services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return correlations!; });
                services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return inboxPort!; });
            }
        }
        // W9.4: the way for the Octopus watcher's events into the acting train (the same port the shadow run uses; W8.1: also the Octopus watcher's deployment events to the incident saga, so it is registered whenever there is a home repository)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return octopusPort ?? new ShadowOctopusPort(_ => Task.FromResult(false), false); });
        // W6.1: the items the saga owns, for the sweep (resolving it builds the host first, which makes it; null without a home repository)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return v2Ownership!; });
        // W6.6: the jobs of the recurring slots, for a look at which are filled (resolving it builds the host first, which fills them)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return jobs; });
        // W6.6: the saga's parts, for the verbs' feed (S1) (resolving it builds the host first, which makes them)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return workItemParts!; });
        // W9.10h: the producers of the saga's inputs that are the gateway's own paths (the park and unpark of the runtime adapters, the credit guard and the admin endpoint; the runtime manager's budgets and refused starts)
        if (c.Engine.V2Acts && c.Instance.Home is { } sagaHome)
            services.AddSingleton(sp =>
            {
                var parts = sp.GetRequiredService<WorkItemParts>(); var host = sp.GetRequiredService<WolverineHostService>();
                return new SagaInputs(parts.Ownership!, parts.Store, sagaHome, m => host.PublishAsync(m), m => host.PublishAsync(m));
            });
        // W6.7: the views of the items the saga owns (status, dashboard, digest), read from the saga's table and the item history; no host is needed for a read, only the database. Nowhere else is it registered, so
        // every other environment answers exactly as before
        if (c.Engine.V2Acts && c.Database.ConnectionString is { } viewsConnection)
            services.AddSingleton(sp => new V2ItemViews(new SchemaGatedViewSource(sp.GetRequiredService<FactorySchemaStatus>(),
                new SqlWorkItemViewSource(() => WorkItemWiring.Open(WolverineHostService.WithConnectTimeout(viewsConnection, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds))))));
        // W9.10o: the health block of /api/v1/status, where the Wolverine host runs (shadow or v2: tdd shows it too) and a database is configured; the jobs and the re-arm are those of the engine that acts, absent in shadow
        if (c.Database.ConnectionString is { } healthConnection)
            services.AddSingleton(sp =>
            {
                _ = sp.GetRequiredService<WolverineHostService>();
                var cs = WolverineHostService.WithConnectTimeout(healthConnection, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds);
                return new V2Health(new SqlV2HealthSource(() => new Microsoft.Data.SqlClient.SqlConnection(cs)), sp.GetRequiredService<FactorySchemaStatus>(), c.Engine, () => c.Engine.V2Acts && c.Instance.Home != null ? jobs : null, () => rearm);
            });
        // W4.3: the scheduler of the recurring jobs (resolving it builds the host first, which fills the registry)
        if (c.Engine.V2Acts && c.Instance.Home != null)
            services.AddSingleton(sp => { var h = sp.GetRequiredService<WolverineHostService>(); return new RecurringJobScheduler(recurring!, jobs, r => h.PublishAsync(r), () => c.Leader.MayAct); });
        // W9.10p: where the engine acts and v1's AutoPromoter still promotes the app's uat and prod, the train's shadow of them is found through the same holder (the AutoPromoter's passes, the status endpoints)
        if (c.Engine.V2Acts && c.WolverineHost && c.Instance.Home != null && AutoPromoter.Runs(c.Instance, true))
            services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return shadowHolder; });
        // W5.2: the shadow run of the delivery train (resolving these builds the host first, which makes them)
        if (c.Engine.ShadowRuns && c.Instance.Home != null)
        {
            services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return shadowHolder; });
            services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return octopusPort!; });
            services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return inboxPort!; });
        }
        // W2.3: shadow history, only where the environment is a shadow environment (v2 acting records its own history, later)
        if (c.Engine.ShadowRuns)
            services.AddSingleton<IShadowHistory>(sp => new ShadowHistory(r => sp.GetRequiredService<WolverineHostService>().PublishAsync(r), c.Instance.Home));
        return services;
    }
}

/// <summary>The saga's views answer an empty set until the schema of the database is good: the read can run before the host has applied the scripts (a fresh database has no tables yet).</summary>
sealed class SchemaGatedViewSource(FactorySchemaStatus schema, IWorkItemViewSource inner) : IWorkItemViewSource
{
    public Task<WorkItemViewSet> Read(WorkItemViewQuery query, CancellationToken ct) =>
        schema.IsGood ? inner.Read(query, ct) : Task.FromResult(new WorkItemViewSet([], false));
}
