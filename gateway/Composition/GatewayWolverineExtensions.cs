using Fworker.Contracts.Seams;
using Fworker.Contracts.Catalog;
using Fworker.Integrations.Commands;
// The Wolverine host's registration (task W2.2, docs/design/wolverine-host.md). Nothing is registered without a connection string (FACTORY_DB_CONNECTION):
// no service, no hosted service, no database call, and the gateway is v1 exactly as before. With one, the services below join the graph and
// the hosted service is added by AddGatewayBackground (the only place hosted services are registered), after LeadershipService (the node leaves with the lease, so it starts and stops beside it).
static class GatewayWolverineExtensions
{
    /// <summary>How long a claim of the ingress deduplication is kept: longer than any redelivery of a webhook.</summary>
    public static readonly TimeSpan IngressDedupRetention = TimeSpan.FromDays(7);

    public static IServiceCollection AddGatewayWolverine(this IServiceCollection services, GatewayComposition c)
    {
        if (!c.WolverineHost) return services;
        services.AddSingleton(c.Database);
        services.AddSingleton(WolverineHostOptions.From(c.Instance));
        // built after LeadershipService, whose hand-over callbacks it wraps (the order of the callbacks is the order of the stop: hand-over, then the node leaves)
        JournalCommandSink? journalSink = null;
        CopilotReconciler? copilotReconciler = null;
        IngressCorrelations? correlations = null; IngressInboxPort? inboxPort = null;
        ShadowOctopusPort? octopusPort = null; var shadowHolder = new DeliveryShadowHolder();
        var jobs = new RecurringJobRegistry(); RecurringJobSlots? recurring = null;
        V2Ownership? v2Ownership = null; WorkItemParts? workItemParts = null;
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<LeadershipService>();
            var host = new WolverineHostService(sp.GetRequiredService<DatabaseOptions>(), sp.GetRequiredService<WolverineHostOptions>(), sp.GetRequiredService<GatewayLeader>());
            if (c.Engine.V2Acts)
            {
                // W3.3: the Copilot launch runs under a reservation, settled by CopilotReconcileService (a hosted service of AddGatewayBackground)
                // with the database the host needs, the reservations and the idempotency ledger are in SQL and survive the kill the host is built for (#481)
                var stores = new SqlCommandStores(WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds));
                var reservations = stores.Reservations;
                copilotReconciler = new CopilotReconciler(reservations, new CopilotTaskLookup(sp.GetRequiredService<RuntimeRegistry>()));
                RuntimeCommandWiring.Attach(host, sp.GetRequiredService<RuntimeRegistry>(), new CopilotLaunchPort(sp.GetRequiredService<Fworker.Contracts.Seams.ICommandPort>(), reservations, copilotReconciler), stores.Ledger,
                    (runtime, execution, item) => correlations?.FromRuntime(runtime, execution, item));   // W4.1: the task or agent id finds its item later
                journalSink = JournalCommandWiring.Attach(host, c, ledger: stores.Ledger);   // W3.2: the plant's journal lines become commands
                // W6.0, W6.1: the WorkItemSaga of the hybrid composition: v1's plant still offers the sessions, the saga decides the items it has started, and v1's sweep leaves those items alone (V2Ownership)
                // W6.6: the docs plan's steps run in it; the board it moves cards on is the catalog's, read when the first card moves
                var workItems = WorkItemWiring.Make(c, WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds), boardRef: () => sp.GetService<ISystemCatalog>()?.Current.Board);
                WorkItemWiring.Attach(host, workItems);
                workItemParts = workItems;
                v2Ownership = workItems.Ownership;
                if (c.Instance.Home is { } home)
                {
                    // W4.1: webhook events into the durable inbox, deduplicated, with the correlation map (also written by the verbs and the runtimes)
                    var cs = WolverineHostService.WithConnectTimeout(c.Database.ConnectionString!, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds);
                    var map = new Fworker.Integrations.Correlation.SqlCorrelationMap(cs);
                    var dedup = new Fworker.Integrations.Correlation.SqlIngressDedup(cs);
                    IngressInboxWiring.Attach(host, new Fworker.Integrations.Correlation.IngressInboxProcessor(dedup, map, home));
                    // W5.0: ItemMerged, derived from the merge webhook and the board reconciler's poll (v1 emits nothing); needs GH_TOKEN or the author App to read the pull request and the item
                    if (ItemMergedWiring.Make(c, home, cs, Environment.GetEnvironmentVariable) is { } merged) ItemMergedWiring.Attach(host, merged, new Fworker.Workflows.Delivery.DeliveredToTable(() => ItemMergedWiring.Open(cs)));
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
                    correlations = new IngressCorrelations(map, home);
                    inboxPort = new IngressInboxPort(m => host.PublishAsync(m));
                    // W4.1 follow-up (#485): the claims of the deduplication do not live for ever
                    jobs.Add(RecurringJobSlots.IngressDedupPurge, _ => { dedup.Purge(IngressDedupRetention); return Task.CompletedTask; });
                    // W4.3: the board reconciler publishes what the board shows into the inbox as source "poll", the message a webhook sends
                    if (BoardReconcilerJob.Make(c, sp.GetRequiredService<ISystemCatalog>(), inboxPort, Environment.GetEnvironmentVariable) is { } reconcile) jobs.Add(RecurringJobSlots.BoardReconciler, reconcile);
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
                shadowHolder.Housekeeping = () => dedup.Purge(IngressDedupRetention);   // (no recurring jobs in the shadow run: the shadow's tick calls this once an hour)
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
        if (c.Engine.V2Acts) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return new CopilotReconcileService(copilotReconciler!); });
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
        // W6.1: the items the saga owns, for the sweep (resolving it builds the host first, which makes it; null without a home repository)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return v2Ownership!; });
        // W6.6: the jobs of the recurring slots, for a look at which are filled (resolving it builds the host first, which fills them)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return jobs; });
        // W6.6: the saga's parts, for the verbs' feed (S1) (resolving it builds the host first, which makes them)
        if (c.Engine.V2Acts && c.Instance.Home != null) services.AddSingleton(sp => { _ = sp.GetRequiredService<WolverineHostService>(); return workItemParts!; });
        // W4.3: the scheduler of the recurring jobs (resolving it builds the host first, which fills the registry)
        if (c.Engine.V2Acts && c.Instance.Home != null)
            services.AddSingleton(sp => { var h = sp.GetRequiredService<WolverineHostService>(); return new RecurringJobScheduler(recurring!, jobs, r => h.PublishAsync(r), () => c.Leader.MayAct); });
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
