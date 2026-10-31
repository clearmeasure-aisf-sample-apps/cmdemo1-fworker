using System.Text.Json.Nodes;
using Fworker.Workflows.Adoption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.9b: the adoption in a gateway, where it exists and where it must not. Only an engine that acts (with a database, a home repository and a GitHub credential) has the recurring job and the "adoption" block of the v2 status;
/// tdd's shadow run and v1 have neither (nothing of it is made), and the marker of the kill switch is written only by a process that the switch turned back to v1 in an environment named for v2.
/// </summary>
public sealed class AdoptionWiringTests
{
    static readonly string Token = "ghp_" + new string('A', 36);   // (composed at run time: a token the gateway takes for a credential)

    static GatewayEnvironment Env(string? mode, bool token = true, bool killSwitch = false)
    {
        var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        if (token) env.Set("GH_TOKEN", Token);
        if (killSwitch) env.Set("FACTORY_V2_OFF", "1");
        env.EditConfig = cfg => cfg["engine"] = new JsonObject
        {
            ["v2Environments"] = new JsonArray(mode == "v2" ? "tdd" : "other"), ["shadowEnvironments"] = new JsonArray(mode == "shadow" ? "tdd" : "other"),
        };
        env.Apply();
        return env;
    }

    static GatewayHost Start()
    {
        var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway did not finish starting");
        return host;
    }

    [Fact]
    public async Task AnActingEngine_HasTheAdoptionJob_AndItsBlockInTheV2Status()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env("v2");
        using var host = Start();
        Assert.True(host.Services.GetRequiredService<RecurringJobRegistry>().Has(RecurringJobSlots.Adoption));
        var block = await host.Services.GetRequiredService<V2Health>().Block(default);
        var adoption = Assert.IsType<JsonObject>(block["adoption"]);
        Assert.Equal("idle", (string?)adoption["state"]);   // (not yet run: the lease is off in the tests and the job waits for its first minute)
        // (review of #602: the saga's old-generation inputs are held from the building of the host until the host has decided the break and the adoption has run; the host decides before its listeners start)
        Assert.True((bool)adoption["hold"]!["held"]!);
        Assert.NotNull(host.Services.GetRequiredService<WolverineHostService>().BeforeHost);
        Assert.Equal(1, RecurringJobSlots.Default().Slots.Count(s => s.Name == RecurringJobSlots.Adoption));
    }

    [Fact]
    public async Task WithoutACredentialNothingCouldBeRead_NoJob_NoBlock()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env("v2", token: false);
        env.Set("GH_TOKEN", "unset");
        using var host = Start();
        Assert.False(host.Services.GetRequiredService<RecurringJobRegistry>().Has(RecurringJobSlots.Adoption));
        Assert.False((await host.Services.GetRequiredService<V2Health>().Block(default)).ContainsKey("adoption"));
    }

    [Fact]
    public async Task TheShadowRun_HasNoAdoption_AndWritesNoMarker()
    {
        if (!StubPwsh.Supported) return;
        using var env = Env("shadow");
        using var host = Start();
        Assert.Null(host.Services.GetService<RecurringJobRegistry>());
        Assert.Null(host.Services.GetRequiredService<WolverineHostService>().BeforeHost);   // (tdd's shadow run holds nothing)
        Assert.False((await host.Services.GetRequiredService<V2Health>().Block(default)).ContainsKey("adoption"));
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), h => h is KillSwitchMarkerService);
    }

    [Fact]
    public void TheKillSwitchMarker_IsRegisteredOnlyForAProcessTurnedBackToV1InAnEnvironmentNamedForV2()
    {
        if (!StubPwsh.Supported) return;
        using (var env = Env("v2", killSwitch: true))
        {
            using var host = Start();
            Assert.Single(host.Services.GetServices<IHostedService>().OfType<KillSwitchMarkerService>());
            Assert.Null(host.Services.GetService<RecurringJobRegistry>());   // (v1 acts: no engine, no job)
        }
        using (var env = Env("v2"))
        {
            using var host = Start();
            Assert.Empty(host.Services.GetServices<IHostedService>().OfType<KillSwitchMarkerService>());
        }
        using (var env = Env("shadow", killSwitch: true))
        {
            using var host = Start();
            Assert.Empty(host.Services.GetServices<IHostedService>().OfType<KillSwitchMarkerService>());
        }
    }

    [Fact]
    public async Task TheMarkerServiceWritesV1RanOnce_AndGivesUpQuietlyWhenTheDatabaseDoesNot()
    {
        var markers = new InMemoryEngineMarkers();
        var service = new KillSwitchMarkerService(markers, "node-b", () => new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero));
        await service.StartAsync(default);
        await service.ExecuteTask!;
        Assert.Equal(("node-b", "FACTORY_V2_OFF"), (markers.Rows[EngineMarkerNames.V1Ran].Holder, markers.Rows[EngineMarkerNames.V1Ran].Detail));
        // a database that refuses is tried again, and the service stops with the host
        var failing = new InMemoryEngineMarkers { BeforeMark = _ => throw new InvalidOperationException("down") };
        var other = new KillSwitchMarkerService(failing, "node-b");
        await other.StartAsync(default);
        await other.StopAsync(default);
        Assert.Empty(failing.Rows);
    }
}
