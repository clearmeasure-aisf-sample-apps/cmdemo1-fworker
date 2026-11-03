using System.Text.Json.Nodes;
using Fworker.Database;

// How the Wolverine host runs (task W2.2, docs/design/wolverine-host.md). Two profiles, chosen by config/instance.json block "wolverine", key "mode":
// - "solo" (the default and the mode of the deployed gateway): one node, no node agent, no leader election; the gateway's lease decides who acts and the node runs only on
//   the holder (W9.10a, WolverineHostService). The remaining timers (the durability agent's scheduled-job poll, the orphan sweep, the clean-ups, metrics) are set to 4 hours
//   (from the time the database was serverless and paused after 60 minutes without a session; tdd is Azure SQL S0 since 2026-10-29, which does not pause, so the long
//   timers only keep an idle gateway quiet). No session stays open and no lock is held.
// - "balanced": Wolverine's leader election (the revision of #311, closed as superseded by the lease decision of 2026-10-30). Election needs the node agent's polling, and
//   the leader holds a SQL Server application lock on a session of its own for as long as it leads. Its timers are 30 s (Wolverine's own are 5 to 10 s). Not used by a deployment.
// The values can be changed in the same block (seconds); a value below its floor, or not a number, leaves the default of the profile.
sealed record WolverineHostOptions
{
    /// <summary>Schema of Wolverine's own tables (the inbox, outbox, nodes, dead letters); the gateway's tables are in "factory". Fixed: script 011 of Fworker.Database writes it as a literal.</summary>
    public const string Schema = SchemaNames.Wolverine;
    public string ServiceName { get; init; } = "fworker-gateway";
    /// <summary>Balanced mode with Wolverine's leader election; false is Solo.</summary>
    public bool LeaderElection { get; init; }
    /// <summary>Load the handler code generated at build time (gateway/Internal/Generated, WolverineCodegen) instead of compiling it at first use. That code is generated for the deployed configuration (deploy.overrides.json merged over the defaults), so only the deployed image turns it on: a gateway with other settings generates other code for the same handler names (WolverineHostService.ConfigureCodeGeneration).</summary>
    public bool PreGeneratedCode { get; init; }

    // Node heartbeat (the node agent's health check) and everything that rides the leader election: the first one is delayed too, so that a start does not poll.
    public TimeSpan HealthCheckPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan FirstHealthCheckExecution { get; init; } = TimeSpan.FromHours(1);
    // A node whose last heartbeat is older than this is recovered by the leader. Must be longer than the heartbeat; 3 beats.
    public TimeSpan StaleNodeTimeout { get; init; } = TimeSpan.FromHours(12);
    // Reassigning the agents of nodes that went away (leader election's check of the node table)
    public TimeSpan NodeReassignmentPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan FirstNodeReassignmentExecution { get; init; } = TimeSpan.FromHours(1);
    // The durability agent: scheduled messages, recovery of the inbox and outbox, the sweep of orphaned messages, clean-ups, metrics sampling
    public TimeSpan ScheduledJobPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan ScheduledJobFirstExecution { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan OrphanedMessageSweepPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan HandledMessageCleanupPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan DeduplicationCleanupPollingTime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan MetricsCollectionSamplingInterval { get; init; } = TimeSpan.FromHours(4);

    // A database that was paused takes about a minute to answer its first connection: the connection waits for it, and the start retries in the background.
    public int ConnectTimeoutSeconds { get; init; } = 90;
    public TimeSpan RetryFirst { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(60);
    // Bound of the host's stop at shutdown; the platform's grace is 30 s and the hand-over and the flush ran before this
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // The seconds of each setting of block "wolverine", by key; floors keep a typo from turning the polling on at full speed
    static readonly (string Key, int FloorSeconds)[] Keys =
    {
        ("healthCheckSeconds", 60), ("firstHealthCheckSeconds", 0), ("staleNodeSeconds", 120), ("nodeReassignmentSeconds", 60), ("firstNodeReassignmentSeconds", 0),
        ("scheduledJobSeconds", 60), ("firstScheduledJobSeconds", 0), ("orphanSweepSeconds", 60), ("cleanupSeconds", 60), ("deduplicationCleanupSeconds", 60),
        ("metricsSeconds", 60), ("connectTimeoutSeconds", 5), ("retryFirstSeconds", 1), ("retryMaxSeconds", 1), ("stopSeconds", 1),
    };

    // the timers of the balanced profile: the node agent has to run for an election, so they are short; the cost is the database awake
    public static readonly WolverineHostOptions BalancedProfile = new()
    {
        LeaderElection = true,
        HealthCheckPollingTime = TimeSpan.FromSeconds(30), FirstHealthCheckExecution = TimeSpan.FromSeconds(5), StaleNodeTimeout = TimeSpan.FromMinutes(5),
        NodeReassignmentPollingTime = TimeSpan.FromSeconds(30), FirstNodeReassignmentExecution = TimeSpan.FromSeconds(5),
        ScheduledJobPollingTime = TimeSpan.FromMinutes(1), ScheduledJobFirstExecution = TimeSpan.FromSeconds(5),
        OrphanedMessageSweepPollingTime = TimeSpan.FromMinutes(5), HandledMessageCleanupPollingTime = TimeSpan.FromMinutes(10),
        DeduplicationCleanupPollingTime = TimeSpan.FromMinutes(10), MetricsCollectionSamplingInterval = TimeSpan.FromMinutes(5),
    };

    public static WolverineHostOptions From(InstanceConfig cfg)
    {
        var block = cfg.Root?["wolverine"] as JsonObject;
        var d = block?["mode"] is JsonValue m && m.TryGetValue<string>(out var ms) && ms.Trim().Equals("balanced", StringComparison.OrdinalIgnoreCase) ? BalancedProfile : new WolverineHostOptions();
        if (block == null) return d;
        TimeSpan T(string key, TimeSpan dflt)
        {
            var floor = Keys.First(k => k.Key == key).FloorSeconds;
            // a test gateway (FACTORY_DB_CREATE, which nothing deployed sets) may poll for scheduled messages every 5 seconds: the delivery train looks again at its timers, and a suite of Tier 1 cannot wait a minute for each
            if (key == "scheduledJobSeconds" && Environment.GetEnvironmentVariable("FACTORY_DB_CREATE") == "1") floor = Math.Min(floor, 5);
            try { return block[key] is JsonValue v && v.TryGetValue<int>(out var s) && s >= floor ? TimeSpan.FromSeconds(s) : dflt; }
            catch { return dflt; }
        }
        var o = new WolverineHostOptions
        {
            LeaderElection = d.LeaderElection,
            PreGeneratedCode = block["preGeneratedCode"] is JsonValue pg && pg.TryGetValue<bool>(out var pgb) && pgb,
            HealthCheckPollingTime = T("healthCheckSeconds", d.HealthCheckPollingTime), FirstHealthCheckExecution = T("firstHealthCheckSeconds", d.FirstHealthCheckExecution),
            StaleNodeTimeout = T("staleNodeSeconds", d.StaleNodeTimeout), NodeReassignmentPollingTime = T("nodeReassignmentSeconds", d.NodeReassignmentPollingTime),
            FirstNodeReassignmentExecution = T("firstNodeReassignmentSeconds", d.FirstNodeReassignmentExecution),
            ScheduledJobPollingTime = T("scheduledJobSeconds", d.ScheduledJobPollingTime), ScheduledJobFirstExecution = T("firstScheduledJobSeconds", d.ScheduledJobFirstExecution),
            OrphanedMessageSweepPollingTime = T("orphanSweepSeconds", d.OrphanedMessageSweepPollingTime), HandledMessageCleanupPollingTime = T("cleanupSeconds", d.HandledMessageCleanupPollingTime),
            DeduplicationCleanupPollingTime = T("deduplicationCleanupSeconds", d.DeduplicationCleanupPollingTime), MetricsCollectionSamplingInterval = T("metricsSeconds", d.MetricsCollectionSamplingInterval),
            ConnectTimeoutSeconds = (int)T("connectTimeoutSeconds", TimeSpan.FromSeconds(d.ConnectTimeoutSeconds)).TotalSeconds,
            RetryFirst = T("retryFirstSeconds", d.RetryFirst), RetryMax = T("retryMaxSeconds", d.RetryMax), StopTimeout = T("stopSeconds", d.StopTimeout),
        };
        // the node must outlive a heartbeat: a stale timeout not above the heartbeat would have every node recovered as soon as it is quiet
        return o.StaleNodeTimeout > o.HealthCheckPollingTime ? o : o with { StaleNodeTimeout = o.HealthCheckPollingTime * 3 };
    }
}
