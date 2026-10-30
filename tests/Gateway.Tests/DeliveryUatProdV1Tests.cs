using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10p (#555, owner decision 2026-10-30): where the engine acts, the app's uat and prod stay with v1's AutoPromoter. The settings the train gets say so (the environments are decided in shadow, not promoted),
/// the loop runs unless delivery.autoPromote.trainActs hands the environments to the train (W9.12), and a gateway whose engine acts starts the loop. No database is needed: the gateway of the last test is the one of
/// WorkItemWiringTests, with a connection string nothing listens on.
/// </summary>
public sealed class DeliveryUatProdV1Tests
{
    static readonly RepoId Home = RepoId.Parse("acme/work");

    static InstanceConfig Config(string json, string env = "tdd")
    {
        var p = Path.Combine(Path.GetTempPath(), $"uprod-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return InstanceConfig.Load(p, n => n == "FACTORY_ENVIRONMENT" ? env : null);
    }

    static string Json(bool trainActs = false, bool dependencies = true, bool autoPromote = true, string gatewayEnvironment = "tdd") =>
        "{\"owner\":\"acme\",\"repo\":\"work\",\"octopus\":{\"project\":\"acme-ui\",\"environments\":{\"tdd\":\"Tdd\",\"uat\":\"Uat\",\"prod\":\"Prod\"}},\"delivery\":{" +
        $"\"dependenciesDelivered\":{(dependencies ? "true" : "false")},\"autoPromote\":{{\"enabled\":{(autoPromote ? "true" : "false")},\"environments\":[\"uat\",\"prod\"],\"gatewayEnvironment\":\"{gatewayEnvironment}\"{(trainActs ? ",\"trainActs\":true" : "")}}}}}}}";

    static IReadOnlyList<DeliveryPolicy> Policies(InstanceConfig cfg, bool engineActs)
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        return new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(cfg, () => snapshot, engineActs)).All();
    }

    // ---- the settings --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhereTheEngineActs_TheTrainDecidesUatAndProdInShadow_AndPromotesNone()
    {
        var s = DeliveryShadowSettings.From(InstanceConfig_(), engineActs: true)!;
        Assert.Empty(s.Environments!);
        Assert.Equal(["uat", "prod"], s.ShadowEnvironments);
        var p = Policies(InstanceConfig_(), engineActs: true);
        Assert.All(p.Where(x => x.Environment != "tdd"), x => { Assert.True(x.Acts); Assert.True(x.Shadow); Assert.False(x.DependenciesDelivered); });
        var tdd = p.Single(x => x.Environment == "tdd");   // the train acts for tdd only: it observes the lifecycle's deployment
        Assert.False(tdd.Acts); Assert.False(tdd.Shadow);
    }

    [Fact]
    public void TheShadowRun_IsWhatItWas_EveryNamedEnvironmentIsV1sAndAllTheShadowHandlersRecord()
    {
        var s = DeliveryShadowSettings.From(InstanceConfig_())!;
        Assert.Equal(["uat", "prod"], s.Environments);
        Assert.Empty(s.ShadowEnvironments!);
        Assert.All(Policies(InstanceConfig_(), engineActs: false), x => Assert.False(x.Shadow));
    }

    [Fact]
    public void WhenTheTrainActs_ItPromotesThem_AndTheRuleHoldsThere()
    {
        var cfg = InstanceConfig_(trainActs: true);
        Assert.True(cfg.TrainActs);
        var s = DeliveryShadowSettings.From(cfg, engineActs: true)!;
        Assert.Equal(["uat", "prod"], s.Environments);
        Assert.Empty(s.ShadowEnvironments!);
        Assert.All(Policies(cfg, engineActs: true).Where(x => x.Environment != "tdd"), x => { Assert.True(x.Acts); Assert.False(x.Shadow); Assert.True(x.DependenciesDelivered); });
    }

    [Fact]
    public void WhereTheAutoPromoteIsNotThisGateways_NothingIsShadowed_ThereIsNothingV1Promotes()
    {
        var elsewhere = Config(Json(gatewayEnvironment: "uat"));
        var s = DeliveryShadowSettings.From(elsewhere, engineActs: true)!;
        Assert.Empty(s.Environments!); Assert.Empty(s.ShadowEnvironments!);
        Assert.All(Policies(elsewhere, engineActs: true), x => { Assert.False(x.Acts); Assert.False(x.Shadow); });
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"delivery\":{\"autoPromote\":{}}}", false)]
    [InlineData("{\"delivery\":{\"autoPromote\":{\"trainActs\":false}}}", false)]
    [InlineData("{\"delivery\":{\"autoPromote\":{\"trainActs\":true}}}", true)]
    [InlineData("{\"delivery\":{\"autoPromote\":{\"trainActs\":\"yes\"}}}", false)]
    [InlineData("{\"delivery\":{\"autoPromote\":{\"trainActs\":1}}}", false)]
    [InlineData("{\"delivery\":\"x\"}", false)]
    public void TrainActs_IsOffUnlessTheFileSaysTrue(string json, bool expected) => Assert.Equal(expected, Config(json).TrainActs);

    [Fact]
    public void TheShippedDefaultsAndTheDeployedOverrides_LeaveUatAndProdWithV1()
    {
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        Assert.False(InstanceConfig.Load(Path.Combine(root, "config", "instance.json"), _ => null).TrainActs);
        // the deployed overrides name no trainActs (W9.12 adds it), and the setting exists in the defaults, so the merge of the image would accept it then
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "instance.json")))!["delivery"]!["autoPromote"]!["trainActs"]);
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "deploy.overrides.json")))!["delivery"]?["autoPromote"]?["trainActs"]);
    }

    static InstanceConfig InstanceConfig_(bool trainActs = false) => Config(Json(trainActs));

    // ---- the loop ------------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, true)]    // v1, autoPromote names this gateway
    [InlineData(true, false, true)]     // the engine acts and the train does not: the AutoPromoter still promotes uat and prod
    [InlineData(true, true, false)]     // W9.12: the train acts, the loop does not run
    [InlineData(false, true, true)]     // (trainActs means nothing where the engine does not act)
    public void TheAutoPromoter_RunsWhereV1Promotes_AndWhereTheEngineActsUntilTheTrainTakesOver(bool engineActs, bool trainActs, bool runs) =>
        Assert.Equal(runs, AutoPromoter.Runs(InstanceConfig_(trainActs), engineActs));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheAutoPromoter_DoesNotRunWhereAutoPromoteIsOffOrNotThisGateways(bool engineActs)
    {
        Assert.False(AutoPromoter.Runs(Config(Json(autoPromote: false)), engineActs));
        Assert.False(AutoPromoter.Runs(Config(Json(gatewayEnvironment: "uat")), engineActs));
    }

    [Fact]
    public void AGatewayWhoseEngineActs_StartsTheAutoPromoter_ForUatAndProd_AndTheTrainTakesItOnlyWhenTold()
    {
        if (!StubPwsh.Supported) return;
        Assert.True(Enabled(trainActs: false, v2: true), "the engine acts and the train does not promote: the AutoPromoter runs");
        Assert.False(Enabled(trainActs: true, v2: true), "the train promotes: the loop does not run");
        Assert.True(Enabled(trainActs: false, v2: false), "v1");
    }

    static bool Enabled(bool trainActs, bool v2)
    {
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.EditConfig = cfg =>
        {
            cfg["engine"] = new JsonObject { ["v2Environments"] = v2 ? new JsonArray("tdd") : new JsonArray(), ["shadowEnvironments"] = new JsonArray() };
            var ap = new JsonObject { ["enabled"] = true, ["environments"] = new JsonArray("uat", "prod"), ["gatewayEnvironment"] = "tdd" };
            if (trainActs) ap["trainActs"] = true;
            cfg["delivery"] = new JsonObject { ["autoPromote"] = ap };
        };
        env.Apply();
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway did not finish starting");
        return host.Services.GetServices<IHostedService>().OfType<AutoPromoter>().Single().Enabled;
    }
}
