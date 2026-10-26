using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>The configuration read at start is in the container (C3): one instance each, and the leader lease as <see cref="ILeadership"/>.</summary>
public sealed class ConfigurationRegistrationTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    static void Same<T>(IServiceProvider sp) where T : class
    {
        var a = sp.GetRequiredService<T>();
        Assert.Same(a, sp.GetRequiredService<T>());
    }

    [Fact]
    public void Singletons_ResolveToOneInstance()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        Same<GatewayOptions>(sp);
        Same<InstanceConfig>(sp);
        Same<TimingOptions>(sp);
        Same<RevocationOptions>(sp);
        Same<MessagingSettings>(sp);
        Same<KeeperSettings>(sp);
        Same<GitHubBudget>(sp);
        Same<ILeadership>(sp);
        Same<QcSettings>(sp);
        Same<LeaderSettings>(sp);
        Same<LeaseSettings>(sp);
        Same<HostSettings>(sp);
        Same<ManagerActionSettings>(sp);
        Same<BaseUpdateSettings>(sp);
        Same<RuntimeSettings>(sp);
        Same<CursorCloudSettings>(sp);
        Same<CopilotCloudSettings>(sp);
        // the plant holds the registered instances, not copies of its own
        var plant = sp.GetRequiredService<Plant>();
        Assert.Same(sp.GetRequiredService<LeaseSettings>(), plant.Lease);
        Assert.Same(sp.GetRequiredService<HostSettings>(), plant.Hosts);
        Assert.Same(sp.GetRequiredService<ManagerActionSettings>(), plant.MgrActions);
        Assert.Same(sp.GetRequiredService<MessagingSettings>(), plant.Msg);
        Assert.IsType<GatewayLeader>(sp.GetRequiredService<ILeadership>());
        Assert.False(sp.GetRequiredService<ILeadership>().On);   // FACTORY_LEADER_LEASE=0 in the fixture
        Assert.True(sp.GetRequiredService<ILeadership>().MayAct);
    }

    [Fact]
    public void Values_AreTheFixturesEnvironmentAndConfiguration()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var o = sp.GetRequiredService<GatewayOptions>();
        Assert.True(o.OwnerMode);
        Assert.Equal(GatewayEnvironment.ApiPort, o.ApiPort);
        Assert.Equal(3600, o.SweepSeconds);
        var c = sp.GetRequiredService<InstanceConfig>();
        Assert.Equal(Path.Combine(gw.Env.Dir, "instance.test.json"), c.Path);
        Assert.True(c.OwnerSessions);          // GatewayEnvironment.TestConfig switches them on
        Assert.False(c.OneItemPerSession);     // and plant.oneItemPerSession off
        Assert.True(sp.GetRequiredService<RevocationOptions>().SkipRevocations);
        Assert.False(sp.GetRequiredService<RevocationOptions>().GitHubConfigured);   // GH_TOKEN "unset"
    }
}
