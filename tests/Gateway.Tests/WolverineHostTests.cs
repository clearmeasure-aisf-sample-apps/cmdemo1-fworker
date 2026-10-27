using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The Wolverine host (task W2.2, gateway/Composition/WolverineHostService.cs): registered only with FACTORY_DB_CONNECTION. Without it the graph, the hosted
/// order and /api/v1/status are what they were; with one, the host is a hosted service after LeadershipService that never holds up the start or /ready.
/// </summary>
internal static class WolverineHostTestSupport
{
    /// <summary>A connection string nothing listens on: the connection is refused at once.</summary>
    public const string Unreachable = "Server=127.0.0.1,9;Database=fw;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";

    public static readonly string? Sql = Environment.GetEnvironmentVariable("FACTORY_TEST_SQL");

    /// <summary>The service types that exist only with a connection string.</summary>
    public static readonly Type[] HostTypes = { typeof(DatabaseOptions), typeof(WolverineHostOptions), typeof(WolverineHostService) };

    /// <summary>The environment of the test gateways is tdd: lists it in engine.shadowEnvironments, which (with a connection string) registers the host (W2.3).</summary>
    public static void Shadow(GatewayEnvironment env) => env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray(), ["shadowEnvironments"] = new JsonArray("tdd") };

    public static WolverineHostOptions Fast() => new() { RetryFirst = TimeSpan.FromMilliseconds(50), RetryMax = TimeSpan.FromMilliseconds(200), StopTimeout = TimeSpan.FromSeconds(2) };
}

/// <summary>Without a connection string (every existing test): no host, no service, no hosted service, no status block.</summary>
public sealed class WolverineHostOffTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void NoConnectionString_NothingIsRegistered_AndTheHostedOrderIsToday()
    {
        if (!StubPwsh.Supported) return;
        Assert.Null(Environment.GetEnvironmentVariable("FACTORY_DB_CONNECTION"));
        foreach (var t in WolverineHostTestSupport.HostTypes) Assert.DoesNotContain(gw.Host.Captured!, d => d.ServiceType == t);
        Assert.DoesNotContain(gw.Host.Captured!, d => d.ServiceType.FullName?.StartsWith("Wolverine", StringComparison.Ordinal) == true || d.ServiceType.FullName?.StartsWith("Fworker.Persistence", StringComparison.Ordinal) == true);
        var order = CompositionGraph.GatewayHostedOrder(gw.Host.Services);
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "BaseWatch", "Sweeper", "AutoPromoter", "PlantTicker", "McpSessionSweeper" }, order);
        Assert.DoesNotContain(gw.Host.Services.GetServices<IHostedService>(), h => h.GetType().Namespace?.StartsWith("Wolverine", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NoConnectionString_StatusHasNoWolverineBlock()
    {
        if (!StubPwsh.Supported) return;
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        Assert.Null(a.Json?["wolverine"]);
    }
}

/// <summary>With a connection string and a database that cannot be reached: the gateway starts and is ready anyway, and the host keeps trying in the background.</summary>
public sealed class WolverineHostUnreachableFixture : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env) { env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); }
}

public sealed class WolverineHostUnreachableDatabaseTests(WolverineHostUnreachableFixture gw) : IClassFixture<WolverineHostUnreachableFixture>
{
    [Fact]
    public void Registered_AsSingletons_AndHostedRightAfterTheLease()
    {
        if (!StubPwsh.Supported) return;
        foreach (var t in WolverineHostTestSupport.HostTypes)
        {
            var ds = gw.Host.Captured!.Where(d => d.ServiceType == t).ToList();
            Assert.Single(ds);
            Assert.Equal(ServiceLifetime.Singleton, ds[0].Lifetime);
        }
        CompositionGraph.AssertSingletons(gw.Host.Services, gw.Host.Captured!);
        var order = CompositionGraph.GatewayHostedOrder(gw.Host.Services);
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "WolverineHostService", "BaseWatch", "Sweeper", "AutoPromoter", "PlantTicker", "McpSessionSweeper" }, order);
        Assert.Same(gw.Host.Services.GetRequiredService<WolverineHostService>(), gw.Host.Services.GetServices<IHostedService>().OfType<WolverineHostService>().Single());
    }

    [Fact]
    public async Task TheGatewayIsReady_AndTheStatusSaysTheHostIsStillTrying_WithoutTheConnectionString()
    {
        if (!StubPwsh.Supported) return;
        Assert.NotNull(gw.ReadyAfter);
        Assert.Equal(200, (await gw.Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        // the first attempt waits for a database that does not answer for the whole connect timeout (90 s): the start is "starting", not failed, and nothing else waits
        JsonNode? w = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            w = (await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["wolverine"];
            if ((int?)w?["attempts"] >= 1) break;
            await Task.Delay(100);
        }
        Assert.Equal("starting", (string?)w?["state"]);
        Assert.Equal("solo", (string?)w?["mode"]);
        Assert.Equal(1, (int?)w?["attempts"]);
        Assert.DoesNotContain("Password", w!.ToJsonString());
        Assert.DoesNotContain("127.0.0.1", w.ToJsonString());
    }
}

/// <summary>The stop with the lease on and the Wolverine host registered (its database does not answer): hand-over, then the transcripts' flush, within the grace.</summary>
public sealed class WolverineHostStopOrderTests
{
    [Fact]
    public Task StopOrder_HandOverBeforeTranscriptFlush_WithTheHostRegistered_Within30Seconds() => StopOrderTests.Run(env => { env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); });
}

/// <summary>The service itself, without a database: retries, never waits for it, leaves with the lease, comes back, stops at shutdown.</summary>
public sealed class WolverineHostServiceTests
{
    sealed class FakeHost : IHost
    {
        public int Started, Stopped, Disposed;
        public IServiceProvider Services => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) { Started++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stopped++; return Task.CompletedTask; }
        public void Dispose() => Disposed++;
    }

    static GatewayLeader Leader() => new(new Runner(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")), new LeaderSettings { On = true }, "0.0.0");

    static WolverineHostService Service(GatewayLeader leader, Func<string, WolverineHostOptions, IHost> factory, Func<string, CancellationToken, Task>? migrate = null) =>
        new(DatabaseOptions.From(n => n == "FACTORY_DB_CONNECTION" ? WolverineHostTestSupport.Unreachable : null), WolverineHostTestSupport.Fast(), leader)
        { HostFactory = factory, Migrate = migrate ?? ((_, _) => Task.CompletedTask) };

    static async Task Until(Func<bool> c, int seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (!c() && sw.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(10);
        Assert.True(c());
    }

    [Fact]
    public async Task StartAsync_DoesNotWaitForTheDatabase_AndRunsOnceItAnswers()
    {
        var release = new TaskCompletionSource();   // a paused database: the first connection takes its time
        var fake = new FakeHost();
        var svc = Service(Leader(), (_, _) => fake, async (_, ct) => await release.Task.WaitAsync(ct));
        var sw = Stopwatch.StartNew();
        await svc.StartAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(WolverineHostService.Phase.Starting, svc.State);
        Assert.Equal(0, fake.Started);
        release.SetResult();
        await Until(() => svc.State == WolverineHostService.Phase.Running);
        Assert.Equal(1, fake.Started);
        await svc.StopAsync(CancellationToken.None);
        Assert.Equal((1, 1), (fake.Stopped, fake.Disposed));
    }

    [Fact]
    public async Task AFailingStart_IsRetriedInTheBackground_UntilItWorks()
    {
        var tries = 0; var hosts = new List<FakeHost>();
        var svc = Service(Leader(), (_, _) => { var h = new FakeHost(); hosts.Add(h); return h; }, (_, _) => ++tries < 3 ? throw new InvalidOperationException("Password=secret") : Task.CompletedTask);
        await svc.StartAsync(CancellationToken.None);
        await Until(() => svc.State == WolverineHostService.Phase.Running);
        Assert.Equal(3, svc.Status()["attempts"]!.GetValue<int>());
        Assert.Null(svc.Status()["lastFailure"]);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailureIsReportedByItsTypeOnly()
    {
        var svc = Service(Leader(), (_, _) => new FakeHost(), (_, _) => throw new InvalidOperationException("Server=db;Password=secret"));
        await svc.StartAsync(CancellationToken.None);
        await Until(() => svc.Status()["lastFailure"] != null);
        Assert.Equal("InvalidOperationException", (string?)svc.Status()["lastFailure"]);
        Assert.DoesNotContain("secret", svc.Status().ToJsonString());
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Supersession_RunsTheHandOverFirst_ThenTheNodeLeaves_AndAStandbyThatTakesTheLeaseAgainBringsItBack()
    {
        var leader = Leader(); var order = new List<string>(); var hosts = new List<FakeHost>();
        leader.OnSuperseded = _ => { order.Add("hand-over"); return Task.CompletedTask; };   // what LeadershipService sets
        var svc = Service(leader, (_, _) => { var h = new FakeHost(); hosts.Add(h); return h; });
        await svc.StartAsync(CancellationToken.None);
        await Until(() => svc.State == WolverineHostService.Phase.Running);
        await leader.OnSuperseded!(null);
        order.Add(hosts[0].Stopped == 1 ? "node-left" : "node-still-up");
        Assert.Equal(new[] { "hand-over", "node-left" }, order);
        Assert.Equal(WolverineHostService.Phase.Stopped, svc.State);
        await leader.OnReacquired!(null);
        await Until(() => svc.State == WolverineHostService.Phase.Running);
        Assert.Equal(2, hosts.Count);
        await svc.StopAsync(CancellationToken.None);
        Assert.All(hosts, h => Assert.Equal(1, h.Stopped));
    }

    [Fact]
    public async Task SupersessionWhileStarting_StopsTheStart_AndNoHostIsLeftRunning()
    {
        var leader = Leader(); var fake = new FakeHost();
        var svc = Service(leader, (_, _) => fake, async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        await svc.StartAsync(CancellationToken.None);
        await leader.OnSuperseded!(null);
        Assert.Equal(WolverineHostService.Phase.Stopped, svc.State);
        Assert.Equal(0, fake.Started);
    }

    [Fact]
    public async Task StopAsync_IsBounded_WhenTheNodeDoesNotStop()
    {
        var svc = Service(Leader(), (_, _) => new SlowStopHost());
        await svc.StartAsync(CancellationToken.None);
        await Until(() => svc.State == WolverineHostService.Phase.Running);
        var sw = Stopwatch.StartNew();
        await svc.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), sw.Elapsed.ToString());
    }

    sealed class SlowStopHost : IHost
    {
        public IServiceProvider Services => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }

    [Fact]
    public void ConnectTimeout_OutlastsAResumingDatabase_AndNeverLowersAHigherOne()
    {
        Assert.Equal(90, new SqlConnectionStringBuilder(WolverineHostService.WithConnectTimeout("Server=s;Database=d;Connect Timeout=15", 90)).ConnectTimeout);
        Assert.Equal(120, new SqlConnectionStringBuilder(WolverineHostService.WithConnectTimeout("Server=s;Database=d;Connect Timeout=120", 90)).ConnectTimeout);
    }

    [Fact]
    public void Options_AreLongerThanTheDatabaseIdlePause_AndStaleOutlastsTheHeartbeat()
    {
        var o = new WolverineHostOptions();
        foreach (var t in new[] { o.HealthCheckPollingTime, o.NodeReassignmentPollingTime, o.ScheduledJobPollingTime, o.OrphanedMessageSweepPollingTime, o.HandledMessageCleanupPollingTime, o.DeduplicationCleanupPollingTime, o.MetricsCollectionSamplingInterval })
            Assert.True(t > TimeSpan.FromMinutes(60), t.ToString());
        Assert.True(o.StaleNodeTimeout > o.HealthCheckPollingTime);
        Assert.True(o.ConnectTimeoutSeconds >= 60);
    }

    [Fact]
    public void Options_CanBeChangedInTheConfiguration_AboveTheirFloor()
    {
        var path = Path.Combine(Path.GetTempPath(), "wolverine-opts-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """{"wolverine":{"healthCheckSeconds":600,"scheduledJobSeconds":1,"staleNodeSeconds":300,"connectTimeoutSeconds":"x"}}""");
        try
        {
            var o = WolverineHostOptions.From(InstanceConfig.Load(path, _ => null));
            Assert.Equal(TimeSpan.FromSeconds(600), o.HealthCheckPollingTime);
            Assert.Equal(TimeSpan.FromHours(4), o.ScheduledJobPollingTime);   // below its floor: the default
            Assert.Equal(TimeSpan.FromSeconds(1800), o.StaleNodeTimeout);    // not above the heartbeat: three beats
            Assert.Equal(90, o.ConnectTimeoutSeconds);                        // not a number: the default
        }
        finally { File.Delete(path); }
    }
}
