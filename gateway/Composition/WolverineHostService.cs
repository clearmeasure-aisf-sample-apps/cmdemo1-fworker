using System.Text.Json.Nodes;
using Fworker.Database;
using Microsoft.Data.SqlClient;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;

// The Wolverine host (task W2.2, docs/design/wolverine-host.md), registered only when FACTORY_DB_CONNECTION is set (GatewayWolverineExtensions). It is a second,
// nested host: Wolverine's own container and hosted services, which this service starts and stops, so that
// - the gateway's DI graph and its start are what they were (a database that is paused, down or refusing never fails the gateway's start: the start below runs in
//   the background and retries; /ready waits for the schema only where a database is configured, FactorySchemaStatus, and for nothing else of the database),
// - the stop is ours: the node leaves when the lease is lost and at the stop of the host, after the hand-over and the transcripts' flush.
// What it does once up: applies the scripts of Fworker.Database with DbUp and checks that the database is what they build (the gateway's tables, schema "factory", and
// Wolverine's own, schema "wolverine"), then runs Wolverine against SQL Server with the durable inbox and outbox in Balanced mode, which is Wolverine's leader election:
// the nodes elect one leader that owns the agents. Wolverine builds and diffs nothing (AutoCreate.None): the scripts own every table (docs/design/fworker-database.md).
// It is inert: no handler is registered, nothing is published, nothing reads the board. The gateway's lease still decides who acts in v1.
sealed class WolverineHostService : IHostedService
{
    public enum Phase { Idle, Starting, Running, Stopped }

    readonly string connectionString;
    readonly WolverineHostOptions options;
    readonly ILeadership leadership;
    readonly object gate = new();
    CancellationTokenSource? cts;
    Task? starting;
    IHost? host;
    Phase phase = Phase.Idle;
    int attempts;
    DateTime? since, readyAt;
    string lastFailure = "";
    bool shuttingDown;

    /// <summary>Builds the host to start; replaceable by a test (the real one needs SQL Server).</summary>
    public Func<string, WolverineHostOptions, IHost> HostFactory { get; set; }
    /// <summary>W3.1: set only where the engine mode is V2. Adds the runtime commands' service, handlers and queue to the host; null leaves the host as W2.3 made it.</summary>
    public Action<IServiceCollection, WolverineOptions>? RuntimeCommands { get; set; }
    /// <summary>Applies the scripts of Fworker.Database and checks the schema against them; replaceable by a test. The report says whether the schema is good.</summary>
    public Func<string, CancellationToken, Task<SchemaReport>> Schema { get; set; }
    readonly FactorySchemaStatus schemaStatus;

    public WolverineHostService(DatabaseOptions database, WolverineHostOptions options, GatewayLeader leader, FactorySchemaStatus? schemaStatus = null)
    {
        connectionString = database.ConnectionString ?? throw new InvalidOperationException("the Wolverine host needs FACTORY_DB_CONNECTION");
        this.options = options; leadership = leader; this.schemaStatus = schemaStatus ?? new FactorySchemaStatus(required: true);
        Schema = (cs, ct) => FactorySchema.UpgradeAndVerifyAsync(WithConnectTimeout(cs, options.ConnectTimeoutSeconds),
            new SchemaRunOptions { CreateDatabase = database.CreateIfMissing, Log = line => { try { Console.WriteLine(line); } catch { } } }, ct);
        HostFactory = (cs, o) => BuildHost(cs, o, RuntimeCommands);
        // LeadershipService sets leader.OnSuperseded and OnReacquired when it is built, and the registration builds it first (GatewayWolverineExtensions); they are
        // wrapped here: its hand-over runs first, then the node leaves; a standby that takes the lease again brings the node back
        var superseded = leader.OnSuperseded; var reacquired = leader.OnReacquired;
        leader.OnSuperseded = async cur => { if (superseded != null) await superseded(cur); await LeaveAsync("superseded"); };
        leader.OnReacquired = async pred => { if (reacquired != null) await reacquired(pred); Begin(); };
    }

    public Phase State { get { lock (gate) return phase; } }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Begin();
        return Task.CompletedTask;   // never waits for the database: /ready waits for the schema (FactorySchemaStatus), which the background start below applies
    }

    // the retried start, in the background; one at a time
    void Begin()
    {
        lock (gate)
        {
            if (shuttingDown || leadership.Replaced || phase is Phase.Starting or Phase.Running) return;
            phase = Phase.Starting; since = DateTime.UtcNow; attempts = 0; lastFailure = "";
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            starting = Task.Run(() => RunAsync(ct));
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        var pause = options.RetryFirst;
        while (!ct.IsCancellationRequested)
        {
            IHost? h = null;
            try
            {
                int attempt;
                lock (gate) attempt = ++attempts;
                await ApplySchema(attempt, ct);
                h = HostFactory(connectionString, options);
                await h.StartAsync(ct);
                if (ct.IsCancellationRequested)
                {   // the node was told to leave while it started: it is stopped here, so that a Balanced leader's lock is not left to the end of the process
                    try { await h.StopAsync(options.StopTimeout); } catch { }
                    throw new OperationCanceledException(ct);
                }
                lock (gate)
                {
                    host = h; phase = Phase.Running; readyAt = DateTime.UtcNow; lastFailure = "";
                }
                Log("started", null);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await Dispose(h);
                return;
            }
            catch (Exception e)
            {
                await Dispose(h);
                // the type and the SQL error number only: the message of a connection failure can carry the server's name, and the string is a credential
                lock (gate) lastFailure = e is SqlException se ? $"SqlException {se.Number}" : e.GetType().Name;
                Log("failed", lastFailure);
            }
            try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { return; }
            pause = TimeSpan.FromTicks(Math.Min(options.RetryMax.Ticks, pause.Ticks * 2));
        }
    }

    // The scripts, then the check that the catalog is what they build. The Wolverine host is never started on a schema that is not good.
    async Task ApplySchema(int attempt, CancellationToken ct)
    {
        schemaStatus.Upgrading(attempt);
        SchemaReport report;
        try
        {
            report = await Schema(connectionString, ct);
            if (!report.Good) throw new SchemaNotGoodException(report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            schemaStatus.Failed(attempt, e);
            SchemaLog("failed", FactorySchemaStatus.Describe(e, out var script) + (script == null ? "" : " " + script));
            throw;
        }
        schemaStatus.Good(report);
        SchemaLog("good", $"applied {report.Applied.Count}, journaled {report.Journaled}, extra {report.Extra}, {(int)report.Duration.TotalMilliseconds} ms");
    }

    static void SchemaLog(string what, string detail)
    {
        try { Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "schema", ["event"] = what, ["detail"] = detail }.ToJsonString()); } catch { }
    }

    // The node leaves (the lease is lost): stops what runs and what is being started. Bounded; a standby that takes the lease again begins anew.
    async Task LeaveAsync(string why)
    {
        IHost? h; Task? t; CancellationTokenSource? c;
        lock (gate) { h = host; host = null; t = starting; c = cts; if (phase != Phase.Idle) phase = Phase.Stopped; }
        c?.Cancel();
        try { if (t != null) await t.WaitAsync(options.StopTimeout); } catch { }
        if (h != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await h.StopAsync(options.StopTimeout); } catch (Exception e) { Console.Error.WriteLine($"wolverine stop: {e.GetType().Name}"); }
            await Dispose(h);
            Log("stopped", why + " " + (int)sw.Elapsed.TotalMilliseconds + " ms");
        }
    }

    /// <summary>
    /// Publishes one message through the running host (W2.3: shadow history). False when the host is not up; the caller drops the message. An exception is the
    /// bus's (a database that does not answer): the caller counts it.
    /// </summary>
    public async Task<bool> PublishAsync<T>(T message) where T : notnull
    {
        IHost? h;
        lock (gate) h = phase == Phase.Running ? host : null;
        if (h == null) return false;
        using var scope = h.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message);
        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) shuttingDown = true;
        await LeaveAsync("shutdown");
    }

    static async Task Dispose(IHost? h)
    {
        if (h == null) return;
        try { h.Dispose(); } catch { }
        await Task.CompletedTask;
    }

    static void Log(string what, string? detail)
    {
        try { Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "wolverine", ["event"] = what, ["detail"] = detail }.ToJsonString()); } catch { }
    }

    /// <summary>The block of /api/v1/status: where the host is, never the connection string.</summary>
    public JsonObject Status()
    {
        lock (gate)
            return new JsonObject
            {
                ["state"] = phase.ToString().ToLowerInvariant(), ["mode"] = options.LeaderElection ? "balanced" : "solo", ["attempts"] = attempts, ["since"] = since?.ToString("o"),
                ["startedAt"] = readyAt?.ToString("o"), ["lastFailure"] = lastFailure == "" ? null : lastFailure,
                ["healthCheckSeconds"] = (long)options.HealthCheckPollingTime.TotalSeconds, ["scheduledJobSeconds"] = (long)options.ScheduledJobPollingTime.TotalSeconds,
            };
    }

    /// <summary>The connection string with a connect timeout that outlasts a database resuming from its pause.</summary>
    public static string WithConnectTimeout(string connectionString, int seconds)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (b.ConnectTimeout < seconds) b.ConnectTimeout = seconds;
        return b.ConnectionString;
    }

    /// <summary>The connection string Wolverine's message store uses: the connect timeout, and SqlClient's default command timeout (the start probes four tables by existence and builds nothing).</summary>
    internal static string ForMessageStore(string connectionString, int connectTimeoutSeconds) => WithConnectTimeout(connectionString, connectTimeoutSeconds);

    /// <summary>Where Wolverine writes the code it generates: the image's /app is not writable for the gateway's user, so the default (Internal/Generated under the content root) logs "Unable to write code file".</summary>
    internal static string CodegenDirectory => Path.Combine(Path.GetTempPath(), "fworker-wolverine-codegen");

    /// <summary>Auto (not Dynamic, which compiles everything on every start): pre-generated code is loaded when there is some, compiled otherwise, and written to a writable directory.</summary>
    internal static void ConfigureCodeGeneration(WolverineOptions w)
    {
        w.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        var dir = CodegenDirectory;
        try { Directory.CreateDirectory(dir); } catch (Exception) { /* generation still works without a written copy; Wolverine logs it */ }
        w.CodeGeneration.GeneratedCodeOutputPath = dir;
    }

    /// <summary>
    /// The host as BuildHost makes it, with a connection string that parses and is never opened, built and never started. The golden test of Wolverine's storage reads the DDL of its
    /// message store from it (WolverineStorageScriptTests), so the exported DDL always comes from the gateway's real options. <paramref name="runtimeCommands"/> adds a durable local
    /// queue, the one thing the runtime commands' hook adds that touches storage. <paramref name="connectionString"/> is for the test that checks a database against Wolverine's
    /// own model (TheScriptsBuildWolverinesStorage); the export needs none.
    /// </summary>
    internal static IHost BuildHostForSchemaExport(WolverineHostOptions o, bool runtimeCommands, string? connectionString = null) =>
        BuildHost(connectionString ?? "Server=tcp:export.invalid,1433;Database=export;User Id=export;Password=export;Encrypt=False;TrustServerCertificate=True", o,
            runtimeCommands ? (_, w) => w.LocalQueue("runtime-commands").UseDurableInbox() : null);

    static IHost BuildHost(string connectionString, WolverineHostOptions o, Action<IServiceCollection, WolverineOptions>? runtimeCommands = null)
    {
        var cs = WithConnectTimeout(connectionString, o.ConnectTimeoutSeconds);
        WolverineOptions? wolverine = null;
        return new HostBuilder()
            // errors only: a SqlClient message can name the server
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error).AddConsole())
            .ConfigureServices(s => s.AddSingleton(new ShadowHistoryStore(cs)))
            .UseWolverine(w =>
            {
                w.ServiceName = o.ServiceName;
                w.Discovery.DisableConventionalDiscovery();   // no handler is found by convention; the handlers below are named
                // W2.3: the one handler is shadow history's (ShadowHistoryHandler), whose code Wolverine generates at its first use: Auto loads pre-generated code when there is
                // some and compiles otherwise (the WolverineFx.RuntimeCompilation package, added with W2.3)
                w.Discovery.IncludeType(typeof(ShadowHistoryHandler));
                ConfigureCodeGeneration(w);
                // The scripts of Fworker.Database build Wolverine's tables (011); Wolverine neither builds nor diffs them. None skips the Weasel diff at start (it took about 80 s on the
                // S0 database of tdd) and keeps four cheap existence probes. OverrideAutoCreateResources stops the on-demand paths (IMessageStoreAdmin.MigrateAsync, storage clear-handled).
                // Never call IHost.SetupResources() or ResetResourceState(): they always migrate with CreateOrUpdate.
                w.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                w.PersistMessagesWithSqlServer(ForMessageStore(connectionString, o.ConnectTimeoutSeconds), SchemaNames.Wolverine).OverrideAutoCreateResources(AutoCreate.None);
                // shadow history: a durable local queue, so a record is stored in the database (wolverine schema) before it is handled; a database that does not
                // answer is retried with growing pauses, then the record goes to the dead letters
                w.PublishMessage<ShadowHistoryRecord>().ToLocalQueue("shadow-history");
                w.LocalQueue("shadow-history").UseDurableInbox().MaximumParallelMessages(2);
                w.Policies.UseDurableOutboxOnAllSendingEndpoints();
                // W9.10o: a transient fault of the database (the SQL error numbers of SqlFaults) and the conflict exceptions of the handlers are retried on a long, finite schedule (quick tries, then 5 minutes to 3 hours) before the dead letters,
                // and this rule comes first so that no rule after it takes the same exceptions; any other SqlException keeps the three quick tries and then the dead letters
                Fworker.Workflows.Faults.FaultPolicy.Apply(w);
                w.Policies.OnException<SqlException>().RetryWithCooldown(Fworker.Workflows.Faults.FaultPolicy.Quick).Then.MoveToErrorQueue();
                w.Durability.Mode = o.LeaderElection ? DurabilityMode.Balanced : DurabilityMode.Solo;
                var d = w.Durability;
                d.HealthCheckPollingTime = o.HealthCheckPollingTime; d.FirstHealthCheckExecution = o.FirstHealthCheckExecution; d.StaleNodeTimeout = o.StaleNodeTimeout;
                d.NodeReassignmentPollingTime = o.NodeReassignmentPollingTime; d.FirstNodeReassignmentExecution = o.FirstNodeReassignmentExecution;
                d.ScheduledJobPollingTime = o.ScheduledJobPollingTime; d.ScheduledJobFirstExecution = o.ScheduledJobFirstExecution;
                d.OrphanedMessageSweepPollingTime = o.OrphanedMessageSweepPollingTime; d.HandledMessageCleanupPollingTime = o.HandledMessageCleanupPollingTime;
                d.DeduplicationCleanupPollingTime = o.DeduplicationCleanupPollingTime; d.MetricsCollectionSamplingInterval = o.MetricsCollectionSamplingInterval;
                wolverine = w;
            })
            // after UseWolverine, whose callback has run by now: the runtime commands add their service, handlers and queue to the same options
            .ConfigureServices(s => { if (runtimeCommands != null && wolverine != null) runtimeCommands(s, wolverine); })
            .Build();
    }
}
