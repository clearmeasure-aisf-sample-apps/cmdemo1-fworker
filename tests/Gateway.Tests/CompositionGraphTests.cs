using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The gateway's object graph as the composition builds it (C5a, gateway-composition.md 3 B), pinned before the registrations moved out of
/// Program.cs into gateway/Composition/: the same services, each registered once as a singleton and resolving to one instance; the hosted
/// services in the same order, and the same instances as the singletons; the plant's hooks and the base watch's leader gate set.
/// </summary>
public static class CompositionGraph
{
    /// <summary>The services Program.cs registered before C5a; every one stays a single singleton registration.</summary>
    public static readonly Type[] Singletons =
    {
        typeof(EventHub), typeof(QcSettings), typeof(GatewayOptions), typeof(InstanceConfig), typeof(TimingOptions), typeof(RevocationOptions),
        typeof(MessagingSettings), typeof(KeeperSettings), typeof(GitHubBudget), typeof(TranscriptStore), typeof(TranscriptService), typeof(Runner),
        typeof(LeaderSettings), typeof(ILeadership), typeof(GatewayLeader), typeof(SnapshotCoordinator), typeof(ISnapshotCoordinator),
        typeof(LeadershipService), typeof(RevocationService), typeof(IRevocationService), typeof(OperatorReleaseLoader), typeof(IOperatorReleaseLoader),
        typeof(LeaseSettings), typeof(HostSettings), typeof(ManagerActionSettings), typeof(BaseUpdateSettings), typeof(RuntimeSettings),
        typeof(CursorCloudSettings), typeof(CopilotCloudSettings), typeof(Plant), typeof(Messenger), typeof(QcHub), typeof(BaseWatch),
    };

    /// <summary>The interfaces registered as aliases of a concrete singleton: both resolve to the same instance.</summary>
    public static readonly (Type Alias, Type Concrete)[] Aliases =
    {
        (typeof(ILeadership), typeof(GatewayLeader)), (typeof(ISnapshotCoordinator), typeof(SnapshotCoordinator)),
        (typeof(IRevocationService), typeof(RevocationService)), (typeof(IOperatorReleaseLoader), typeof(OperatorReleaseLoader)),
    };

    /// <summary>The gateway's own hosted services, in the order the host starts them (it stops them in reverse).</summary>
    public static string[] GatewayHostedOrder(IServiceProvider sp) =>
        sp.GetServices<IHostedService>().Select(h => h.GetType()).Where(t => t.Assembly == typeof(Plant).Assembly).Select(t => t.Name).ToArray();

    public static void AssertSingletons(IServiceProvider sp, IServiceCollection captured)
    {
        foreach (var t in Singletons)
        {
            var ds = captured.Where(d => d.ServiceType == t).ToList();
            Assert.True(ds.Count == 1, $"{t.Name} is registered {ds.Count} times (wanted once)");
            Assert.True(ds[0].Lifetime == ServiceLifetime.Singleton, $"{t.Name} is registered {ds[0].Lifetime} (wanted Singleton)");
            Assert.Same(sp.GetRequiredService(t), sp.GetRequiredService(t));
        }
        foreach (var (alias, concrete) in Aliases) Assert.Same(sp.GetRequiredService(concrete), sp.GetRequiredService(alias));
    }

    /// <summary>The hosted services that are also singletons are the very instances the host runs.</summary>
    public static void AssertHostedAreSingletons(IServiceProvider sp)
    {
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Same(sp.GetRequiredService<RevocationService>(), hosted.OfType<RevocationService>().Single());
        Assert.Same(sp.GetRequiredService<OperatorReleaseLoader>(), hosted.OfType<OperatorReleaseLoader>().Single());
        Assert.Same(sp.GetRequiredService<TranscriptService>(), hosted.OfType<TranscriptService>().Single());
        Assert.Same(sp.GetRequiredService<LeadershipService>(), hosted.OfType<LeadershipService>().Single());
        Assert.Same(sp.GetRequiredService<BaseWatch>(), hosted.OfType<BaseWatch>().Single());
    }
}

/// <summary>The default fixture: owner mode, no runtime switched on.</summary>
public sealed class CompositionGraphTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void Services_AreOneSingletonEach()
    {
        if (!StubPwsh.Supported) return;
        CompositionGraph.AssertSingletons(gw.Host.Services, gw.Host.Captured!);
    }

    [Fact]
    public void HostedServices_StartInTodaysOrder()
    {
        if (!StubPwsh.Supported) return;
        var order = CompositionGraph.GatewayHostedOrder(gw.Host.Services);
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "BaseWatch", "Sweeper", "PlantTicker" }, order);
        CompositionGraph.AssertHostedAreSingletons(gw.Host.Services);
    }

    [Fact]
    public void HostedServices_GatewaysOwnComeBeforeTheFrameworks()
    {
        if (!StubPwsh.Supported) return;
        // the gateway's hosted services are registered before Build(); the web host's own (GenericWebHostService) is added by Build() and starts last
        var all = gw.Host.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        var lastOwn = all.FindLastIndex(t => t.Assembly == typeof(Plant).Assembly);
        var firstOther = all.FindIndex(t => t.Assembly != typeof(Plant).Assembly);
        Assert.True(firstOther < 0 || firstOther > lastOwn, "hosted services: " + string.Join(", ", all.Select(t => t.FullName)));
    }

    [Fact]
    public void Plant_HooksAndGatesAreSet()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var plant = sp.GetRequiredService<Plant>();
        // owner mode: the lease's renewal, take-back and the sweep after a take-back run through the rule scripts and the hub
        Assert.NotNull(plant.OnRenew);
        Assert.NotNull(plant.OnTakeBack);
        Assert.NotNull(plant.RequestSweepAfterTakeBack);
        Assert.Same(sp.GetRequiredService<ISnapshotCoordinator>().Restored, plant.RestoredTask);
        Assert.NotNull(sp.GetRequiredService<BaseWatch>().MayAct);
        Assert.True(sp.GetRequiredService<BaseWatch>().MayAct!());   // no lease in the fixture: the gateway acts
        // the plant holds the registered settings and no runtime manager (none switched on)
        Assert.Same(sp.GetRequiredService<MessagingSettings>(), plant.Msg);
        Assert.Same(sp.GetRequiredService<QcSettings>(), plant.QcCfg);
        Assert.Same(sp.GetRequiredService<KeeperSettings>(), plant.Keeper);
        Assert.Null(plant.Runtimes);
        Assert.Null(plant.Cursor);
        Assert.Null(plant.Copilot);
        Assert.Null(plant.Octopus);
    }

    [Fact]
    public async Task Plant_TakeBackRunsExpireClaimThroughTheRuleScripts()
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        var before = StubPwsh.Read(gw.Env.StubLog).Count;
        // the stub answers {"ok":true} with exit 0: a take-back that the rule script accepted
        Assert.True(await plant.OnTakeBack!(424242, "alice", "test"));
        Assert.Contains(StubPwsh.Read(gw.Env.StubLog).Skip(before), c => c.Verb == "expire-claim");
        before = StubPwsh.Read(gw.Env.StubLog).Count;
        // and a renewal: claim-item, whose answer carries no lease_until here, so no new lease time
        Assert.Null(await plant.OnRenew!(424242, "alice"));
        Assert.Contains(StubPwsh.Read(gw.Env.StubLog).Skip(before), c => c.Verb == "claim-item");
    }
}

/// <summary>Cursor and Copilot switched on (their APIs unreachable): one RuntimeLoop each, between the sweeper and the plant ticker.</summary>
public sealed class RuntimesOnGateway : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env)
    {
        // the fixture's configuration file, written here (Apply writes instance.test.json, which this one replaces as FACTORY_CONFIG)
        Directory.CreateDirectory(env.Dir);
        var path = Path.Combine(env.Dir, "instance.runtimes.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(env.Root, "config", "instance.json")))!;
        cfg["ownerSessions"] = true;
        var m = cfg["messaging"] as JsonObject ?? new JsonObject(); cfg["messaging"] = m;
        m["enabled"] = true; m["testHooks"] = true; m["allowedWorkers"] = new JsonArray("*");
        cfg["intake"] = new JsonObject { ["enabled"] = false };
        cfg["plant"] = new JsonObject { ["oneItemPerSession"] = false };
        var api = (JsonObject)cfg["api"]!;
        foreach (var (kind, secretKey) in new[] { ("cursor", "apiKey"), ("copilot", "token") })
        {
            var c = (JsonObject)api[kind]!;
            c["enabled"] = true; c["apiUrl"] = "http://127.0.0.1:9"; c["gatewayUrl"] = "http://127.0.0.1:" + GatewayEnvironment.ApiPort; c[secretKey] = "test-" + kind;
            c["environments"] = new JsonObject();
        }
        File.WriteAllText(path, cfg.ToJsonString());
        env.Set("FACTORY_CONFIG", path);
    }
}

public sealed class CompositionGraphRuntimesTests(RuntimesOnGateway gw) : IClassFixture<RuntimesOnGateway>
{
    [Fact]
    public void Services_AreOneSingletonEach()
    {
        if (!StubPwsh.Supported) return;
        CompositionGraph.AssertSingletons(gw.Host.Services, gw.Host.Captured!);
    }

    [Fact]
    public void HostedServices_OneRuntimeLoopPerRuntime_InTodaysOrder()
    {
        if (!StubPwsh.Supported) return;
        var order = CompositionGraph.GatewayHostedOrder(gw.Host.Services);
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "BaseWatch", "Sweeper", "RuntimeLoop", "RuntimeLoop", "PlantTicker" }, order);
        CompositionGraph.AssertHostedAreSingletons(gw.Host.Services);
    }

    [Fact]
    public void Plant_HoldsTheRuntimeManagers_WiredByTheirLoops()
    {
        if (!StubPwsh.Supported) return;
        var plant = gw.Host.Services.GetRequiredService<Plant>();
        Assert.Null(plant.Runtimes);
        Assert.Null(plant.Octopus);
        Assert.NotNull(plant.Cursor);
        Assert.NotNull(plant.Copilot);
        Assert.Equal("cursor-cloud", plant.Cursor!.Kind);
        Assert.Equal("copilot-cloud", plant.Copilot!.Kind);
        foreach (var rm in new[] { plant.Cursor, plant.Copilot })
        {
            // set by the RuntimeLoop's factory, which the host ran at its start
            Assert.Same(plant, rm.Plant);
            Assert.NotNull(rm.Park);
            Assert.NotNull(rm.ReleaseClaim);
            // the owner record of the task that owns an item, wired after Build() (C5c moves this)
            Assert.NotNull(rm.Owned);
            Assert.NotNull(rm.Ending);
        }
        Assert.NotNull(plant.CopilotOwns);
        Assert.NotNull(plant.CursorOwns);
    }

    [Fact]
    public async Task Status_ShowsBothRuntimes()
    {
        if (!StubPwsh.Supported) return;
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        var rt = a.Json?["runtimes"];
        Assert.NotNull(rt);
        Assert.Contains("cursor", rt!.ToJsonString());
        Assert.Contains("copilot", rt.ToJsonString());
    }
}
