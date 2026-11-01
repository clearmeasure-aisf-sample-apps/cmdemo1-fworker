using Xunit;

namespace Gateway.Tests;

/// <summary>The engine switch (task W0.0, gateway/Configuration/EngineOptions.cs): the effective mode for each environment.</summary>
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

    internal static string BlockFor(string v2, string unused = "") => Block(v2);
    static string Block(string v2) => "{\"engine\":{\"v2Environments\":[" + v2 + "]}}";

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
            Assert.False(e.V2Acts);
        }
    }

    [Fact]
    public void ShippedDefaults_TheListIsDeclaredAndEmpty()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")))!;
        Assert.Empty(root["engine"]!["v2Environments"]!.AsArray());
        Assert.Null(root["engine"]!["shadowEnvironments"]);
    }

    [Fact]
    public void DeployOverrides_PutTddInV2()
    {
        // W9.9c "the tdd cutover": the deployed image merges engine.v2Environments = [tdd] over the empty default ; uat and
        // prod stay v1. Changing this pin is a protected-area change (deploy.overrides.json) and needs its own pull request.
        var merged = ApplyOverrides.Merge(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")), File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")));
        Assert.Equal(new[] { "tdd" }, merged["engine"]!["v2Environments"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Null(merged["engine"]!["shadowEnvironments"]);
        var cfg = Config(merged["engine"]!.ToJsonString() is var j ? "{\"engine\":" + j + "}" : "");
        Assert.Equal(EngineMode.V2, EngineOptions.From(cfg, "tdd", Env()).Mode);
        Assert.Equal(EngineMode.V1, EngineOptions.From(cfg, "uat", Env()).Mode);
        Assert.Equal(EngineMode.V1, EngineOptions.From(cfg, "prod", Env()).Mode);
    }

    [Fact]
    public void DeployOverrides_HoldTheRestOfTheW99cChecklistForTdd()
    {
        // W9.9c settings checklist on the merged configuration (the overrides over config/instance.json); a change of one of these is a change of the cutover and needs its own pull request.
        var merged = ApplyOverrides.Merge(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json")), File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")));
        string[] Strings(System.Text.Json.Nodes.JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.True(merged["intake"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(new[] { "docs", "feature" }, Strings(merged["intake"]!["v2Plans"]));
        Assert.Equal("tdd", merged["delivery"]!["finalEnvironment"]!.GetValue<string>());
        Assert.Equal("octopus", merged["scheduler"]!["runtime"]!.GetValue<string>());
        // #437 option A: the Copilot and Cursor runtimes are off for the measured batch
        Assert.False(merged["api"]!["cursor"]!["enabled"]!.GetValue<bool>());
        Assert.False(merged["api"]!["copilot"]!["environments"]!["tdd"]!["enabled"]!.GetValue<bool>());
        // uat and prod stay with v1's promoter; the merge strategy is the default's
        var overrides = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")))!;
        Assert.Null(overrides["delivery"]?["autoPromote"]?["trainActs"]);
        Assert.NotEqual(true, merged["delivery"]!["autoPromote"]!["trainActs"]?.GetValue<bool>());
        Assert.Null(overrides["mergeStrategy"]);
    }

    [Theory]
    [InlineData("tdd", "v1")]
    [InlineData("uat", "v1")]
    [InlineData("prod", "v1")]
    [InlineData("local", "v2")]
    public void OverridesApplied_EachEnvironmentResolvesOwnEntry(string environment, string label)
    {
        var expected = label == "v2" ? EngineMode.V2 : EngineMode.V1;
        var cfg = Config(Block("\"local\""));
        var e = EngineOptions.From(cfg, environment, Env());
        Assert.Equal(expected, e.Mode);
        Assert.Equal(expected, e.Configured);
        Assert.Equal(expected == EngineMode.V2, e.V2Acts);
    }

    [Theory]
    [InlineData(" TDD ")]
    [InlineData("Tdd")]
    public void EnvironmentAndListsAreTrimmedAndCaseInsensitive(string environment)
    {
        var e = EngineOptions.From(Config(Block("\" Tdd\"")), environment, Env());
        Assert.Equal(EngineMode.V2, e.Mode);
        Assert.Equal("tdd", e.Environment);
    }

    [Theory]
    [InlineData(null)]                                          // no file
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"engine\":[1]}")]
    [InlineData("{\"engine\":{\"v2Environments\":\"tdd\"}}")]
    [InlineData("{\"engine\":{\"v2Environments\":[1,null,true,\"\"]}}")]
    [InlineData("{\"engine\":{\"shadowEnvironments\":[\"tdd\"]}}")]   // (the shadow list is no setting any more)
    public void MissingOrMalformedConfiguration_IsV1(string? json)
    {
        var e = EngineOptions.From(Config(json), "tdd", Env());
        Assert.Equal(EngineMode.V1, e.Mode);
    }

    [Fact]
    public void Status_Block()
    {
        var s = EngineOptions.From(Config(Block("\"tdd\"")), "tdd", Env()).Status();
        Assert.Equal("v2", (string?)s["mode"]);
        Assert.Equal("v2", (string?)s["configured"]);
        Assert.Null(s["killSwitch"]);
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
        var v2 = EngineOptions.From(InstanceConfig.Load(Write(EngineOptionsTests.BlockFor("\"tdd\"")), _ => null), "tdd", _ => null);
        Assert.True(v2.Reported);
        Assert.False(EngineOptions.From(InstanceConfig.Load(Write(EngineOptionsTests.BlockFor("\"tdd\"")), _ => null), "uat", _ => null).Reported);
    }

    static string Write(string json) { var p = Path.Combine(Path.GetTempPath(), "engine-status-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(p, json); return p; }
}
