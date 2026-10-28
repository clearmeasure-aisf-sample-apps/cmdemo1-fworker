using System.Collections.Concurrent;
using Fworker.Persistence;
using JasperFx.CodeGeneration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The harness for Wolverine tracked sessions (W2.4): a database of its own on the SQL Server of FACTORY_TEST_SQL (the service container of tests.yml),
/// the migrations of Fworker.Persistence applied, and a Wolverine host on it with the durable inbox and outbox (schema "wolverine", Solo mode, as the
/// gateway's host runs). A test runs a message through the host and waits for everything it causes with <see cref="Track"/>, then reads the database.
/// Where FACTORY_TEST_SQL is unset (a developer machine) <see cref="Available"/> is false and a test returns at once. Later waves add their handlers
/// through the configure callback of <see cref="StartAsync"/>; the messages below are the harness's own and belong to no production code.
/// </summary>
public sealed class TrackedSessionHost : IAsyncDisposable
{
    public static readonly string? ServerConnection = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");
    public static bool Available => !string.IsNullOrWhiteSpace(ServerConnection);

    readonly string dbName = "fw_tracked_" + Guid.NewGuid().ToString("N")[..16];
    IHost? host;

    public string ConnectionString { get; private set; } = "";
    public IHost Host => host ?? throw new InvalidOperationException("StartAsync first");
    public HarnessProbe Probe { get; } = new();

    /// <summary>Makes the database, migrates it, starts the host. <paramref name="configure"/> adds options (handlers, policies) to the harness's.</summary>
    public static async Task<TrackedSessionHost> StartAsync(Action<WolverineOptions>? configure = null)
    {
        var h = new TrackedSessionHost();
        try { await h.InitAsync(configure); }
        catch { await h.DisposeAsync(); throw; }
        return h;
    }

    async Task InitAsync(Action<WolverineOptions>? configure)
    {
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{dbName}]", master);
            await create.ExecuteNonQueryAsync();
        }
        ConnectionString = new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = dbName }.ConnectionString;
        await using (var db = Open()) await db.Database.MigrateAsync();
        host = await BuildHost(configure);
    }

    /// <summary>
    /// Kills the host as a crash does: whatever a handler was doing is abandoned, then a new
    /// host starts on the same database with <paramref name="configure"/>. What was in the durable inbox and outbox stays there; the in-memory state of the old host is gone.
    /// </summary>
    public async Task RestartAsync(Action<WolverineOptions>? configure = null)
    {
        var old = host; host = null;
        // a crash does not wait for the handler it interrupts: the old host is stopped in the background and its abandoned handlers never come back
        if (old != null) _ = Task.Run(async () => { try { await old.StopAsync(); } catch { } try { old.Dispose(); } catch { } });
        SqlConnection.ClearAllPools();
        host = await BuildHost(configure);
    }

    async Task<IHost> BuildHost(Action<WolverineOptions>? configure)
    {
        return await new HostBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical))
            .ConfigureServices(s => s.AddSingleton(Probe).AddSingleton(this))
            .UseWolverine(w =>
            {
                w.ServiceName = "fworker-tracked-tests";
                w.Discovery.DisableConventionalDiscovery();
                // no runtime compiler (WolverineFx.RuntimeCompilation) is a package of this repository, as in the gateway's host: the harness's handlers are
                // pre-built MessageHandler<T> classes, which need no code generation. Handlers of W3 on are generated ahead of time or the package is added (follow-up).
                w.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                w.AddMessageHandler(new PingHandler(Probe));
                w.AddMessageHandler(new PongHandler(Probe));
                w.AddMessageHandler(new FlakyHandler(Probe));
                w.AddMessageHandler(new PoisonHandler(Probe));
                w.AddMessageHandler(new RecordHandler(Probe, this));
                // pre-built handlers are routed by hand: every harness message goes to one durable local queue (inbox and outbox in SQL Server)
                w.PublishMessage<HarnessPing>().ToLocalQueue("harness");
                w.PublishMessage<HarnessPong>().ToLocalQueue("harness");
                w.PublishMessage<HarnessFlaky>().ToLocalQueue("harness");
                w.PublishMessage<HarnessPoison>().ToLocalQueue("harness");
                w.PublishMessage<HarnessRecord>().ToLocalQueue("harness");
                w.LocalQueue("harness").UseDurableInbox();
                w.PersistMessagesWithSqlServer(ConnectionString, "wolverine");
                w.Durability.Mode = DurabilityMode.Solo;
                w.Policies.UseDurableLocalQueues();   // the durable inbox and outbox, as the gateway's host
                // the failure rules the examples prove: a transient failure is retried; a poison one is dead-lettered after three attempts in all
                w.OnException<HarnessTransientException>().RetryTimes(5);
                w.OnException<HarnessPoisonException>().RetryTimes(2).Then.MoveToErrorQueue();
                configure?.Invoke(w);
            })
            .StartAsync();
    }

    public FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>Runs <paramref name="message"/> through the host's handler pipeline and waits until it and every message it caused has been handled.</summary>
    public Task<ITrackedSession> Track(object message, int timeoutSeconds = 60) =>
        Host.TrackActivity().Timeout(TimeSpan.FromSeconds(timeoutSeconds)).DoNotAssertOnExceptionsDetected().InvokeMessageAndWaitAsync(message);

    /// <summary>Publishes <paramref name="message"/> (it goes through the durable local queue) and waits for the same.</summary>
    public Task<ITrackedSession> TrackSend(object message, int timeoutSeconds = 60) =>
        Host.TrackActivity().Timeout(TimeSpan.FromSeconds(timeoutSeconds)).DoNotAssertOnExceptionsDetected().SendMessageAndWaitAsync(message);

    public async Task<long> Count(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (host != null) { try { await host.StopAsync(TimeSpan.FromSeconds(10)); } catch { } host.Dispose(); }
        try
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"IF DB_ID('{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }
}

/// <summary>What the harness handlers saw, per key, for the assertions.</summary>
public sealed class HarnessProbe
{
    readonly ConcurrentDictionary<string, int> attempts = new();
    readonly ConcurrentDictionary<string, int> delivered = new();
    public int NextAttempt(string key) => attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
    public int Attempts(string key) => attempts.GetValueOrDefault(key);
    public void Delivered(string key) => delivered.AddOrUpdate(key, 1, (_, n) => n + 1);
    public int DeliveredCount(string key) => delivered.GetValueOrDefault(key);
}

// The harness's own messages: trivial, test-only, no production meaning.
/// <summary>Cascades <see cref="HarnessPong"/> (the outbox).</summary>
public record HarnessPing(string Key);
public record HarnessPong(string Key);
/// <summary>Fails with a transient error for the first <paramref name="FailTimes"/> attempts, then succeeds.</summary>
public record HarnessFlaky(string Key, int FailTimes);
/// <summary>Always fails with a poison error.</summary>
public record HarnessPoison(string Key);
/// <summary>Writes the ItemHistory row of the message (idempotent by MessageId and Kind); fails once after the write when asked, so Wolverine redelivers it.</summary>
public record HarnessRecord(string ItemKey, bool FailOnceAfterWrite);

public sealed class HarnessTransientException(string m) : Exception(m);
public sealed class HarnessPoisonException(string m) : Exception(m);
