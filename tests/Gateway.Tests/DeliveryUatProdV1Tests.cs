using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The settings the delivery train gets for the app's uat and prod: since V3.2 v1's AutoPromoter is gone, so wherever delivery.autoPromote names this gateway the train promotes the named environments and
/// decides none of them in shadow.
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

    static string Json(bool dependencies = true, bool autoPromote = true, string gatewayEnvironment = "tdd") =>
        "{\"owner\":\"acme\",\"repo\":\"work\",\"octopus\":{\"project\":\"acme-ui\",\"environments\":{\"tdd\":\"Tdd\",\"uat\":\"Uat\",\"prod\":\"Prod\"}},\"delivery\":{" +
        $"\"dependenciesDelivered\":{(dependencies ? "true" : "false")},\"autoPromote\":{{\"enabled\":{(autoPromote ? "true" : "false")},\"environments\":[\"uat\",\"prod\"],\"gatewayEnvironment\":\"{gatewayEnvironment}\"}}}}}}";

    static IReadOnlyList<DeliveryPolicy> Policies(InstanceConfig cfg, bool engineActs)
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        return new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(cfg, () => snapshot, engineActs)).All();
    }

    // ---- the settings --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhereTheEngineActs_TheTrainPromotesUatAndProd_AndDecidesNoneInShadow()
    {
        var cfg = Config(Json());
        var s = DeliveryShadowSettings.From(cfg, engineActs: true)!;
        Assert.Equal(["uat", "prod"], s.Environments);
        Assert.Empty(s.ShadowEnvironments!);
        Assert.All(Policies(cfg, engineActs: true).Where(x => x.Environment != "tdd"), x => { Assert.True(x.Acts); Assert.False(x.Shadow); Assert.True(x.DependenciesDelivered); });
        var tdd = Policies(cfg, engineActs: true).Single(x => x.Environment == "tdd");   // tdd: the train observes the lifecycle's deployment
        Assert.False(tdd.Acts); Assert.False(tdd.Shadow);
    }

    [Fact]
    public void WhereTheAutoPromoteIsNotThisGateways_NothingIsPromoted()
    {
        var elsewhere = Config(Json(gatewayEnvironment: "uat"));
        var s = DeliveryShadowSettings.From(elsewhere, engineActs: true)!;
        Assert.Empty(s.Environments!); Assert.Empty(s.ShadowEnvironments!);
        Assert.All(Policies(elsewhere, engineActs: true), x => { Assert.False(x.Acts); Assert.False(x.Shadow); });
    }

    [Fact]
    public void TheShippedDefaultsAndTheDeployedOverrides_HaveNoTrainActsAnyMore()
    {
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "instance.json")))!["delivery"]!["autoPromote"]!["trainActs"]);
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "deploy.overrides.json")))!["delivery"]?["autoPromote"]?["trainActs"]);
    }
}
