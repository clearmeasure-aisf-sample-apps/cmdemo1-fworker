using Fworker.Contracts.Ids;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.2 (#339): the merge train of a repository on an in-memory store with a clock the test moves. The tracked tests (MergeTrainTrackedTests) run the same service in a Wolverine host over SQL Server. Every rule here is
/// one of the two strategies (direct, train) or a guard: a transient answer is never taken for a lasting one, an input applied twice gives the same answers, an item that stops asking loses its place.
/// </summary>
public class MergeTrainTests
{
    const string Repo = "acme/orders";
    const string HeadSha = "aaa1111111111111111111111111111111111111";
    const string Commit = "bbb2222222222222222222222222222222222222";
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    sealed class Fx
    {
        public readonly InMemoryMergeTrainStore Store = new();
        public readonly MergeTrainService Svc;
        public DateTimeOffset Now = T0;

        public Fx(MergeStrategy strategy, TimeSpan? expiry = null, TimeSpan? patience = null, TimeSpan? waitRetry = null) =>
            Svc = new MergeTrainService(Store, new MergeTrainSettings(strategy, expiry ?? TimeSpan.FromMinutes(5), patience ?? TimeSpan.FromMinutes(15), waitRetry), () => Now);

        public MergeTrainState State => Store.States[Repo];
        public IEnumerable<int> Line => State.Line.OrderBy(e => e.Order).Select(e => ItemId.Parse(e.Item).Number);

        public static MergeRequested Req(int n, int attempt = 1, string arm = "1.4", int updates = 0) => new(new ItemId("acme", "orders", n), 1, arm, attempt, 10 + n, HeadSha, updates, "");

        public Task<IReadOnlyList<object>> Send(MergeTrainInput i) => Svc.Handle(i);
        public Task<IReadOnlyList<object>> Ready(int n, int attempt = 1, string arm = "1.4") => Send(new ReadyToMerge(Repo, Req(n, attempt, arm)));
        public Task<IReadOnlyList<object>> Answer(int n, MergeResult r, int attempt = 1, string arm = "1.4") => Send(new MergeTrainAnswered(Repo, Req(n, attempt, arm), r));
    }

    static MergeResult Blocked(string reason) => new(MergeResultKind.Blocked, reason, "d", Transient: true);
    static MergeResult Merged() => new(MergeResultKind.Merged, "merged", Commit: Commit);

    static MergeGranted? Granted(IEnumerable<object> o) => o.OfType<MergeGranted>().SingleOrDefault();
    static MergeAttempted? Attempted(IEnumerable<object> o) => o.OfType<MergeAttempted>().SingleOrDefault();
    static IEnumerable<string> Actions(IEnumerable<object> o) => o.OfType<MergeTrainDecided>().Select(d => d.Action);

    static bool IsWait(MergeAttempted a) => a.Result is { Kind: MergeResultKind.Blocked, Reason: MergeTrain.WaitReason, Transient: true };

    // ---- direct --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Direct_EveryRequestIsLetThroughAtOnce_NoOrder_NoWait()
    {
        var fx = new Fx(MergeStrategy.Direct);
        var a = await fx.Ready(1); var b = await fx.Ready(2); var c = await fx.Ready(3);
        foreach (var (o, n) in new[] { (a, 1), (b, 2), (c, 3) })
        {
            var g = Granted(o)!;
            Assert.Equal((n, true), (g.Request.Item.Number, g.FirstTurn));
            Assert.Null(Attempted(o));
        }
        Assert.Equal(3, fx.State.Line.Count);   // (the requests whose answer is outstanding)
        Assert.Empty(a.OfType<MergeTrainDecided>());   // (direct decides nothing until a merge or a refusal)
    }

    [Fact]
    public async Task Direct_TheUpdateOfABehindBranchIsAskedOnTheFirstAttemptOnly_AsTheSagaAlwaysDid()
    {
        var fx = new Fx(MergeStrategy.Direct);
        Assert.True(Granted(await fx.Ready(1))!.FirstTurn);
        await fx.Answer(1, Blocked("checks-pending"));
        Assert.False(Granted(await fx.Ready(1, attempt: 2))!.FirstTurn);
        await fx.Answer(1, Blocked("checks-pending"), attempt: 2);
        Assert.False(Granted(await fx.Ready(1, attempt: 3))!.FirstTurn);
    }

    [Fact]
    public async Task Direct_AMergeIsForwardedToTheSaga_UnderTheKeyOfTheAttempt_AndRecorded()
    {
        var fx = new Fx(MergeStrategy.Direct);
        await fx.Ready(1);
        var o = await fx.Answer(1, Merged());
        var answer = Attempted(o)!;
        Assert.Equal(("merge-attempt|1.4|1", 1, MergeResultKind.Merged, Commit), (answer.Key, answer.Attempt, answer.Result.Kind, answer.Result.Commit));
        Assert.Equal(new ItemId("acme", "orders", 1), answer.Item);
        Assert.Equal([MergeTrainActions.Merged], Actions(o));
        Assert.Empty(fx.State.Line);
        Assert.Equal([("acme/orders#1", Commit)], fx.State.Merged.Select(m => (m.Item, m.Commit)));
    }

    // ---- train ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Train_OneItemAtATime_TheOthersAreAnsweredAWait_NeverAFailure()
    {
        var fx = new Fx(MergeStrategy.Train);
        var first = await fx.Ready(1);
        Assert.True(Granted(first)!.FirstTurn);
        Assert.Equal([MergeTrainActions.Enqueued, MergeTrainActions.Granted], Actions(first));

        var second = await fx.Ready(2); var third = await fx.Ready(3);
        Assert.Null(Granted(second)); Assert.Null(Granted(third));
        var w2 = Attempted(second)!; var w3 = Attempted(third)!;
        Assert.True(IsWait(w2) && IsWait(w3));
        Assert.Contains("1 ahead", w2.Result.Detail); Assert.Contains("2 ahead", w3.Result.Detail);
        Assert.Contains("#1 is first", w2.Result.Detail);
        Assert.Equal(("merge-attempt|1.4|1", 1), (w2.Key, w2.Attempt));
        Assert.Equal([MergeTrainActions.Enqueued], Actions(second));
        Assert.Equal([1, 2, 3], fx.Line);
    }

    [Fact]
    public async Task Train_TheNextItemGetsTheTurnWhenTheFirstIsMerged_AndItsBranchIsUpdatedOnThatFirstTurn()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2); await fx.Ready(3);
        var merged = await fx.Answer(1, Merged());
        Assert.Equal(MergeResultKind.Merged, Attempted(merged)!.Result.Kind);
        Assert.Equal([MergeTrainActions.Merged], Actions(merged));
        Assert.Equal([2, 3], fx.Line);

        // the saga of item 3 asks first: it still waits, item 2 is first
        Assert.True(IsWait(Attempted(await fx.Ready(3, attempt: 2))!));
        // item 2 asks on its second attempt (the first was a wait): its turn is the first it gets, so the update of a branch that is behind is asked now
        var g = Granted(await fx.Ready(2, attempt: 2))!;
        Assert.Equal((2, 2, true), (g.Request.Item.Number, g.Request.Attempt, g.FirstTurn));
    }

    [Theory]
    [InlineData("checks-pending")]
    [InlineData("mergeability-unknown")]
    [InlineData("branch-updated")]
    [InlineData("merge-async")]
    [InlineData("merge-queue")]
    public async Task Train_AHeadThatWaitsForItsOwnChecks_KeepsTheFirstPlace_AndIsGrantedAgainWithoutANewUpdate(string reason)
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2);
        var o = await fx.Answer(1, Blocked(reason));
        Assert.Equal(reason, Attempted(o)!.Result.Reason);
        Assert.Empty(Actions(o));
        Assert.Equal([1, 2], fx.Line);
        Assert.True(IsWait(Attempted(await fx.Ready(2, attempt: 2))!));   // (item 2 still waits)
        var again = Granted(await fx.Ready(1, attempt: 2))!;
        Assert.False(again.FirstTurn);   // (the branch was updated on the first turn)
    }

    [Theory]
    [InlineData("no-auto-merge-label")]
    [InlineData("hold")]
    [InlineData("pr-draft")]
    [InlineData("merge-error")]
    [InlineData("merge-unavailable")]
    [InlineData("github-budget")]
    [InlineData("rate-limited")]
    public async Task Train_AHeadBlockedForAReasonOfItsOwn_GoesToTheBack_SoItCannotHoldTheOthers(string reason)
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2);
        var o = await fx.Answer(1, Blocked(reason));
        Assert.Equal(reason, Attempted(o)!.Result.Reason);   // (the saga hears the real reason)
        Assert.Equal([MergeTrainActions.Rotated], Actions(o));
        Assert.Equal([2, 1], fx.Line);
        Assert.False(MergeTrain.KeepsTheLine(reason));
        // item 2 is first now; item 1 asks again and waits
        Assert.True(IsWait(Attempted(await fx.Ready(1, attempt: 2))!));
        Assert.NotNull(Granted(await fx.Ready(2, attempt: 2)));
    }

    [Theory]
    [InlineData("checks-pending", true)]
    [InlineData("mergeability-unknown", true)]
    [InlineData("branch-updated", true)]
    [InlineData("merge-async", true)]
    [InlineData("merge-queue", true)]
    [InlineData("hold", false)]
    [InlineData("no-auto-merge-label", false)]
    [InlineData("pr-draft", false)]
    [InlineData("anything-else", false)]
    public void TheReasonsThatKeepTheFirstPlace_AreTheWaitsForTheHeadsOwnChecksAndMaster(string reason, bool keeps) => Assert.Equal(keeps, MergeTrain.KeepsTheLine(reason));

    [Fact]
    public async Task Train_AHeadThatHoldsTheFirstPlaceTooLong_GoesToTheBackWhenOthersWait()
    {
        var fx = new Fx(MergeStrategy.Train, expiry: TimeSpan.FromHours(2), patience: TimeSpan.FromMinutes(10));
        await fx.Ready(1); await fx.Ready(2);
        await fx.Answer(1, Blocked("checks-pending"));
        fx.Now = T0.AddMinutes(9);
        Assert.NotNull(Granted(await fx.Ready(1, attempt: 2)));   // (within the patience)
        await fx.Answer(1, Blocked("checks-pending"), attempt: 2);
        fx.Now = T0.AddMinutes(11);
        var o = await fx.Ready(1, attempt: 3);
        Assert.Null(Granted(o));
        Assert.True(IsWait(Attempted(o)!));
        Assert.Equal([MergeTrainActions.Rotated], Actions(o));
        Assert.Equal([2, 1], fx.Line);
        Assert.NotNull(Granted(await fx.Ready(2, attempt: 2)));
    }

    [Fact]
    public async Task Train_AHeadThatIsAlone_IsNeverRotated_PatienceIsForTheSakeOfTheOthers()
    {
        var fx = new Fx(MergeStrategy.Train, expiry: TimeSpan.FromHours(2), patience: TimeSpan.FromMinutes(10));
        await fx.Ready(1);
        await fx.Answer(1, Blocked("checks-pending"));
        fx.Now = T0.AddMinutes(30);
        var o = await fx.Ready(1, attempt: 2);
        Assert.NotNull(Granted(o));
        Assert.Equal([MergeTrainActions.Granted], Actions(o));   // (granted again, not rotated)
    }

    [Fact]
    public async Task Train_AnItemThatStopsAsking_LosesItsPlace_SoTheOthersAreNotHeldForGood()
    {
        var fx = new Fx(MergeStrategy.Train, expiry: TimeSpan.FromMinutes(5));
        await fx.Ready(1); await fx.Ready(2);
        fx.Now = T0.AddMinutes(4);
        Assert.True(IsWait(Attempted(await fx.Ready(2, attempt: 2))!));   // (item 1 was seen 4 minutes ago)
        fx.Now = T0.AddMinutes(6);
        var o = await fx.Ready(2, attempt: 3);
        Assert.NotNull(Granted(o));
        Assert.Equal([MergeTrainActions.Dequeued, MergeTrainActions.Granted], Actions(o));
        Assert.Contains("no merge request", o.OfType<MergeTrainDecided>().First().Reasons[0]);
        Assert.Equal([2], fx.Line);
        // the answer of the item that was dequeued comes late: the saga still hears it, and the merge is recorded
        var late = await fx.Answer(1, Merged());
        Assert.Equal(MergeResultKind.Merged, Attempted(late)!.Result.Kind);
        Assert.Contains(fx.State.Merged, m => m.Item == "acme/orders#1");
    }

    [Fact]
    public async Task Train_AnItemMergedElsewhere_ReleasesItsPlace_AndAnUnknownRepositoryCreatesNoRow()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2);
        var o = await fx.Send(new MergeTrainLeft(Repo, new ItemId("acme", "orders", 1), "merged elsewhere"));
        Assert.Equal([MergeTrainActions.Dequeued], Actions(o));
        Assert.Equal("merged elsewhere", o.OfType<MergeTrainDecided>().Single().Reasons[0]);
        Assert.NotNull(Granted(await fx.Ready(2, attempt: 2)));

        var empty = new Fx(MergeStrategy.Train);
        Assert.Empty(await empty.Send(new MergeTrainLeft("acme/other", new ItemId("acme", "other", 5), "merged elsewhere")));
        Assert.Empty(empty.Store.States);
        Assert.Empty(await fx.Send(new MergeTrainLeft(Repo, new ItemId("acme", "orders", 77), "merged elsewhere")));   // (not in the line: nothing to do)
    }

    [Fact]
    public async Task Train_EachRepositoryHasItsOwnLine()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1);
        var other = await fx.Send(new ReadyToMerge("acme/billing", new MergeRequested(new ItemId("acme", "billing", 1), 1, "1.4", 1, 11, HeadSha, 0, "")));
        Assert.NotNull(Granted(other));   // (item 1 of orders does not stand in front of billing)
        Assert.Equal(2, fx.Store.States.Count);
    }

    [Fact]
    public async Task Train_ANewArmTakesTheLastPlace_AndALateRequestOfAnEarlierAttemptChangesNothing()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2);
        // item 1 was demoted and validated again (a new arm): it stands behind item 2
        var o = await fx.Ready(1, arm: "1.9");
        Assert.Null(Granted(o));
        Assert.True(IsWait(Attempted(o)!));
        Assert.Equal([2, 1], fx.Line);

        await fx.Ready(2, attempt: 2);
        var before = fx.State.ToJson();
        Assert.Empty(await fx.Ready(2, attempt: 1));   // (attempt 1 of item 2 again, delivered late)
        Assert.Equal(before, fx.State.ToJson());
    }

    // ---- a transient answer is never a lasting one -----------------------------------------------------------------------------------------

    public static IEnumerable<object[]> TransientAnswers() =>
    [
        [new MergeResult(MergeResultKind.Blocked, "checks-pending", "d", Transient: true)],
        [new MergeResult(MergeResultKind.Blocked, "merge-error", "HttpRequestException", Transient: true)],
        [new MergeResult(MergeResultKind.Blocked, "github-budget", "low", Transient: true, RetryAfter: TimeSpan.FromMinutes(3))],
        [new MergeResult(MergeResultKind.Failed, "merge-refused", "GitHub said 502", Transient: true)],
        [new MergeResult(MergeResultKind.Failed, "rate-limited", "wait", Transient: true, RetryAfter: TimeSpan.FromMinutes(1))],
    ];

    [Theory]
    [MemberData(nameof(TransientAnswers))]
    public async Task ATransientAnswer_KeepsTheItemInTheLine_AndReachesTheSagaUnchanged_InBothStrategies(MergeResult answer)
    {
        foreach (var strategy in new[] { MergeStrategy.Train, MergeStrategy.Direct })
        {
            var fx = new Fx(strategy);
            await fx.Ready(1);
            var o = await fx.Answer(1, answer);
            Assert.Equal(answer, Attempted(o)!.Result);   // (not rewritten, not turned into a refusal)
            Assert.DoesNotContain(MergeTrainActions.Dequeued, Actions(o));
            Assert.Empty(fx.State.Merged);
            if (strategy == MergeStrategy.Train) Assert.Equal([1], fx.Line);   // (the item is still the train's: its next request is answered the same way as before)
            // and asking again works: the turn is granted again
            Assert.NotNull(Granted(await fx.Ready(1, attempt: 2)));
        }
    }

    [Theory]
    [InlineData(MergeResultKind.Failed, "merge-refused", "no")]
    [InlineData(MergeResultKind.Failed, "not-mergeable", "conflicts")]
    [InlineData(MergeResultKind.Failed, "merge-out-of-date", "behind")]
    [InlineData(MergeResultKind.HeadMoved, "stale-head", "moved")]
    public async Task OnlyARefusalForGood_OrAMovedHead_TakesTheItemOutOfTheLine(MergeResultKind kind, string reason, string detail)
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1); await fx.Ready(2);
        var o = await fx.Answer(1, new MergeResult(kind, reason, detail, Transient: false));
        Assert.Equal([MergeTrainActions.Dequeued], Actions(o));
        Assert.Equal(reason, Attempted(o)!.Result.Reason);
        Assert.Equal([2], fx.Line);
    }

    // ---- delivery is at least once ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestDeliveredTwice_IsAnsweredTheSameWay_AndChangesNothingTheSecondTime()
    {
        var fx = new Fx(MergeStrategy.Train);
        var first = await fx.Ready(1);
        var sequence = fx.State.Sequence;
        var again = await fx.Ready(1);
        Assert.Equal(Granted(first), Granted(again));
        Assert.Empty(Actions(again));   // (nothing is decided twice)
        Assert.Equal((sequence, 1, 1), (fx.State.Sequence, fx.State.Line.Count, fx.State.Line[0].Turns));

        var wait = await fx.Ready(2);
        Assert.Equal(Attempted(wait), Attempted(await fx.Ready(2)));
        Assert.Equal([1, 2], fx.Line);
    }

    [Fact]
    public async Task AnAnswerDeliveredTwice_ReachesTheSagaTwiceUnderOneKey_AndTheMergeIsRecordedOnce()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1);
        var one = await fx.Answer(1, Merged());
        var two = await fx.Answer(1, Merged());
        Assert.Equal(Attempted(one), Attempted(two));   // (the saga applies a key once)
        Assert.Equal([MergeTrainActions.Merged], Actions(one));
        Assert.Empty(Actions(two));
        Assert.Single(fx.State.Merged);
    }

    [Fact]
    public async Task TheMergedMemory_IsBounded()
    {
        var fx = new Fx(MergeStrategy.Direct);
        for (var n = 1; n <= MergeTrainState.KeepMerged + 5; n++) { await fx.Ready(n); await fx.Answer(n, Merged() with { Commit = n.ToString("x40") }); }
        Assert.Equal(MergeTrainState.KeepMerged, fx.State.Merged.Count);
        Assert.Equal("acme/orders#" + (MergeTrainState.KeepMerged + 5), fx.State.Merged[^1].Item);
    }

    // ---- the service ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWriterThatLostTheRace_RunsAgainOnTheStateThatWon()
    {
        var fx = new Fx(MergeStrategy.Train);
        var raced = false;
        fx.Store.BeforeSave = key =>
        {
            if (raced) return;
            raced = true;
            // another writer enqueues item 9 first
            var other = MergeTrainState.New(key);
            other.Line.Add(new MergeTrainEntry { Item = "acme/orders#9", Generation = 1, ArmId = "1.4", Attempt = 1, PullRequest = 19, Head = HeadSha, Order = 1, EnqueuedAt = T0, LastSeen = T0, Granted = true, Turns = 1, HeadSince = T0 });
            other.NextOrder = 2;
            Assert.True(fx.Store.Save(key, other, 0, default).GetAwaiter().GetResult());
        };
        var o = await fx.Ready(1);
        Assert.True(IsWait(Attempted(o)!));   // item 9 was first on the state that won
        Assert.Contains("#9 is first", Attempted(o)!.Result.Detail);
        Assert.Equal([9, 1], fx.Line);
    }

    [Fact]
    public async Task AWriterThatKeepsLosing_FailsTheMessage_SoWolverineRunsItAgain()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(1);
        Action<string>? bump = null;
        // every save finds the row one revision further than it read: another writer is always first
        bump = key =>
        {
            var rec = fx.Store.Load(key, default).GetAwaiter().GetResult()!;
            fx.Store.BeforeSave = null;
            fx.Store.Save(key, rec.State, rec.Revision, default).GetAwaiter().GetResult();
            fx.Store.BeforeSave = bump;
        };
        fx.Store.BeforeSave = bump;
        var e = await Assert.ThrowsAsync<MergeTrainConflictException>(() => fx.Ready(2));
        Assert.Contains("acme/orders", e.Message);
    }

    [Fact]
    public async Task TheStateRoundTripsAsJson_WithTheLineInOrder()
    {
        var fx = new Fx(MergeStrategy.Train);
        await fx.Ready(3); await fx.Ready(1); await fx.Ready(2);
        var again = MergeTrainState.FromJson(fx.State.ToJson());
        Assert.Equal(fx.State.ToJson(), again.ToJson());
        Assert.Equal(["acme/orders#3", "acme/orders#1", "acme/orders#2"], again.Line.OrderBy(e => e.Order).Select(e => e.Item));
        Assert.Equal("acme/orders#3", again.First!.Item);
    }

    // ---- the strategies ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, MergeStrategy.Direct, false)]
    [InlineData("", MergeStrategy.Direct, false)]
    [InlineData("direct", MergeStrategy.Direct, false)]
    [InlineData("DIRECT", MergeStrategy.Direct, false)]
    [InlineData("train", MergeStrategy.Train, false)]
    [InlineData(" Train ", MergeStrategy.Train, false)]
    [InlineData("queue", MergeStrategy.Direct, true)]
    [InlineData("Queue", MergeStrategy.Direct, true)]
    [InlineData("nonsense", MergeStrategy.Direct, true)]
    public void TheMergeQueueIsDeferred_AndAnythingUnknownMergesDirectly_WithAWarning(string? text, MergeStrategy expected, bool warns)
    {
        var (strategy, warning) = MergeTrainSettings.ParseStrategy(text);
        Assert.Equal(expected, strategy);
        Assert.Equal(warns, warning is not null);
        if (text?.Trim().Equals("queue", StringComparison.OrdinalIgnoreCase) == true) Assert.Contains("deferred", warning);
    }

    [Fact]
    public void TheSettingsDefaults_AreFiveMinutesOfExpiry_AndFifteenOfPatience()
    {
        Assert.Equal((MergeStrategy.Direct, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)), (MergeTrainSettings.Default.Strategy, MergeTrainSettings.Default.Expiry, MergeTrainSettings.Default.Patience));
        var odd = new MergeTrainSettings(MergeStrategy.Train, TimeSpan.Zero, TimeSpan.FromSeconds(-1), null);
        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)), (odd.Expiry, odd.Patience));
    }

    // ---- the saga's side of the contract -----------------------------------------------------------------------------------------------

    static async Task<SagaFixture> AtMerge()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(HeadSha); await fx.Review(); await fx.Done();
        await fx.Verdict(true, HeadSha);
        return fx;
    }

    [Fact]
    public async Task TheWaitOfTheLine_CarriesTheConfiguredPause_AndTheSagaAsksAgainAfterIt()
    {
        var fx = await AtMerge();
        var request = Assert.IsType<MergeRequested>(fx.Commands.Last());
        var train = new Fx(MergeStrategy.Train, waitRetry: TimeSpan.FromSeconds(7));
        await train.Ready(1);
        var wait = Attempted(await train.Send(new ReadyToMerge(Repo, request)))!;
        Assert.Equal(TimeSpan.FromSeconds(7), wait.Result.RetryAfter);
        await fx.Send(wait);
        Assert.Equal(fx.Now.AddSeconds(7), fx.State.MergeRetryAt);
        // not configured: the saga's own pause
        var plain = new Fx(MergeStrategy.Train);
        await plain.Ready(1);
        Assert.Null(Attempted(await plain.Ready(2))!.Result.RetryAfter);
    }

    [Fact]
    public async Task TheWaitOfTheLine_IsABlockedAnswerTheSagaAsksAgainAfter_NotAFailureAndNoPark()
    {
        var fx = await AtMerge();
        var request = Assert.IsType<MergeRequested>(fx.Commands.Last());
        var train = new Fx(MergeStrategy.Train);
        await train.Ready(1);   // (another item has the first place)
        var wait = Attempted(await train.Send(new ReadyToMerge(Repo, request)))!;
        Assert.True(IsWait(wait));
        Assert.Equal(MergeTrain.AnswerKey(request), wait.Key);

        var r = await fx.Send(wait);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal([WorkItemActions.MergeBlocked], r.Decisions.Select(d => d.Action));
        Assert.Equal(MergeTrain.WaitReason, r.Decisions[0].Reasons[0]);
        Assert.False(fx.State.Parked);
        Assert.Equal(fx.Now.AddSeconds(60), fx.State.MergeRetryAt);
        Assert.Contains(r.Timers, t => t.Message is MergeRetryDue);

        // the retry asks again, and when the first merged the train lets the item through
        fx.Now = fx.Now.AddSeconds(61);
        await fx.Send(Assert.IsType<MergeRetryDue>(r.Timers.Single(t => t.Message is MergeRetryDue).Message));
        var second = Assert.IsType<MergeRequested>(fx.Commands.Last());
        Assert.Equal(2, second.Attempt);
        await train.Answer(1, Merged());
        var granted = Granted(await train.Send(new ReadyToMerge(Repo, second)))!;
        Assert.True(granted.FirstTurn);
        var merged = await train.Send(new MergeTrainAnswered(Repo, second, Merged()));
        var done = await fx.Send(Attempted(merged)!);
        Assert.Contains(WorkItemActions.Merged, done.Decisions.Select(d => d.Action));
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
    }
}
