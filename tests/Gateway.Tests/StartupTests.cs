using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>Today's start: the container validates, and the gateway starts with only the test configuration.</summary>
public sealed class StartupTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void ContainerValidates_WithValidateOnBuildAndValidateScopes()
    {
        if (!StubPwsh.Supported) return;
        // the host itself was built with both options (GatewayHost.CreateHost) and started; and, explicitly, the same registrations build again
        Assert.NotNull(gw.Host.Services);
        Assert.NotNull(gw.Host.Captured);
        var copy = new ServiceCollection();
        foreach (var d in gw.Host.Captured!) ((ICollection<ServiceDescriptor>)copy).Add(d);
        using var sp = copy.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(sp);
    }

    [Fact]
    public async Task StartsWithTestConfiguration_ReadyWithin10SecondsInOwnerMode()
    {
        if (!StubPwsh.Supported) return;
        Assert.NotNull(gw.ReadyAfter);
        Assert.Equal(200, (await gw.Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        var build = await gw.Host.SendAsync("GET", "/_build", GatewayEnvironment.ApiPort);
        Assert.Equal(200, build.Status);
        Assert.Equal(true, (bool?)build.Json?["ownerMode"]);
    }
}

/// <summary>
/// Today's stop, with the leader lease and durable transcripts on (the revocations are read, through the stub): the hand-over (the final plant
/// snapshot, then the lease released) runs before the transcripts' flush, because ApplicationStopping runs its callbacks last-registered
/// first; and the whole stop fits the platform's 30 s grace.
/// </summary>
public sealed class StopOrderTests
{
    [Fact]
    public Task StopOrder_HandOverBeforeTranscriptFlush_Within30Seconds() => Run(null);

    /// <summary>The scenario; <paramref name="change"/> adds to the environment (WolverineHostTests runs it again with the Wolverine host registered).</summary>
    public static async Task Run(Action<GatewayEnvironment>? change)
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPT_SAVE_SECONDS", "3600");
        env.Set("FACTORY_TIMING_leaderCheckSeconds", "3600");
        change?.Invoke(env);
        env.Apply();
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            // 1. acting: revocations read, lease acquired, snapshot looked for, lease checked
            JsonNode? status = null;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                var a = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
                status = a.Json;
                if (a.Status == 200 && (string?)status?["leader"]?["role"] == "acting") break;
                await Task.Delay(200);
            }
            Assert.Equal("acting", (string?)status?["leader"]?["role"]);
            Assert.Equal(true, (bool?)status?["transcripts"]?["durable"]);
            // 2. a dirty session: the save loop (every 10 s) tries it once and the stub refuses, so it stays dirty and is not tried again for
            // FACTORY_TRANSCRIPT_SAVE_SECONDS; only the flush at the stop saves it
            var post = await host.SendAsync("POST", "/api/v1/runtime/transcript", GatewayEnvironment.ApiPort, Auth.WorkerToken(env.TokenSecret, "alice"), "{\"lines\":[\"a\",\"b\"]}");
            Assert.Equal(200, post.Status);
            sw.Restart();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                var t = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["transcripts"];
                if ((long?)t?["saveFailures"] >= 1 && (long?)t?["dirty"] >= 1 && (long?)t?["saving"] == 0) break;
                await Task.Delay(200);
            }
            Assert.Contains(StubPwsh.Read(env.StubLog), c => c.Verb == "transcript-save");
            File.WriteAllText(Path.Combine(env.Dir, "transcript-ok"), "");
            var before = StubPwsh.Read(env.StubLog).Count;
            // 3. the stop
            var stop = Stopwatch.StartNew();
            await host.DisposeAsync();
            disposed = true;
            stop.Stop();
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(30), $"the stop took {stop.Elapsed.TotalSeconds:0.0} s");
            var after = StubPwsh.Read(env.StubLog).Skip(before).ToList();
            var trail = string.Join(", ", after.Select(c => $"{c.Verb}{(c.Action != "" ? ":" + c.Action : "")}{(c.Final ? ":final" : "")}"));
            var snap = after.FindIndex(c => c.Verb == "plant-snapshot-save" && c.Final);
            var release = after.FindIndex(c => c.Verb == "leader-lease" && c.Action == "release");
            var flush = after.FindIndex(c => c.Verb == "transcript-save");
            Assert.True(snap >= 0 && release > snap && flush > release, $"wanted the final snapshot, then the lease release, then the transcript save; the stub saw: {trail}");
        }
        finally
        {
            if (!disposed) await host.DisposeAsync();
        }
    }
}
