using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W6.1: the WorkItemSaga's transitions, without a database: the state machine (WorkItemSaga) behind its service on an in-memory store. The tracked tests (WorkItemTrackedTests) run the same
/// machine in a Wolverine host over SQL Server.
/// </summary>
public class WorkItemSagaTests
{
    // a decision without the identity of its lists (a redelivery publishes the saved copy)
    static string Shape(WorkItemDecided d) => $"{d.Sequence}|{d.Generation}|{d.Action}|{d.Plan}|{d.Step}|{d.ArmId}|{string.Join(';', d.Reasons)}";

    // ---- plans as data ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Plans_AreDataTheDesignNames()
    {
        Assert.Equal([StepKind.Design, StepKind.Develop, StepKind.Verify, StepKind.Merge, StepKind.Deliver], WorkPlans.FeaturePlan.Steps);
        Assert.Equal([StepKind.Design, StepKind.Develop, StepKind.Verify, StepKind.Merge], WorkPlans.DocsPlan.Steps);
        Assert.Equal([StepKind.Develop, StepKind.Verify, StepKind.Merge, StepKind.Deliver], WorkPlans.HotfixPlan.Steps);
        Assert.True(WorkPlans.HotfixPlan.TopRank);
        Assert.False(WorkPlans.FeaturePlan.TopRank);
        Assert.Equal(StepKind.Verify, WorkPlans.DocsPlan.After(StepKind.Develop));
        Assert.Null(WorkPlans.DocsPlan.After(StepKind.Merge));
        Assert.Null(WorkPlans.DocsPlan.After(StepKind.Deliver));   // (the docs plan has no Deliver)
        Assert.Same(WorkPlans.DocsPlan, WorkPlans.Find("DOCS"));
        Assert.Null(WorkPlans.Find("nope"));
        Assert.Same(WorkPlans.HotfixPlan, WorkPlans.Choose(null, true));
        Assert.Same(WorkPlans.FeaturePlan, WorkPlans.Choose("", false));
        Assert.Same(WorkPlans.DocsPlan, WorkPlans.Choose("docs", true));
        Assert.Null(WorkPlans.Choose("nope", false));
    }

    // ---- starting ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_BeginsGenerationOne_OnTheFeaturePlan_ArmsTheFirstStep_AndAsksForItsLease()
    {
        var fx = new SagaFixture();
        var r = await fx.Start();
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal((1, "feature", "Design", "1.1"), (r.Decision.Generation, r.Decision.Plan, r.Decision.Step, r.Decision.ArmId));
        Assert.Equal([WorkItemActions.Started, WorkItemActions.StepEntered], fx.Actions);
        Assert.Equal([1L, 2L], fx.Decisions.Select(d => d.Sequence));
        Assert.All(fx.Decisions, d => Assert.Equal((Item, 1), (d.Item, d.Generation)));
        var timer = Assert.Single(fx.Timers);
        var lease = Assert.IsType<PhaseLeaseExpired>(timer.Message);
        Assert.Equal((Item, 1, "1.1", T0.AddMinutes(60)), (lease.Item, lease.Generation, lease.ArmId, lease.Due));
        Assert.Equal(T0.AddMinutes(60), timer.Due);
        Assert.Equal((WorkItemStatus.Open, StepKind.Design, T0.AddMinutes(60)), (fx.State.Status, fx.State.Step, fx.State.LeaseDue));
    }

    [Fact]
    public async Task Start_ChoosesThePlan_ByNameOrHotfix()
    {
        var hot = new SagaFixture(); await hot.Start(hotfix: true);
        Assert.Equal(("hotfix", StepKind.Develop), (hot.State.Plan, hot.State.Step));
        var docs = new SagaFixture(); await docs.Start(plan: "docs");
        Assert.Equal(("docs", StepKind.Design), (docs.State.Plan, docs.State.Step));
        var named = new SagaFixture(); await named.Start(plan: "feature", hotfix: true);
        Assert.Equal("feature", named.State.Plan);   // (the name wins)
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("redeliver")]
    public async Task Start_OnAPlanThatDoesNotStartAnItem_IsRefused_AndSavesNothing(string plan)
    {
        var fx = new SagaFixture();
        var r = await fx.Start(plan);
        Assert.Equal(WorkItemOutcome.Refused, r.Decision.Outcome);
        Assert.Empty(fx.Store.States);
        Assert.Empty(fx.Decisions);
    }

    [Fact]
    public async Task Start_OfAnOpenItem_ChangesNothing_AndOnlyTheStartRightAfterItIsARedelivery()
    {
        var fx = new SagaFixture();
        var first = await fx.Start();
        var again = await fx.Start();   // nothing happened since: the same start, delivered again after a crash between the save and the publish
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        Assert.Equal(first.Decisions.Select(Shape), again.Decisions.Select(Shape));
        Assert.Equal(first.Timers.Select(t => t.Due), again.Timers.Select(t => t.Due));
        Assert.Equal(1, fx.State.Generation);
        Assert.Equal(1, fx.Store.RevisionOf(fx.Key));   // (saved once)
        await fx.Done();
        var late = await fx.Start();   // the item moved on: this is a second start of an open item
        Assert.Equal(WorkItemOutcome.Refused, late.Decision.Outcome);
        Assert.Empty(late.Decisions);
    }

    // ---- steps -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFeaturePlan_WalksAllFiveSteps_EachUnderANewArm_AndDeliverEndsWithTheDelivery()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var seen = new List<(StepKind, string)> { (fx.State.Step, fx.Arm) };
        for (var i = 0; i < 4; i++) { await fx.Done(); seen.Add((fx.State.Step, fx.Arm)); }
        Assert.Equal([(StepKind.Design, "1.1"), (StepKind.Develop, "1.2"), (StepKind.Verify, "1.3"), (StepKind.Merge, "1.4"), (StepKind.Deliver, "1.5")], seen);
        Assert.Equal(5, fx.Decisions.Count(d => d.Action == WorkItemActions.StepEntered));
        // the deliver step has no lease and no timer; a StepCompleted does not end it (the delivery does)
        Assert.Null(fx.State.LeaseDue);
        var r = await fx.Done();
        Assert.Equal(WorkItemOutcome.Refused, r.Decision.Outcome);
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
    }

    [Fact]
    public async Task TheDocsPlan_EndsAfterMerge()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Merge);
        var r = await fx.Done();
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
        Assert.Equal(WorkItemActions.Completed, r.Decisions.Last().Action);
        Assert.Null(r.Decision.Step);
        Assert.Null(fx.State.LeaseDue);
    }

    [Fact]
    public async Task AMessageOfAnEarlierArm_IsStale_AndAKeyAppliedBeforeIsADuplicate()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var first = fx.Arm;
        var done = new StepCompleted(Item, 1, first, "done-design");
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Send(done)).Decision.Outcome);
        var rev = fx.Store.RevisionOf(fx.Key);
        // a session that held the first arm reports again, with another key: the item is on the next arm
        var stale = await fx.Send(new StepCompleted(Item, 1, first, "done-design-2"));
        Assert.Equal(WorkItemOutcome.Stale, stale.Decision.Outcome);
        Assert.Equal(StepKind.Develop, fx.State.Step);
        // the same message again after something else happened: a duplicate, nothing published
        await fx.Budget(BudgetKind.Registered);
        var dup = await fx.Send(done);
        Assert.Equal(WorkItemOutcome.Duplicate, dup.Decision.Outcome);
        Assert.Empty(dup.Decisions);
        Assert.Equal(rev + 1, fx.Store.RevisionOf(fx.Key));   // (only the budget message was saved)
    }

    [Fact]
    public async Task ARedelivery_OfTheLastInput_PublishesItsDecisionsAndTimersAgain_WithTheSameSequenceNumbers()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var done = new StepCompleted(Item, 1, fx.Arm, "done-design");
        var first = await fx.Send(done);
        var rev = fx.Store.RevisionOf(fx.Key);
        var again = await fx.Svc.Handle(done);   // the handler died after the save: Wolverine runs it again
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        Assert.Equal(first.Decisions.Select(Shape), again.Decisions.Select(Shape));
        Assert.Equal(first.Decisions.Select(d => d.Sequence), again.Decisions.Select(d => d.Sequence));
        var lease = Assert.IsType<PhaseLeaseExpired>(Assert.Single(again.Timers).Message);
        Assert.Equal("1.2", lease.ArmId);
        Assert.Equal(rev, fx.Store.RevisionOf(fx.Key));
    }

    // ---- the design decides the plan ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMarkerInTheDesign_SwitchesTheFeatureItemToDocs_AndTheLatestDesignDecides()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var arm = fx.Arm;
        var r = await fx.Design(marker: true);
        Assert.Equal("docs", fx.State.Plan);
        Assert.Equal([WorkItemActions.PlanSwitched], r.Decisions.Select(d => d.Action));
        Assert.Contains("feature to docs", r.Decisions[0].Reasons[0]);
        Assert.Equal((StepKind.Design, arm), (fx.State.Step, fx.Arm));   // (same step, same arm)
        Assert.Empty(r.Timers);
        await fx.Design(marker: false);
        Assert.Equal("feature", fx.State.Plan);
        Assert.Equal(false, fx.State.DesignMarker);
        await fx.Design(marker: true);
        await fx.Done();
        Assert.Equal(("docs", StepKind.Develop, true), (fx.State.Plan, fx.State.Step, fx.State.DesignMarker));
    }

    [Fact]
    public async Task LeavingTheDesignWithoutOne_RecordsThatTheItemIsNotDocsOnly()
    {
        var fx = new SagaFixture();
        await fx.Start();
        Assert.Null(fx.State.DesignMarker);
        await fx.Done();
        Assert.Equal(false, fx.State.DesignMarker);
    }

    [Fact]
    public async Task ADesignRecordedAfterTheDesign_IsStale()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var design = fx.Arm;
        await fx.Done();
        var r = await fx.Send(new DesignRecorded(Item, 1, design, "late", true));
        Assert.Equal(WorkItemOutcome.Stale, r.Decision.Outcome);
        Assert.Equal("feature", fx.State.Plan);
    }

    [Fact]
    public async Task APullRequestOfADocsItem_ThatIsNotDocumentation_SwitchesItToFeature_AndOnlyWhenThePortSaysSo()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Done();
        Assert.Equal(StepKind.Develop, fx.State.Step);
        fx.Docs.Files = null;   // the port cannot tell
        var unknown = await fx.Send(new PullRequestChanged(Item, 0, "pr-1", 11, "aaa", ["src/App.cs"]));
        Assert.Equal("docs", fx.State.Plan);
        Assert.Empty(unknown.Decisions);
        Assert.Equal((11, "aaa"), (fx.State.PullRequest, fx.State.Head));
        fx.Docs.Files = true;
        await fx.Send(new PullRequestChanged(Item, 0, "pr-2", 11, "bbb", ["docs/a.md"]));
        Assert.Equal("docs", fx.State.Plan);
        fx.Docs.Files = false;
        var code = await fx.Send(new PullRequestChanged(Item, 0, "pr-3", 11, "ccc", ["docs/a.md", "src/App.cs"]));
        Assert.Equal("feature", fx.State.Plan);
        Assert.Equal(StepKind.Develop, fx.State.Step);
        Assert.Equal(WorkItemActions.PlanSwitched, Assert.Single(code.Decisions).Action);
    }

    // ---- the merge and the delivery ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMerge_IsAFact_ItCompletesTheMergeStepWhicheverStepTheItemIsOn_AndEntersDeliver()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.WalkTo(StepKind.Verify);
        var r = await fx.Merged();
        Assert.Equal([WorkItemActions.Merged, WorkItemActions.StepEntered], r.Decisions.Select(d => d.Action));
        Assert.Equal((StepKind.Deliver, "feature", "abcdef0123456789abcdef0123456789abcdef01", 11), (fx.State.Step, fx.State.Plan, fx.State.MergeCommit, fx.State.PullRequest));
        // the same merge again is a duplicate key; another commit on an item past its merge is refused
        Assert.Equal(WorkItemOutcome.Duplicate, (await fx.Merged()).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Merged("1111111111111111111111111111111111111111")).Decision.Outcome);
    }

    [Fact]
    public async Task TheMerge_OfADocsItem_CompletesIt()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        var r = await fx.Merged();
        Assert.Equal([WorkItemActions.Merged, WorkItemActions.Completed], r.Decisions.Select(d => d.Action));
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
    }

    [Fact]
    public async Task ReachingTheFinalEnvironment_StartsTheSettleWindow_AndAnUndeliveredInsideItReturnsTheItemToDeliver()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.Merged();
        var armDeliver = fx.Arm;
        var tdd = await fx.Delivered("tdd");
        Assert.Empty(tdd.Decisions);   // (not the final environment: recorded, nothing decided)
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
        var prod = await fx.Delivered("prod");
        Assert.Equal([WorkItemActions.Settling], prod.Decisions.Select(d => d.Action));
        Assert.Equal((WorkItemStatus.Settling, T0.AddHours(24)), (fx.State.Status, fx.State.SettleUntil));
        var settle = Assert.IsType<SettleWindowElapsed>(Assert.Single(prod.Timers).Message);
        Assert.NotEqual(armDeliver, settle.ArmId);   // (the window has an arm of its own)
        Assert.Equal(T0.AddHours(24), prod.Timers[0].Due);
        // the lease timer of the deliver step cannot expire what is settling
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new PhaseLeaseExpired(Item, 1, armDeliver, T0))).Decision.Outcome);

        var lost = await fx.Lost("prod");
        Assert.Equal([WorkItemActions.Undelivered, WorkItemActions.StepEntered], lost.Decisions.Select(d => d.Action));
        Assert.Equal((WorkItemStatus.Open, StepKind.Deliver, null), (fx.State.Status, fx.State.Step, fx.State.SettleUntil));
        Assert.NotEqual(settle.ArmId, fx.Arm);
        // the timer of the first window arrives: the arm is not the current one
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(settle)).Decision.Outcome);
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
        // delivered again: a new window, and its end completes the item
        var again = await fx.Delivered("prod", "1.0.6");
        var window = Assert.IsType<SettleWindowElapsed>(Assert.Single(again.Timers).Message);
        var end = await fx.Send(window);
        Assert.Equal([WorkItemActions.Completed], end.Decisions.Select(d => d.Action));
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
    }

    [Fact]
    public async Task AfterTheWindow_ALateMessageIsNotFound_AndAnUndeliveredStartsTheNextGenerationOnTheRedeliverPlan()
    {
        var fx = new SagaFixture();
        await fx.Start(); await fx.Merged(); await fx.Delivered("prod");
        var window = Assert.IsType<SettleWindowElapsed>(fx.Timers.Last().Message);
        await fx.Send(window);
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
        var sequence = fx.State.Sequence;
        // late timers and webhooks end quietly
        foreach (WorkItemInput late in new WorkItemInput[] { new PhaseLeaseExpired(Item, 1, "1.1", T0), new StepCompleted(Item, 1, "1.5", "x"), new MergeSeen(Item, "m2", new string('b', 40), 3), new DeliverySeen(Item, "d2", "uat", "1.0.9") })
        {
            var r = await fx.Send(late);
            Assert.Equal(WorkItemOutcome.NotFound, r.Decision.Outcome);
            Assert.Empty(r.Decisions);
        }
        Assert.Equal(sequence, fx.State.Sequence);
        // Undelivered of an environment the instance was never delivered to: nothing to redeliver
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Lost("uat", "lost-uat")).Decision.Outcome);
        var back = await fx.Lost("prod");
        Assert.Equal(WorkItemOutcome.Applied, back.Decision.Outcome);
        Assert.Equal([WorkItemActions.Reopened, WorkItemActions.StepEntered], back.Decisions.Select(d => d.Action));
        Assert.Equal((2, "redeliver", StepKind.Deliver, WorkItemStatus.Open), (fx.State.Generation, fx.State.Plan, fx.State.Step, fx.State.Status));
        Assert.Equal("2.1", fx.Arm);
        Assert.True(back.Decisions[0].Sequence > sequence);   // (the sequence goes on across generations)
        Assert.False(fx.State.Delivered.ContainsKey("prod"));
        // the old generation's timers and messages name generation 1
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Send(new StepCompleted(Item, 1, "1.5", "late"))).Decision.Outcome);
    }

    [Fact]
    public async Task Reopen_OfACompletedItem_StartsTheNextGeneration_AndOfAnOpenOneChangesNothing()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new ReopenWorkItem(Item, "moved back", "reopen-0"))).Decision.Outcome);
        await fx.Merged();
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
        var r = await fx.Send(new ReopenWorkItem(Item, "moved back", "reopen-1"));
        Assert.Equal([WorkItemActions.Reopened, WorkItemActions.StepEntered], r.Decisions.Select(d => d.Action));
        Assert.Equal((2, "docs", StepKind.Design, "2.1"), (fx.State.Generation, fx.State.Plan, fx.State.Step, fx.Arm));
        // a redelivery of the reopen finds the new instance with its key as the last one
        var again = await fx.Send(new ReopenWorkItem(Item, "moved back", "reopen-1"));
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        Assert.Equal(r.Decisions.Select(Shape), again.Decisions.Select(Shape));
    }

    [Fact]
    public async Task AMergeSeenOnTheMergeOfABuildThatSkippedItsCodeChecks_SettlesTheItemAsDocs()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var early = await fx.Send(new MergeFactsRead(Item, 0, "facts-0", [], ["docs/a.md"]));
        Assert.Equal(WorkItemOutcome.Refused, early.Decision.Outcome);   // (not merged yet)
        await fx.Merged();
        fx.Docs.Merge = null;
        var unknown = await fx.Send(new MergeFactsRead(Item, 0, "facts-1", [new BuildCheck("Integration Build (SQL container)", "in_progress", null)], null));
        Assert.Equal("feature", fx.State.Plan);
        Assert.Empty(unknown.Decisions);
        fx.Docs.Merge = new MergeDocsOnlyVerdict(false, "build");
        await fx.Send(new MergeFactsRead(Item, 0, "facts-2", [new BuildCheck("Integration Build (SQL container)", "completed", "success")], null));
        Assert.Equal("feature", fx.State.Plan);
        fx.Docs.Merge = new MergeDocsOnlyVerdict(true, "build");
        var docs = await fx.Send(new MergeFactsRead(Item, 0, "facts-3", [new BuildCheck("Integration Build (SQL container)", "completed", "skipped")], null));
        Assert.Equal([WorkItemActions.PlanSwitched, WorkItemActions.Completed], docs.Decisions.Select(d => d.Action));
        Assert.Equal(("docs", WorkItemStatus.Completed), (fx.State.Plan, fx.State.Status));
    }

    // ---- NotFound and generations --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageForAnItemWithNoSaga_IsNotFound_AndWritesNothing()
    {
        var fx = new SagaFixture();
        WorkItemInput[] all =
        [
            new StepCompleted(Item, 0, "1.1", "k"), new DesignRecorded(Item, 1, "1.1", "k", true), new BudgetSpent(Item, 1, "1.1", "k", BudgetKind.Failure, ""), new LeaseRenewed(Item, 1, "1.1", "k"),
            new PullRequestChanged(Item, 0, "k", 1, "h", []), new MergeSeen(Item, "k", new string('a', 40), 1), new DeliverySeen(Item, "k", "prod", "1"), new DeliveryLost(Item, "k", "prod"),
            new ParkWorkItem(Item, 0, "k", "x"), new ResumeWorkItem(Item, 0, "k"), new PhaseLeaseExpired(Item, 1, "1.1", T0), new SettleWindowElapsed(Item, 1, "1.1"), new ReopenWorkItem(Item, "x", "k"),
            new MergeFactsRead(Item, 0, "k", [], null),
        ];
        foreach (var m in all)
        {
            var r = await fx.Send(m);
            Assert.Equal(WorkItemOutcome.NotFound, r.Decision.Outcome);
            Assert.Empty(r.Decisions); Assert.Empty(r.Timers);
        }
        Assert.Empty(fx.Store.States);
    }

    [Fact]
    public async Task AMessageOfAnotherGeneration_IsNotFound_ButGenerationZeroMeansTheCurrentOne()
    {
        var fx = new SagaFixture();
        await fx.Start();
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Send(new StepCompleted(Item, 2, fx.Arm, "k"))).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Send(new ParkWorkItem(Item, 3, "k", "x"))).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Send(new PullRequestChanged(Item, 0, "k", 1, "h", []))).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Send(new PullRequestChanged(Item, 1, "k2", 1, "h2", []))).Decision.Outcome);
    }

    // ---- the budgets ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFailuresOfAPhase_ParkTheItemAtTheLimit_AndResumeStartsThePhaseAgain()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var first = await fx.Budget(BudgetKind.Failure);
        Assert.Empty(first.Decisions);
        Assert.Equal(1, fx.State.FailuresOf(StepKind.Design));
        var second = await fx.Budget(BudgetKind.Failure);
        var parked = Assert.Single(second.Decisions);
        Assert.Equal(WorkItemActions.Parked, parked.Action);
        Assert.StartsWith("runtime-failed: 2 failures in Design", parked.Reasons[0]);
        Assert.True(fx.State.Parked);
        Assert.Null(fx.State.LeaseDue);
        Assert.Empty(second.Timers);
        // a parked item takes no step message and no more budget counts
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Done()).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Budget(BudgetKind.Failure)).Decision.Outcome);
        var arm = fx.Arm;
        var resumed = await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Equal([WorkItemActions.Resumed, WorkItemActions.StepEntered], resumed.Decisions.Select(d => d.Action));
        Assert.False(fx.State.Parked);
        Assert.Equal(0, fx.State.FailuresOf(StepKind.Design));
        Assert.NotEqual(arm, fx.Arm);
        Assert.IsType<PhaseLeaseExpired>(Assert.Single(resumed.Timers).Message);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new ResumeWorkItem(Item, 0, "resume-2"))).Decision.Outcome);   // (not parked)
    }

    [Fact]
    public async Task TheLaunchesInARow_AreCountedPerPhase_AndTheOneBeyondTheLimitParksTheItem()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.Budget(BudgetKind.Launch); await fx.Budget(BudgetKind.Launch);
        Assert.Equal(2, fx.State.LaunchesOf(StepKind.Design));
        await fx.Done();   // another phase: the count in a row starts again with its first launch
        await fx.Budget(BudgetKind.Launch);
        Assert.Equal((0, 1), (fx.State.LaunchesOf(StepKind.Design), fx.State.LaunchesOf(StepKind.Develop)));
        await fx.Budget(BudgetKind.Launch); await fx.Budget(BudgetKind.Launch);
        Assert.Equal(3, fx.State.LaunchesOf(StepKind.Develop));
        Assert.False(fx.State.Parked);
        var r = await fx.Budget(BudgetKind.Launch);   // the fourth asked for
        Assert.StartsWith("launches-exhausted: 3 executions in a row without finishing Develop", Assert.Single(r.Decisions).Reasons[0]);
        Assert.True(fx.State.Parked);
        Assert.Equal(3, fx.State.LaunchesOf(StepKind.Develop));   // (the refused one is not counted)
    }

    [Fact]
    public async Task TheIdleFollowUps_AreCountedForTheItemInAnyPhase_AReconnectStartsThemAgain_AndTheLimitParksTheItem()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.Budget(BudgetKind.IdleFollowUp);
        await fx.Done();   // (any phase)
        await fx.Budget(BudgetKind.Registered);
        Assert.Equal(0, fx.State.IdleFollowUps);
        await fx.Budget(BudgetKind.IdleFollowUp);
        Assert.False(fx.State.Parked);
        var r = await fx.Budget(BudgetKind.IdleFollowUp);
        Assert.StartsWith("followups-idle: 2 follow-up sessions in a row", Assert.Single(r.Decisions).Reasons[0]);
        Assert.True(fx.State.Parked);
    }

    [Fact]
    public async Task TheBudgets_AreInTheSavedState_ARestartGrantsNoExtraRound()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.Budget(BudgetKind.Failure); await fx.Budget(BudgetKind.Launch); await fx.Budget(BudgetKind.IdleFollowUp);
        // a new service on the same store is a restarted process
        var restarted = new WorkItemService(fx.Store, new WorkItemSaga(fx.Docs), () => fx.Now);
        var r = await restarted.Handle(new BudgetSpent(Item, 1, fx.Arm, "after-restart", BudgetKind.Failure, ""));
        Assert.Contains("runtime-failed", Assert.Single(r.Decisions).Reasons[0]);
        Assert.Equal(1, fx.State.LaunchesOf(StepKind.Design));
    }

    [Fact]
    public async Task AnAdminPark_StopsTheLease_AndAParkedItemStillHearsTheMerge()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.WalkTo(StepKind.Verify);
        var p = await fx.Send(new ParkWorkItem(Item, 0, "park-1", "waiting for a person"));
        Assert.Equal("waiting for a person", Assert.Single(p.Decisions).Reasons[0]);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new ParkWorkItem(Item, 0, "park-2", "again"))).Decision.Outcome);
        Assert.Null(fx.State.LeaseDue);
        var m = await fx.Merged();   // merged by a person meanwhile: a fact the saga takes
        Assert.Equal((StepKind.Deliver, false), (fx.State.Step, fx.State.Parked));
        Assert.Equal([WorkItemActions.Merged, WorkItemActions.StepEntered], m.Decisions.Select(d => d.Action));
    }

    [Fact]
    public async Task AnItemParkedInsideItsSettleWindow_ResumesIntoTheWindow_NotBackToItsDeliverStep()
    {
        var fx = new SagaFixture();
        await fx.Start(); await fx.Merged(); await fx.Delivered("prod");
        var until = fx.State.SettleUntil;
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person looks"));
        Assert.True(fx.State.Parked);
        var r = await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Equal([WorkItemActions.Resumed], r.Decisions.Select(d => d.Action));
        Assert.Equal((WorkItemStatus.Settling, false, until), (fx.State.Status, fx.State.Parked, fx.State.SettleUntil));
    }

    // ---- leases --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaseThatRunsOut_CountsAFailure_ArmsTheStepAgain_AndTheSecondParksTheItem()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var first = Assert.IsType<PhaseLeaseExpired>(fx.Timers.Last().Message);
        fx.Now = first.Due;
        var r = await fx.Send(first);
        Assert.Equal([WorkItemActions.LeaseExpired, WorkItemActions.StepEntered], r.Decisions.Select(d => d.Action));
        Assert.Equal(("1.2", 1, StepKind.Design), (fx.Arm, fx.State.FailuresOf(StepKind.Design), fx.State.Step));
        var second = Assert.IsType<PhaseLeaseExpired>(Assert.Single(r.Timers).Message);
        Assert.Equal((first.Due.AddMinutes(60), "1.2"), (second.Due, second.ArmId));
        // the first timer delivered again: it names an arm that is gone, but it is the last input, so it only publishes what it published
        Assert.Equal(WorkItemOutcome.Duplicate, (await fx.Send(first)).Decision.Outcome);
        Assert.Equal(1, fx.State.FailuresOf(StepKind.Design));
        fx.Now = second.Due;
        var parked = await fx.Send(second);
        Assert.Equal([WorkItemActions.LeaseExpired, WorkItemActions.Parked], parked.Decisions.Select(d => d.Action));
        Assert.True(fx.State.Parked);
        Assert.Empty(parked.Timers);
    }

    [Fact]
    public async Task ARenewedLease_HasATimerOfItsOwn_AndTheOldTimerIsStale()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var old = Assert.IsType<PhaseLeaseExpired>(fx.Timers.Last().Message);
        fx.Now = T0.AddMinutes(30);
        var renewed = await fx.Send(new LeaseRenewed(Item, 1, fx.Arm, "beat-1"));
        Assert.Empty(renewed.Decisions);
        var fresh = Assert.IsType<PhaseLeaseExpired>(Assert.Single(renewed.Timers).Message);
        Assert.Equal(T0.AddMinutes(90), fresh.Due);
        fx.Now = old.Due;
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(old)).Decision.Outcome);
        Assert.Equal(0, fx.State.FailuresOf(StepKind.Design));
        fx.Now = fresh.Due;
        Assert.Equal([WorkItemActions.LeaseExpired, WorkItemActions.StepEntered], (await fx.Send(fresh)).Decisions.Select(d => d.Action));
    }

    [Fact]
    public async Task TheDeliverStep_HasNoLease()
    {
        var fx = new SagaFixture();
        await fx.Start(); await fx.Merged();
        Assert.Null(fx.State.LeaseDue);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new LeaseRenewed(Item, 1, fx.Arm, "beat"))).Decision.Outcome);
    }

    [Fact]
    public async Task APolicy_SetsTheLeasesTheBudgetsAndTheSettleWindow()
    {
        var policy = new WorkItemPolicy(new Dictionary<StepKind, TimeSpan?> { [StepKind.Design] = TimeSpan.FromMinutes(5) }, Budgets: new RecoverySettings(MaxFailuresPerPhase: 1), SettleWindow: TimeSpan.FromHours(1), FinalEnvironment: "uat");
        var fx = new SagaFixture(policy);
        await fx.Start();
        Assert.Equal(T0.AddMinutes(5), fx.State.LeaseDue);
        var r = await fx.Budget(BudgetKind.Failure);   // (one failure is the limit)
        Assert.Equal(WorkItemActions.Parked, Assert.Single(r.Decisions).Action);
        await fx.Send(new ResumeWorkItem(Item, 0, "resume"));
        await fx.Merged();
        await fx.Delivered("prod");   // (not the final environment of this policy)
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
        var uat = await fx.Delivered("uat");
        Assert.Equal(T0.AddHours(1), uat.Timers[0].Due);
    }

    // ---- the state -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheState_RoundTripsThroughItsJson_ItemIdsAndDecisionsIncluded()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.Budget(BudgetKind.Failure);
        await fx.Design(true);
        var back = WorkItemState.FromJson(fx.State.ToJson());
        Assert.Equal(fx.State.ToJson(), back.ToJson());
        Assert.Equal(Item, back.ItemId);
        Assert.NotEmpty(back.Emitted);
        Assert.All(back.Emitted, d => Assert.Equal(Item, d.Item));
        Assert.Equal(fx.State.Emitted.Select(Shape), back.Emitted.Select(Shape));
        Assert.Equal(("docs", StepKind.Design), (back.Plan, back.Step));
        Assert.Contains("\"Step\":\"Design\"", fx.State.ToJson());   // (enums are names, readable in the table)
    }

    [Fact]
    public async Task ABusinessKey_IsKept_ButOnlyTheNewestKeepKeysOfThem()
    {
        var policy = new WorkItemPolicy(WorkItemPolicy.Default.Leases, KeepKeys: 3);
        var fx = new SagaFixture(policy);
        await fx.Start();
        for (var i = 0; i < 5; i++) await fx.Budget(BudgetKind.Registered, $"k{i}");
        Assert.Equal(["k2", "k3", "k4"], fx.State.Keys);
    }

    // ---- concurrent writers --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWriterThatLostTheRevision_AppliesItsInputAgainToTheStateThatWon()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var moved = false;
        fx.Store.BeforeSave = key =>
        {
            if (moved) return;
            moved = true;
            // another node's idle follow-up lands between this writer's read and its save
            var rec = fx.Store.Load(key, default).Result!;
            rec.State.IdleFollowUps = 1;
            Assert.True(fx.Store.Save(key, rec.State, rec.Revision, default).Result);
        };
        var r = await fx.Done();
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal((StepKind.Develop, 1), (fx.State.Step, fx.State.IdleFollowUps));   // both writes are in the state
        Assert.Equal(3, fx.Store.RevisionOf(fx.Key));
    }

    [Fact]
    public async Task AnItemThatKeepsChanging_EndsInAConflict_ForWolverineToRetry()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var inside = false;
        fx.Store.BeforeSave = key =>
        {
            if (inside) return;
            inside = true;
            var rec = fx.Store.Load(key, default).Result!;
            Assert.True(fx.Store.Save(key, rec.State, rec.Revision, default).Result);   // another writer is first, every time
            inside = false;
        };
        await Assert.ThrowsAsync<WorkItemConflictException>(() => fx.Done());
    }

    [Fact]
    public async Task TwoNodesStartingTheSameItem_StartItOnce()
    {
        var fx = new SagaFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fx.Svc.Handle(new StartWorkItem(Item, "intake"))));
        Assert.Single(fx.Store.States);
        Assert.Equal(1, fx.State.Generation);
        Assert.Equal(1, results.Count(r => r.Decision.Outcome == WorkItemOutcome.Applied));
        Assert.All(results.Where(r => r.Decision.Outcome != WorkItemOutcome.Applied), r => Assert.Contains(r.Decision.Outcome, new[] { WorkItemOutcome.Duplicate, WorkItemOutcome.Refused }));
    }

    // ---- what the store answers ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStore_NamesTheOwnedItems_AndCountsTheOpenOnesPerRepository()
    {
        var fx = new SagaFixture();
        await fx.Start();
        var other = new ItemId("acme", "orders", 8); var third = new ItemId("acme", "billing", 3);
        await fx.Svc.Handle(new StartWorkItem(other, "intake", "docs"));
        await fx.Svc.Handle(new StartWorkItem(third, "intake"));
        Assert.Equal(["acme/billing#3", "acme/orders#7", "acme/orders#8"], (await fx.Store.OwnedItems(default)).Order());
        var counts = await fx.Store.InFlightByRepository(default);
        Assert.Equal((2, 1), (counts["acme/orders"], counts["acme/billing"]));
        // a completed item is no longer owned; an item inside its settle window is owned but not in flight
        await fx.Svc.Handle(new MergeSeen(other, "m", new string('c', 40), 5));
        await fx.Merged(); await fx.Delivered("prod");
        Assert.Equal(["acme/billing#3", "acme/orders#7"], (await fx.Store.OwnedItems(default)).Order());
        counts = await fx.Store.InFlightByRepository(default);
        Assert.Equal(0, counts.GetValueOrDefault("acme/orders"));   // (7 is settling, 8 is done)
        Assert.Equal(1, counts["acme/billing"]);
    }
}
