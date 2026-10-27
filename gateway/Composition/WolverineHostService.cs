using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.SqlServer;

// The Wolverine host (task W2.2, docs/design/wolverine-host.md), registered only when FACTORY_DB_CONNECTION is set (GatewayWolverineExtensions). It is a second,
// nested host: Wolverine's own container and hosted services, which this service starts and stops, so that
// - the gateway's DI graph and its start are what they were (a database that is paused, down or refusing never fails the gateway's start or /ready: the start
//   below runs in the background and retries),
// - the stop is ours: the node leaves when the lease is lost and at the stop of the host, after the hand-over and the transcripts' flush.
// What it does once up: applies the migrations of Fworker.Persistence (the gateway's tables, schema "factory"), then runs Wolverine against SQL Server with the
// durable inbox and outbox (schema "wolverine") in Balanced mode, which is Wolverine's leader election: the nodes elect one leader that owns the agents.
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
    public Func<string, WolverineHostOptions, IHost> HostFactory { get; set; } = BuildHost;
    /// <summary>Applies the migrations; replaceable by a test.</summary>
    public Func<string, CancellationToken, Task> Migrate { get; set; }

    public WolverineHostService(DatabaseOptions database, WolverineHostOptions options, GatewayLeader leader)
    {
        connectionString = database.ConnectionString ?? throw new InvalidOperationException("the Wolverine host needs FACTORY_DB_CONNECTION");
        this.options = options; leadership = leader;
        Migrate = (cs, ct) => MigrateAsync(cs, options.ConnectTimeoutSeconds, ct);
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
        return Task.CompletedTask;   // never waits for the database: /ready does not depend on it
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
                lock (gate) attempts++;
                await Migrate(connectionString, ct);
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

    static async Task MigrateAsync(string connectionString, int connectTimeoutSeconds, CancellationToken ct)
    {
        var cs = WithConnectTimeout(connectionString, connectTimeoutSeconds);
        await using var db = new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);
        await db.Database.MigrateAsync(ct);
    }

    static IHost BuildHost(string connectionString, WolverineHostOptions o)
    {
        var cs = WithConnectTimeout(connectionString, o.ConnectTimeoutSeconds);
        return new HostBuilder()
            // errors only: a SqlClient message can name the server
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error).AddConsole())
            .UseWolverine(w =>
            {
                w.ServiceName = o.ServiceName;
                w.Discovery.DisableConventionalDiscovery();   // inert: no handler of this assembly is registered
                // no handler means no generated code: Static needs no runtime compiler (WolverineFx.RuntimeCompilation) in the image; a wave that adds handlers pre-generates or adds it
                w.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                w.PersistMessagesWithSqlServer(cs, o.Schema);
                w.Durability.Mode = o.LeaderElection ? DurabilityMode.Balanced : DurabilityMode.Solo;
                var d = w.Durability;
                d.HealthCheckPollingTime = o.HealthCheckPollingTime; d.FirstHealthCheckExecution = o.FirstHealthCheckExecution; d.StaleNodeTimeout = o.StaleNodeTimeout;
                d.NodeReassignmentPollingTime = o.NodeReassignmentPollingTime; d.FirstNodeReassignmentExecution = o.FirstNodeReassignmentExecution;
                d.ScheduledJobPollingTime = o.ScheduledJobPollingTime; d.ScheduledJobFirstExecution = o.ScheduledJobFirstExecution;
                d.OrphanedMessageSweepPollingTime = o.OrphanedMessageSweepPollingTime; d.HandledMessageCleanupPollingTime = o.HandledMessageCleanupPollingTime;
                d.DeduplicationCleanupPollingTime = o.DeduplicationCleanupPollingTime; d.MetricsCollectionSamplingInterval = o.MetricsCollectionSamplingInterval;
            })
            .Build();
    }
}
