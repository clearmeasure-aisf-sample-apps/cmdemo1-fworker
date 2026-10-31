using Fworker.Contracts.Messaging;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.3 (#340): the feature plan's steps on the WorkItemSaga, without a database (FeaturePlanTrackedTests runs the same machine in a host). Every rule is one v1 applies in a verb script or one of D5's recovery rows
/// (delivery-speed.md 5: Functional Testing with CI alongside, red CI as a fix-forward round, "the merge still needs both"), plus the item's conversation with the CapacityScheduler and the merge line.
/// </summary>
public class FeaturePlanTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Head2 = "ccc3333333333333333333333333333333333333";

    static WorkItemPolicy Policy(bool scheduler = true) => WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(scheduler) };

    static IEnumerable<T> Of<T>(SagaFixture fx) => fx.Commands.OfType<T>();

    int stamp;
    DateTimeOffset At(SagaFixture fx) => fx.Now.AddSeconds(++stamp);

    Task<WorkItemResult> Checks(SagaFixture fx, ChecksVerdict v, string head = Head, params string[] failed) => fx.Send(new ChecksObserved(Item, 0, $"checks|{++stamp}", head, v, failed));
    Task<WorkItemResult> Grant(SagaFixture fx, string slot = "s1", string phase = "design") => fx.Send(new SlotAssigned(Item, 1, $"slot|{slot}", slot, "octopus", "host-a", phase, At(fx), fx.Now.AddHours(4)));
    Task<WorkItemResult> Hold(SagaFixture fx, string text = "file-overlap acme/orders#3") => fx.Send(new SlotHeld(Item, 1, $"held|{text}|{++stamp}", "file-overlap", text["file-overlap ".Length..], "acme/orders#3", At(fx)));

    /// <summary>A started feature item with no scheduler, walked to the step.</summary>
    static async Task<SagaFixture> At(StepKind step, WorkItemPolicy? policy = null, string[]? files = null)
    {
        var fx = new SagaFixture(policy ?? Policy(scheduler: false));
        await fx.Start();
        if (step == StepKind.Design) return fx;
        await fx.Review(); await fx.Done();
        if (step == StepKind.Develop) return fx;
        await fx.PullRequest(Head, 11, files ?? ["src/a.cs"]); await fx.Review(); await fx.Done();
        if (step == StepKind.Verify) return fx;
        await fx.Verdict(true, Head);
        return fx;
    }

    // ---- inert without the rules: the core's steps ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheFeatureRules_TheFeaturePlanIsTheCores_NoReviewNoChecksNoCommand()
    {
        var fx = new SagaFixture();   // the default policy: the engine does not carry the plan's steps
        await fx.Start();
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Done()).Decision.Outcome);   // (no review asked)
        await fx.Done(); await fx.Done();
        Assert.Equal(StepKind.Merge, fx.State.Step);
        Assert.Equal(WorkItemOutcome.Refused, (await Checks(fx, ChecksVerdict.Red)).Decision.Outcome);
        Assert.Empty(fx.Commands);
        Assert.Empty(fx.State.Outbox);
    }

    [Fact]
    public async Task TheDocsPlan_AsksTheSchedulerForNothing_EvenWhereTheFeatureRulesAreOn()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        await fx.Verdict(true, Head);
        Assert.IsType<MergeRequested>(Assert.Single(fx.Commands));
        Assert.Equal("docs", Assert.IsType<MergeRequested>(fx.Commands[0]).Plan);
        Assert.All(fx.Commands, c => Assert.IsNotType<SlotWanted>(c));
    }

    // ---- design and development ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDesign_IsLeftOnlyAfterACompleteReview_AndItsFilesLineIsTheFootprint()
    {
        var fx = new SagaFixture(Policy(scheduler: false));
        await fx.Start();
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);
        await fx.Send(new DesignRecorded(Item, 1, fx.Arm, "design|1", false, ["src/a.cs", "src/b.cs"]));
        Assert.Equal(["src/a.cs", "src/b.cs"], fx.State.Footprint);
        Assert.False(fx.State.FootprintFromPullRequest);
        await fx.Review(complete: false);
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);   // (findings do not satisfy the gate)
        await fx.Review();
        await fx.Done();
        Assert.Equal((StepKind.Develop, "feature"), (fx.State.Step, fx.State.Plan));
    }

    [Fact]
    public async Task TheDesignsMarker_StillSwitchesTheItemToTheDocsPlan_AndBack()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await fx.Design(marker: true);
        Assert.Equal("docs", fx.State.Plan);
        await fx.Design(marker: false);
        Assert.Equal("feature", fx.State.Plan);
    }

    [Fact]
    public async Task Development_IsLeftOnlyWithACompleteReviewAndAPullRequest_AndTheCheckedHeadIsNotRequired()
    {
        var fx = await At(StepKind.Develop);
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);
        await fx.Review();
        Assert.StartsWith("no-open-pr", (await fx.Done()).Decision.Reason);
        await fx.PullRequest(Head, 11, "src/a.cs");
        // CI may still be running: Functional Testing starts at the push (delivery-speed.md 5.1)
        await fx.Done();
        Assert.Equal((StepKind.Verify, "feature"), (fx.State.Step, fx.State.Plan));
        Assert.Equal("", fx.State.ChecksState);
    }

    [Fact]
    public async Task ThePullRequestsFiles_ReplaceTheFootprint_AndTheSchedulerIsTold()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await fx.Send(new DesignRecorded(Item, 1, fx.Arm, "design|1", false, ["src/**"]));
        await Grant(fx);
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head, 11, "src/a.cs", "src/b.cs");
        Assert.Equal(["src/a.cs", "src/b.cs"], fx.State.Footprint);
        Assert.True(fx.State.FootprintFromPullRequest);
        var change = Assert.Single(Of<FootprintChanged>(fx));
        Assert.Equal(["src/a.cs", "src/b.cs"], change.Files);
        Assert.StartsWith("footprint|acme/orders#7|1|", change.Key);
    }

    // ---- verify: CI alongside, red CI as a fix-forward round -----------------------------------------------------------------------------

    [Fact]
    public async Task RedChecksOnTheHeadUnderTest_AreAFixForwardRound_NotADemotion_AndTheNextPushIsTestedAgain()
    {
        var fx = await At(StepKind.Verify);
        var armBefore = fx.Arm;
        var r = await Checks(fx, ChecksVerdict.Red, Head, "Build result", "Qodana");
        Assert.Equal(("checks-red", "fix-forward", "step-entered"), (r.Decisions[0].Action, r.Decisions[1].Action, r.Decisions[2].Action));
        Assert.Equal(("round 1 of 4", "CI is red on the head under test"), (r.Decisions[1].Reasons[0], r.Decisions[1].Reasons[1]));
        Assert.Equal((StepKind.Develop, 1, 0), (fx.State.Step, fx.State.FixForwardRounds, fx.State.Demotions));
        Assert.NotEqual(armBefore, fx.Arm);
        Assert.Equal(["Build result", "Qodana"], fx.State.ChecksFailed);
        // the owner fixes and pushes: Functional Testing runs again on the next head
        await fx.Review(); await fx.PullRequest(Head2, 11, "src/a.cs"); await fx.Done();
        Assert.Equal((StepKind.Verify, Head2), (fx.State.Step, fx.State.Head));
        Assert.Equal("", fx.State.ChecksState);   // (what was observed of the old head says nothing of the new one)
    }

    [Fact]
    public async Task TheRoundsOfFixForward_AreBounded_AndGreenChecksStartThemAgain()
    {
        var fx = await At(StepKind.Verify, Policy(scheduler: false));
        for (var round = 1; round <= 4; round++)
        {
            await Checks(fx, ChecksVerdict.Red, fx.State.Head);
            Assert.Equal((StepKind.Develop, round), (fx.State.Step, fx.State.FixForwardRounds));
            await fx.Review(); await fx.PullRequest($"{round}{Head[1..]}", 11, "src/a.cs"); await fx.Done();
        }
        var last = await Checks(fx, ChecksVerdict.Red, fx.State.Head, "Build result");
        Assert.True(fx.State.Parked);
        Assert.StartsWith("fix-forward-exhausted", fx.State.ParkReason);
        Assert.Contains("4 fix-forward rounds", fx.State.ParkReason);
        Assert.Contains(last.Decisions, d => d.Action == "parked");

        var fresh = await At(StepKind.Verify);
        await Checks(fresh, ChecksVerdict.Red);
        Assert.Equal(1, fresh.State.FixForwardRounds);
        await fresh.Review(); await fresh.PullRequest(Head2, 11, "src/a.cs"); await fresh.Done();
        await Checks(fresh, ChecksVerdict.Green, Head2);
        Assert.Equal(0, fresh.State.FixForwardRounds);   // (v1 forgets the rounds when the checks are green)
    }

    [Fact]
    public async Task ChecksOfAnOtherHead_OrOfAnItemPastItsMerge_OrParked_AreNotTheItemsRound()
    {
        var fx = await At(StepKind.Verify);
        var old = await Checks(fx, ChecksVerdict.Red, "ddd4444444444444444444444444444444444444");
        Assert.Equal(WorkItemOutcome.Stale, old.Decision.Outcome);
        Assert.Equal((StepKind.Verify, 0), (fx.State.Step, fx.State.FixForwardRounds));
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Equal(WorkItemOutcome.Refused, (await Checks(fx, ChecksVerdict.Red)).Decision.Outcome);

        var merged = await At(StepKind.Merge);
        await merged.Merged();
        Assert.Equal(StepKind.Deliver, merged.State.Step);
        Assert.Equal(WorkItemOutcome.Stale, (await Checks(merged, ChecksVerdict.Red)).Decision.Outcome);
    }

    [Fact]
    public async Task RedChecksInDevelopment_AreRecorded_AndCostNoRound()
    {
        var fx = await At(StepKind.Verify);
        await Checks(fx, ChecksVerdict.Red);   // -> Development (round 1)
        Assert.Equal(StepKind.Develop, fx.State.Step);
        var again = await Checks(fx, ChecksVerdict.Red);   // the watcher reads the same red head again before the owner pushed
        Assert.Equal(WorkItemOutcome.Applied, again.Decision.Outcome);
        Assert.Equal((StepKind.Develop, 1), (fx.State.Step, fx.State.FixForwardRounds));
    }

    [Fact]
    public async Task PendingChecks_RenewTheLeaseOfTheStepThatWaitsForThem_AndANewHeadMakesTheOldObservationVoid()
    {
        var fx = await At(StepKind.Verify);
        var due = fx.State.LeaseDue!.Value;
        fx.Now = fx.Now.AddMinutes(100);
        var r = await Checks(fx, ChecksVerdict.Pending);
        Assert.Equal("pending", fx.State.ChecksState);
        Assert.True(fx.State.LeaseDue > due);
        Assert.Contains(r.Timers, t => t.Message is PhaseLeaseExpired);
        await Checks(fx, ChecksVerdict.Green);
        Assert.Equal("green", fx.State.ChecksState);
        await fx.PullRequest(Head2, 11, "src/a.cs");
        Assert.Equal(("", ""), (fx.State.ChecksHead, fx.State.ChecksState));
    }

    [Fact]
    public async Task APassOnAHeadWhoseChecksAreRed_IsAFixForwardRound_NotAMerge()
    {
        // red was seen while the owner still developed (no round: it is the owner's own fix), and the item went to Functional Testing on that head all the same
        var fx = await At(StepKind.Develop);
        await fx.PullRequest(Head, 11, "src/a.cs");
        await Checks(fx, ChecksVerdict.Red, Head, "Build result");
        Assert.Equal((StepKind.Develop, 0), (fx.State.Step, fx.State.FixForwardRounds));
        await fx.Review(); await fx.Done();
        Assert.Equal(StepKind.Verify, fx.State.Step);
        var r = await fx.Verdict(true, Head);
        Assert.Equal((StepKind.Develop, 1), (fx.State.Step, fx.State.FixForwardRounds));
        Assert.Contains(r.Decisions, d => d.Action == "fix-forward");
        Assert.Empty(Of<MergeRequested>(fx));
        Assert.Equal("", fx.State.VerdictHead);
    }

    [Fact]
    public async Task AFailVerdict_IsTheQualityDemotion_AndHasItsOwnBudget()
    {
        var fx = await At(StepKind.Verify);
        await Checks(fx, ChecksVerdict.Red);
        await fx.Review(); await fx.PullRequest(Head2, 11, "src/a.cs"); await fx.Done();
        await fx.Verdict(false, Head2, summary: "criterion 2 not met");
        Assert.Equal((StepKind.Develop, 1, 1), (fx.State.Step, fx.State.Demotions, fx.State.FixForwardRounds));
    }

    // ---- merge: the merge still needs both ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APassBeforeCIEnds_WaitsWithoutMergeCalls_AndGreenChecksOnThePassHeadAskForTheMerge()
    {
        var fx = await At(StepKind.Verify);
        var r = await fx.Verdict(true, Head);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        Assert.True(fx.State.MergeAwaitingChecks);
        Assert.Empty(Of<MergeRequested>(fx));
        Assert.Equal(("merge-blocked", "checks-pending"), (r.Decisions.Last().Action, r.Decisions.Last().Reasons[0]));
        Assert.Equal(0, fx.State.MergeAttempt);

        var pending = await Checks(fx, ChecksVerdict.Pending);
        Assert.Empty(pending.Sent.OfType<MergeRequested>());
        Assert.NotNull(fx.State.LeaseDue);
        await Checks(fx, ChecksVerdict.Green);
        var m = Assert.Single(Of<MergeRequested>(fx));
        Assert.Equal(("feature", Head, 1), (m.Plan, m.Head, m.Attempt));
        Assert.False(fx.State.MergeAwaitingChecks);
        Assert.True(fx.State.TrainEntered);
    }

    [Fact]
    public async Task APassAfterTheChecksWereGreen_AsksForTheMergeAtOnce()
    {
        var fx = await At(StepKind.Verify);
        await Checks(fx, ChecksVerdict.Green);
        await fx.Verdict(true, Head);
        Assert.Equal("feature", Assert.Single(Of<MergeRequested>(fx)).Plan);
        Assert.False(fx.State.MergeAwaitingChecks);
    }

    [Fact]
    public async Task RedChecksWhileTheMergeWaits_AreAFixForwardRound_AndThePassIsVoid()
    {
        var fx = await At(StepKind.Verify);
        await fx.Verdict(true, Head);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        await Checks(fx, ChecksVerdict.Red, Head, "Build result");
        Assert.Equal((StepKind.Develop, 1, ""), (fx.State.Step, fx.State.FixForwardRounds, fx.State.VerdictHead));
        Assert.False(fx.State.MergeAwaitingChecks);
    }

    [Fact]
    public async Task TheMergesAnswers_AreTheDocsPlansDecisions_AndAHeadThatMovedReleasesThePlaceInTheLine()
    {
        var fx = await At(StepKind.Verify);
        await Checks(fx, ChecksVerdict.Green);
        await fx.Verdict(true, Head);
        // blocked: ask again after the pause, and the place in the line is kept
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "merge-train", "waits for #3", Transient: true));
        Assert.NotNull(fx.State.MergeRetryAt);
        Assert.Empty(Of<MergeTrainLeft>(fx));
        // the PASS covers another head than the branch's: nothing was merged, a new verdict is needed
        fx.Now = fx.State.MergeRetryAt!.Value;
        await fx.Send(new MergeRetryDue(Item, 1, fx.Arm, fx.State.MergeRetryAt!.Value));
        await fx.Attempted(new MergeResult(MergeResultKind.HeadMoved, "head-moved", "the branch moved"));
        Assert.Equal(StepKind.Verify, fx.State.Step);
        var left = Assert.Single(Of<MergeTrainLeft>(fx));
        Assert.Equal(("acme/orders", SagaFixture.Item), (left.Key, left.Item));
        Assert.False(fx.State.TrainEntered);
    }

    [Fact]
    public async Task ANewHeadAfterThePass_SendsTheItemBackToVerify_AndReleasesThePlace()
    {
        var fx = await At(StepKind.Verify);
        await Checks(fx, ChecksVerdict.Green);
        await fx.Verdict(true, Head);
        Assert.True(fx.State.TrainEntered);
        await fx.PullRequest(Head2, 11, "src/a.cs");
        Assert.Equal((StepKind.Verify, ""), (fx.State.Step, fx.State.VerdictHead));
        Assert.Single(Of<MergeTrainLeft>(fx));
        Assert.Equal("", fx.State.ChecksState);
    }

    [Fact]
    public async Task AMergeAnItemParkedOrHandedBack_LeavesTheLine_AndAMergeThatHappenedDoesNot()
    {
        var parked = await At(StepKind.Verify);
        await Checks(parked, ChecksVerdict.Green);
        await parked.Verdict(true, Head);
        await parked.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Single(Of<MergeTrainLeft>(parked));

        var merged = await At(StepKind.Verify);
        await Checks(merged, ChecksVerdict.Green);
        await merged.Verdict(true, Head);
        await merged.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: "bbb2222222222222222222222222222222222222"));
        Assert.Equal(StepKind.Deliver, merged.State.Step);
        Assert.Empty(Of<MergeTrainLeft>(merged));   // (the train settles its line by the answer; ItemMerged releases it too)
    }

    // ---- the item's slot ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStepThatNeedsASession_AsksForASlot_AndTheLeaseRunsOnlyFromTheGrant()
    {
        var fx = new SagaFixture(Policy());
        var start = await fx.Start();
        var ask = Assert.Single(start.Sent.OfType<SlotWanted>());
        Assert.Equal(("design", false, 1, "1.1"), (ask.Phase, ask.Resume, ask.ColumnDepth, ask.ArmId));
        Assert.Equal(Item, ask.Item);
        Assert.Equal(fx.Now, ask.RequestedAt);
        Assert.True(fx.State.SlotAsked);
        Assert.Null(fx.State.LeaseDue);
        Assert.DoesNotContain(start.Timers, t => t.Message is PhaseLeaseExpired);

        var granted = await Grant(fx);
        Assert.Equal(("s1", false), (fx.State.SlotId, fx.State.SlotAsked));
        Assert.NotNull(fx.State.LeaseDue);
        Assert.Single(granted.Timers, t => t.Message is PhaseLeaseExpired);
        Assert.Equal(("slot-granted", "octopus on host-a"), (granted.Decisions.Single().Action, granted.Decisions.Single().Reasons[0]));
        Assert.True(fx.State.EverGranted);
    }

    [Fact]
    public async Task TheNextSteps_AskAgain_AsAnOwnedItem_AndKeepTheSlotTheSessionHolds()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await Grant(fx);
        await fx.Review();
        var done = await fx.Done();   // Design -> Development
        var ask = Assert.Single(done.Sent.OfType<SlotWanted>());
        Assert.Equal(("develop", true, 2), (ask.Phase, ask.Resume, ask.ColumnDepth));
        Assert.False(fx.State.SlotAsked);          // (the session goes on: the lease runs at once)
        Assert.NotNull(fx.State.LeaseDue);
        Assert.Equal("s1", fx.State.SlotId);
        await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Review();
        var verify = await fx.Done();
        Assert.Equal(("verify", 3), (verify.Sent.OfType<SlotWanted>().Single().Phase, verify.Sent.OfType<SlotWanted>().Single().ColumnDepth));
        var keys = Of<SlotWanted>(fx).Select(a => a.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());   // (a key per request: asking again is a new request)
    }

    [Fact]
    public async Task AHoldFromTheScheduler_TakesTheSlotAndTheLeaseAway_ThenTheGrantGivesThemBack()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await Grant(fx);
        await fx.Review(); await fx.Done();   // Design -> Development; the scheduler decides the overlap
        var held = await Hold(fx);
        Assert.Equal(("held", "file-overlap acme/orders#3"), (held.Decisions.Single().Action, held.Decisions.Single().Reasons[0]));
        Assert.Equal(("", true, null, "file-overlap acme/orders#3"), (fx.State.SlotId, fx.State.SlotAsked, fx.State.LeaseDue, fx.State.SlotWait));
        // the same wait is not decided twice
        var again = await Hold(fx);
        Assert.Empty(again.Decisions);
        var granted = await Grant(fx, "s2", "develop");
        Assert.Equal(("s2", false), (fx.State.SlotId, fx.State.SlotAsked));
        Assert.NotNull(fx.State.LeaseDue);
        Assert.Equal("slot-granted", granted.Decisions.Single().Action);
    }

    [Fact]
    public async Task ASchedulerWordThatArrivesOutOfOrder_IsStale()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        var early = At(fx);
        var late = At(fx);
        // the grant (late) is applied first; the wait that preceded it (early) arrives after it
        await fx.Send(new SlotAssigned(Item, 1, "slot|s1", "s1", "octopus", null, "design", late, fx.Now.AddHours(4)));
        var r = await fx.Send(new SlotHeld(Item, 1, "held|x|1", "file-overlap", "acme/orders#3", "acme/orders#3", early));
        Assert.Equal(WorkItemOutcome.Stale, r.Decision.Outcome);
        Assert.Equal("s1", fx.State.SlotId);
        // and the same the other way round
        var other = new SagaFixture(Policy());
        await other.Start();
        await other.Send(new SlotHeld(Item, 1, "held|y|2", "runtime-at-capacity", "", null, late));
        Assert.Equal(WorkItemOutcome.Stale, (await other.Send(new SlotAssigned(Item, 1, "slot|s9", "s9", "octopus", null, "design", early, fx.Now.AddHours(4)))).Decision.Outcome);
        Assert.Equal("", other.State.SlotId);
    }

    [Fact]
    public async Task AGrantTheItemCanNoLongerUse_IsGivenBackAtOnce()
    {
        var fx = await At(StepKind.Merge, Policy());
        var r = await fx.Send(new SlotAssigned(Item, 1, "slot|s5", "s5", "octopus", null, "verify", At(fx), fx.Now.AddHours(4)));
        Assert.Equal("end|acme/orders#7|s5", Assert.Single(r.Sent.OfType<SessionEnded>()).Key);
        Assert.Equal("s5", r.Sent.OfType<SessionEnded>().Single().SlotId);
        Assert.Equal("", fx.State.SlotId);

        var parked = new SagaFixture(Policy());
        await parked.Start();
        await parked.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        var late = await parked.Send(new SlotAssigned(Item, 1, "slot|s6", "s6", "octopus", null, "design", At(parked), parked.Now.AddHours(4)));
        Assert.Equal("s6", Assert.Single(late.Sent.OfType<SessionEnded>()).SlotId);
    }

    // ---- W9.10j: the saga starts the session of its slot ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AGrant_StartsTheSessionOfTheSlot_WithTheArmItWasGrantedFor_OnceForTheSlot()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        var armAtTheGrant = fx.State.ArmId;
        var granted = await Grant(fx);
        var start = Assert.Single(granted.Sent.OfType<SessionStartRequested>());
        Assert.Equal(("s1", "octopus", "host-a", "design", armAtTheGrant, "start|acme/orders#7|s1"), (start.SlotId, start.Runtime, start.Host, start.Phase, start.ArmId, start.Key));
        Assert.Equal(1, start.Generation);
        // the same grant told twice (the same key) is published again with the same key, which the start's handler takes as one start; a newer word of the same slot (another key) is no new start
        var again = await Grant(fx);
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        Assert.Equal(start.Key, Assert.Single(again.Sent.OfType<SessionStartRequested>()).Key);
        var newer = await fx.Send(new SlotAssigned(Item, 1, "slot|s1|again", "s1", "octopus", "host-a", "design", At(fx), fx.Now.AddHours(4)));
        Assert.Empty(newer.Sent.OfType<SessionStartRequested>());
        // a later slot (the session was lost, the item asked again) is a start of its own
        await fx.Send(new SessionLost(Item, 1, "expired|s1", "s1", "the slot ran out of time", At(fx)));
        var next = await Grant(fx, "s2");
        Assert.Equal("start|acme/orders#7|s2", Assert.Single(next.Sent.OfType<SessionStartRequested>()).Key);
    }

    [Fact]
    public async Task AGrantTheItemCanNoLongerUse_StartsNoSession()
    {
        var fx = await At(StepKind.Merge, Policy());
        var r = await fx.Send(new SlotAssigned(Item, 1, "slot|s5", "s5", "octopus", null, "verify", At(fx), fx.Now.AddHours(4)));
        Assert.Empty(r.Sent.OfType<SessionStartRequested>());
        var parked = new SagaFixture(Policy());
        await parked.Start();
        await parked.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Empty((await parked.Send(new SlotAssigned(Item, 1, "slot|s6", "s6", "octopus", null, "design", At(parked), parked.Now.AddHours(4)))).Sent.OfType<SessionStartRequested>());
    }

    [Fact]
    public async Task APlanThatUsesNoScheduler_StartsNoSessionOfItsOwn()
    {
        var fx = new SagaFixture(Policy(scheduler: false));
        await fx.Start();
        Assert.Empty(fx.Commands.OfType<SessionStartRequested>());
        Assert.Empty(fx.State.Outbox.OfType<StartSession>());
    }

    [Fact]
    public async Task ALostSession_FreesTheSlot_AndTheItemAsksAgainAsAnOwnedItem_WithoutCountingAFailure()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await Grant(fx);
        await fx.Review(); await fx.Done();
        var lost = await fx.Send(new SessionLost(Item, 1, "expired|s1", "s1", "the slot ran out of time", At(fx)));
        Assert.Equal("session-lost", lost.Decisions.Single().Action);
        Assert.Equal(("", true, null), (fx.State.SlotId, fx.State.SlotAsked, fx.State.LeaseDue));
        var ask = Assert.Single(lost.Sent.OfType<SlotWanted>());
        Assert.Equal(("develop", true), (ask.Phase, ask.Resume));
        Assert.Equal(0, fx.State.FailuresOf(StepKind.Develop));
        // a loss of a slot the item does not hold is stale, and one for a plan without slots is refused
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new SessionLost(Item, 1, "expired|s0", "s0", "x", At(fx)))).Decision.Outcome);
        await Grant(fx, "s2", "develop");
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new SessionLost(Item, 1, "expired|s1b", "s1", "x", At(fx)))).Decision.Outcome);
    }

    [Fact]
    public async Task ALeaseThatRanOut_EndsTheDeadSessionsSlot_BeforeTheStepAsksAgain()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start();
        await Grant(fx);
        fx.Now = fx.Now.AddMinutes(61);
        var r = await fx.Send(new PhaseLeaseExpired(Item, 1, fx.Arm, fx.State.LeaseDue!.Value));
        Assert.Equal(["session-ended", "slot-wanted"], r.Sent.Select(c => c is SessionEnded ? "session-ended" : c is SlotWanted ? "slot-wanted" : "?").ToArray());
        Assert.Equal(("", true), (fx.State.SlotId, fx.State.SlotAsked));
        Assert.Equal(1, fx.State.FailuresOf(StepKind.Design));
    }

    [Fact]
    public async Task TheMergeNeedsNoSession_TheSlotGoesBack_AndTheMergeClearsTheItem()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(); await Grant(fx);
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Review(); await fx.Done();
        fx.Commands.Clear();
        await fx.Verdict(true, Head);   // Merge: the session is over
        var end = Assert.Single(Of<SessionEnded>(fx));
        Assert.Equal("s1", end.SlotId);
        Assert.Empty(Of<ClearItem>(fx));   // (the item stays in flight for the file-overlap rule until it is merged)
        Assert.Equal("", fx.State.SlotId);
        await fx.Merged();
        var clear = Assert.Single(Of<ClearItem>(fx));
        Assert.Equal("merged", clear.Reason);
        await fx.Delivered("prod");
        await fx.Send(new SettleWindowElapsed(Item, 1, fx.Arm));
        Assert.Single(Of<ClearItem>(fx));   // (once: completing the item adds none)
    }

    [Fact]
    public async Task AParkedItem_GivesItsSlotBackAndLeavesTheScheduler_AndAResumedOneAsksAgain()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(); await Grant(fx);
        fx.Commands.Clear();
        var parked = await fx.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Equal(["session-ended", "clear-item"], parked.Sent.Select(c => c is SessionEnded ? "session-ended" : c is ClearItem ? "clear-item" : "?").ToArray());
        Assert.Equal(("", false), (fx.State.SlotId, fx.State.SlotAsked));
        var resumed = await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        var ask = Assert.Single(resumed.Sent.OfType<SlotWanted>());
        Assert.Equal(("design", true), (ask.Phase, ask.Resume));   // (an owned item: rank 2)
        Assert.True(fx.State.SlotAsked);
        // parking twice in a row clears once more only when a request was made in between
        var again = await fx.Send(new ParkWorkItem(Item, 0, "park-2", "person"));
        Assert.Single(again.Sent.OfType<ClearItem>());
        Assert.Equal(2, Of<ClearItem>(fx).Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public async Task AnItemHandedBackToV1_OrSwitchedToTheDocsPlan_GivesEverythingBack()
    {
        // the engine carries the feature plan only: a design with the docs-only marker hands the item back to v1
        var back = new SagaFixture(Policy() with { CarriedPlans = ["feature"] });
        await back.Start(); await Grant(back);
        back.Commands.Clear();
        var handed = await back.Design(marker: true);
        Assert.Equal(WorkItemStatus.Completed, back.State.Status);
        Assert.Contains(handed.Decisions, d => d.Action == "handed-back");
        Assert.Equal(["session-ended", "clear-item"], handed.Sent.Select(c => c is SessionEnded ? "session-ended" : c is ClearItem ? "clear-item" : "?").ToArray());

        // the engine carries both: the docs plan has no use of the scheduler, and its step runs on its lease
        var fx = new SagaFixture(Policy());
        await fx.Start(); await Grant(fx);
        fx.Commands.Clear();
        var switched = await fx.Design(marker: true);
        Assert.Equal("docs", fx.State.Plan);
        Assert.Equal(["session-ended", "clear-item"], switched.Sent.Select(c => c is SessionEnded ? "session-ended" : c is ClearItem ? "clear-item" : "?").ToArray());
        Assert.Equal(("", false), (fx.State.SlotId, fx.State.SlotAsked));
        Assert.NotNull(fx.State.LeaseDue);
    }

    [Fact]
    public async Task ADocsItemThatTurnsOutToBeAFeature_AsksForASlotWhereItIs()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(plan: "docs");
        Assert.Empty(Of<SlotWanted>(fx));
        var r = await fx.Design(marker: false);   // the latest design does not carry the marker
        Assert.Equal("feature", fx.State.Plan);
        var ask = Assert.Single(r.Sent.OfType<SlotWanted>());
        Assert.Equal("design", ask.Phase);
        Assert.True(fx.State.SlotAsked);
        Assert.Null(fx.State.LeaseDue);
    }

    // ---- redelivery, serialisation ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARedeliveredInput_PublishesItsCommandsAgain_AndAnotherInputDropsThem()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(); await Grant(fx);
        await fx.Review();
        var done = await fx.Send(new StepCompleted(Item, 1, fx.Arm, "done|1"));
        var ask = Assert.Single(done.Sent.OfType<SlotWanted>());
        // the handler died after the save: the same input again
        var armBefore = fx.Arm;
        var replay = await fx.Svc.Handle(new StepCompleted(Item, 1, "1.1", "done|1"));
        Assert.Equal(WorkItemOutcome.Duplicate, replay.Decision.Outcome);
        var again = Assert.Single(replay.Sent.OfType<SlotWanted>());
        Assert.Equal((ask.Key, ask.Phase, ask.ArmId), (again.Key, again.Phase, again.ArmId));
        Assert.Equal(armBefore, fx.Arm);
        // the next input does not repeat them
        var next = await fx.Send(new LeaseRenewed(Item, 1, fx.Arm, "renew-1"));
        Assert.Empty(next.Sent);
    }

    [Fact]
    public void TheStatesCommands_RoundTripThroughJson_AsTheirOwnTypes()
    {
        var state = new WorkItemState { Item = "acme/orders#7", Generation = 1 };
        state.Outbox.AddRange([new AskSlot("a", "1.1", "design", true, 1, ["x"], false, SagaFixture.T0), new EndSession("e", "s1"), new ClearSlot("c", "parked"), new ChangeFootprint("f", ["y"]), new LeaveTrain("l", "parked")]);
        var back = WorkItemState.FromJson(state.ToJson());
        Assert.Equal([typeof(AskSlot), typeof(EndSession), typeof(ClearSlot), typeof(ChangeFootprint), typeof(LeaveTrain)], back.Outbox.Select(c => c.GetType()).ToArray());
        Assert.Equal(["x"], ((AskSlot)back.Outbox[0]).Footprint);
        // a state saved before the feature plan has none of it
        var old = WorkItemState.FromJson("""{"Item":"acme/orders#7","Generation":1,"Plan":"feature"}""");
        Assert.Empty(old.Outbox);
        Assert.Equal(("", false, 0), (old.SlotId, old.SlotAsked, old.FixForwardRounds));
    }

    [Fact]
    public void TheMessagesOfTheFeaturePlan_AreInputsOfTheSaga_WithStableIdentities()
    {
        var inputs = WorkItemTopology.Inputs.ToList();
        foreach (var t in new[] { typeof(ChecksObserved), typeof(SlotAssigned), typeof(SlotHeld), typeof(SessionLost) })
        {
            Assert.Contains(t, inputs);
            Assert.NotNull(Attribute.GetCustomAttribute(t, typeof(MessageIdentityAttribute)));
        }
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(SlotWanted), typeof(MessageIdentityAttribute)));
        // what the scheduler publishes reaches the saga through the router
        var granted = WorkItemRouting.Handle(new SlotGranted(Item, 1, "s1", "octopus", "h", "guaranteed", "design", SagaFixture.T0, SagaFixture.T0.AddHours(4)));
        Assert.IsType<SlotAssigned>(Assert.Single(granted));
        var held = WorkItemRouting.Handle(new Held(Item, 1, "file-overlap", "acme/orders#3", Fworker.Contracts.Ids.ItemId.Parse("acme/orders#3"), SagaFixture.T0));
        Assert.Equal("file-overlap acme/orders#3", Assert.IsType<SlotHeld>(Assert.Single(held)).Waiting);
        Assert.IsType<SessionLost>(Assert.Single(WorkItemRouting.Handle(new SlotExpired(Item, 1, "s1", "octopus", null, SagaFixture.T0))));
    }
}
