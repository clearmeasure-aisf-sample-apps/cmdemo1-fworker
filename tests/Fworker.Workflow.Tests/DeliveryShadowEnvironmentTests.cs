using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10p (#555, owner decision 2026-10-30): where the engine acts, the app's uat and prod stay with v1's AutoPromoter. The train decides them in shadow (DeliveryPolicy.Shadow): the decisions are published and
/// compared with the AutoPromoter's, and the commands it would send leave as ShadowCommand, which only DeliveryActingShadowHandlers records, so no PromoteRelease or AnswerSignOff reaches an executor. The train
/// acts for tdd only, where it observes the lifecycle's deployment and publishes DeliveredTo.
/// </summary>
public class DeliveryShadowEnvironmentTests
{
    static async Task<TrainFixture> OneRelease(Action<TrainFixture>? configure = null, string env = "uat")
    {
        var f = new TrainFixture(env, shadow: true); configure?.Invoke(f);
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        return f;
    }

    // ---- the policy ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheShadowEnvironments_AreDecidedButNotPromoted_AndTddIsNeither()
    {
        var f = new TrainFixture(shadow: true, dependencies: true);
        var uat = f.Policies.For("orders", "uat")!; var prod = f.Policies.For("orders", "prod")!; var tdd = f.Policies.For("orders", "tdd")!;
        Assert.All(new[] { uat, prod }, p => { Assert.True(p.Acts); Assert.True(p.Shadow); Assert.False(p.DependenciesDelivered); });   // (v1 has no such rule, so the comparison with it would differ for no defect)
        Assert.False(tdd.Acts); Assert.False(tdd.Shadow); Assert.Equal(Promoter.Pipeline, tdd.Promoter);
    }

    [Fact]
    public void AnEnvironmentThePolicyPromotes_IsNoShadow_AndNothingIsShadowedByDefault()
    {
        var f = new TrainFixture(dependencies: true);
        Assert.All(DeliveryPolicy.Chain.Select(e => f.Policies.For("orders", e)!), p => Assert.False(p.Shadow));
        Assert.True(f.Policies.For("orders", "uat")!.DependenciesDelivered);
        // named as both, it is promoted: the shadow list only holds what the promoted list does not
        var both = new CatalogDeliveryPolicies(() => new Fworker.Contracts.Catalog.CatalogSnapshot(Fworker.Contracts.Catalog.CatalogSource.SystemJson, T0, "acme", Repo, null,
            [new Fworker.Contracts.Catalog.RepositoryEntry(Repo, [new Fworker.Contracts.Catalog.DeployableEntry("orders", Repo, null, null, null, null, false)])], []),
            _ => new DeliveryPolicySettings("p", Promoter.Gateway, ["uat"], null, null, null, null, 2, false, ["uat", "prod"]));
        Assert.False(both.For("orders", "uat")!.Shadow);
        Assert.True(both.For("orders", "prod")!.Shadow);
    }

    // ---- the train in a shadow environment -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARelease_IsDecidedPromote_ButOnlyAShadowCommandLeaves()
    {
        var f = await OneRelease();
        await f.Mature();
        Assert.Equal("promote", f.LastDecision!.Action);
        Assert.Equal("1.0.2", f.LastDecision.Release);
        Assert.Empty(f.Of<PromoteRelease>());
        var c = Assert.Single(f.Of<ShadowCommand>());
        Assert.Equal(new ShadowCommand(f.Key, "PromoteRelease", "PromoteRelease|orders|1.0.2|uat", "1.0.2"), c);
        // as in an environment it promotes, the request is kept in the state, so a later input neither decides nor sends it again
        await f.After(TimeSpan.FromMinutes(1));
        Assert.Single(f.Of<ShadowCommand>());
        Assert.Equal("following", f.LastDecision.Action);
    }

    [Fact]
    public async Task ASignOff_IsDecided_ButOnlyAShadowCommandLeaves()
    {
        var f = await OneRelease();
        await f.Mature();
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.SignOff("1.0.2", "Uat");
        Assert.Equal("signoff-proceed", f.LastDecision!.Action);
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal(new ShadowCommand(f.Key, "AnswerSignOff", "AnswerSignOff|orders|I-1.0.2", "1.0.2"), f.Of<ShadowCommand>().Last());
    }

    [Fact]
    public async Task TheDecisionsOfAPromotedEnvironment_AreTheCommandsThemselves()
    {
        var f = new TrainFixture("uat"); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a'); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        Assert.Empty(f.Of<ShadowCommand>());
    }

    [Fact]
    public async Task Tdd_IsObserved_TheLifecyclesDeploymentDeliversItsItems_AndNoCommandLeaves()
    {
        var f = new TrainFixture("tdd", shadow: true); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a');
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Tdd", "Started");
        await f.Deploy("1.0.2", "Tdd", "Succeeded");
        var delivered = Assert.Single(f.Of<DeliveredTo>());
        Assert.Equal(new DeliveredTo(new ItemId(Repo, 7), "tdd", "1.0.2"), delivered);
        Assert.Equal("delivered", f.LastDecision!.Action);
        Assert.Empty(f.Of<PromoteRelease>()); Assert.Empty(f.Of<AnswerSignOff>()); Assert.Empty(f.Of<ShadowCommand>());
    }

    // ---- the shadow records them, and compares them with the AutoPromoter's --------------------------------------------------------------------------------------

    sealed class Rig(TrainFixture fx)
    {
        public readonly TrainFixture Fx = fx;
        public readonly InMemoryShadowComparisonStore Store = new();
        public DateTimeOffset Now = T0;
        public DeliveryShadow Shadow => shadow ??= new DeliveryShadow(Fx.Policies, Store, () => Now, TimeSpan.FromMinutes(5));
        DeliveryShadow? shadow;

        /// <summary>What the acting host's handlers do with the train's messages.</summary>
        public void Feed()
        {
            foreach (var m in Fx.All)
                switch (m)
                {
                    case TrainDecided d: DeliveryActingShadowHandlers.Handle(d, Shadow, Fx.Policies); break;
                    case ShadowCommand c: DeliveryActingShadowHandlers.Handle(c, Shadow); break;
                }
        }
    }

    static JsonArray V1(params object[] actions) => new(actions.Select(a => (JsonNode?)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(a))).ToArray());

    [Fact]
    public async Task TheTrainsDecision_IsComparedWithTheAutoPromoters_AndItsCommandIsRecordedNotExecuted()
    {
        var r = new Rig(await OneRelease());
        await r.Fx.Mature();
        r.Feed();
        r.Now = r.Fx.Now;
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat", task = "ServerTasks-9", items = new[] { 7 } }));   // the AutoPromoter's pass
        r.Now = r.Fx.Now.AddSeconds(30);
        await r.Shadow.Tick(default);
        var row = Assert.Single(r.Store.All, x => x.Release == "1.0.2" && x.V2Action == "promote");
        Assert.Equal(ShadowOutcomes.Match, row.Outcome);
        var status = r.Shadow.Status();
        Assert.Equal(1L, (long)status["v2"]!["commandsRecorded"]!);
        Assert.Equal(0L, (long)status["v2"]!["commandsExecuted"]!);
        var report = await r.Shadow.Report(10, null, default);
        Assert.Equal("PromoteRelease", (string)((JsonArray)report["recentCommands"]!)[0]!["command"]!);
        Assert.False((bool)((JsonArray)report["recentCommands"]!)[0]!["executed"]!);
    }

    [Fact]
    public async Task ADecisionOfAnEnvironmentTheTrainPromotes_IsNoShadowDecision()
    {
        var r = new Rig(new TrainFixture("uat"));   // (no shadow environment: the history records what the train decides there, the shadow has nothing to compare)
        var f = r.Fx; await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a'); await f.Mature();
        r.Feed();
        Assert.Equal(0L, (long)r.Shadow.Status()["v2"]!["decisions"]!);
    }

    [Fact]
    public void ACommandThatIsNotARealOne_IsStillRecordedByName()
    {
        var r = new Rig(new TrainFixture(shadow: true));
        DeliveryActingShadowHandlers.Handle(new ShadowCommand("orders/uat", "AnswerSignOff", "k", "1.0.2"), r.Shadow);
        Assert.Equal(1L, (long)r.Shadow.Status()["v2"]!["commandsRecorded"]!);
    }
}
