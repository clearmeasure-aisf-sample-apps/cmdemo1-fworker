using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10f: the steps of v1's sweep as rules of the WorkItemSaga, without a database. Each test names the sweep's step it stands for (plugin/scripts/lib/Verbs.Sweep.ps1): the settle's wait for master's CI and its parks
/// (D5), the auto-unpark of master-broken, the expiry of a park, merge pickup, the demotion of a PASS whose pull request was closed, clear-hold, the idle owner, and the settle's re-arm.
/// </summary>
public class SweepStepsTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Commit = "abcdef0123456789abcdef0123456789abcdef01";
    const string Later = "bbb2222222222222222222222222222222222222";

    static WorkItemPolicy Policy(SweepRules? sweep = null, bool scheduler = false) =>
        WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(scheduler), Sweep = sweep ?? SweepRules.Default };

    int n;
    Task<WorkItemResult> Master(SagaFixture fx, MasterVerdict v, string commit = Commit, string via = "", IReadOnlyList<RerunRun>? rerun = null, params string[] failed) =>
        fx.Send(new MasterObserved(Item, 0, $"master|{++n}", commit, v, failed, via, rerun));
    Task<WorkItemResult> Closed(SagaFixture fx, int pr = 11) => fx.Send(new PullRequestClosed(Item, 0, $"closed|{++n}", pr));

    /// <summary>A feature item at the given step (no scheduler), with a PASS recorded for <see cref="Head"/> when the step is the Merge.</summary>
    static async Task<SagaFixture> At(StepKind step, WorkItemPolicy? policy = null)
    {
        var fx = new SagaFixture(policy ?? Policy());
        await fx.Start();
        if (step == StepKind.Design) return fx;
        await fx.Review(); await fx.Done();
        if (step == StepKind.Develop) return fx;
        await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Review(); await fx.Done();
        if (step == StepKind.Verify) return fx;
        await fx.Verdict(true, Head);
        if (step == StepKind.Merge) return fx;
        await fx.Merged(Commit);
        return fx;
    }

    static DateTimeOffset DueOf<T>(SagaFixture fx) where T : WorkItemInput => fx.Timers.Last(t => t.Message is T).Due;

    // ---- the watch of master's CI (the settle's master-green wait, D5) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AMergedCodeItem_AwaitsMasterGreen_WithATimeoutTimer()
    {
        var fx = await At(StepKind.Deliver);
        Assert.Equal((StepKind.Deliver, "waiting"), (fx.State.Step, fx.State.MasterState));
        var timer = Assert.IsType<MasterWatchElapsed>(fx.Timers.Last(t => t.Message is MasterWatchElapsed).Message);
        Assert.Equal(fx.Now + SweepRules.Default.MasterTimeout, timer.Due);
        Assert.Equal(timer.Due, fx.State.MasterDue);
    }

    [Fact]
    public async Task WithoutTheSweepRules_NothingWatchesMaster_AndADocsItemEndsAtTheMerge()
    {
        var core = new SagaFixture(WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false) });
        await core.Start(); await core.Review(); await core.Done(); await core.PullRequest(Head, 11, "src/a.cs"); await core.Review(); await core.Done(); await core.Verdict(true, Head); await core.Merged(Commit);
        Assert.Equal(("", StepKind.Deliver), (core.State.MasterState, core.State.Step));
        Assert.DoesNotContain(core.Timers, t => t.Message is MasterWatchElapsed);
        Assert.Equal(WorkItemOutcome.Refused, (await Master(core, MasterVerdict.Green)).Decision.Outcome);

        var docs = new SagaFixture(Policy());
        await docs.Start(plan: "docs"); await docs.Review(); await docs.Done(); await docs.PullRequest(Head); await docs.Review(); await docs.Done(); await docs.Verdict(true, Head); await docs.Merged(Commit);
        Assert.False(docs.State.IsOpen);
        Assert.Equal("", docs.State.MasterState);
    }

    [Fact]
    public async Task GreenMaster_EndsTheWait_AndLeavesNoTimer()
    {
        var fx = await At(StepKind.Deliver);
        var r = await Master(fx, MasterVerdict.Green);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal(("green", Commit), (fx.State.MasterState, fx.State.MasterGreenVia));
        Assert.Null(fx.State.MasterDue);
        Assert.Contains(WorkItemActions.MasterGreen, fx.Actions);
        // the timer that was scheduled finds the wait over
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new MasterWatchElapsed(Item, 1, DueOf<MasterWatchElapsed>(fx)))).Decision.Outcome);
        Assert.False(fx.State.Parked);
    }

    [Fact]
    public async Task RedMaster_ParksTheItemMasterBroken_WithTheFailedChecks_AndTheWaitStays()
    {
        var fx = await At(StepKind.Deliver);
        var r = await Master(fx, MasterVerdict.Red, failed: ["acceptance", "unit"]);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.True(fx.State.Parked);
        Assert.StartsWith("master-broken:", fx.State.ParkReason);
        Assert.Equal("broken", fx.State.MasterState);
        Assert.Equal(["acceptance", "unit"], fx.State.MasterFailed);
        Assert.Equal(fx.Now + SweepRules.Default.ParkTime, fx.State.ParkUntil);
        Assert.Contains(WorkItemActions.MasterBroken, fx.Actions);
        // the park's timer is there, the wait's is not
        Assert.IsType<ParkExpired>(Assert.Single(r.Timers).Message);
    }

    [Fact]
    public async Task NoVerdictWithinTheTimeout_ParksTheItemMasterGreenTimeout()
    {
        var fx = await At(StepKind.Deliver);
        var due = fx.State.MasterDue!.Value;
        fx.Now = due;
        var r = await fx.Send(new MasterWatchElapsed(Item, 1, due));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.True(fx.State.Parked);
        Assert.StartsWith("master-green-timeout:", fx.State.ParkReason);
        Assert.Equal("timeout", fx.State.MasterState);
        Assert.Contains(WorkItemActions.MasterTimeout, fx.Actions);
        // the same timer twice is one park
        Assert.NotEqual(WorkItemOutcome.Applied, (await fx.Send(new MasterWatchElapsed(Item, 1, due))).Decision.Outcome);
    }

    [Fact]
    public async Task AReRunOfTheFailedJobs_CountsTheTimeoutAgainFromTheReRun_AndTheOldTimerIsStale()
    {
        var fx = await At(StepKind.Deliver);
        var first = fx.State.MasterDue!.Value;
        fx.Now = first.AddMinutes(-5);
        await Master(fx, MasterVerdict.Pending, rerun: [new RerunRun { Id = 9, Attempt = 1 }]);
        Assert.Equal(fx.Now + SweepRules.Default.MasterTimeout, fx.State.MasterDue);
        Assert.Equal([9L], fx.State.MasterRerunRuns.Select(r => r.Id));
        fx.Now = first;
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new MasterWatchElapsed(Item, 1, first))).Decision.Outcome);
        Assert.False(fx.State.Parked);
        // a second report of a re-run does not move it again (one re-run, issue 79)
        var due = fx.State.MasterDue;
        fx.Now = fx.Now.AddMinutes(1);
        await Master(fx, MasterVerdict.Pending, rerun: [new RerunRun { Id = 9, Attempt = 2 }]);
        Assert.Equal(due, fx.State.MasterDue);
    }

    [Fact]
    public async Task AnObservationOfAnotherCommit_OrOfAnItemNotWaiting_IsStale()
    {
        var fx = await At(StepKind.Deliver);
        Assert.Equal(WorkItemOutcome.Stale, (await Master(fx, MasterVerdict.Green, Later)).Decision.Outcome);
        Assert.Equal("waiting", fx.State.MasterState);
        var verify = await At(StepKind.Verify);
        Assert.Equal(WorkItemOutcome.Stale, (await Master(verify, MasterVerdict.Green)).Decision.Outcome);
    }

    [Fact]
    public async Task AMasterBrokenPark_IsLiftedWhenMasterIsGreen_ByTheSagaItself_ATimesPerParkCode()
    {
        var fx = await At(StepKind.Deliver);
        await Master(fx, MasterVerdict.Red, failed: ["unit"]);
        Assert.True(fx.State.Parked);
        fx.Now = fx.Now.AddMinutes(20);
        var r = await Master(fx, MasterVerdict.Green, via: Later);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.Null(fx.State.ParkUntil);
        Assert.Equal(("green", Later), (fx.State.MasterState, fx.State.MasterGreenVia));
        Assert.Equal(1, fx.State.AutoUnparks[SweepParks.MasterBroken]);
        Assert.Contains(fx.Decisions, d => d.Action == WorkItemActions.Resumed && d.Reasons[0].Contains("master is green again"));
        Assert.Empty(r.Timers);
    }

    [Fact]
    public async Task TheLiftsOfMasterBroken_AreBoundedByAutoUnparkMax_ThenAPersonOrTheEndOfThePark()
    {
        var fx = await At(StepKind.Deliver, Policy(SweepRules.Default with { AutoUnparkMax = 0 }));
        await Master(fx, MasterVerdict.Red, failed: ["unit"]);
        var refused = await Master(fx, MasterVerdict.Green);
        Assert.Equal(WorkItemOutcome.Refused, refused.Decision.Outcome);
        Assert.StartsWith("master-broken:", fx.State.ParkReason);
        Assert.Equal("broken", fx.State.MasterState);
        // the park ends by expiry: the item is no longer held and the wait goes on to the next reading of master, which now lets it through (the settle's Find-GreenMaster)
        fx.Now = fx.State.ParkUntil!.Value;
        await fx.Send(new ParkExpired(Item, 1, fx.Now));
        Assert.False(fx.State.Parked);
        Assert.Equal("broken", fx.State.MasterState);
        Assert.Equal(WorkItemOutcome.Applied, (await Master(fx, MasterVerdict.Green)).Decision.Outcome);
        Assert.Equal("green", fx.State.MasterState);
        Assert.Empty(fx.State.AutoUnparks);
    }

    [Fact]
    public async Task ATimedOutWait_GoesOnByItselfOnceMasterIsGreenForTheMerge()
    {
        var fx = await At(StepKind.Deliver);
        fx.Now = fx.State.MasterDue!.Value;
        await fx.Send(new MasterWatchElapsed(Item, 1, fx.Now));
        Assert.StartsWith("master-green-timeout", fx.State.ParkReason);
        Assert.Equal(WorkItemOutcome.Applied, (await Master(fx, MasterVerdict.Green)).Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.Equal("green", fx.State.MasterState);
        // (the timeout park is no auto-unpark: the settle polled it, v1 counts nothing)
        Assert.Empty(fx.State.AutoUnparks);
    }

    [Fact]
    public async Task ADeliveryOfTheItem_MeansMasterWasGreen_AndEndsAMasterPark()
    {
        var fx = await At(StepKind.Deliver);
        await Master(fx, MasterVerdict.Red, failed: ["unit"]);
        await fx.Delivered("tdd", "1.0.9");
        Assert.Equal("green", fx.State.MasterState);
        Assert.False(fx.State.Parked);
        Assert.Null(fx.State.ParkUntil);
    }

    [Fact]
    public async Task ARedMasterOfAnItemThatIsParkedAlready_ChangesNothing()
    {
        var fx = await At(StepKind.Deliver);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person said so"));
        // (the wait's timer is not running while the item is parked; a red reading is left for the park to end)
        Assert.Equal(WorkItemOutcome.Refused, (await Master(fx, MasterVerdict.Red, failed: ["unit"])).Decision.Outcome);
        Assert.Equal("waiting", fx.State.MasterState);
        Assert.Equal("a person said so", fx.State.ParkReason);
    }

    // ---- the settle's re-arm (issue 114) -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMergedItemWithNoWatch_GetsOneAgain_WithTheTimeoutCountedFromNow()
    {
        var fx = await At(StepKind.Deliver);
        // a state saved before the watch existed
        var rec = (await fx.Store.Load(fx.Key, default))!;
        var old = rec.State; old.MasterState = ""; old.MasterDue = null; old.MasterSince = null;
        Assert.True(await fx.Store.Save(fx.Key, old, rec.Revision, default));
        fx.Now = fx.Now.AddHours(3);
        var r = await fx.Send(new RearmSettle(Item, 0, "rearm|1", "merged with no settle step"));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal(("waiting", fx.Now + SweepRules.Default.MasterTimeout), (fx.State.MasterState, fx.State.MasterDue!.Value));
        Assert.Contains(WorkItemActions.SettleRearmed, fx.Actions);
        Assert.Contains(r.Timers, t => t.Message is MasterWatchElapsed);
        // a watch that is armed is left alone
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new RearmSettle(Item, 0, "rearm|2", "again"))).Decision.Outcome);
    }

    [Fact]
    public async Task TheReArm_RefusesAnItemThatIsNotMerged()
    {
        var fx = await At(StepKind.Merge);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new RearmSettle(Item, 0, "rearm|1", "x"))).Decision.Outcome);
    }

    // ---- merge pickup (Invoke-MergePickup) ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StepKind.Develop)]
    [InlineData(StepKind.Verify)]
    [InlineData(StepKind.Merge)]
    public async Task APersonsMerge_FromWhateverStepTheItemWasOn_EndsTheMergeStep_AndStartsTheWatchOfMaster(StepKind step)
    {
        var fx = await At(step);
        var r = await fx.Merged(Commit);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal((StepKind.Deliver, Commit, "waiting"), (fx.State.Step, fx.State.MergeCommit, fx.State.MasterState));
        Assert.Contains(WorkItemActions.Merged, fx.Actions);
        Assert.Contains(r.Timers, t => t.Message is MasterWatchElapsed);
        // the owner's session and the slot go with the merge, and the lease is not running any more
        Assert.Null(fx.State.LeaseDue);
    }

    [Fact]
    public async Task APersonsMerge_OfAParkedItem_TakesTheItemOutOfItsPark()
    {
        var fx = await At(StepKind.Merge);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: needs a person"));
        Assert.True(fx.State.Parked);
        await fx.Merged(Commit);
        Assert.False(fx.State.Parked);
        Assert.Null(fx.State.ParkUntil);
        Assert.Null(fx.State.OwnerIdleDue);
        Assert.Equal("waiting", fx.State.MasterState);
    }

    [Fact]
    public async Task AMergeSeenTwice_IsOneMerge()
    {
        var fx = await At(StepKind.Verify);
        await fx.Merged(Commit);
        var due = fx.State.MasterDue;
        Assert.NotEqual(WorkItemOutcome.Applied, (await fx.Merged(Commit)).Decision.Outcome);
        Assert.Equal(due, fx.State.MasterDue);
    }

    // ---- park expiry (Reset-ExpiredPark) -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AParkLastsParkHours_ThenTheItemTakesUpItsStepAgain_WithTheCountsThatParkedItFromZero()
    {
        var fx = await At(StepKind.Verify);
        // two demotions and fix-forward rounds that parked the item (set through the store: the machine that counts them is tested elsewhere)
        var rec = (await fx.Store.Load(fx.Key, default))!; rec.State.Demotions = 2; rec.State.FixForwardRounds = 4; rec.State.MechanicalFixes = 1;
        await fx.Store.Save(fx.Key, rec.State, rec.Revision, default);
        var r = await fx.Send(new ParkWorkItem(Item, 0, "park-1", "fix-forward-exhausted: CI stayed red"));
        var due = fx.Now + SweepRules.Default.ParkTime;
        Assert.Equal(due, fx.State.ParkUntil);
        Assert.Equal(due, Assert.IsType<ParkExpired>(Assert.Single(r.Timers).Message).Due);
        var arm = fx.Arm;
        fx.Now = due;
        var e = await fx.Send(new ParkExpired(Item, 1, due));
        Assert.Equal(WorkItemOutcome.Applied, e.Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.Equal((0, 0, 0), (fx.State.Demotions, fx.State.FixForwardRounds, fx.State.MechanicalFixes));
        Assert.Equal(StepKind.Verify, fx.State.Step);
        Assert.NotEqual(arm, fx.Arm);   // (the step is armed anew: its lease runs again)
        Assert.NotNull(fx.State.LeaseDue);
        Assert.Contains(WorkItemActions.ParkExpired, fx.Actions);
    }

    [Fact]
    public async Task AParkTimer_OfAParkThatWasLiftedOrReplaced_IsStale()
    {
        var fx = await At(StepKind.Develop);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "first", Minutes: 10));
        var first = fx.State.ParkUntil!.Value;
        Assert.Equal(fx.Now.AddMinutes(10), first);   // (a park names its own length)
        await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Null(fx.State.ParkUntil);
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new ParkExpired(Item, 1, first))).Decision.Outcome);
        fx.Now = fx.Now.AddMinutes(1);
        await fx.Send(new ParkWorkItem(Item, 0, "park-2", "second"));
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new ParkExpired(Item, 1, first))).Decision.Outcome);
        Assert.True(fx.State.Parked);
    }

    [Fact]
    public async Task AnExpiredMasterPark_FreesTheItem_ButTheWaitForMasterGoesOn()
    {
        var fx = await At(StepKind.Deliver);
        await Master(fx, MasterVerdict.Red, failed: ["unit"]);
        var until = fx.State.ParkUntil!.Value;
        fx.Now = until;
        var arm = fx.Arm;
        await fx.Send(new ParkExpired(Item, 1, until));
        Assert.False(fx.State.Parked);
        Assert.Equal(("broken", StepKind.Deliver, arm), (fx.State.MasterState, fx.State.Step, fx.Arm));   // (not entered again: the delivery step has no session to start)
        Assert.Equal(WorkItemOutcome.Applied, (await Master(fx, MasterVerdict.Green)).Decision.Outcome);
    }

    [Fact]
    public async Task WithoutTheSweepRules_AParkNeverExpires()
    {
        var fx = new SagaFixture(WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false) });
        await fx.Start();
        var r = await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person said so"));
        Assert.Null(fx.State.ParkUntil);
        Assert.Empty(r.Timers);
    }

    // ---- a PASS whose pull request was closed (Invoke-Demote, pr-closed) -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task APassWhosePullRequestWasClosedWithoutMerging_IsVoid_AndTheItemGoesBackToDevelopment()
    {
        var fx = await At(StepKind.Merge);
        var r = await Closed(fx);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal((StepKind.Develop, 1, "", 0), (fx.State.Step, fx.State.Demotions, fx.State.VerdictHead, fx.State.PullRequest));
        Assert.Contains(fx.Decisions, d => d.Action == WorkItemActions.Demoted && d.Reasons.Contains("pr-closed"));
        // the closed pull request is not the item's any more: Development is left only with a new one
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);
    }

    [Fact]
    public async Task APassWhosePullRequestWasClosed_PastTheDemotionLimit_ParksTheItem()
    {
        var fx = await At(StepKind.Merge);
        var rec = (await fx.Store.Load(fx.Key, default))!; rec.State.Demotions = fx.Saga.Policy.Recovery.MaxDemotions; await fx.Store.Save(fx.Key, rec.State, rec.Revision, default);
        await Closed(fx);
        Assert.True(fx.State.Parked);
        Assert.StartsWith("demotion-limit:", fx.State.ParkReason);
    }

    [Fact]
    public async Task AClosedPullRequest_IsNoDemotionWhereNoPassWaits_OrForAnotherPullRequest()
    {
        var verify = await At(StepKind.Verify);
        Assert.Equal(WorkItemOutcome.Refused, (await Closed(verify)).Decision.Outcome);
        var merge = await At(StepKind.Merge);
        Assert.Equal(WorkItemOutcome.Stale, (await Closed(merge, pr: 99)).Decision.Outcome);
        Assert.Equal(StepKind.Merge, merge.State.Step);
        var delivered = await At(StepKind.Deliver);
        Assert.Equal(WorkItemOutcome.Refused, (await Closed(delivered)).Decision.Outcome);
    }

    [Fact]
    public async Task APassOfAnItemWithoutTheAutoMergeLabel_IsProcessed_AClosedPullRequestDoesNotDemoteIt_AndItsOwnerDoesNotWait()
    {
        var fx = await At(StepKind.Merge);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "pass-without-auto-merge"));
        Assert.Null(fx.State.OwnerIdleDue);   // (the sweep's owner-released-idle was for a PASS still to be merged)
        Assert.Equal(WorkItemOutcome.Refused, (await Closed(fx)).Decision.Outcome);
        Assert.Equal((StepKind.Merge, true), (fx.State.Step, fx.State.Parked));
    }

    [Fact]
    public async Task ADocsItemsClosedPullRequest_IsDemotedToo()
    {
        var fx = new SagaFixture(Policy());
        await fx.Start(plan: "docs"); await fx.Review(); await fx.Done(); await fx.PullRequest(Head); await fx.Review(); await fx.Done(); await fx.Verdict(true, Head);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        await Closed(fx);
        Assert.Equal((StepKind.Develop, 1), (fx.State.Step, fx.State.Demotions));
    }

    // ---- clear-hold (Invoke-ClearHoldIfApproved) ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInspectorsHold_EndsWithAHumanApprovalOfTheHeadThePassCovers_AndTheMergeGoesOn()
    {
        var fx = await At(StepKind.Verify);
        await fx.Send(new ChecksObserved(Item, 0, "checks|green", Head, ChecksVerdict.Green, []));
        await fx.Verdict(true, Head);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: mandatory escalation"));
        Assert.True(fx.State.Parked);
        Assert.Null(fx.State.MergeRequest);
        var r = await fx.Send(new ApprovalSeen(Item, 0, "approval|1", 11, Head, "a-person"));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.Equal(("a-person", StepKind.Merge), (fx.State.ApprovedBy, fx.State.Step));
        Assert.Contains(WorkItemActions.HoldCleared, fx.Actions);
        Assert.IsType<MergeRequested>(Assert.Single(r.Sent));   // (the checks of the head are green: the merge is asked for again)
        Assert.Null(fx.State.ParkUntil);
        Assert.Null(fx.State.OwnerIdleDue);
    }

    [Fact]
    public async Task AnApproval_OfAnotherHead_OrOfAnItemThatIsNotHeld_ClearsNothing()
    {
        var fx = await At(StepKind.Merge);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new ApprovalSeen(Item, 0, "approval|1", 11, Head, "a-person"))).Decision.Outcome);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: x"));
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new ApprovalSeen(Item, 0, "approval|2", 11, Later, "a-person"))).Decision.Outcome);
        Assert.True(fx.State.Parked);
        // a park that is not the inspector's is not cleared by an approval
        var other = await At(StepKind.Merge);
        await other.Send(new ParkWorkItem(Item, 0, "park-1", "merge-blocked: something"));
        Assert.Equal(WorkItemOutcome.Refused, (await other.Send(new ApprovalSeen(Item, 0, "approval|3", 11, Head, "a-person"))).Decision.Outcome);
    }

    // ---- the idle owner (owner-released-idle) -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOwnerOfAHeldPass_GivesTheItemUp_AfterIdleReleaseMinutes_Once()
    {
        var fx = await At(StepKind.Merge);
        var parked = await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: x"));
        var due = fx.Now + SweepRules.Default.IdleReleaseAfter;
        Assert.Equal(due, fx.State.OwnerIdleDue);
        Assert.Contains(parked.Timers, t => t.Message is OwnerIdleElapsed i && i.Due == due);
        fx.Now = due;
        fx.Commands.Clear();
        var r = await fx.Send(new OwnerIdleElapsed(Item, 1, due));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        var release = Assert.IsType<OwnerReleaseRequested>(Assert.Single(r.Sent));
        Assert.Equal(Item, release.Item);
        Assert.Contains("idle for more than 60 min on a hold (inspector-escalated)", release.Reason);
        Assert.Contains(WorkItemActions.OwnerReleased, fx.Actions);
        Assert.True(fx.State.OwnerReleased);
        Assert.True(fx.State.Parked);   // (the park stands: the item waits for a person)
        // the same timer again is a redelivery (it publishes the release again, which is idempotent); a timer of the same park is no second release
        Assert.Equal(WorkItemOutcome.Duplicate, (await fx.Send(new OwnerIdleElapsed(Item, 1, due))).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new OwnerIdleElapsed(Item, 1, due.AddMinutes(1)))).Decision.Outcome);
    }

    [Fact]
    public async Task OnlyAHeldPassHasAnOwnerThatWaits_AndAnUnparkEndsTheWait()
    {
        var develop = await At(StepKind.Develop);
        await develop.Send(new ParkWorkItem(Item, 0, "park-1", "a person said so"));
        Assert.Null(develop.State.OwnerIdleDue);

        var fx = await At(StepKind.Merge);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: x"));
        var due = fx.State.OwnerIdleDue!.Value;
        await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Null(fx.State.OwnerIdleDue);
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new OwnerIdleElapsed(Item, 1, due))).Decision.Outcome);
    }

    // ---- the re-arm of the timers (W9.10o) ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALostParkTimer_IsPublishedAgainByTheRearm_AndTheSagaTakesItAsTheTimer()
    {
        var fx = new SagaFixture(Policy()); fx.Store.Clock = () => fx.Now;
        var sent = new List<object>();
        var rearm = new WorkItemRearm(fx.Store, fx.Saga.Policy, m => { sent.Add(m); return Task.FromResult(true); }, () => fx.Now);
        await fx.Start();
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person said so"));
        var due = fx.State.ParkUntil!.Value;
        fx.Now = due.AddMinutes(1);
        await rearm.Run(default);
        Assert.Empty(sent);   // (within the grace: Wolverine's own timer may still fire)
        fx.Now = due + WorkItemRearm.OverdueGrace + TimeSpan.FromSeconds(1);
        var report = await rearm.Run(default);
        var expired = Assert.IsType<ParkExpired>(Assert.Single(sent));
        Assert.Equal(1, report.Timers);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Send(expired)).Decision.Outcome);
        Assert.False(fx.State.Parked);
    }

    // ---- the plant manager's release-owner and requeue (W9.10k) -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheManagersReleaseOfAnOwner_IsRecordedAndAsksTheGatewayToGiveTheClaimUp_OncePerCall()
    {
        var fx = await At(StepKind.Develop);
        var step = fx.State.Step; var arm = fx.State.ArmId; var lease = fx.State.LeaseDue;
        var r = await fx.Send(new OwnerReleaseAsked(Item, 0, "manager-release|1", "stalled owner"));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        var release = Assert.IsType<OwnerReleaseRequested>(Assert.Single(r.Sent));
        Assert.Equal(Item, release.Item); Assert.Equal("stalled owner", release.Reason);
        Assert.Contains(WorkItemActions.OwnerReleaseAsked, fx.Actions);
        // nothing else about the item changes: the step, the arm and the lease are what they were
        Assert.Equal((step, arm, lease), (fx.State.Step, fx.State.ArmId, fx.State.LeaseDue));
        // the same call again is applied once; another call is another release
        Assert.Equal(WorkItemOutcome.Duplicate, (await fx.Send(new OwnerReleaseAsked(Item, 0, "manager-release|1", "stalled owner"))).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Send(new OwnerReleaseAsked(Item, 0, "manager-release|2", ""))).Decision.Outcome);
        // a delivered or completed item has no owner to release
        var merged = await At(StepKind.Deliver);
        Assert.Equal(WorkItemOutcome.Applied, (await merged.Send(new OwnerReleaseAsked(Item, 0, "manager-release|3", "x"))).Decision.Outcome);   // (open, on its Deliver step: the claim may still be there)
        Assert.Equal(WorkItemOutcome.NotFound, (await merged.Send(new OwnerReleaseAsked(Item, 2, "manager-release|4", "x"))).Decision.Outcome);   // another generation
    }

    [Fact]
    public async Task TheManagersRequeue_IsRecordedForAnItemASessionCanTake_AndRefusedForAnyOther()
    {
        var fx = await At(StepKind.Develop);
        var arm = fx.State.ArmId;
        var r = await fx.Send(new RequeueWorkItem(Item, 0, "manager-requeue|1", "stuck offer"));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Contains(WorkItemActions.Requeued, fx.Actions);
        Assert.Equal(arm, fx.State.ArmId);   // (the arm stands: the session that is working it, if any, still answers it)
        Assert.Empty(r.Sent);
        Assert.Equal(WorkItemOutcome.Duplicate, (await fx.Send(new RequeueWorkItem(Item, 0, "manager-requeue|1", "stuck offer"))).Decision.Outcome);

        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person said so"));
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new RequeueWorkItem(Item, 0, "manager-requeue|2", ""))).Decision.Outcome);   // parked: unpark offers it again

        var merge = await At(StepKind.Merge);
        Assert.Equal(WorkItemOutcome.Refused, (await merge.Send(new RequeueWorkItem(Item, 0, "manager-requeue|3", ""))).Decision.Outcome);   // no session works the Merge step
        var deliver = await At(StepKind.Deliver);
        Assert.Equal(WorkItemOutcome.Refused, (await deliver.Send(new RequeueWorkItem(Item, 0, "manager-requeue|4", ""))).Decision.Outcome);
    }
}
