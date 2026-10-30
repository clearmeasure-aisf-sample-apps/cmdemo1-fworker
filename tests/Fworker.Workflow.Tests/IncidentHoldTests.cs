using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W8.1: the delivery train under an incident's hold. A hold that names no item of a hotfix stops every promotion (as it did before); a hold that does lets the release that carries the hotfix through, judged by
/// the same gates as any release; a train that only observes (tdd: the pipeline deploys) records the hold and stops nothing. The train answers the rollback request of an incident.
/// </summary>
public class IncidentHoldTests
{
    /// <summary>uat train, baseline 1.0.1 in every environment, item #7 merged as commit a, release 1.0.2 built from a and deployed to tdd.</summary>
    static async Task<TrainFixture> OneRelease()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        return f;
    }

    [Fact]
    public async Task AHoldWithNoHotfix_StopsEveryPromotion_AsBefore()
    {
        var f = await OneRelease();
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1: unhealthy", "orders/uat#1", []));
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal("held", f.LastDecision!.Action);
        Assert.Equal(["incident orders/uat#1: unhealthy"], f.LastDecision.Reasons);
        Assert.Equal("orders/uat#1", f.State.HeldIncident);
    }

    [Fact]
    public async Task AReleaseThatCarriesTheHotfix_IsPromoted_AfterTheSameEvidence()
    {
        var f = await OneRelease();
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["acme/orders#7"]));
        // the gates are the release's own: the evidence of the previous environment is still due
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Contains("the tdd app has not been Healthy long enough yet", string.Join(" ", f.LastDecision!.Reasons));
        await f.Mature();
        var cmd = Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal(("1.0.2", "uat"), (cmd.Version, cmd.Environment));
        Assert.Equal("promote", f.LastDecision!.Action);
    }

    [Fact]
    public async Task AReleaseThatCarriesTheHotfix_StillNeedsEveryGateOfItsItems()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a', ["#7 carries the 'hold:owner' label"]);
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["acme/orders#7"]));
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Contains("#7 carries the 'hold:owner' label", f.LastDecision.Reasons);
    }

    [Fact]
    public async Task AReleaseWithoutTheHotfix_WaitsForTheHold_AndTheHotfixOneIsPromotedWhenItsItemIsNamed()
    {
        var f = await OneRelease();
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["acme/orders#99"]));
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal("waiting", f.LastDecision!.Action);
        var why = Assert.Single(f.LastDecision.Reasons);
        Assert.Equal("an incident holds the train (orders/uat#1): only the release that carries the hotfix acme/orders#99 is promoted", why);
        // the incident names the item of its hotfix: the hold is replaced, the release carries it
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["ACME/orders#7"]));
        Assert.Equal(["acme/orders#7"], f.State.HoldAllow);
        Assert.Single(f.Of<PromoteRelease>());
        // and the end of the incident releases the hold for good
        await f.Send(new TrainHold(f.Key, false, ""));
        Assert.False(f.State.Held); Assert.Empty(f.State.HoldAllow); Assert.Equal("", f.State.HeldIncident);
    }

    [Fact]
    public async Task ASignOff_UnderTheHold_IsAnsweredOnlyForTheReleaseOfTheHotfix()
    {
        var f = await OneRelease();
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["acme/orders#99"]));
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.SignOff("1.0.2", "Uat");
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("held", f.LastDecision!.Action);
        await f.Send(new TrainHold(f.Key, true, "incident orders/uat#1", "orders/uat#1", ["acme/orders#7"]));
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.True(a.Proceed);
    }

    [Fact]
    public async Task ATrainThatOnlyObserves_RecordsTheHold_AndStopsNothing()
    {
        // tdd: the release's lifecycle deploys it, the train observes and delivers what Octopus shows
        var f = new TrainFixture(env: "tdd", promoter: Promoter.Pipeline); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a');
        await f.Send(new TrainHold(f.Key, true, "incident orders/tdd#1", "orders/tdd#1", []));
        Assert.True(f.State.Held);
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Tdd", "Started"); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        Assert.Equal("1.0.2", Assert.Single(f.Of<DeliveredTo>()).Release);
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.DoesNotContain(f.Of<TrainDecided>(), d => d.Action == "held");
    }

    [Fact]
    public async Task TheRollbackRequest_OfATrainThatDoesNotRollBack_IsRefused_AndDeploysNothing()
    {
        // one contract with W8.3: uat's train does not roll back by itself (tdd only), so the incident is told it is refused and goes to its owner; RollbackTests has the tdd train
        var f = await OneRelease();
        var request = new TrainRollback(f.Key, "orders/uat#1", 1, null, "release 1.0.2 was deployed 5 minutes before the incident");
        var r = await f.Send(request);
        var a = Assert.Single(r.Messages.OfType<RollbackAnswered>());
        Assert.Equal((f.Key, "orders/uat#1", 1, "refused", RollbackCodes.NotPermitted), (a.Key, a.IncidentId, a.Generation, a.Outcome, a.Code));
        Assert.Equal(f.Now, a.At);
        Assert.Empty(r.Messages.OfType<Fworker.Contracts.Commands.RollbackRelease>());
    }
}
