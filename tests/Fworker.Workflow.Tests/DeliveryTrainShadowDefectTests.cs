using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// Issue 500 (W5 shadow analysis on tdd): the train must not decide on a replay's partial state, must say one decision per open release and retract it when the release is done with, must not lose
/// an input to a read that fails, and must look again by itself. One test per defect, written to fail on the code before the fix.
/// </summary>
public class DeliveryTrainShadowDefectTests
{
    static readonly string[] HoldGap = ["#8 carries the 'hold:owner' label"];

    /// <summary>uat train, baseline 1.0.1 (commit d) everywhere, item #7 merged as commit a, release 1.0.2 built from a and finished in tdd.</summary>
    static async Task<TrainFixture> OneRelease(string[]? gaps = null)
    {
        var f = new TrainFixture();
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a', gaps);
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        return f;
    }

    // ---- defect 1: no decision on a replay's partial state ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shape of tdd's replay: the watcher publishes every release first and every deployment after (Watcher.Poll), the deployments of a release as Started then Succeeded, an old release
    /// deployed to uat among newer ones that are not.
    /// </summary>
    static async Task<TrainFixture> TddShapedReplay(bool markers, Func<TrainFixture, string, Task>? after = null)
    {
        var f = new TrainFixture();
        await f.Baseline();
        char[] commits = ['a', 'b', 'e', 'f', 'g'];   // 1.0.2 .. 1.0.6, a chain on top of the baseline commit d
        f.Range('d', 'a'); f.Range('d', 'a', 'b'); f.Range('d', 'a', 'b', 'e');
        f.Range('e', 'f'); f.Range('e', 'f', 'g');
        f.Reads.Running["tdd"] = "1.0.6";
        if (markers) await f.Send(new TrainReplay(f.Key, true));
        for (var i = 0; i < commits.Length; i++) { f.Now += TimeSpan.FromMinutes(1); await f.Merge(2 + i, commits[i]); }
        for (var i = 0; i < commits.Length; i++) { f.Now += TimeSpan.FromMinutes(1); await f.Release($"1.0.{2 + i}", commits[i]); }
        if (after is not null) await after(f, "releases");
        for (var i = 0; i < commits.Length; i++)
        {
            var v = $"1.0.{2 + i}";
            f.Now += TimeSpan.FromMinutes(1);
            await f.Deploy(v, "Tdd", "Started"); await f.Deploy(v, "Tdd", "Succeeded");
        }
        if (after is not null) await after(f, "tdd");
        f.Now += TimeSpan.FromMinutes(1);
        await f.Deploy("1.0.4", "Uat", "Started"); await f.Deploy("1.0.4", "Uat", "Succeeded");
        if (after is not null) await after(f, "uat");
        if (markers) await f.Send(new TrainReplay(f.Key, false));
        return f;
    }

    [Fact]
    public async Task WithoutTheReplayMarkers_TheTrainDecidesOnPartialState_TheDefect()
    {
        string? firstWaiting = null;
        await TddShapedReplay(false, (f, stage) =>
        {
            if (stage == "releases" && f.Of<TrainDecided>().FirstOrDefault(d => d.Action == "waiting") is { } w) firstWaiting = w.Reasons[0];
            return Task.CompletedTask;
        });
        // every release was seen, no deployment yet: the train says the newest release has no deployment to tdd, and v1 says no such thing
        Assert.Equal("it has no deployment to Tdd", firstWaiting);
    }

    [Fact]
    public async Task WhileTheReplayIsApplied_TheTrainDecidesNothingAndReadsNothing_ThenJudgesOnce()
    {
        var seen = new List<string>();
        var f = await TddShapedReplay(true, (fx, stage) =>
        {
            Assert.Empty(fx.Of<TrainDecided>());
            Assert.Empty(fx.Of<PromoteRelease>());
            Assert.Empty(fx.Of<DeliveredTo>());
            Assert.Equal(0, fx.Reads.Probes + fx.Reads.Compares);
            seen.Add(stage);
            return Task.CompletedTask;
        });
        Assert.Equal(["releases", "tdd", "uat"], seen);
        // the end of the replay: one judgment of the whole state
        var decided = f.Of<TrainDecided>().ToList();
        Assert.NotEmpty(decided);
        Assert.All(decided, d => Assert.True(d.Replay));
        var delivered = Assert.Single(decided, d => d.Action == "delivered");
        Assert.Equal("1.0.4", delivered.Release);
        Assert.Equal(["1.0.2", "1.0.3"], delivered.Superseded!.Order());
        Assert.Equal(["acme/orders#2", "acme/orders#3", "acme/orders#4"], f.Of<DeliveredTo>().Select(d => d.Item.ToString()).Order());
        // only the open releases are described, and none of them as having no deployment to tdd
        Assert.DoesNotContain(decided, d => d.Reasons.Any(r => r.Contains("no deployment")));
        Assert.Contains(decided, d => d is { Action: "waiting", Release: "1.0.6" });
        Assert.Null(f.State.ReplayingSince);
    }

    [Fact]
    public async Task ADecisionAfterTheReplay_IsLive()
    {
        var f = await TddShapedReplay(true);
        f.All.Clear();
        await f.After(TimeSpan.FromMinutes(5)); await f.After(TimeSpan.FromMinutes(5)); await f.After(TimeSpan.FromMinutes(5));
        Assert.Contains(f.Of<TrainDecided>(), d => d.Action == "promote" && !d.Replay);
    }

    [Fact]
    public async Task AReplayWhoseEndNeverComes_StopsHoldingTheTrainBack()
    {
        var f = new TrainFixture(); await f.Baseline();
        await f.Send(new TrainReplay(f.Key, true));
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        Assert.Empty(f.Of<TrainDecided>());
        f.Now += DeliveryTrainService.ReplayLimit + TimeSpan.FromMinutes(1);
        await f.Wake();
        Assert.NotEmpty(f.Of<TrainDecided>());
    }

    // ---- the scheduled-envelope backlog (tdd 1.0.1069, 1.0.1071): every input of a restart's replay scheduled a timer of its own ---------------------------------------------

    [Fact]
    public async Task AReplayOfManyInputs_SchedulesExactlyOneTimer_ForTheHoldOfTheTrain()
    {
        var f = new TrainFixture(); await f.Baseline();
        var timers = new List<DateTimeOffset>();
        async Task Count(Task<TrainResult> send) { if ((await send).WakeAt is { } w) timers.Add(w); }
        await Count(f.Send(new TrainReplay(f.Key, true)));
        // 40 releases with a deployment to each of the three environments and two events each: about what the watcher's windows replay
        for (var i = 0; i < 40; i++)
        {
            f.Now += TimeSpan.FromSeconds(1);
            var v = $"1.0.{10 + i}";
            await Count(f.Release(v, 'a')); await Count(f.Release(v, 'a'));
            foreach (var e in new[] { "Tdd", "Uat", "Prod" }) { await Count(f.Deploy(v, e, "Started")); await Count(f.Deploy(v, e, "Succeeded")); }
        }
        // the one timer is the hold: it is the train's pending wake, due when the hold runs out
        var hold = Assert.Single(timers);
        Assert.Equal(f.State.ReplayingSince + DeliveryTrainService.ReplayLimit, hold);
        Assert.Equal(hold, f.State.WakeDue);
        Assert.Empty(f.Of<TrainDecided>());
        Assert.Equal(0, f.Reads.Probes + f.Reads.Compares);
        // the end of the replay: the train judges once and its wake moves to the earlier time of what it waits for (never a second timer for the same time)
        await Count(f.Send(new TrainReplay(f.Key, false)));
        Assert.All(timers, t => Assert.True(t <= hold));
        Assert.True(timers.Count <= 2, $"{timers.Count} timers for one replay");   // (the hold, and at most the one wake of the judgment at the end)
    }

    [Fact]
    public async Task AnInputWhileTheHoldIsPending_SendsNoTimer_AndTheFiredHoldIsReplacedOnlyIfTheReplayIsStillHeld()
    {
        var f = new TrainFixture(); await f.Baseline();
        Assert.NotNull((await f.Send(new TrainReplay(f.Key, true))).WakeAt);
        Assert.Null((await f.Merge(7, 'a')).WakeAt);
        Assert.Null((await f.Release("1.0.2", 'a')).WakeAt);
        // a second replay begin (the watcher restarts it) holds longer than the pending timer: no second timer, the earlier one stands
        var first = f.State.WakeDue;
        f.Now += TimeSpan.FromMinutes(3);
        var second = await f.Send(new TrainReplay(f.Key, true));
        Assert.Null(second.WakeAt);
        Assert.Equal(first, f.State.WakeDue);
        // the first timer fires while the replay is still held: it is the pending one, and the hold's new end goes out as the one timer
        f.Now = first!.Value;
        var fired = await f.Wake(first);
        Assert.Equal(f.State.ReplayingSince!.Value + DeliveryTrainService.ReplayLimit, fired.WakeAt);
        Assert.Equal(fired.WakeAt, f.State.WakeDue);
        Assert.Empty(fired.Messages);
        // a timer that is not the pending one (the first, delivered again) does nothing
        var stale = await f.Wake(first);
        Assert.Null(stale.WakeAt);
        Assert.Empty(stale.Messages);
    }

    [Fact]
    public async Task AStaleTimer_WhileAnotherIsPending_ReadsNothing_ButOneWithNoPendingTimerStillLooks()
    {
        var f = await OneRelease();
        var pending = f.State.WakeDue;
        Assert.NotNull(pending);
        var reads = f.Reads.Probes + f.Reads.Compares;
        // (a train that waits has a pending timer; a timer of another time than that one is stale)
        var stale = await f.Wake(pending!.Value.AddSeconds(-30));
        Assert.Equal(TrainResult.None, stale);
        Assert.Equal(reads, f.Reads.Probes + f.Reads.Compares);
        Assert.Equal(pending, f.State.WakeDue);
        // the pending timer itself looks, as it always did
        f.Now = pending.Value;
        await f.Wake(pending);
        Assert.True(f.Reads.Probes + f.Reads.Compares > reads, "the pending timer must evaluate");
        // a stale timer with no pending one (the pending one fired and the evaluation scheduled none, or it was lost) still evaluates
        var rec = (await f.Store.Load(f.Key, default))!; rec.State.WakeDue = null;
        Assert.True(await f.Store.Save(f.Key, rec.State, rec.Revision, default));
        reads = f.Reads.Probes + f.Reads.Compares;
        await f.Wake(f.Now.AddSeconds(-30));
        Assert.True(f.Reads.Probes + f.Reads.Compares > reads, "with no pending timer a stale one looks (it heals a lost timer)");
    }

    // ---- defect 5 (tdd 1.0.838 and 1.0.839): after a restart the train decided nothing, because nothing had changed ------------------------------------------------

    /// <summary>Two open releases with a decision each, published and saved: 1.0.2 waits for its item's gate, 1.0.3 has no deployment to Tdd.</summary>
    static async Task<TrainFixture> TwoDecisions()
    {
        var f = await OneRelease(HoldGap);
        f.Now += TimeSpan.FromMinutes(1);
        await f.Release("1.0.3", 'b');
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.2" && d.Action == "waiting");
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.3" && d.Action == "waiting");
        f.All.Clear();
        return f;
    }

    static string[] Released(TrainFixture f) => f.Of<TrainDecided>().Select(d => d.Release).Distinct().OrderBy(r => r).ToArray();

    [Fact]
    public async Task AfterARestartsReplay_EveryCurrentDecisionIsPublishedAgain_ThoughNothingChanged()
    {
        var f = await TwoDecisions();
        // a restart: the watcher replays Octopus's history (the state already has all of it), and whoever listens to the decisions starts with none
        await f.Send(new TrainReplay(f.Key, true));
        await f.Release("1.0.2", 'a'); await f.Release("1.0.3", 'b');
        Assert.Empty(f.Of<TrainDecided>());
        await f.Send(new TrainReplay(f.Key, false));
        var again = f.Of<TrainDecided>().ToList();
        Assert.Equal(["1.0.2", "1.0.3"], again.Select(d => d.Release).OrderBy(r => r).ToArray());
        Assert.All(again, d => { Assert.Equal("waiting", d.Action); Assert.True(d.Replay); Assert.NotEmpty(d.Reasons); });
        Assert.False(f.State.RepublishPending);
        // and only once: the next look, with nothing changed, says nothing
        f.All.Clear();
        await f.After(TimeSpan.FromMinutes(1));
        Assert.Empty(f.Of<TrainDecided>());
    }

    [Fact]
    public async Task ADecisionSavedBeforeTheDetailsWereKept_IsDecidedAfreshAfterARestart()
    {
        var f = await TwoDecisions();
        var rec = (await f.Store.Load(f.Key, default))!;
        rec.State.DecisionDetails.Clear();   // (what the deployed state looks like: fingerprints only)
        Assert.True(await f.Store.Save(f.Key, rec.State, rec.Revision, default));
        await f.Send(new TrainReplay(f.Key, true));
        await f.Send(new TrainReplay(f.Key, false));
        Assert.Equal(["1.0.2", "1.0.3"], Released(f));
    }

    [Fact]
    public async Task AReleaseThatCannotBeReadAtTheRestartsReplayEnd_KeepsItsDecision_AndIsPublishedWithTheOthers()
    {
        var f = await TwoDecisions();
        f.Reads.Fail = new HttpRequestException("the shadow run's reads of this minute are spent");   // 1.0.2's range cannot be read: it is not judged again, its decision stands
        await f.Send(new TrainReplay(f.Key, true));
        await f.Send(new TrainReplay(f.Key, false));
        Assert.Equal(["1.0.2", "1.0.3"], Released(f));
        Assert.Contains(f.Of<TrainDecided>(), d => d.Release == "1.0.2" && d.Reasons.Any(r => r.Contains("hold:owner")));
        Assert.False(f.State.RepublishPending);
    }

    [Fact]
    public async Task AReplayWhoseEndNeverComes_PublishesTheDecisionsAgainWhenTheHoldRunsOut()
    {
        var f = await TwoDecisions();
        await f.Send(new TrainReplay(f.Key, true));
        f.Now += DeliveryTrainService.ReplayLimit + TimeSpan.FromMinutes(1);
        await f.Wake();
        Assert.Equal(["1.0.2", "1.0.3"], Released(f));
    }

    // ---- defect 2: a later outcome clears what the train said about a release -----------------------------------------------------------------------------------

    [Fact]
    public async Task ADeliveredRelease_SaysWhichReleasesItSuperseded_SoTheShadowCanRetractThem()
    {
        var f = await OneRelease();
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b', HoldGap);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        Assert.Contains("1.0.2", f.State.Decisions.Keys); Assert.Contains("1.0.3", f.State.Decisions.Keys);
        // 1.0.3 reaches uat by a person
        await f.Deploy("1.0.3", "Uat", "Started"); await f.Deploy("1.0.3", "Uat", "Succeeded");
        var delivered = f.Of<TrainDecided>().Last(d => d.Action == "delivered");
        Assert.Equal("1.0.3", delivered.Release);
        Assert.Equal(["1.0.2"], delivered.Superseded);
        Assert.Empty(f.State.Decisions);   // nothing is left to say about either
    }

    [Fact]
    public async Task AReleaseThatIsNotJudgedAnyMore_IsRetractedWithAnIdle()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a', ["#7 has no 'Auto Merge' label"]);
        // 1.0.1b is assembled first and deployed later; 1.0.2 is built from the same commit
        f.Now += TimeSpan.FromMinutes(1); await f.Release("1.0.1b", 'a');
        f.Now += TimeSpan.FromMinutes(1); f.Reads.Running["tdd"] = "1.0.2"; await f.ReleaseInPrevious("1.0.2", 'a');
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Equal("1.0.2", f.LastDecision.Release);
        f.All.Clear();
        await f.Deploy("1.0.1b", "Uat", "Started"); await f.Deploy("1.0.1b", "Uat", "Succeeded");   // uat now runs the commit 1.0.2 was built from
        var idle = Assert.Single(f.Of<TrainDecided>(), d => d.Action == "idle");
        Assert.Equal("1.0.2", idle.Release);
        Assert.DoesNotContain("1.0.2", f.State.Decisions.Keys);
        // and it is said once
        f.All.Clear(); await f.After(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain(f.Of<TrainDecided>(), d => d.Action == "idle");
    }

    // ---- defect 3: one decision for every open release v1 would judge ---------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryOpenRelease_GetsItsOwnDecision_NotOnlyTheNewestBlockedOne()
    {
        var f = await OneRelease(["#7 carries the 'hold:owner' label"]);
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b', HoldGap);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        var waiting = f.Of<TrainDecided>().Where(d => d.Action == "waiting").GroupBy(d => d.Release).ToDictionary(g => g.Key, g => g.Last());
        Assert.Contains("1.0.3", waiting.Keys);
        Assert.Contains("1.0.2", waiting.Keys);   // v1 names this one too (uat 2.4.1261 was v1-only)
        Assert.Contains("#7 carries the 'hold:owner' label", waiting["1.0.2"].Reasons);
    }

    [Fact]
    public async Task ABlockedNewerRelease_IsDescribed_WhileTheOlderOneIsPromoted()
    {
        var f = await OneRelease();
        f.Range('d', 'a', 'b');
        f.Now += TimeSpan.FromMinutes(1);
        await f.Merge(8, 'b', HoldGap);
        f.Reads.Running["tdd"] = "1.0.3";
        await f.ReleaseInPrevious("1.0.3", 'b');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.Mature();
        var decided = f.Of<TrainDecided>().ToList();
        Assert.Contains(decided, d => d is { Action: "waiting", Release: "1.0.3" });
        Assert.Contains(decided, d => d is { Action: "promote", Release: "1.0.2" });
    }

    // ---- defect 4: no lost input ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(TimeoutException))]
    public async Task AReadThatFails_DoesNotLoseTheInput_TheTrainLooksAgainByItself(Type failure)
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        f.Reads.Fail = (Exception)Activator.CreateInstance(failure, "the read budget is low")!;
        var seen = await f.Release("1.0.2", 'a');
        var tdd = await f.Deploy("1.0.2", "Tdd", "Started");
        var done = await f.Deploy("1.0.2", "Tdd", "Succeeded");   // the evaluation needs a read now, and fails
        // nothing is thrown, the facts are kept, and a wake is asked for
        Assert.Contains("1.0.2", f.State.Releases.Keys);
        Assert.True(f.State.Deployments["1.0.2"]["tdd"].Succeeded);
        Assert.Equal(f.Now + DeliveryTrainService.TransientRetry, done.WakeAt);
        Assert.Empty(done.Messages);
        // the read answers again: the wake judges the state the saved inputs made
        f.Reads.Fail = null;
        await f.Fire(done);
        Assert.Contains(f.Of<TrainDecided>(), d => d is { Action: "waiting", Release: "1.0.2" });
        Assert.NotNull(seen); Assert.NotNull(tdd);
    }

    [Fact]
    public async Task TheTransientRetry_WhileAnotherWakeIsPending_IsNotStale_AndEvaluates_AsOutgoingStampsIt()
    {
        var f = new TrainFixture("tdd"); await f.Baseline();   // (the tdd train settles the deployment itself, with a read that no release is judged around)
        f.Range('d', 'a'); await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.Release("1.0.2", 'a'); await f.Deploy("1.0.2", "Tdd", "Started");
        // a train that waits has a wake to come (SafetyWake, a poll or a command retry)
        var rec = (await f.Store.Load(f.Key, default))!; rec.State.WakeDue = f.Now + DeliveryTrainService.SafetyWake;
        Assert.True(await f.Store.Save(f.Key, rec.State, rec.Revision, default));
        var pending = f.State.WakeDue;
        f.Now += TimeSpan.FromMinutes(1);
        f.Reads.Fail = new HttpRequestException("403");
        var failed = await f.Deploy("1.0.2", "Tdd", "Succeeded");   // the delivery to tdd is settled with a read of GitHub, which fails
        Assert.True(failed.Retry);
        Assert.Equal(f.Now + DeliveryTrainService.TransientRetry, failed.WakeAt);
        Assert.Equal(pending, f.State.WakeDue);   // (the failed look kept nothing: the pending wake stands)
        // the retry goes out with no due time: it is not a timer of the train's pending wake and must never count as stale
        Assert.Null(DeliveryTrainHandlers.WakeFor(failed, f.Key)!.Due);
        var scheduled = DeliveryTrainHandlers.Outgoing(failed, f.Key);
        Assert.Single(scheduled);
        // the train's own wake still carries its due time
        Assert.Equal(pending, DeliveryTrainHandlers.WakeFor(new TrainResult([], pending), f.Key)!.Due);
        f.Reads.Fail = null;
        var reads = f.Reads.Probes + f.Reads.Compares;
        await f.Fire(failed);
        Assert.True(f.Reads.Probes + f.Reads.Compares > reads, "the retry must evaluate though another wake is pending");
        Assert.Contains("1.0.2", f.State.Settled);
    }

    [Fact]
    public async Task AFailureThatIsNoReadFailure_StillFailsTheMessage_ButTheInputIsSaved()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        f.Reads.Fail = new InvalidOperationException("a defect");
        await f.Release("1.0.2", 'a');   // (no deployment to tdd: no read)
        await f.Deploy("1.0.2", "Tdd", "Started");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Deploy("1.0.2", "Tdd", "Succeeded"));
        Assert.True(f.State.Deployments["1.0.2"]["tdd"].Succeeded);
    }

    // ---- defect 7: the timers ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWaitingTrain_LooksAgainAtLeastEveryTenMinutes_EvenWhenNoGapIsTimeBound()
    {
        var f = await OneRelease(["#7 carries the 'hold:owner' label"]);
        var r = await f.After(TimeSpan.FromMinutes(1));   // the hold lifts only by a label change the train may not hear of
        Assert.Equal(f.Now + DeliveryTrainService.SafetyWake, r.WakeAt);
        // and again after that, as long as it waits
        var again = await f.After(DeliveryTrainService.SafetyWake);
        Assert.Equal(f.Now + DeliveryTrainService.SafetyWake, again.WakeAt);
    }

    [Fact]
    public async Task ATrainWithNothingToWaitFor_AsksForNoTimer()
    {
        var f = new TrainFixture(); await f.Baseline();
        var r = await f.After(TimeSpan.FromMinutes(1));
        Assert.Null(r.WakeAt);
        Assert.Empty(f.Of<TrainDecided>());
    }

    [Fact]
    public async Task EveryKindOfHealthGap_WakesTheTrain()
    {
        // the app does not run the release yet, and the reads are too young: the poll
        var f = await OneRelease();
        f.Reads.Running["tdd"] = "1.0.1";
        var notRunning = await f.After(TimeSpan.FromMinutes(1));
        Assert.Contains("does not run release 1.0.2", f.LastDecision!.Reasons[0]);
        Assert.True(notRunning.WakeAt <= f.Now + TimeSpan.FromSeconds(120));
        // an unhealthy answer after the deployment finished: only a new deployment lifts it, so the safety interval
        f.Reads.Running["tdd"] = "1.0.2"; f.Reads.Status["tdd"] = "Unhealthy";
        var unhealthy = await f.After(TimeSpan.FromMinutes(1));
        Assert.StartsWith("the tdd app answered Unhealthy", f.LastDecision!.Reasons[0]);
        Assert.NotNull(unhealthy.WakeAt);
        Assert.True(unhealthy.WakeAt <= f.Now + DeliveryTrainService.SafetyWake);
    }

    // ---- tdd 2026-10-31: a restart's replay left one timer per input behind, and the burst of full evaluations 30 minutes later ended in the dead letters ------------

    [Fact]
    public async Task AReplayOfManyInputs_SchedulesOneTimerForItsHold_NotOnePerInput()
    {
        var f = new TrainFixture(); await f.Baseline();
        var results = new List<TrainResult> { await f.Send(new TrainReplay(f.Key, true)) };
        for (var i = 0; i < 40; i++)
        {
            f.Now += TimeSpan.FromSeconds(1);
            results.Add(i % 2 == 0 ? await f.Release($"2.0.{i}", 'a') : await f.Deploy($"2.0.{i - 1}", "Tdd", "Started"));
        }
        var hold = f.State.ReplayingSince!.Value + DeliveryTrainService.ReplayLimit;
        var timers = results.Where(r => r.WakeAt is not null).ToList();
        Assert.Single(timers);
        Assert.Equal(hold, timers[0].WakeAt);
        Assert.Equal(hold, f.State.WakeDue);
        // the end marker ends the hold; the hold's timer, when it comes, is the pending one: it looks again once and is cleared
        await f.Send(new TrainReplay(f.Key, false));
        f.Now = hold;
        await f.Wake(hold);
        Assert.NotEqual(hold, f.State.WakeDue);
    }

    [Fact]
    public async Task AReplaysHold_KeepsAnEarlierPendingTimer_AndSchedulesItsOwnWhenThatOneFires()
    {
        var f = await OneRelease(HoldGap);   // waiting: the safety wake is pending
        var pending = f.State.WakeDue!.Value;
        Assert.True(pending < f.Now + DeliveryTrainService.ReplayLimit);
        var r = await f.Send(new TrainReplay(f.Key, true));
        Assert.Null(r.WakeAt);   // the pending timer stands; the hold needs none yet
        Assert.Equal(pending, f.State.WakeDue);
        f.Now = pending;
        var fired = await f.Wake(pending);   // still held: the timer for the hold's end goes out now
        Assert.Equal(f.State.ReplayingSince!.Value + DeliveryTrainService.ReplayLimit, fired.WakeAt);
    }

    [Fact]
    public async Task TheTransientRetry_WhileTheSafetyWakeIsPending_StillEvaluates()
    {
        // The retry of a failure inside a release's judgement is recorded as the train's pending wake (it is earlier than the safety wake), so when it fires it is the pending one and looks: the retry chain does not end at its first link.
        var f = await OneRelease(HoldGap);   // waiting: the safety wake is pending
        var pending = f.State.WakeDue!.Value;
        Assert.True(pending > f.Now + DeliveryTrainService.TransientRetry);
        f.Reads.Fail = new HttpRequestException("the read budget is spent");
        var failed = await f.Deploy("1.0.2", "Tdd", "Succeeded");   // the evaluation needs a read of the running version, and fails: it asks for the retry
        Assert.Equal(f.Now + DeliveryTrainService.TransientRetry, failed.WakeAt);
        Assert.Equal(failed.WakeAt, f.State.WakeDue);   // (recorded as the pending wake)
        f.Reads.Fail = null;
        var sequence = f.State.Sequence;
        await f.Fire(failed);
        Assert.True(f.State.Sequence > sequence, "the retry evaluated (a look saves the state; the early return did not)");
        Assert.NotNull(f.State.WakeDue);   // (the safety wake carries the chain on)
    }

    [Fact]
    public async Task AStaleTimer_WithNoPendingOne_StillLooks_SoTheChainIsNeverLost()
    {
        var f = await OneRelease(HoldGap);
        var rec = (await f.Store.Load(f.Key, default))!;
        rec.State.WakeDue = null;   // (the pending timer was lost between the save and the outbox)
        Assert.True(await f.Store.Save(f.Key, rec.State, rec.Revision, default));
        f.Now += TimeSpan.FromMinutes(1);
        var r = await f.Wake(f.Now - TimeSpan.FromSeconds(30));
        Assert.NotNull(r.WakeAt);
        Assert.Equal(r.WakeAt, f.State.WakeDue);
    }
}
