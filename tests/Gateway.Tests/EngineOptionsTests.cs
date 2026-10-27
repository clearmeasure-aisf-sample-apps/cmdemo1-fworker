using Xunit;

namespace Gateway.Tests;

/// <summary>The engine switch and the FACTORY_V2_OFF kill switch (task W0.0, gateway/Configuration/EngineOptions.cs): the effective mode for each environment.</summary>
public sealed class EngineOptionsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("engine-options-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Func<string, string?> Env(params (string Name, string? Value)[] vars) { var d = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal); return n => d.TryGetValue(n, out var v) ? v : null; }

    InstanceConfig Config(string? json)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        if (json != null) File.WriteAllText(path, json);
        return InstanceConfig.Load(path, Env());
    }

    internal static string BlockFor(string v2, string shadow) => Block(v2, shadow);
    static string Block(string v2, string shadow) =>"{\"engine\":{\"v2Environments\":[" + v2 + "],\"shadowEnvironments\":[" + shadow + "]}}";

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json")) && Directory.Exists(Path.Combine(d.FullName, "gateway"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void ShippedDefaults_EveryEnvironmentRunsV1()
    {
        var cfg = InstanceConfig.Load(Path.Combine(RepoRoot(), "config", "instance.json"), Env());
        foreach (var name in new[] { "tdd", "uat", "prod", "local", "" })
        {
            var e = EngineOptions.From(cfg, name, Env());
            Assert.Equal(EngineMode.V1, e.Mode);
            Assert.Equal(EngineMode.V1, e.Configured);
            Assert.False(e.KillSwitch);
            Assert.False(e.V2Acts);
            Assert.False(e.ShadowRuns);
        }
    }

    [Fact]
    public void ShippedDefaults_BothListsAreDeclaredAndEmpty()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")))!;
        Assert.Empty(root["engine"]!["v2Environments"]!.AsArray());
        Assert.Empty(root["engine"]!["shadowEnvironments"]!.AsArray());
    }

    [Fact]
    public void DeployOverrides_PutTddInShadowAndNothingInV2()
    {
        // W2 "tdd joins shadow mode": the deployed image merges engine.shadowEnvironments = [tdd] over the empty default; v2Environments stays empty
        // until W9.9, so v1 still acts everywhere. Changing this pin is a protected-area change (deploy.overrides.json) and needs its own pull request.
        var merged = ApplyOverrides.Merge(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")), File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")));
        Assert.Equal(new[] { "tdd" }, merged["engine"]!["shadowEnvironments"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Empty(merged["engine"]!["v2Environments"]!.AsArray());
        var cfg = Config(merged["engine"]!.ToJsonString() is var j ? "{\"engine\":" + j + "}" : "");
        Assert.Equal(EngineMode.Shadow, EngineOptions.From(cfg, "tdd", Env()).Mode);
        Assert.Equal(EngineMode.V1, EngineOptions.From(cfg, "uat", Env()).Mode);
        Assert.Equal(EngineMode.V1, EngineOptions.From(cfg, "prod", Env()).Mode);
    }

    [Theory]
    [InlineData("tdd", "shadow")]
    [InlineData("uat", "v1")]
    [InlineData("prod", "v1")]
    [InlineData("local", "v2")]
    public void OverridesApplied_EachEnvironmentResolvesOwnEntry(string environment, string label)
    {
        var expected = label switch { "v2" => EngineMode.V2, "shadow" => EngineMode.Shadow, _ => EngineMode.V1 };
        var cfg = Config(Block("\"local\"", "\"tdd\""));
        var e = EngineOptions.From(cfg, environment, Env());
        Assert.Equal(expected, e.Mode);
        Assert.Equal(expected, e.Configured);
        Assert.Equal(expected == EngineMode.V2, e.V2Acts);
        Assert.Equal(expected == EngineMode.Shadow, e.ShadowRuns);
    }

    [Theory]
    [InlineData(" TDD ")]
    [InlineData("Tdd")]
    public void EnvironmentAndListsAreTrimmedAndCaseInsensitive(string environment)
    {
        var e = EngineOptions.From(Config(Block("", "\" Tdd\"")), environment, Env());
        Assert.Equal(EngineMode.Shadow, e.Mode);
        Assert.Equal("tdd", e.Environment);
    }

    [Fact]
    public void V2WinsWhereAnEnvironmentIsInBothLists() =>
        Assert.Equal(EngineMode.V2, EngineOptions.From(Config(Block("\"tdd\"", "\"tdd\"")), "tdd", Env()).Mode);

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" yes ", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("off", false)]
    [InlineData(null, false)]
    public void KillSwitch_ReadsFactoryV2Off(string? value, bool on)
    {
        Assert.Equal(on, EngineOptions.IsKillSwitch(value));
        Assert.Equal(on, EngineOptions.From(Config(Block("", "")), "tdd", Env(("FACTORY_V2_OFF", value))).KillSwitch);
    }

    [Theory]
    [InlineData("tdd", "v2")]      // v2Environments
    [InlineData("local", "shadow")]
    [InlineData("uat", "v1")]
    public void KillSwitchOn_PutsEveryEnvironmentOnV1WithNoShadow(string environment, string configured)
    {
        var cfg = Config(Block("\"tdd\"", "\"local\""));
        var off = EngineOptions.From(cfg, environment, Env());
        Assert.Equal(configured, EngineOptions.Label(off.Mode));
        var on = EngineOptions.From(cfg, environment, Env(("FACTORY_V2_OFF", "1")));
        Assert.Equal(EngineMode.V1, on.Mode);
        Assert.Equal(configured, EngineOptions.Label(on.Configured));   // what the file names is still reported
        Assert.False(on.V2Acts);
        Assert.False(on.ShadowRuns);
    }

    [Theory]
    [InlineData(null)]                                          // no file
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"engine\":[1]}")]
    [InlineData("{\"engine\":{\"v2Environments\":\"tdd\",\"shadowEnvironments\":{\"tdd\":true}}}")]
    [InlineData("{\"engine\":{\"v2Environments\":[1,null,true,\"\"],\"shadowEnvironments\":null}}")]
    public void MissingOrMalformedConfiguration_IsV1(string? json)
    {
        var e = EngineOptions.From(Config(json), "tdd", Env());
        Assert.Equal(EngineMode.V1, e.Mode);
    }

    [Fact]
    public void Status_Block()
    {
        var s = EngineOptions.From(Config(Block("", "\"tdd\"")), "tdd", Env(("FACTORY_V2_OFF", "1"))).Status();
        Assert.Equal("v1", (string?)s["mode"]);
        Assert.Equal("shadow", (string?)s["configured"]);
        Assert.True((bool?)s["killSwitch"]);
        Assert.Equal("tdd", (string?)s["environment"]);
    }
}

/// <summary>The engine block of /api/v1/status in a gateway that starts on the shipped defaults (the test fixture's environment is tdd).</summary>
public sealed class EngineStatusTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task Status_ReportsTheEffectiveEngine()
    {
        if (!StubPwsh.Supported) return;
        var sp = gw.Host.Services;
        var engine = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<EngineOptions>(sp);
        Assert.Same(engine, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<EngineOptions>(sp));
        // plain v1 adds no block (the C0 pins list the keys): its absence means v1
        Assert.False(engine.Reported);
        var r = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Null(r.Json!["engine"]);
    }

    [Fact]
    public void Reported_OnlyWhereNotPlainV1()
    {
        var shadow = EngineOptions.From(InstanceConfig.Load(Write(EngineOptionsTests.BlockFor("", "\"tdd\"")), _ => null), "tdd", _ => null);
        Assert.True(shadow.Reported);
        Assert.False(EngineOptions.From(InstanceConfig.Load(Write(EngineOptionsTests.BlockFor("", "\"tdd\"")), _ => null), "uat", _ => null).Reported);
        Assert.True(EngineOptions.From(InstanceConfig.Load(Write("{}"), _ => null), "uat", n => n == "FACTORY_V2_OFF" ? "1" : null).Reported);
    }

    static string Write(string json) { var p = Path.Combine(Path.GetTempPath(), "engine-status-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(p, json); return p; }
}
