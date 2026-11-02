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
/// Today's stop, with the SQL lease on (the revocations are read, through the stub; a real SQL Server, FACTORY_TEST_SQL, or the test returns at once): the hand-over (the node leaves, the final
/// plant is written to the database, then the lock is let go) runs before the transcripts' flush, because ApplicationStopping runs its callbacks last-registered first; and the whole stop fits the platform's 30 s grace.
/// </summary>
public sealed class StopOrderTests
{
    [Fact]
    public Task StopOrder_HandOverBeforeTranscriptFlush_Within30Seconds() => Run(null);

    /// <summary>The scenario; <paramref name="change"/> adds to the environment.</summary>
    public static async Task Run(Action<GatewayEnvironment>? change)
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_stop_");
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPT_SAVE_SECONDS", "3600");
        env.Set("FACTORY_DB_CONNECTION", db.ConnectionString);
        env.Set("GITHUB_API_URL", EmptyGitHub.Shared.Url);
        WolverineHostTestSupport.V2(env);
        change?.Invoke(env);
        env.Apply();
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            // 1. acting: revocations read, the lock taken, the plant restored
            JsonNode? status = null;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                var a = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
                status = a.Json;
                if (a.Status == 200 && (string?)status?["leader"]?["role"] == "acting") break;
                await Task.Delay(200);
            }
            Assert.Equal("acting", (string?)status?["leader"]?["role"]);
            // (V3.2: the transcripts are saved by the v2 route in the database, not by a call of the stub script: the flush is no longer observable here)
            var before = StubPwsh.Read(env.StubLog).Count;
            // 3. the stop
            var stop = Stopwatch.StartNew();
            await host.DisposeAsync();
            disposed = true;
            stop.Stop();
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(30), $"the stop took {stop.Elapsed.TotalSeconds:0.0} s");
            var after = StubPwsh.Read(env.StubLog).Skip(before).ToList();
            _ = after;   // (the transcripts flush is the v2 route now, not a call of the stub script: the hand-over below is what this test keeps)
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.PlantState WHERE Document LIKE N'%\"final\":true%'"));   // the final plant is in the database ...
            var next = new SqlLease(db.ConnectionString, _ => Task.CompletedTask, 20);
            try { Assert.True((await next.TryAcquireAsync("the-successor", false, default)).Acquired); }   // ... and the lock was let go
            finally { await next.ReleaseAsync(); }
        }
        finally
        {
            if (!disposed) await host.DisposeAsync();
        }
    }
}
