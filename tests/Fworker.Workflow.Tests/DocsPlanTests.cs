using Fworker.Contracts.Commands;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W6.6 (#335): the docs plan's steps on the WorkItemSaga, without a database (the tracked tests run the same machine in a host). Every rule is one v1 applies to a docs-only item in a verb script
/// (record-review, complete, validation-verdict, merge) or one of D5's recovery rows; the comments name the script.
/// </summary>
public class DocsPlanTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Merged = "bbb2222222222222222222222222222222222222";

    static async Task<SagaFixture> AtMerge(WorkItemPolicy? policy = null)
    {
        var fx = new SagaFixture(policy);
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        await fx.Verdict(true, Head);
        return fx;
    }

    static MergeRequested Last(SagaFixture fx) => Assert.IsType<MergeRequested>(fx.Commands.Last());

    // ---- design and development: reviews and the pull request ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheDesign_IsLeftOnlyAfterACompleteReview_AndEveryRoundCounts()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        var noReview = await fx.Done();
        Assert.Equal(WorkItemOutcome.Refused, noReview.Decision.Outcome);
        Assert.StartsWith("reviewer-verdict-missing", noReview.Decision.Reason);
        var findings = await fx.Review(complete: false);
        Assert.Equal(("reviewed", "round 1 of 3", "REVIEW: FINDINGS"), (findings.Decisions.Single().Action, findings.Decisions.Single().Reasons[0], findings.Decisions.Single().Reasons[1]));
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Done()).Decision.Outcome);   // (findings do not satisfy the gate)
        await fx.Review();
        Assert.Equal(2, fx.State.ReviewRounds["Design"]);
        await fx.Done();
        Assert.Equal(StepKind.Develop, fx.State.Step);
    }

    [Fact]
    public async Task TheReviewRoundsOfTheDesign_AreFewerOnceTheDesignRecordedTheDocsOnlyMarker_AndPastTheLimitTheReviewIsRefused()
    {
        // Verb_record_review: a docs-only design gets min(maxReviewRounds, docsOnlyMaxReviewRounds) rounds; past them: review-rounds-exhausted
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Design(marker: true);
        await fx.Review(complete: false); await fx.Review(complete: false);
        var third = await fx.Review();
        Assert.Equal(WorkItemOutcome.Refused, third.Decision.Outcome);
        Assert.StartsWith("review-rounds-exhausted", third.Decision.Reason);
        Assert.Contains("2 rounds", third.Decision.Reason);

        // without the marker (the design not recorded yet, or recorded without it) the column has maxReviewRounds
        var plain = new SagaFixture();
        await plain.Start(plan: "docs");
        for (var i = 0; i < 3; i++) Assert.Equal(WorkItemOutcome.Applied, (await plain.Review(complete: false)).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Refused, (await plain.Review()).Decision.Outcome);

        // the limits are the policy's
        var tuned = new SagaFixture(WorkItemPolicy.Default with { Docs = new DocsPlanRules(MaxReviewRounds: 5, DocsOnlyMaxReviewRounds: 4) });
        await tuned.Start(plan: "docs");
        await tuned.Design(marker: true);
        for (var i = 0; i < 4; i++) Assert.Equal(WorkItemOutcome.Applied, (await tuned.Review(complete: false)).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Refused, (await tuned.Review()).Decision.Outcome);
    }

    [Fact]
    public async Task TheColumnsThatNeedNoReview_AreLeftWithoutOne()
    {
        // reviewVerdictColumns names the columns whose completion needs the reviewer's verdict; a configuration without the design column leaves it freely
        var fx = new SagaFixture(WorkItemPolicy.Default with { Docs = new DocsPlanRules(ReviewedSteps: [StepKind.Develop]) });
        await fx.Start(plan: "docs");
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Done()).Decision.Outcome);
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);   // (the development still needs one)
        var none = new SagaFixture(WorkItemPolicy.Default with { Docs = new DocsPlanRules(ReviewedSteps: []) });
        await none.Start(plan: "docs");
        await none.Done();
        await none.PullRequest(Head);
        Assert.Equal(WorkItemOutcome.Applied, (await none.Done()).Decision.Outcome);
        Assert.True(DocsPlanRules.Default.NeedsReview(StepKind.Design) && DocsPlanRules.Default.NeedsReview(StepKind.Develop));
        Assert.False(DocsPlanRules.Default.NeedsReview(StepKind.Verify) || DocsPlanRules.Default.NeedsReview(StepKind.Merge));
    }

    [Fact]
    public async Task Development_IsLeftOnlyWithACompleteReviewAndAPullRequest()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        Assert.Equal(StepKind.Develop, fx.State.Step);
        // Verb_complete checks the reviewer's verdict first, then the pull request (no-open-pr)
        Assert.StartsWith("reviewer-verdict-missing", (await fx.Done()).Decision.Reason);
        await fx.Review();
        Assert.StartsWith("no-open-pr", (await fx.Done()).Decision.Reason);
        await fx.PullRequest(Head);
        await fx.Done();
        Assert.Equal((StepKind.Verify, "docs"), (fx.State.Step, fx.State.Plan));
    }

    [Fact]
    public async Task AStepThatTakesNoReview_RefusesIt_AndAParkedItemTakesNone()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Verify);
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Review()).Decision.Outcome);
        var parked = new SagaFixture();
        await parked.Start(plan: "docs");
        await parked.Send(new ParkWorkItem(Item, 0, "park-1", "person"));
        Assert.Equal(WorkItemOutcome.Refused, (await parked.Review()).Decision.Outcome);
        Assert.Empty(parked.State.ReviewRounds);
    }

    [Fact]
    public async Task EnteringAStepAgain_StartsItsReviewsAgain()
    {
        // a lease that ran out re-enters the step under a new arm; what the old arm reviewed does not cover it
        var dev = new SagaFixture();
        await dev.Start(plan: "docs");
        await dev.Review(); await dev.Done();
        await dev.Review();
        Assert.Contains("Develop", dev.State.ReviewsComplete);
        dev.Now = dev.Now.AddMinutes(121);
        await dev.Send(new PhaseLeaseExpired(Item, 1, dev.Arm, dev.State.LeaseDue!.Value));
        Assert.Equal(StepKind.Develop, dev.State.Step);
        Assert.DoesNotContain("Develop", dev.State.ReviewsComplete);
        Assert.False(dev.State.ReviewRounds.ContainsKey("Develop"));   // (v1: reviews of the column start empty when a session claims the item)
        Assert.Equal(1, dev.State.ReviewRounds["Design"]);
    }

    // ---- verify ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_IsLeftOnlyByAVerdict()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Verify);
        var done = await fx.Done();
        Assert.Equal(WorkItemOutcome.Refused, done.Decision.Outcome);
        Assert.StartsWith("use-validation-verdict", done.Decision.Reason);
        Assert.Equal(StepKind.Verify, fx.State.Step);
    }

    [Fact]
    public async Task APassCoversTheHeadItNames_AndMovesTheItemToTheMerge_WhichAsksForTheMergeOnce()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        var pass = await fx.Verdict(true, Head, summary: "the page reads well");
        Assert.Equal([WorkItemActions.Verdict, WorkItemActions.StepEntered, WorkItemActions.MergeRequested], pass.Decisions.Select(d => d.Action));
        Assert.Equal((StepKind.Merge, Head), (fx.State.Step, fx.State.VerdictHead));
        var command = Assert.Single(pass.Sent);
        var request = Assert.IsType<MergeRequested>(command);
        Assert.Equal((Item, 1, fx.Arm, 1, 11, Head, 0, ""), (request.Item, request.Generation, request.ArmId, request.Attempt, request.PullRequest, request.Head, request.UpdatesSoFar, request.UpdatedFrom));
        // the merge step has its lease; a renewal asks for nothing again
        Assert.Equal(fx.Now.AddMinutes(30), fx.State.LeaseDue);
        var renewed = await fx.Send(new LeaseRenewed(Item, 1, fx.Arm, "renew-1"));
        Assert.Empty(renewed.Sent);
    }

    [Fact]
    public async Task AVerdictForAnotherHeadThanThePullRequestHas_IsRefused_AndOneThatNamesNoHeadTakesTheKnownOne()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        var wrong = await fx.Verdict(true, "ccc3333333333333333333333333333333333333");
        Assert.Equal(WorkItemOutcome.Refused, wrong.Decision.Outcome);
        Assert.StartsWith("head-mismatch", wrong.Decision.Reason);
        Assert.Equal(StepKind.Verify, fx.State.Step);
        await fx.Verdict(true, head: "", pr: 0);
        Assert.Equal((StepKind.Merge, Head), (fx.State.Step, fx.State.VerdictHead));
    }

    [Fact]
    public async Task APassWithoutAPullRequestOrWithoutAHead_IsRefused()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        // the pull request is recorded without a head (the feed could not read it) and the verdict names none: nothing says which head the PASS covers
        await fx.Send(new PullRequestChanged(Item, 0, "pr-nohead", 11, "", ["docs/a.md"]));
        await fx.Review(); await fx.Done();
        Assert.Equal(StepKind.Verify, fx.State.Step);
        var noHead = await fx.Verdict(true, head: "");
        Assert.StartsWith("pass-requires-head", noHead.Decision.Reason);
        var noPr = new SagaFixture();
        await noPr.Start(plan: "docs");
        await noPr.Review(); await noPr.Done();
        await noPr.Send(new PullRequestChanged(Item, 0, "pr-zero", 0, Head, ["docs/a.md"]));   // (a head without a number)
        await noPr.Review();
        Assert.StartsWith("no-open-pr", (await noPr.Done()).Decision.Reason);
    }

    [Fact]
    public async Task AFailSendsTheItemBackToDevelopment_TwiceByD5sFailVerdictRow_AndTheThirdParksIt()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();   // the design
        await fx.PullRequest(Head);
        for (var round = 1; round <= 2; round++)
        {
            await fx.Review(); await fx.Done();   // development
            Assert.Equal(StepKind.Verify, fx.State.Step);
            var fail = await fx.Verdict(false, Head, summary: "criterion not met");
            Assert.Equal([WorkItemActions.Verdict, WorkItemActions.Demoted, WorkItemActions.StepEntered], fail.Decisions.Select(d => d.Action));
            Assert.Equal((StepKind.Develop, round, ""), (fx.State.Step, fx.State.Demotions, fx.State.VerdictHead));
            Assert.Contains($"round-trip {round} of 2", fail.Decisions[1].Reasons[0]);
        }
        await fx.Review(); await fx.Done();
        var third = await fx.Verdict(false, Head, summary: "again");
        Assert.True(fx.State.Parked);
        Assert.Equal(StepKind.Verify, fx.State.Step);
        Assert.Contains(third.Decisions, d => d.Action == WorkItemActions.Parked && d.Reasons[0].StartsWith("verdict-failed: 3 FAIL verdicts, over the demotion limit of 2"));
        Assert.Equal(2, fx.State.Demotions);
    }

    [Fact]
    public async Task TheDemotionsOfThePolicy_AreD5sBudget()
    {
        var fx = new SagaFixture(WorkItemPolicy.Default with { Budgets = RecoverySettings.Default with { MaxDemotions = 0 } });
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Verify);
        await fx.Verdict(false);
        Assert.True(fx.State.Parked);
        Assert.Equal(0, fx.State.Demotions);
    }

    // ---- merge ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMergedAnswer_CompletesTheItem_AndRecordsTheCommit()
    {
        var fx = await AtMerge();
        var r = await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged.ToUpperInvariant()));
        Assert.Equal([WorkItemActions.Merged, WorkItemActions.Completed], r.Decisions.Select(d => d.Action));
        Assert.Equal((WorkItemStatus.Completed, Merged), (fx.State.Status, fx.State.MergeCommit));
        Assert.Null(fx.State.LeaseDue);
        Assert.Null(fx.State.MergeRequest);
        // the merge the delivery train derives afterwards finds no open instance and ends quietly
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Send(new MergeSeen(Item, "merged|" + Merged, Merged, 11))).Decision.Outcome);
    }

    // ---- the issue is closed at Done (v1's settle does it; item #556 stayed open on tdd after a person's merge) ----------------------------------------------------

    static CloseWorkItemIssue? Close(IEnumerable<WorkItemDecided> decisions, bool carriesFeature = false) =>
        decisions.Select(d => WorkItemIssueClose.For(d, carriesFeature)).SingleOrDefault(c => c is not null);

    [Fact]
    public async Task TheMergeOfTheSaga_ClosesTheIssueOnce_ByTheKeyOfTheItemAndGeneration()
    {
        var fx = await AtMerge();
        var r = await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged));
        var close = Assert.IsType<CloseWorkItemIssue>(Close(r.Decisions));
        Assert.Equal((Item, 1, CloseWorkItemIssue.KeyFor(Item, 1)), (close.Item, close.Generation, close.IdempotencyKey));
    }

    [Fact]
    public async Task TheMergeOfAPerson_ClosesTheIssueToo_WhereverTheItemWas()
    {
        // while it is validated, with the PASS recorded and the merge asked of the gateway, and before the pull request existed: the merge fact completes the docs item whichever step it was on
        var merging = await AtMerge();
        var seen = await merging.Send(new MergeSeen(Item, "merged|" + Merged, Merged, 11));
        Assert.Equal(WorkItemStatus.Completed, merging.State.Status);
        Assert.Equal(CloseWorkItemIssue.KeyFor(Item, 1), Assert.IsType<CloseWorkItemIssue>(Close(seen.Decisions)).IdempotencyKey);

        var early = new SagaFixture();
        await early.Start(plan: "docs");
        var earlySeen = await early.Send(new MergeSeen(Item, "merged|" + Merged, Merged, 11));
        Assert.Equal(WorkItemStatus.Completed, early.State.Status);
        Assert.NotNull(Close(earlySeen.Decisions));
    }

    [Fact]
    public async Task OnlyTheCompletion_ClosesTheIssue_NotTheMergeDecision_NotAHandBack_NorAPlanTheEngineDoesNotCarry()
    {
        var fx = await AtMerge();
        var r = await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged));
        Assert.Null(WorkItemIssueClose.For(r.Decisions.Single(d => d.Action == WorkItemActions.Merged), true));
        // a code plan's completion is closed only where the engine carries the code plans (the card follows the same rule)
        var completed = r.Decisions.Single(d => d.Action == WorkItemActions.Completed);
        Assert.Null(WorkItemIssueClose.For(completed with { Plan = "feature" }, carriesFeature: false));
        Assert.NotNull(WorkItemIssueClose.For(completed with { Plan = "feature" }, carriesFeature: true));
        Assert.Null(WorkItemIssueClose.For(completed with { Action = WorkItemActions.HandedBack }, carriesFeature: true));
    }

    [Fact]
    public async Task TheMergeFact_ThatArrivesFirst_EndsTheStep_AndTheAnswerThatComesLaterIsStale()
    {
        var fx = await AtMerge();
        var request = fx.State.MergeRequest!;
        var seen = await fx.Send(new MergeSeen(Item, "merged|" + Merged, Merged, 11));
        Assert.Contains(seen.Decisions, d => d.Action == WorkItemActions.Completed);
        Assert.Null(fx.State.MergeRequest);
        var late = await fx.Send(new MergeAttempted(Item, 1, "1.4", "merge-attempt|1.4|1", request.Attempt, new MergeResult(MergeResultKind.Merged, "already-merged", Commit: Merged)));
        Assert.Equal(WorkItemOutcome.NotFound, late.Decision.Outcome);
    }

    [Fact]
    public async Task ABlockedAnswer_WaitsAndAsksAgain_RenewsTheLease_AndDecidesOncePerReason()
    {
        var fx = await AtMerge();
        fx.Now = fx.Now.AddMinutes(10);
        var blocked = await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", "required checks have not passed", Transient: true));
        Assert.Equal([WorkItemActions.MergeBlocked], blocked.Decisions.Select(d => d.Action));
        Assert.Equal("checks-pending", blocked.Decisions[0].Reasons[0]);
        Assert.Equal(fx.Now.AddSeconds(60), fx.State.MergeRetryAt);
        Assert.Equal(fx.Now.AddMinutes(30), fx.State.LeaseDue);   // (a sign of life)
        var retry = Assert.IsType<MergeRetryDue>(blocked.Timers.Single(t => t.Message is MergeRetryDue).Message);
        Assert.Equal(fx.Now.AddSeconds(60), blocked.Timers.Single(t => t.Message is MergeRetryDue).Due);
        Assert.Contains(blocked.Timers, t => t.Message is PhaseLeaseExpired);

        // the timer comes due: the merge is asked for again (attempt 2), with the same head and the same arm
        fx.Now = fx.Now.AddSeconds(61);
        var again = await fx.Send(retry);
        Assert.Equal([WorkItemActions.MergeRequested], again.Decisions.Select(d => d.Action));
        var second = Last(fx);
        Assert.Equal((2, Head, fx.Arm), (second.Attempt, second.Head, second.ArmId));
        Assert.Null(fx.State.MergeRetryAt);

        // the same reason again is not decided again
        var still = await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true));
        Assert.Empty(still.Decisions);
        Assert.NotNull(fx.State.MergeRetryAt);
        // a timer for a time the merge no longer waits for, or one that arrives twice, does nothing
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(retry)).Decision.Outcome);
    }

    [Fact]
    public async Task TheRetryAfterOfTheAnswer_IsTheNextPause()
    {
        var fx = await AtMerge();
        await fx.Attempted(new MergeResult(MergeResultKind.Failed, "rate-limited", Transient: true, RetryAfter: TimeSpan.FromMinutes(7)));
        Assert.Equal(fx.Now.AddMinutes(7), fx.State.MergeRetryAt);
        Assert.False(fx.State.Parked);
    }

    [Fact]
    public async Task AnAnswerForAnotherAttempt_OrAfterTheStepWasLeft_IsStale()
    {
        var fx = await AtMerge();
        var wrongAttempt = await fx.Send(new MergeAttempted(Item, 1, fx.Arm, "merge-attempt|x|9", 9, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)));
        Assert.Equal(WorkItemOutcome.Stale, wrongAttempt.Decision.Outcome);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        var oldArm = await fx.Send(new MergeAttempted(Item, 1, "1.1", "merge-attempt|1.1|1", 1, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)));
        Assert.Equal(WorkItemOutcome.Stale, oldArm.Decision.Outcome);
        // answered once, the same attempt answered again is a duplicate key
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "merge-queue", Transient: true));
        var dup = await fx.Send(new MergeAttempted(Item, 1, fx.Arm, "merge-attempt|" + fx.Arm + "|1", 1, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)));
        Assert.Equal(WorkItemOutcome.Duplicate, dup.Decision.Outcome);
    }

    [Fact]
    public async Task TheBranchGitHubUpdated_BecomesTheHeadThePassCovers_AndTheUpdatesAreCounted()
    {
        var fx = await AtMerge();
        const string updated = "ddd4444444444444444444444444444444444444";
        // the update was accepted and its head is not there yet: the next call names the head the update started from
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "branch-updated", Transient: true, Updated: true));
        Assert.Equal(Head, fx.State.MergeUpdatedFrom);
        fx.Now = fx.Now.AddSeconds(61);
        await fx.Send(new MergeRetryDue(Item, 1, fx.Arm, fx.State.MergeRetryAt!.Value));
        var second = Last(fx);
        Assert.Equal((Head, Head), (second.Head, second.UpdatedFrom));
        // the head is there and GitHub's own: it is the head the PASS covers from now on, and an update after a refusal counts
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "branch-updated", NewHead: updated.ToUpperInvariant(), Transient: true, Updated: true, UpdatesAdded: 1));
        Assert.Equal((updated, updated, "", 1), (fx.State.VerdictHead, fx.State.Head, fx.State.MergeUpdatedFrom, fx.State.MergeUpdates));
        fx.Now = fx.Now.AddSeconds(61);
        await fx.Send(new MergeRetryDue(Item, 1, fx.Arm, fx.State.MergeRetryAt!.Value));
        var third = Last(fx);
        Assert.Equal((updated, 1, ""), (third.Head, third.UpdatesSoFar, third.UpdatedFrom));
        // the checks of the new head pass and the merge lands; the pull request event of the new head changes nothing
        await fx.PullRequest(updated);
        Assert.Equal(StepKind.Merge, fx.State.Step);
        await fx.Attempted(new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged));
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
    }

    [Fact]
    public async Task AHeadThatMoved_SendsTheItemBackToVerify_ForANewVerdict()
    {
        var fx = await AtMerge();
        var armOfMerge = fx.Arm;
        var r = await fx.Attempted(new MergeResult(MergeResultKind.HeadMoved, "stale-head", "the branch moved after the verdict"));
        Assert.Equal((StepKind.Verify, ""), (fx.State.Step, fx.State.VerdictHead));
        Assert.NotEqual(armOfMerge, fx.Arm);
        Assert.Null(fx.State.MergeRequest);
        Assert.Empty(r.Sent);
        // the new verdict covers the new head and asks for a merge of it
        const string moved = "eee5555555555555555555555555555555555555";
        await fx.PullRequest(moved);
        await fx.Verdict(true, moved);
        Assert.Equal((moved, 1), (Last(fx).Head, Last(fx).Attempt));
    }

    [Fact]
    public async Task ANewHeadAfterThePass_DropsTheMerge_AndTheAnswerOfTheOldRequestIsStale()
    {
        var fx = await AtMerge();
        var merge = fx.Arm;
        const string pushed = "fff6666666666666666666666666666666666666";
        var r = await fx.PullRequest(pushed);
        Assert.Equal((StepKind.Verify, ""), (fx.State.Step, fx.State.VerdictHead));
        Assert.Equal(pushed, fx.State.Head);
        Assert.Empty(r.Sent);
        var late = await fx.Send(new MergeAttempted(Item, 1, merge, "merge-attempt|" + merge + "|1", 1, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)));
        Assert.Equal(WorkItemOutcome.Stale, late.Decision.Outcome);
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
        // the same head again is no news
        var same = await fx.PullRequest(Head);
        Assert.Equal(StepKind.Verify, fx.State.Step);
        _ = same;
    }

    [Fact]
    public async Task AnOutOfDateBranch_IsD5sMechanicalRow_TheOwnerMergesMaster_AndAfterFourItParks()
    {
        var fx = await AtMerge();
        for (var i = 1; i <= 4; i++)
        {
            var r = await fx.Attempted(new MergeResult(MergeResultKind.Failed, "merge-out-of-date", "PR #11 kept falling behind its base"));
            Assert.Equal((StepKind.Develop, i, ""), (fx.State.Step, fx.State.MechanicalFixes, fx.State.VerdictHead));
            Assert.Contains(r.Decisions, d => d.Action == WorkItemActions.Demoted && d.Reasons[0] == $"mechanical fix {i} of 4");
            await fx.Review(); await fx.PullRequest($"{i}{Head[1..]}"); var done = await fx.Done();
            Assert.True(done.Decision.Outcome == WorkItemOutcome.Applied, $"round {i}: {done.Decision.Reason}");
            var pass = await fx.Verdict(true, $"{i}{Head[1..]}");
            Assert.True(pass.Decision.Outcome == WorkItemOutcome.Applied, $"round {i}: {pass.Decision.Reason}");
        }
        var last = await fx.Attempted(new MergeResult(MergeResultKind.Failed, "merge-out-of-date", "again"));
        Assert.True(fx.State.Parked);
        Assert.Contains(last.Decisions, d => d.Action == WorkItemActions.Parked && d.Reasons[0].StartsWith("mechanical-exhausted"));
    }

    [Theory]
    [InlineData("not-mergeable")]
    [InlineData("pr-not-open")]
    [InlineData("github-forbidden")]
    [InlineData("pr-mismatch")]
    [InlineData("update-refused")]
    public async Task AnyOtherRefusal_ParksTheItemWithTheReasonCode(string reason)
    {
        var fx = await AtMerge();
        var r = await fx.Attempted(new MergeResult(MergeResultKind.Failed, reason, "GitHub said no"));
        Assert.True(fx.State.Parked);
        Assert.Equal($"merge-failed: {reason}: GitHub said no", fx.State.ParkReason);
        Assert.Null(fx.State.MergeRetryAt);
        Assert.Null(fx.State.LeaseDue);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public async Task ATransientFailure_IsNotARefusal_AndTooManyBlockedCallsParkTheItem()
    {
        var fx = await AtMerge(WorkItemPolicy.Default with { Docs = new DocsPlanRules(MaxMergeAttempts: 3) });
        for (var i = 1; i <= 2; i++)
        {
            await fx.Attempted(new MergeResult(MergeResultKind.Failed, "merge-refused", "HTTP 405", Transient: true));
            Assert.False(fx.State.Parked);
            fx.Now = fx.Now.AddSeconds(61);
            await fx.Send(new MergeRetryDue(Item, 1, fx.Arm, fx.State.MergeRetryAt!.Value));
            Assert.Equal(i + 1, Last(fx).Attempt);
        }
        // the third call is the last the rules allow: its blocked answer parks the item instead of asking a fourth time
        var parked = await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true));
        Assert.True(fx.State.Parked);
        Assert.StartsWith("merge-blocked: 3 merge calls without a merge (checks-pending)", fx.State.ParkReason);
        Assert.Contains(parked.Decisions, d => d.Action == WorkItemActions.Parked);
        Assert.Null(fx.State.MergeRetryAt);
    }

    [Fact]
    public async Task ALeaseThatRanOut_AsksForTheMergeAgainUnderANewArm_AndTwiceParksIt()
    {
        var fx = await AtMerge();
        var first = fx.Arm;
        fx.Now = fx.Now.AddMinutes(31);
        var expired = await fx.Send(new PhaseLeaseExpired(Item, 1, first, fx.State.LeaseDue!.Value));
        Assert.Contains(expired.Decisions, d => d.Action == WorkItemActions.LeaseExpired);
        Assert.NotEqual(first, fx.Arm);
        Assert.Equal(1, Last(fx).Attempt);   // (the attempts count per arm)
        Assert.Equal(fx.Arm, Last(fx).ArmId);
        // the answer of the first arm's request is stale
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new MergeAttempted(Item, 1, first, "merge-attempt|first|1", 1, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)))).Decision.Outcome);
        fx.Now = fx.Now.AddMinutes(31);
        var second = await fx.Send(new PhaseLeaseExpired(Item, 1, fx.Arm, fx.State.LeaseDue!.Value));
        Assert.True(fx.State.Parked);
        Assert.Empty(second.Sent);
    }

    // ---- park, resume, switches ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AParkedMerge_StopsAskingAndTheAnswersInFlight_AndAResumeAsksAgainFromTheFirstAttempt()
    {
        var fx = await AtMerge();
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "merge-queue", Transient: true));
        var due = fx.State.MergeRetryAt!.Value;
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "a person looks"));
        Assert.Null(fx.State.MergeRetryAt);
        Assert.Equal(WorkItemOutcome.Stale, (await fx.Send(new MergeRetryDue(Item, 1, fx.Arm, due))).Decision.Outcome);
        var resumed = await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Contains(resumed.Decisions, d => d.Action == WorkItemActions.MergeRequested);
        Assert.Equal((1, Head), (Last(fx).Attempt, Last(fx).Head));
    }

    [Fact]
    public async Task APullRequestThatIsNotDocumentation_TakesTheItemOffTheDocsPlan_AndItsMergeRequestWithIt()
    {
        var fx = await AtMerge();
        fx.Docs.Files = false;
        await fx.PullRequest(Head, 11, "docs/a.md", "src/App.cs");
        Assert.Equal("feature", fx.State.Plan);
        Assert.Null(fx.State.MergeRequest);
        Assert.Null(fx.State.MergeRetryAt);
        // the feature plan's merge step takes no merge answer: the saga of the feature plan merges in W7
        Assert.Equal(WorkItemOutcome.Refused, (await fx.Send(new MergeAttempted(Item, 1, fx.Arm, "merge-attempt|x|1", 1, new MergeResult(MergeResultKind.Merged, "merged", Commit: Merged)))).Decision.Outcome);
    }

    // ---- the hybrid composition: only the docs plan is carried -------------------------------------------------------------------------

    static readonly WorkItemPolicy DocsOnlyEngine = WorkItemPolicy.Default with { CarriedPlans = ["docs"] };

    [Fact]
    public async Task AnItemThatLeavesTheDocsPlan_IsHandedBackToV1_NotCompleted_AndOwnedNoMore()
    {
        var fx = new SagaFixture(DocsOnlyEngine);
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        fx.Docs.Files = false;   // the pull request changes code
        var r = await fx.PullRequest(Head, 11, "docs/a.md", "src/App.cs");
        Assert.Equal([WorkItemActions.PlanSwitched, WorkItemActions.HandedBack], r.Decisions.Select(d => d.Action));
        Assert.Contains("feature plan is not carried", r.Decisions[1].Reasons[0]);
        Assert.Equal((WorkItemStatus.Completed, "feature", StepKind.Develop), (fx.State.Status, fx.State.Plan, fx.State.Step));
        Assert.Null(fx.State.LeaseDue);
        Assert.False(fx.State.IsOpen);   // (the ownership the sweep reads is the open instances: v1's sweep takes the item up again)
        Assert.Null(r.Decision.Step);
        // nothing of the saga goes on: a late report or timer ends quietly
        Assert.Equal(WorkItemOutcome.NotFound, (await fx.Done()).Decision.Outcome);
        Assert.DoesNotContain(r.Decisions, d => d.Action == WorkItemActions.Completed);
    }

    [Fact]
    public async Task TheDesignThatLacksTheMarker_HandsAFeatureItemBack_ButANormalDocsItemIsKept()
    {
        var fx = new SagaFixture(DocsOnlyEngine);
        await fx.Start(plan: "docs");
        var r = await fx.Design(marker: false);   // the latest design is not docs-only
        Assert.Equal([WorkItemActions.PlanSwitched, WorkItemActions.HandedBack], r.Decisions.Select(d => d.Action));
        Assert.False(fx.State.IsOpen);
        var kept = new SagaFixture(DocsOnlyEngine);
        await kept.Start(plan: "docs");
        await kept.Design(marker: true);
        Assert.True(kept.State.IsOpen);
        Assert.Equal("docs", kept.State.Plan);
    }

    [Fact]
    public async Task AFeatureItemTheEngineDoesNotCarry_IsHandedBackWhenItSwitchesToDocsToo_OnlyIfTheDocsPlanIsNotCarried()
    {
        var onlyFeature = new SagaFixture(WorkItemPolicy.Default with { CarriedPlans = ["feature"] });
        await onlyFeature.Start();
        var r = await onlyFeature.Design(marker: true);
        Assert.Equal([WorkItemActions.PlanSwitched, WorkItemActions.HandedBack], r.Decisions.Select(d => d.Action));
        Assert.Equal(WorkItemStatus.Completed, onlyFeature.State.Status);
        // the default policy carries every plan, as the tests of the saga run it
        Assert.True(WorkItemPolicy.Default.Carries("anything"));
        Assert.False(DocsOnlyEngine.Carries("feature")); Assert.True(DocsOnlyEngine.Carries("DOCS"));
    }

    // ---- redelivery and persistence ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARedeliveredPass_AsksForTheMergeAgain_AndAnotherInputAfterItDoesNot()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        var verdict = new VerdictRecorded(Item, 1, fx.Arm, "verdict-redelivered", true, 11, Head, "ok");
        var first = await fx.Send(verdict);
        var arm = fx.Arm;
        var again = await fx.Send(verdict);   // (the saga recognises the key of its last transition, whatever arm the item is on now)
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        Assert.Equal(first.Decisions.Select(d => d.Sequence), again.Decisions.Select(d => d.Sequence));
        var resent = Assert.IsType<MergeRequested>(Assert.Single(again.Sent));
        Assert.Equal((arm, 1), (resent.ArmId, resent.Attempt));
        // a later input is not the last one any more: the lease renewal publishes nothing
        await fx.Send(new LeaseRenewed(Item, 1, arm, "renew-1"));
        var renewedAgain = await fx.Send(new LeaseRenewed(Item, 1, arm, "renew-1"));
        Assert.Empty(renewedAgain.Sent);
    }

    [Fact]
    public async Task ARedeliveredBlockedAnswer_PublishesTheSameRetryTimerAgain()
    {
        var fx = await AtMerge();
        var answer = new MergeAttempted(Item, 1, fx.Arm, "merge-attempt|x|1", 1, new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true));
        var first = await fx.Send(answer);
        var again = await fx.Send(answer);
        Assert.Equal(WorkItemOutcome.Duplicate, again.Decision.Outcome);
        var due = first.Timers.Single(t => t.Message is MergeRetryDue).Due;
        Assert.Equal(due, again.Timers.Single(t => t.Message is MergeRetryDue).Due);
    }

    [Fact]
    public async Task TheStateOfTheDocsPlan_SurvivesTheRowAndAStateSavedBeforeItLoads()
    {
        var fx = await AtMerge();
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "merge-queue", Transient: true));
        var back = WorkItemState.FromJson(fx.State.ToJson());
        Assert.Equal((Head, "merge-queue", 2, 2), (back.VerdictHead, back.MergeBlockedReason, back.ReviewRounds.Count, back.ReviewsComplete.Count));
        Assert.Equal(fx.State.MergeRetryAt, back.MergeRetryAt);

        // a state written by W6.1 has none of the fields
        var old = WorkItemState.FromJson("""{"Item":"acme/orders#7","Generation":1,"Status":"open","Plan":"docs","Step":"Verify","ArmId":"1.3"}""");
        Assert.Equal((0, "", null), (old.Demotions, old.VerdictHead, old.MergeRequest));
        Assert.Empty(old.ReviewRounds);
        Assert.Empty(old.ReviewsComplete);
    }

    [Fact]
    public async Task TheFeaturePlan_KeepsItsReportsAndHasNoMergeCall()
    {
        var fx = new SagaFixture();
        await fx.Start();
        await fx.WalkTo(StepKind.Merge);
        Assert.Empty(fx.Commands);
        Assert.Equal(WorkItemOutcome.Applied, (await fx.Done()).Decision.Outcome);
        Assert.Equal(StepKind.Deliver, fx.State.Step);
    }
}
