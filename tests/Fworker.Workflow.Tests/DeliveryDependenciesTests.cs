using System.Data.Common;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.7: the rule dependencies-delivered on. An item whose "Depends on" items are not delivered to the train's environment is held (the release that carries it waits, the release before it
/// goes on), a delivery of the dependency releases it at once, a dependency of another repository counts per environment, and a dependency in a repository with nothing to deliver settles when it is merged.
/// </summary>
public class DeliveryDependenciesTests
{
    sealed class DatabaseDown() : DbException("the delivered-to table does not answer");

    /// <summary>uat train with the rule on and a second repository (acme/web) that has a deployable: baseline 1.0.1 (commit d) everywhere, item #7 (commit a) depends on <paramref name="dependencies"/>, release 1.0.2 built from a and in tdd.</summary>
    static async Task<TrainFixture> OneRelease(string[] dependencies, Action<TrainFixture>? beforeRelease = null)
    {
        var f = new TrainFixture(dependencies: true, web: true); beforeRelease?.Invoke(f);
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a', dependencies: dependencies);
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        return f;
    }

    /// <summary>The release the train asked for: a request Octopus shows nothing of is sent again after five minutes, with the same key, so one command is the one asked for however often it is sent.</summary>
    static PromoteRelease Promoted(TrainFixture f) => f.Of<PromoteRelease>().DistinctBy(c => c.IdempotencyKey).Single();

    static bool MentionsDependency(TrainDecided d) => d.Reasons.Any(r => r.Contains("depends on"));

    // ---- held, and released on delivery ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItem_WhoseDependencyOfAnotherRepositoryIsNotInTheEnvironment_IsHeld_UntilTheDependencyIsDelivered()
    {
        var f = await OneRelease(["acme/web#3"]);
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Equal(["acme/orders#7 depends on acme/web#3, which is not yet delivered to Uat"], f.LastDecision.Reasons);
        Assert.Equal(["acme/orders#7"], f.LastDecision.Items);
        Assert.Equal(0, f.Reads.Probes);   // the health is read only when every other gate holds
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());   // time does not lift it

        f.Delivered.Rows.Add(("acme/web#3", "uat"));
        await f.Send(new TrainDependencyDelivered(f.Key, "acme/web#3", "uat"));
        // at once (not at the safety timer): the item is no longer held, the release goes on to the evidence, whose window starts now
        Assert.Equal(1, f.Reads.Probes);
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.DoesNotContain(f.LastDecision.Reasons, r => r.Contains("depends on"));
        Assert.Contains("has not been Healthy long enough", f.LastDecision.Reasons[0]);
        await f.Mature();
        var cmd = Promoted(f);
        Assert.Equal(("orders", "1.0.2", "uat"), (cmd.Deployable, cmd.Version, cmd.Environment));
        Assert.Contains(f.Of<TrainDecided>(), d => d.Action == "promote" && d.Release == "1.0.2");
    }

    [Fact]
    public async Task ADependencyDeliveredToAnotherEnvironmentOnly_StillHoldsTheItem()
    {
        var f = await OneRelease(["acme/web#3"]);
        f.Delivered.Rows.Add(("acme/web#3", "tdd"));   // delivered to tdd, not to uat
        await f.Send(new TrainDependencyDelivered(f.Key, "acme/web#3", "tdd"));
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal(["acme/orders#7 depends on acme/web#3, which is not yet delivered to Uat"], f.LastDecision!.Reasons);
    }

    [Fact]
    public async Task ADependencyOfTheSameRepository_IsDeliveredByTheSameRelease_OrByAnEarlierOne()
    {
        // #8 (commit b) depends on #7 (commit a): both ride in 1.0.3, so no row is needed
        var f = new TrainFixture(dependencies: true); await f.Baseline();
        f.Range('d', 'a', 'b');
        await f.Merge(7, 'a'); await f.Merge(8, 'b', dependencies: ["acme/orders#7"]);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        await f.Mature();
        Assert.Equal("1.0.3", Promoted(f).Version);

        // an earlier release that carried #5 and a row for it: #9 depends on #5 and is promoted
        var g = new TrainFixture(dependencies: true); await g.Baseline();
        g.Range('d', 'c'); await g.Merge(9, 'c', dependencies: ["acme/orders#5"]);
        g.Reads.Running["tdd"] = "1.0.4";
        await g.ReleaseInPrevious("1.0.4", 'c');
        await g.Mature();
        Assert.Empty(g.Of<PromoteRelease>());
        Assert.Equal(["acme/orders#9 depends on acme/orders#5, which is not yet delivered to Uat"], g.LastDecision!.Reasons);
        g.Delivered.Rows.Add(("acme/orders#5", "uat"));
        await g.Send(new TrainDependencyDelivered(g.Key, "acme/orders#5", "uat"));
        await g.Mature();
        Assert.Equal("1.0.4", Promoted(g).Version);
    }

    [Fact]
    public async Task TheReleaseBeforeAHeldRelease_IsPromoted_AndTheHeldOneAfterTheDelivery()
    {
        var f = await OneRelease([]);   // 1.0.2 carries #7, which depends on nothing
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b', dependencies: ["acme/web#3"]);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.Mature();
        // 1.0.3 carries #8, which waits for acme/web#3: the train does not promote it and goes on with the release before it
        var cmd = Promoted(f);
        Assert.Equal("1.0.2", cmd.Version);
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.3" && d.Action == "waiting" && d.Reasons.Contains("acme/orders#8 depends on acme/web#3, which is not yet delivered to Uat"));
    }

    // ---- a delivery that nobody waits for -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeliveryNoCandidateWaitsFor_DoesNotMakeTheTrainLookAgain()
    {
        var f = await OneRelease(["acme/web#3"]);
        var (compares, probes, lookups, sequence) = (f.Reads.Compares, f.Reads.Probes, f.Delivered.Lookups, f.State.Sequence);
        var r = await f.Send(new TrainDependencyDelivered(f.Key, "acme/web#9", "uat"));
        Assert.Empty(r.Messages);
        Assert.Null(r.WakeAt);
        Assert.Equal((compares, probes, lookups, sequence), (f.Reads.Compares, f.Reads.Probes, f.Delivered.Lookups, f.State.Sequence));

        // a train that has not seen anything yet has no candidate either
        var fresh = new TrainFixture(dependencies: true, web: true);
        var none = await fresh.Svc.Handle(new TrainDependencyDelivered(fresh.Key, "acme/web#3", "uat"));
        Assert.Empty(none.Messages);
        Assert.Empty(fresh.Store.States);
    }

    // ---- the rule is off unless the policy turns it on -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithTheRuleOff_NothingIsHeldForADependency_AndNothingIsLookedUp()
    {
        var f = new TrainFixture(dependencies: false, web: true); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a', dependencies: ["acme/web#3", "acme/docs#4"]);
        f.Reads.Running["tdd"] = "1.0.2"; await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Mature();
        Assert.Equal("1.0.2", Promoted(f).Version);
        Assert.DoesNotContain(f.Of<TrainDecided>(), MentionsDependency);
        Assert.Equal(0, f.Delivered.Lookups);
    }

    // ---- a repository with nothing to deliver --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADependencyInARepositoryWithoutADeployable_SettlesWhenItIsMerged_AndHoldsTheItemUntilThen()
    {
        var f = await OneRelease(["acme/docs#4"]);   // (acme/docs is no repository of the catalog's deployables)
        Assert.Equal(["acme/orders#7 depends on acme/docs#4, which is not yet merged"], f.LastDecision!.Reasons);
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());

        f.Delivered.Merged.Add("acme/docs#4");   // merged: no release will ever carry it
        await f.Send(new TrainWake(f.Key));
        Assert.DoesNotContain(f.LastDecision!.Reasons, r => r.Contains("depends on"));
        await f.Mature();
        Assert.Equal("1.0.2", Promoted(f).Version);
    }

    [Fact]
    public async Task ADependencyInARepositoryWithADeployable_NeedsItsDelivery_MergedIsNotEnough()
    {
        var f = await OneRelease(["acme/web#3"]);
        f.Delivered.Merged.Add("acme/web#3");
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal(["acme/orders#7 depends on acme/web#3, which is not yet delivered to Uat"], f.LastDecision!.Reasons);
    }

    // ---- faults are not holds ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADatabaseThatDoesNotAnswer_HoldsNothingAndDecidesNothing_ButIsLookedAtAgain()
    {
        var f = await OneRelease(["acme/web#3"], x => x.Delivered.Fail = new DatabaseDown());
        // the look failed: no decision names a dependency (a read that failed is no gap), nothing is promoted, and the train looks again soon
        Assert.DoesNotContain(f.Of<TrainDecided>(), MentionsDependency);
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.DoesNotContain(f.State.DecisionDetails.Values.SelectMany(d => d.Reasons), r => r.Contains("depends on"));
        var again = await f.Wake();
        Assert.Equal(f.Now + DeliveryTrainService.TransientRetry, again.WakeAt);

        // the database is back and the dependency is delivered: nothing was kept from the failed looks
        f.Delivered.Fail = null; f.Delivered.Rows.Add(("acme/web#3", "uat"));
        await f.Mature();
        Assert.Equal("1.0.2", Promoted(f).Version);
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswer_DuringASignOff_IsTriedAgain_NotAnAbort()
    {
        var g = new TrainFixture(dependencies: true, web: true); await g.Baseline();
        g.Range('d', 'a'); await g.Merge(7, 'a', dependencies: ["acme/web#3"]);
        g.Delivered.Rows.Add(("acme/web#3", "uat"));
        g.Reads.Running["tdd"] = "1.0.2"; await g.ReleaseInPrevious("1.0.2", 'a');
        await g.Mature();
        await g.Deploy("1.0.2", "Uat", "Started");
        g.Delivered.Fail = new DatabaseDown();
        var r = await g.SignOff("1.0.2", "Uat");
        Assert.Empty(g.Of<AnswerSignOff>());   // not answered, and above all not aborted: the lookup failed, the dependency is still delivered
        Assert.Equal(g.Now + DeliveryTrainService.TransientRetry, r.WakeAt);
        g.Delivered.Fail = null;
        await g.Wake();
        Assert.True(g.Of<AnswerSignOff>().Single().Proceed);
    }

    // ---- the router --------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADelivery_WakesTheTrainsOfItsEnvironment_ThatActAndHaveTheRuleOn()
    {
        var on = new TrainFixture(dependencies: true, web: true);
        var router = new DeliveryTrainRouter(on.Policies, () => T0);
        var inputs = router.Route(new DeliveredTo(ItemId.Parse("acme/web#3"), "uat", "2.0.1"));
        var wake = Assert.IsType<TrainDependencyDelivered>(Assert.Single(inputs));
        Assert.Equal(("orders/uat", "acme/web#3", "uat"), (wake.Key, wake.Item, wake.Environment));
        Assert.Equal(["orders/prod"], router.Route(new DeliveredTo(ItemId.Parse("acme/web#3"), "prod", "2.0.1")).Select(i => i.Key));
        Assert.Empty(router.Route(new DeliveredTo(ItemId.Parse("acme/web#3"), "tdd", "2.0.1")));   // the pipeline deploys tdd: no train acts there

        // the rule off (every train of the shadow run), or no promotion by the gateway: nobody is woken
        var off = new TrainFixture(dependencies: false, web: true);
        Assert.Empty(new DeliveryTrainRouter(off.Policies, () => T0).Route(new DeliveredTo(ItemId.Parse("acme/web#3"), "uat", "2.0.1")));
        var external = new TrainFixture(promoter: Promoter.External, dependencies: true, web: true);
        Assert.Empty(new DeliveryTrainRouter(external.Policies, () => T0).Route(new DeliveredTo(ItemId.Parse("acme/web#3"), "uat", "2.0.1")));
    }

    [Fact]
    public void ThePolicySetting_IsCarriedToTheTrainsOfTheDeployable()
    {
        Assert.True(new TrainFixture(dependencies: true).Policies.For("orders", "uat")!.DependenciesDelivered);
        Assert.False(new TrainFixture(dependencies: false).Policies.For("orders", "uat")!.DependenciesDelivered);
        Assert.False(new DeliveryPolicySettings("p").DependenciesDelivered);   // (the settings' own default is off)
    }
}
