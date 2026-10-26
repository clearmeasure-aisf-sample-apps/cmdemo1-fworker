using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The plant manager on demand switched on (gateway/Runtimes/ManagerCopilot.cs): owner mode, messaging and the manager enabled, the Copilot
/// runtime on with its API unreachable and api.copilot.listSeconds 1, so the manager's tick runs every 5 s. Nothing is pending, so no task is
/// launched. Pinned before C5d moved the manager's construction and its tick loop out of Program.cs (ManagerCopilotService).
/// </summary>
public sealed class ManagerCopilotOnGateway : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env) => Write(env);

    /// <summary>Writes the fixture's configuration file and points FACTORY_CONFIG at it (before <see cref="GatewayEnvironment.Apply"/>).</summary>
    public static void Write(GatewayEnvironment env)
    {
        Directory.CreateDirectory(env.Dir);
        var path = Path.Combine(env.Dir, "instance.manager.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(env.Root, "config", "instance.json")))!;
        cfg["ownerSessions"] = true;
        var m = cfg["messaging"] as JsonObject ?? new JsonObject(); cfg["messaging"] = m;
        m["enabled"] = true; m["testHooks"] = true; m["allowedWorkers"] = new JsonArray("*");
        cfg["manager"] = new JsonObject { ["enabled"] = true, ["allowedNames"] = new JsonArray("supervisor") };
        cfg["intake"] = new JsonObject { ["enabled"] = false };
        cfg["plant"] = new JsonObject { ["oneItemPerSession"] = false };
        var c = (JsonObject)((JsonObject)cfg["api"]!)["copilot"]!;
        c["enabled"] = true; c["apiUrl"] = "http://127.0.0.1:9"; c["gatewayUrl"] = "http://127.0.0.1:" + GatewayEnvironment.ApiPort; c["token"] = "test-copilot";
        c["listSeconds"] = 1;
        c["manager"] = new JsonObject { ["enabled"] = true, ["maxPerDay"] = 3, ["cooldownMinutes"] = 0.25, ["maxMinutes"] = 1 };
        c["environments"] = new JsonObject();
        File.WriteAllText(path, cfg.ToJsonString());
        env.Set("FACTORY_CONFIG", path);
    }
}

public sealed class ManagerCopilotGatewayTests(ManagerCopilotOnGateway gw) : IClassFixture<ManagerCopilotOnGateway>
{
    static async Task<JsonNode?> ManagerCopilotStatus(GatewayHost host)
    {
        var a = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        return a.Json?["manager"]?["copilot"];
    }

    [Fact]
    public async Task Status_CarriesTheManagerCopilotBlock_WithTodaysFields()
    {
        if (!StubPwsh.Supported) return;
        var mc = await ManagerCopilotStatus(gw.Host);
        Assert.NotNull(mc);
        var keys = ((JsonObject)mc!).Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "enabled", "present", "running", "task", "url", "launches", "today", "maxPerDay", "cooldownMinutes", "maxMinutes", "last", "lastEnd", "minutes", "status", "usage", "next", "error" }, keys);
        Assert.Equal(true, (bool?)mc["enabled"]);
        Assert.Equal(false, (bool?)mc["present"]);
        Assert.Equal(0, (int?)mc["launches"]);
        Assert.Equal(3, (int?)mc["maxPerDay"]);
        Assert.Equal(0.25, (double?)mc["cooldownMinutes"]);
        Assert.Equal(1, (double?)mc["maxMinutes"]);
    }

    [Fact]
    public async Task TickLoop_RunsEveryFiveSeconds_AndFindsNothingPending()
    {
        if (!StubPwsh.Supported) return;
        // listSeconds below 5: the first tick comes 5 s after the start; it records why it launched nothing
        var sw = Stopwatch.StartNew();
        string next = "";
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            next = (string?)(await ManagerCopilotStatus(gw.Host))?["next"] ?? "";
            if (next != "") break;
            await Task.Delay(250);
        }
        Assert.Equal("nothing-pending", next);
        Assert.Equal(0, (int?)(await ManagerCopilotStatus(gw.Host))?["launches"]);
    }

    [Fact]
    public async Task McpTools_AskTheManagerWhetherATaskIsLive_AndEndIt()
    {
        if (!StubPwsh.Supported) return;
        var tools = GatewayMcp.Tools;
        Assert.NotNull(tools);
        Assert.NotNull(tools!.ManagerLive);
        Assert.NotNull(tools.ManagerEnd);
        // no task runs: no name is live, and an end is a no-op
        Assert.False(tools.ManagerLive!("mgr-cop-000000"));
        await tools.ManagerEnd!("mgr-cop-000000", "session-end");
        Assert.Equal(false, (bool?)(await ManagerCopilotStatus(gw.Host))?["running"]);
    }

    [Fact]
    public void Composition_RegistersTheManagerOnce_AndItsTickAfterThePlantTicker()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        CompositionGraph.AssertSingletons(sp, gw.Host.Captured!);
        foreach (var t in new[] { typeof(ManagerCopilot), typeof(ManagerCopilotService) })
        {
            var ds = gw.Host.Captured!.Where(d => d.ServiceType == t).ToList();
            Assert.True(ds.Count == 1 && ds[0].Lifetime == ServiceLifetime.Singleton, $"{t.Name}: {ds.Count} registrations");
        }
        // the two loops Program.cs started last, in that order, after the gateway's other hosted services
        Assert.Equal(new[] { "RevocationService", "OperatorReleaseLoader", "TranscriptService", "LeadershipService", "BaseWatch", "Sweeper", "RuntimeLoop", "PlantTicker", "CopilotCiReleaseService", "CloudOwnerWatch", "ManagerCopilotService", "McpSessionSweeper" },
            CompositionGraph.GatewayHostedOrder(sp));
        CompositionGraph.AssertHostedAreSingletons(sp);
        var tick = sp.GetServices<IHostedService>().OfType<ManagerCopilotService>().Single();
        Assert.Same(sp.GetRequiredService<ManagerCopilotService>(), tick);
        // api.copilot.listSeconds 1: the tick of a test plant, every 5 s
        Assert.Equal(TimeSpan.FromSeconds(5), tick.Interval);
        Assert.Equal(McpSessionSweeper.DefaultInterval, sp.GetServices<IHostedService>().OfType<McpSessionSweeper>().Single().Interval);
    }
}

/// <summary>The stop with the manager on: both loops end with the host, and the stop fits the platform's 30 s grace.</summary>
public sealed class ManagerCopilotStopTests
{
    [Fact]
    public async Task Stop_EndsTheManagerTickAndTheMcpSweep()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        ManagerCopilotOnGateway.Write(env);
        env.Apply();
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var hosted = host.Services.GetServices<IHostedService>().ToList();
            var tick = hosted.OfType<ManagerCopilotService>().Single();
            var sweep = hosted.OfType<McpSessionSweeper>().Single();
            Assert.NotNull(tick.ExecuteTask);
            Assert.NotNull(sweep.ExecuteTask);
            Assert.False(sweep.ExecuteTask!.IsCompleted);
            var stop = Stopwatch.StartNew();
            await host.DisposeAsync();
            disposed = true;
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(30), $"the stop took {stop.Elapsed.TotalSeconds:0.0} s");
            Assert.True(tick.ExecuteTask!.IsCompleted, "the manager's tick still runs after the stop");
            Assert.True(sweep.ExecuteTask.IsCompleted, "the MCP session sweep still runs after the stop");
        }
        finally
        {
            if (!disposed) await host.DisposeAsync();
        }
    }
}

/// <summary>The default fixture: the manager is not enabled, so there is no on-demand manager and no manager.copilot block.</summary>
public sealed class ManagerCopilotOffTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task Status_HasNoManagerCopilotBlock()
    {
        if (!StubPwsh.Supported) return;
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        Assert.Null(a.Json?["manager"]?["copilot"]);
    }

    [Fact]
    public async Task McpTools_ManagerHooksAnswerNoTask()
    {
        if (!StubPwsh.Supported) return;
        var tools = GatewayMcp.Tools;
        Assert.NotNull(tools);
        Assert.False(tools!.ManagerLive!("mgr-cop-000000"));
        await tools.ManagerEnd!("mgr-cop-000000", "session-end");
    }

    [Fact]
    public void Composition_RegistersNoManager_ButItsSettings()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        Assert.Null(sp.GetService<ManagerCopilot>());
        Assert.Null(sp.GetService<ManagerCopilotService>());
        Assert.Empty(sp.GetServices<IHostedService>().OfType<ManagerCopilotService>());
        Assert.False(sp.GetRequiredService<ManagerCopilotSettings>().Enabled);
    }
}
