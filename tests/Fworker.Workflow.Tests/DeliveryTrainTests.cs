using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W5.1: the DeliveryTrainSaga's transitions on fakes (no database, no Octopus, no GitHub): candidates, evidence, the newest qualifying release, supersession, the Sign-off, timers, the
/// delivered-to output, the policy. The same flows run on a Wolverine tracked session in DeliveryTrainTrackedTests.
/// </summary>
public class DeliveryTrainTests
{
    static readonly string[] HoldGap = ["#8 carries the 'hold:owner' label"];

    /// <summary>uat train, baseline 1.0.1 (commit d) in every environment, item #7 merged as commit a, release 1.0.2 built from a.</summary>
    static async Task<TrainFixture> OneRelease(Action<TrainFixture>? configure = null, string[]? gaps = null)
    {
        var f = new TrainFixture(); configure?.Invoke(f);
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a', gaps);
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        return f;
    }

    // ---- candidates, evidence and the promotion --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARelease_WaitsForTheEvidence_ThenIsPromoted_Once()
    {
        var f = await OneRelease();
        // the tdd deployment just finished: one Healthy read is not enough (two reads 5 min apart, the later 10 min after the deployment)
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Equal("1.0.2", f.LastDecision.Release);
        Assert.Contains("the tdd app has not been Healthy long enough yet (two Healthy reads 5 min apart, the later 10 min after the deployment finished)", f.LastDecision.Reasons);
        Assert.Empty(f.Of<PromoteRelease>());
        await f.After(TimeSpan.FromMinutes(5)); Assert.Empty(f.Of<PromoteRelease>());
        var third = await f.After(TimeSpan.FromMinutes(5));   // reads at 0, 5 and 10 min: the last is 10 min after the deployment finished and 5 min after the one before
        var cmd = Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal(new PromoteRelease("PromoteRelease|orders|1.0.2|uat", "orders", "orders-project", "1.0.2", "uat"), cmd);
        Assert.Equal("promote", f.LastDecision!.Action);
        Assert.Equal(["acme/orders#7"], f.LastDecision.Items);
        Assert.Contains(third.Messages, m => m is PromoteRelease);
        // a later input does not promote again: the request is kept in the state until Octopus shows the deployment
        await f.After(TimeSpan.FromMinutes(1));
        Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal("following", f.LastDecision.Action);
    }

    [Fact]
    public async Task TheEvidenceIsTheD2Composite_NotRunningVersion_NonHealthy_AndNoUrl()
    {
        var f = await OneRelease(x => { });
        f.Reads.Running["tdd"] = "1.0.1";   // the app still runs the old release
        await f.After(TimeSpan.FromMinutes(1));
        Assert.Contains("the tdd app does not run release 1.0.2 yet (/_build: 1.0.1)", f.LastDecision!.Reasons);
        f.Reads.Running["tdd"] = "1.0.2"; f.Reads.Status["tdd"] = "Unhealthy";
        await f.After(TimeSpan.FromMinutes(1));
        Assert.StartsWith("the tdd app answered Unhealthy on /_healthcheck/detailed at 2026-10-28T10:02:00.000Z, after its deployment of 1.0.2 finished: the release waits for a new deployment there", f.LastDecision!.Reasons[0]);
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task AHoldOnTheItem_BlocksThePromotion_AndReleasingItLetsItThrough()
    {
        var f = await OneRelease(gaps: ["#7 carries the 'hold:owner' label"]);
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Equal(["#7 carries the 'hold:owner' label"], f.LastDecision.Reasons);
        Assert.Equal(0, f.Reads.Probes);   // v1 reads the health only when every other gate holds
        await f.Send(new TrainGatesChanged(f.Key, "acme/orders#7", new ItemMergedGates(true, "", true, false, false, [])));
        Assert.Equal("waiting", f.LastDecision.Action);
        Assert.Equal(1, f.Reads.Probes);
    }

    [Fact]
    public async Task TheNewestQualifyingRelease_FallsBackToTheOneBeforeABlockedRelease()
    {
        var f = await OneRelease();
        // 1.0.3 carries item #8, which is on hold: the release is blocked and the train falls back to 1.0.2
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b', HoldGap);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        // the evidence of 1.0.2 completes while 1.0.3 is blocked
        f.Reads.Running["tdd"] = "1.0.2";
        await f.Mature();
        var cmd = Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal("1.0.2", cmd.Version);
    }

    [Fact]
    public async Task TwoQualifyingReleases_ArePromotedAsTheNewest_AndTheOlderIsSuperseded()
    {
        var f = await OneRelease();
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b');
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        await f.Mature();
        // (declared difference from v1: v1 promotes 1.0.2 and then 1.0.3; the train promotes only the newest, which carries both items)
        var cmd = Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal("1.0.3", cmd.Version);
        Assert.Equal(["acme/orders#7", "acme/orders#8"], f.LastDecision!.Items.Order());
        await f.Deploy("1.0.3", "Uat", "Started");
        await f.Deploy("1.0.3", "Uat", "Succeeded");
        Assert.Equal(["acme/orders#7", "acme/orders#8"], f.Of<DeliveredTo>().Select(d => d.Item.ToString()).Order());
        Assert.All(f.Of<DeliveredTo>(), d => Assert.Equal(("uat", "1.0.3"), (d.Environment, d.Release)));
        var delivered = f.Of<TrainDecided>().Single(d => d.Action == "delivered");
        Assert.Equal(["delivered in release 1.0.3, superseding 1.0.2"], delivered.Reasons);
        Assert.Equal("1.0.3", f.State.Superseded["1.0.2"]);
        Assert.Equal("1.0.3", f.State.Current);
        Assert.Empty(f.State.Candidates);
    }

    [Fact]
    public async Task AReleaseThatIsNotNewerThanWhatRuns_IsNotPromoted()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Reads.Running["tdd"] = "1.0.4";
        await f.ReleaseInPrevious("1.0.4", 'd');   // the same commit as the baseline: v1 skips it
        await f.After(TimeSpan.FromMinutes(20));
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task ACommitThatIsNoWorkItemsMerge_IsAHardGap()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'x');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'x');
        Assert.Equal(["commit xxxxxxx in the release is not the merge commit of a factory work item".Replace("xxxxxxx", Sha('x')[..7])], f.LastDecision!.Reasons);
    }

    [Fact]
    public async Task AReleaseWhoseReadsFail_DoesNotTakeTheDecisionsOfTheOtherReleasesWithIt()
    {
        // tdd 1.0.833: the newest release that succeeded in tdd needed a long range from GitHub, the shadow's reads were refused, and the look was thrown away: no release got a decision
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Reads.Fail = new HttpRequestException("the shadow run's reads of this minute are spent");
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Tdd", "Started"); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        f.Now = f.Now.AddMinutes(1);
        await f.Release("1.0.3", 'b');   // newer, and it has no deployment to Tdd: a decision that needs no read
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.3" && d.Reasons.Any(r => r.Contains("no deployment to Tdd")));
        Assert.Empty(f.Of<PromoteRelease>());
        f.All.Clear();
        // the unread release is looked at again soon, and decided once its reads work
        var wake = await f.Wake();
        Assert.NotNull(wake.WakeAt);
        Assert.True(wake.WakeAt <= f.Now.AddMinutes(1));
        f.Reads.Fail = null;
        await f.After(TimeSpan.FromSeconds(20));
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.2");
    }

    [Fact]
    public async Task AnOlderReleaseWhoseReadFails_DoesNotHoldBackThePromotionOfTheNewerOneThatQualifies()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a', 'b');
        await f.Merge(7, 'a'); await f.Merge(8, 'b');
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.2", 'a');
        f.Now = f.Now.AddMinutes(1);
        await f.ReleaseInPrevious("1.0.3", 'b');
        f.Reads.FailCompareOf.Add(Sha('a'));   // (the older release's own range cannot be read; the newer one's can)
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>(), p => p.Version == "1.0.3");
    }

    [Fact]
    public async Task AFailedDeploymentToTheTarget_BlocksThatRelease()
    {
        var f = await OneRelease();
        await f.Deploy("1.0.2", "Uat", "Started", "D-9");
        await f.Deploy("1.0.2", "Uat", "Failed", "D-9");
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Equal(["its deployment to Uat ended Failed (D-9); the operator deploys it again, or the item goes back to Development"], f.LastDecision.Reasons);
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task AReleaseNotYetSucceededInThePreviousEnvironment_WaitsWithV1sText()
    {
        var f = new TrainFixture(); await f.Baseline();
        await f.Release("1.0.2", 'a');
        Assert.Equal(["it has no deployment to Tdd"], f.LastDecision!.Reasons);
        await f.Deploy("1.0.2", "Tdd", "Started");
        Assert.Equal(["its deployment to Tdd is still Started (D-1.0.2-Tdd)"], f.LastDecision!.Reasons);
    }

    // ---- parity with the v1 promotion pass (Invoke-AutoPromoteInner): the waiting reasons are v1's texts, character for character --------------------------------------
    // Declared differences, which the shadow run (W5.2) reports as explained: the train judges only the newest release that succeeded in the previous environment (v1 judges every release of the
    // queue, and promotes an older one that also qualifies before the newer); a release that has not succeeded in the previous environment gets that reason alone (v1 puts the content gaps first);
    // the Sign-off waiting clock starts when the Sign-off is seen (v1: when the pass first judged it); an unreadable range or an item that vanished is not retried by a pass but by the next input or timer.

    [Fact]
    public async Task Parity_AReleaseWithNoCommit_Waits_WithV1sText()
    {
        var f = new TrainFixture(); await f.Baseline();
        await f.Send(new TrainReleaseSeen(f.Key, "R", "1.0.2", null, f.Now));
        await f.Deploy("1.0.2", "Tdd", "Started"); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        Assert.Equal(["the release names no commit (its build information or notes)"], f.LastDecision!.Reasons);
        Assert.Equal(0, f.Reads.Probes);
    }

    [Fact]
    public async Task Parity_ARangeTheCommitHostCannotCompare_Waits_WithV1sText()
    {
        var f = new TrainFixture(); await f.Baseline();   // no compare registered: the host answers 404/422
        await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        Assert.Equal([$"GitHub cannot compare the commit running in Uat ({Sha('d')[..7]}, release 1.0.1) with the release's"], f.LastDecision!.Reasons);
    }

    [Fact]
    public async Task Parity_AnItemWithoutAutoMergePassOrVideo_ReportsEachOfV1sGaps()
    {
        var gaps = new[] { "#7 has no 'Auto Merge' label", "#7 has no pull request merged after a Functional Testing PASS verdict", "#7 has the 'video' label and no demo video published (release demo-7, asset demo-7.mp4)" };
        var f = await OneRelease(gaps: gaps);
        Assert.Equal(gaps, f.LastDecision!.Reasons);
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task Parity_ACommitThatIsAnotherItemsSettledMerge_RidesAlong_AndItsGatesAreRead()
    {
        // a docs-only item merged straight to Done is no candidate; v1 reads its gates too (Find-CommitItem, Get-ItemPromotionGapsRead)
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'x', 'a');
        f.Reads.Lookups[Sha('x')] = new CommitItemLookup(5, "");
        f.Reads.Gates["acme/orders#5"] = new ItemMergedGates(false, "", false, false, false, ["#5 has no 'Auto Merge' label"]);
        await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        Assert.Equal(["#5 has no 'Auto Merge' label"], f.LastDecision!.Reasons);
        Assert.Equal(["acme/orders#5", "acme/orders#7"], f.LastDecision.Items.Order());
    }

    // ---- the Sign-off --------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The release promoted by the train and its deployment waiting at the Sign-off.</summary>
    static async Task<TrainFixture> AtTheSignOff(Action<TrainFixture>? configure = null)
    {
        var f = await OneRelease(configure);
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.SignOff("1.0.2", "Uat");
        return f;
    }

    [Fact]
    public async Task ASignOff_OfADeploymentTheTrainAskedFor_IsAnsweredProceed_WhenEveryGateHolds()
    {
        var f = await AtTheSignOff();
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.Equal(new AnswerSignOff("AnswerSignOff|orders|I-1.0.2", "orders", "I-1.0.2", "T-1.0.2-Uat", true, "Signed off by the factory worker gateway: every gate of the release holds (docs/design/auto-promote.md)."), a);
        Assert.Equal("signoff-proceed", f.LastDecision!.Action);
    }

    [Fact]
    public async Task ASignOff_IsAborted_WhenAnItemGetsAHoldWhileItWaits()
    {
        var f = await OneRelease();
        await f.Mature();
        await f.Send(new TrainGatesChanged(f.Key, "acme/orders#7", new ItemMergedGates(true, "hold:owner", true, false, false, ["#7 carries the 'hold:owner' label"])));
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.SignOff("1.0.2", "Uat");
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.False(a.Proceed);
        Assert.Equal("Aborted by the factory worker gateway: #7 carries the 'hold:owner' label", a.Notes);
        Assert.Equal("signoff-abort", f.LastDecision!.Action);
    }

    [Fact]
    public async Task ASignOff_IsAborted_WhenThePreviousEnvironmentIsNotHealthyNow()
    {
        var f = await OneRelease();
        await f.Mature();
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.Status["tdd"] = "Unhealthy";
        await f.SignOff("1.0.2", "Uat");
        Assert.Equal("Aborted by the factory worker gateway: the tdd app answered Unhealthy on /_healthcheck/detailed", Assert.Single(f.Of<AnswerSignOff>()).Notes);
    }

    [Fact]
    public async Task ASignOff_OfSomeoneElsesDeployment_IsOnlyFollowed()
    {
        var f = new TrainFixture(); await f.Baseline();
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.SignOff("1.0.2", "Uat");
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("following", f.LastDecision!.Action);
        Assert.Contains("did not create", f.LastDecision.Reasons[0]);
    }

    /// <summary>The release promoted, its deployment waiting at the Sign-off, and a soft gap: the previous environment's deployment of the release is running again (a redeploy).</summary>
    static async Task<(TrainFixture F, TrainResult Result)> AtAWaitingSignOff()
    {
        var f = await OneRelease(); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.Deploy("1.0.2", "Tdd", "Started", "D-redeploy");
        return (f, await f.SignOff("1.0.2", "Uat"));
    }

    [Fact]
    public async Task AWaitingSignOff_IsKept_UntilTheLimit_ThenAborted()
    {
        var (f, r) = await AtAWaitingSignOff();
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        Assert.Equal(["the Sign-off of task T-1.0.2-Uat waits: its deployment to Tdd is still Started (D-redeploy)"], f.LastDecision.Reasons);
        Assert.Empty(f.Of<AnswerSignOff>());
        await f.After(TimeSpan.FromHours(1)); Assert.Empty(f.Of<AnswerSignOff>());
        await f.After(TimeSpan.FromHours(1.1));
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.False(a.Proceed);
        Assert.Equal("Aborted by the factory worker gateway: waited 2.1 h at the Sign-off (limit 2 h): its deployment to Tdd is still Started (D-redeploy)", a.Notes);
        Assert.Equal("signoff-abort", f.LastDecision!.Action);
    }

    [Fact]
    public async Task AWaitingSignOff_SchedulesThePollAndTheLimit()
    {
        var (f, r) = await AtAWaitingSignOff();
        Assert.Equal(f.Now.AddSeconds(120), r.WakeAt);   // the poll comes first; the limit (since + 2 h) is looked at when the polls reach it
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        // when the previous environment settles, the Sign-off goes through at the next look
        await f.Deploy("1.0.2", "Tdd", "Succeeded", "D-redeploy");
        Assert.True(f.Of<AnswerSignOff>().Single().Proceed);
    }

    // ---- timers --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTrain_SchedulesOneTimer_WhileTheEvidenceCollects_AndNoneWhenNothingWaitsOnTime()
    {
        var f = await OneRelease();
        Assert.Equal(f.Now.AddSeconds(120), f.State.WakeDue);
        // another input before the timer fires sends the same due time again (the first may have been lost before the outbox); the pending time does not move
        var r = await f.Merge(9, 'c', ["#9 has no 'Auto Merge' label"]);
        Assert.Equal(f.State.WakeDue, r.WakeAt);
        // the timer that is pending fires: it clears itself and the evaluation schedules the next
        var due = f.State.WakeDue;
        f.Now = due!.Value;
        var fired = await f.Wake(due);
        Assert.Equal(f.Now.AddSeconds(120), fired.WakeAt);
        // a stale timer looks again and starts no chain
        var stale = await f.Wake(due);
        Assert.Null(stale.WakeAt);
    }

    [Fact]
    public async Task ARequestWithNoDeploymentInOctopus_IsSentAgainAfterFiveMinutes_WithTheSameKey()
    {
        var f = await OneRelease();
        await f.Mature();
        await f.After(TimeSpan.FromMinutes(4)); Assert.Single(f.Of<PromoteRelease>());
        await f.After(TimeSpan.FromMinutes(1.1));
        Assert.Equal(2, f.Of<PromoteRelease>().Count());
        Assert.Single(f.Of<PromoteRelease>().Select(c => c.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task ARequestOctopusNeverShows_IsDroppedAfterThirtyMinutes_AndANewerReleaseIsPromoted()
    {
        var f = await OneRelease(); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>().Select(c => c.Version).Distinct());
        // 1.0.3 arrives and qualifies while the request for 1.0.2 shows no deployment
        f.Range('d', 'a', 'b'); await f.Merge(8, 'b'); f.Reads.Running["tdd"] = "1.0.3";
        f.Now += TimeSpan.FromMinutes(1);
        await f.ReleaseInPrevious("1.0.3", 'b');
        await f.After(TimeSpan.FromMinutes(30));
        Assert.Contains(f.Of<TrainDecided>(), d => d.Reasons.Any(r => r.Contains("Octopus showed none within 30 minutes")));
        await f.After(TimeSpan.FromMinutes(5)); await f.After(TimeSpan.FromMinutes(5));
        Assert.Contains(f.Of<PromoteRelease>(), c => c.Version == "1.0.3");
    }

    [Fact]
    public async Task ARedeliveredItemMerged_NeitherReinstatesADeliveredItemNorRevertsItsGates()
    {
        var f = new TrainFixture("tdd"); await f.Baseline();
        await f.Merge(7, 'a'); await f.Merge(9, 'c');
        await f.Send(new TrainGatesChanged(f.Key, "acme/orders#9", new ItemMergedGates(true, "hold:owner", true, false, false, ["#9 carries the 'hold:owner' label"])));
        await f.Merge(9, 'c');   // redelivered with its merge-time gates
        Assert.Equal(["#9 carries the 'hold:owner' label"], f.State.Candidates["acme/orders#9"].Gates.Gaps);
        await f.Release("1.0.2", 'a'); await f.Deploy("1.0.2", "Tdd", "Started"); await f.Deploy("1.0.2", "Tdd", "Succeeded");
        await f.Merge(7, 'a');
        Assert.DoesNotContain("acme/orders#7", f.State.Candidates.Keys);
    }

    // ---- delivered-to --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTddTrain_ObservesTheDeployment_AndDeliversTheItemOfTheReleasesCommit_Once()
    {
        var f = new TrainFixture("tdd"); await f.Baseline();
        await f.Merge(7, 'a');
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Tdd", "Started");
        Assert.Empty(f.Of<DeliveredTo>());
        await f.Deploy("1.0.2", "Tdd", "Succeeded");
        var d = Assert.Single(f.Of<DeliveredTo>());
        Assert.Equal(new DeliveredTo(new ItemId(Repo, 7), "tdd", "1.0.2"), d);
        await f.Deploy("1.0.2", "Tdd", "Succeeded");   // the same fact again (at-least-once)
        await f.Wake();
        Assert.Single(f.Of<DeliveredTo>());
        Assert.Empty(f.Of<PromoteRelease>());   // tdd is deployed by the release's lifecycle
        Assert.Empty(f.Of<Undelivered>());
    }

    [Fact]
    public async Task ARedeliveredInput_GivesTheSameState_AndNoSecondEffect()
    {
        var f = await OneRelease();
        await f.Mature();
        await f.Merge(7, 'a'); await f.Release("1.0.2", 'a');
        Assert.Single(f.Of<PromoteRelease>());
        Assert.Single(f.State.Candidates);
        Assert.Single(f.State.Releases.Where(r => r.Key == "1.0.2"));
    }

    // ---- the policy ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExternalPromoter_IsObservedNeverActedFor()
    {
        var g = new TrainFixture(promoter: Promoter.External); await g.Baseline();
        g.Range('d', 'a'); await g.Merge(7, 'a'); g.Reads.Running["tdd"] = "1.0.2"; await g.ReleaseInPrevious("1.0.2", 'a');
        await g.Mature();
        Assert.Empty(g.Of<PromoteRelease>()); Assert.Empty(g.Of<TrainDecided>()); Assert.Equal(0, g.Reads.Probes);
        // the deployment the external promoter makes still delivers the item
        await g.Deploy("1.0.2", "Uat", "Started"); await g.Deploy("1.0.2", "Uat", "Succeeded");
        Assert.Equal("1.0.2", Assert.Single(g.Of<DeliveredTo>()).Release);
    }

    [Fact]
    public async Task AnEnvironmentTheConfigurationDoesNotName_IsNotPromoted()
    {
        var f = new TrainFixture(environments: ["prod"]); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2"; await f.ReleaseInPrevious("1.0.2", 'a');
        await f.After(TimeSpan.FromMinutes(20));
        Assert.Empty(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task AHeldTrain_PromotesNothing_UntilReleased()
    {
        var f = await OneRelease();
        await f.Send(new TrainHold(f.Key, true, "incident 12"));
        await f.Mature();
        Assert.Empty(f.Of<PromoteRelease>());
        Assert.Equal("held", f.LastDecision!.Action);
        Assert.Equal(["incident 12"], f.LastDecision.Reasons);
        await f.Send(new TrainHold(f.Key, false, ""));   // the first read after the hold completes the evidence (the app was Healthy at the start and is 10 min on)
        Assert.Single(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task TheDependenciesDeliveredSlot_IsOffByDefault_AndHoldsAnItemWhenOn()
    {
        foreach (var on in new[] { false, true })
        {
            var f = new TrainFixture(dependencies: on); await f.Baseline();
            f.Range('d', 'a'); await f.Merge(7, 'a', dependencies: ["acme/orders#5", "other/repo#12"]);
            f.Reads.Running["tdd"] = "1.0.2"; await f.ReleaseInPrevious("1.0.2", 'a');
            if (on) Assert.Equal(["acme/orders#7 depends on acme/orders#5, which is not yet delivered to Uat", "acme/orders#7 depends on other/repo#12, which is not yet delivered to Uat"], f.LastDecision!.Reasons);
            else Assert.Equal("waiting", f.LastDecision!.Action);   // only the evidence waits
            Assert.DoesNotContain(f.LastDecision.Reasons, r => r.Contains("depends on") && !on);
            if (on)
            {
                f.Delivered.Rows.Add(("acme/orders#5", "uat")); f.Delivered.Rows.Add(("other/repo#12", "uat"));
                await f.Wake();
                Assert.DoesNotContain(f.LastDecision!.Reasons, r => r.Contains("depends on"));
            }
        }
    }

    // ---- the store ------------------------------------------------------------------------------------------------------------------------------------------

    sealed class ConflictingStore(ITrainStore inner, int failures) : ITrainStore
    {
        public int Saves;
        public Task<TrainRecord?> Load(string key, CancellationToken ct) => inner.Load(key, ct);
        public Task<bool> Save(string key, TrainState state, int expectedRevision, CancellationToken ct) => Saves++ < failures ? Task.FromResult(false) : inner.Save(key, state, expectedRevision, ct);
    }

    [Fact]
    public async Task AWriterThatLostTheRevisionRace_RunsAgain_AndAPersistentConflictFailsTheMessage()
    {
        var f = new TrainFixture();
        var retry = new DeliveryTrainService(new ConflictingStore(f.Store, 2), f.Policies, f.Reads, f.Delivered, () => f.Now);
        await retry.Handle(new TrainReleaseSeen(f.Key, "R1", "1.0.2", Sha('a'), f.Now));
        Assert.Contains("1.0.2", f.State.Releases.Keys);
        var broken = new DeliveryTrainService(new ConflictingStore(f.Store, 100), f.Policies, f.Reads, f.Delivered, () => f.Now);
        await Assert.ThrowsAsync<TrainConflictException>(() => broken.Handle(new TrainReleaseSeen(f.Key, "R2", "1.0.3", null, f.Now)));
    }

    [Fact]
    public async Task ATrainOfADeployableNotInTheCatalog_DoesNothing()
    {
        var f = new TrainFixture();
        var r = await f.Svc.Handle(new TrainWake("ghost/uat"));
        Assert.Empty(r.Messages);
        Assert.Empty(f.Store.States);
    }

    // ---- the router ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRouter_GivesEachTrainOfTheDeployableTheMergedItem_AndEachOctopusEventToEveryTrain()
    {
        var f = new TrainFixture();
        var router = new DeliveryTrainRouter(f.Policies, () => T0);
        var inputs = router.Route(f.Merged(7, 'a'));
        Assert.Equal(["orders/prod", "orders/tdd", "orders/uat"], inputs.Select(i => i.Key).Order());
        Assert.Empty(router.Route(new ItemMerged(new ItemId(new RepoId("acme", "other"), 1), Sha('a'), 1, new ItemMergedGates(true, "", true, false, false, []), [], "poll")));
        var dep = Assert.IsType<TrainDeploymentSeen>(router.Route(new OctopusDeploymentChanged("D1", "T1", "R1", "1.0.2", "Uat", OctopusDeploymentPhase.Succeeded, Sha('a'))).First(i => i.Key == "orders/uat"));
        Assert.Equal(("Uat", "Succeeded", T0), (dep.Environment, dep.Phase, dep.At));
        Assert.Equal(3, router.Route(new SignOffRequested("I1", "D1", "T1", "R1", "1.0.2", "Uat", null)).Count);
        Assert.Equal(3, router.Route(new OctopusReleaseCreated("R1", "1.0.2", null)).Count);
        Assert.Equal(Sha('a'), ((TrainReleaseSeen)router.Route(new ReleaseCommitResolved("R1", "1.0.2", Sha('a')))[0]).Commit);
    }

    [Fact]
    public void ThePolicy_ComesFromTheCatalog_TddIsThePipelinesAndAnUnconfiguredDeployableIsNeverPromoted()
    {
        var f = new TrainFixture();
        Assert.Equal(Promoter.Pipeline, f.Policies.For("orders", "tdd")!.Promoter);
        Assert.False(f.Policies.For("orders", "tdd")!.Acts);
        Assert.True(f.Policies.For("ORDERS", "uat")!.Acts);
        Assert.Equal("Uat", f.Policies.For("orders", "uat")!.NameOf("uat"));
        Assert.Equal("prod", f.Policies.For("orders", "uat")!.KeyOf("PROD"));
        var snapshot = new Fworker.Contracts.Catalog.CatalogSnapshot(Fworker.Contracts.Catalog.CatalogSource.SystemJson, T0, "acme", Repo, null,
            [new Fworker.Contracts.Catalog.RepositoryEntry(Repo, [new Fworker.Contracts.Catalog.DeployableEntry("orders", Repo, "/health", null, null, null, false)])], []);
        var none = new CatalogDeliveryPolicies(() => snapshot, _ => null);
        Assert.All(none.All(), p => Assert.False(p.Acts));
        Assert.Equal("/health", none.For("orders", "uat")!.HealthPath);
        Assert.Empty(none.ForOctopusEvents());
    }
}
