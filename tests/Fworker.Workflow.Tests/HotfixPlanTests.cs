using Fworker.Contracts.Messaging;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W8.1: the hotfix plan of an incident on the WorkItemSaga (Develop, Verify, Merge, Deliver). Where the engine carries the feature plan's steps the hotfix has them too (reviews, a pull request, CI alongside
/// Functional Testing, fix-forward rounds, the merge line); it asks the scheduler for its slot at the top rank; it has no design to switch to the docs plan, and nothing settles it as docs.
/// </summary>
public class HotfixPlanTests
{
    const string Head = "aaa1111111111111111111111111111111111111";

    static WorkItemPolicy Policy(bool scheduler = true) => WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(scheduler) };

    static async Task<SagaFixture> Started(WorkItemPolicy? policy = null)
    {
        var fx = new SagaFixture(policy ?? Policy());
        await fx.Start(hotfix: true);
        return fx;
    }

    [Fact]
    public async Task AHotfix_StartsInDevelopment_AndAsksTheSchedulerAtTheTopRank()
    {
        var fx = await Started();
        Assert.Equal(("hotfix", StepKind.Develop), (fx.State.Plan, fx.State.Step));
        Assert.True(fx.State.CurrentPlan.TopRank);
        var wanted = Assert.Single(fx.Commands.OfType<SlotWanted>());
        Assert.Equal(("develop", true), (wanted.Phase, wanted.TopRank));

        var feature = new SagaFixture(Policy());
        await feature.Start();
        Assert.False(Assert.Single(feature.Commands.OfType<SlotWanted>()).TopRank);   // (a feature item ranks by its labels)
    }

    [Fact]
    public async Task AHotfix_GoesThroughTheFeatureStepsWithoutADesign_AndEveryGateStillHolds()
    {
        var fx = await Started(Policy(scheduler: false));
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);   // (the reviews are asked for, the plan does not skip a gate)
        await fx.Review();
        Assert.StartsWith("no-open-pr", (await fx.Done()).Decision.Reason);
        await fx.PullRequest(Head, 11, "src/a.cs");
        await fx.Done();
        Assert.Equal((StepKind.Verify, "hotfix"), (fx.State.Step, fx.State.Plan));
        // CI alongside: red on the head under test is a fix-forward round, as for any code change
        var red = await fx.Send(new ChecksObserved(Item, 0, "checks-1", Head, ChecksVerdict.Red, ["Build result"]));
        Assert.Contains(red.Decisions, d => d.Action == WorkItemActions.FixForward);
        Assert.Equal(StepKind.Develop, fx.State.Step);
        await fx.Review(); await fx.PullRequest("bbb2222222222222222222222222222222222222", 11, "src/a.cs"); await fx.Done();
        // a design is nothing a hotfix has: the design message finds the item past it
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Design(marker: true)).Decision.Outcome);
        Assert.Equal("hotfix", fx.State.Plan);
    }

    [Fact]
    public async Task AHotfix_IsMergedThroughTheLine_AsACodeChange_AndIsNeverSettledAsDocs()
    {
        var fx = await Started(Policy(scheduler: false));
        await fx.Review(); await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Done();
        await fx.Verdict(true, Head);
        await fx.Send(new ChecksObserved(Item, 0, "checks-2", Head, ChecksVerdict.Green, []));
        var request = Assert.Single(fx.Commands.OfType<MergeRequested>());
        Assert.Equal(("hotfix", Head), (request.Plan, request.Head));
        Assert.True(fx.State.TrainEntered);
        // the merge: the build of the merge commit that skipped its code checks does not turn a hotfix into a docs item
        await fx.Merged();
        Assert.Equal(StepKind.Deliver, fx.State.Step);
        var facts = await fx.Send(new MergeFactsRead(Item, 0, "facts-1", [new BuildCheck("Build result", "completed", "success")], ["docs/a.md"]));
        Assert.Equal(WorkItemOutcome.Refused, facts.Decision.Outcome);
        Assert.Equal("hotfix", fx.State.Plan);
        await fx.Delivered("prod", "1.0.9");
        Assert.Equal(WorkItemStatus.Settling, fx.State.Status);
    }

    [Fact]
    public async Task AHotfixThatLeavesTheMergeStepWithoutAMerge_GivesItsPlaceInTheLineBack()
    {
        var fx = await Started(Policy(scheduler: false));
        await fx.Review(); await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Done();
        await fx.Verdict(true, Head);
        await fx.Send(new ChecksObserved(Item, 0, "checks-2", Head, ChecksVerdict.Green, []));
        Assert.True(fx.State.TrainEntered);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Single(fx.Commands.OfType<MergeTrainLeft>());
    }

    [Fact]
    public async Task WithoutTheFeatureRules_AHotfixIsTheCoresPlan_NoSlotNoReview()
    {
        var fx = new SagaFixture();
        await fx.Start(hotfix: true);
        Assert.Equal(("hotfix", StepKind.Develop), (fx.State.Plan, fx.State.Step));
        Assert.Empty(fx.Commands);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Done()).Decision.Outcome);
        Assert.Equal(StepKind.Verify, fx.State.Step);
    }

    [Fact]
    public async Task OnlyTheCodePlansAreSchedulable()
    {
        var policy = Policy();
        Assert.True(policy.Schedules("feature")); Assert.True(policy.Schedules("hotfix")); Assert.True(policy.Schedules("Hotfix"));
        Assert.False(policy.Schedules("docs")); Assert.False(policy.Schedules("redeliver"));
        Assert.False(WorkItemPolicy.Default.Schedules("hotfix"));
        Assert.True(WorkPlans.IsCode("hotfix")); Assert.False(WorkPlans.IsCode(null));
        await Task.CompletedTask;
    }
}
