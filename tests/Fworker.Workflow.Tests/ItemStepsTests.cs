using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Delivery.Verbs;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10f: what the watcher of the sweep's item steps reads (master's CI for a merge commit with the one re-run of the failed jobs, a pull request, a human's approval) and what it tells the saga. The reads are the rules of
/// Invoke-Settle (the master-green wait) and Find-HumanApproval over the kit of the delivery verbs; the job is the part that turns a reading into an input of the saga.
/// </summary>
public class ItemStepsReadTests
{
    static readonly CancellationToken Ct = CancellationToken.None;
    static readonly string Merge = VerbFixture.Sha('c');
    static readonly string Later = VerbFixture.Sha('d');
    const string ChecksPath = "repos/acme/orders/commits/";

    static JsonObject CheckRuns(params (string Name, string Status, string? Conclusion)[] runs) =>
        new() { ["check_runs"] = new JsonArray(runs.Select((r, i) => (JsonNode?)new JsonObject { ["name"] = r.Name, ["status"] = r.Status, ["conclusion"] = r.Conclusion, ["started_at"] = $"2026-10-29T11:0{i}:00Z" }).ToArray()) };

    static void Runs(FakeGh gh, params (long Id, string Status, string? Conclusion, int Attempt)[] runs) =>
        gh.Get($"repos/acme/orders/actions/runs?head_sha={Merge}", new JsonObject { ["workflow_runs"] = new JsonArray(runs.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["status"] = r.Status, ["conclusion"] = r.Conclusion, ["run_attempt"] = r.Attempt }).ToArray()) });

    sealed class Kits(FakeGh gh, JsonObject? config = null) : IRuleKitSource
    {
        public Task<T> With<T>(Func<RuleKit, Task<T>> body, CancellationToken ct) => body(VerbFixture.Kit(gh, new FakeStateStore(), config));
    }

    static ItemStepReads Reads(FakeGh gh, JsonObject? config = null) => new(new Kits(gh, config));

    static WorkItemState Waiting(string state = "waiting", DateTimeOffset? since = null, DateTimeOffset? rerunAt = null, params RerunRun[] runs) =>
        new() { Item = "acme/orders#5", MergeCommit = Merge, MasterState = state, MasterSince = since ?? new DateTimeOffset(VerbFixture.Now.AddMinutes(-10)), MasterRerunAt = rerunAt, MasterRerunRuns = [.. runs] };

    [Fact]
    public async Task GreenChecks_AreGreen_AndTheChecksOfTheWorkflowsABuildStartsAreNotJudged()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "success"), ("release", "completed", "failure"), ("architecture", "completed", "failure")));
        var r = await Reads(gh).Read(Waiting(), Ct);
        Assert.Equal((MasterVerdict.Green, Merge), (r.Verdict, r.Via));
        Assert.DoesNotContain(gh.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task PendingChecks_ArePending_WithNothingToTell()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "in_progress", null)));
        var r = await Reads(gh).Read(Waiting(), Ct);
        Assert.Equal(MasterVerdict.Pending, r.Verdict);
        Assert.Null(r.Rerun);
    }

    [Fact]
    public async Task AFailure_IsReRunOnce_AndTheRunsAreTold()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure")));
        Runs(gh, (900, "completed", "failure", 1), (901, "completed", "success", 1));
        gh.Write(HttpMethod.Post, "repos/acme/orders/actions/runs/900/rerun-failed-jobs", status: 201);
        var r = await Reads(gh).Read(Waiting(), Ct);
        Assert.Equal(MasterVerdict.Pending, r.Verdict);
        var run = Assert.Single(r.Rerun!);
        Assert.Equal((900L, 1), (run.Id, run.Attempt));
        Assert.Equal(1, gh.Calls.Count(c => c.Path.EndsWith("rerun-failed-jobs")));
    }

    [Fact]
    public async Task AFailureWaitsWhileAnyRunOfTheCommitIsStillGoing_UnlessTheWaitIsOld()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure")));
        Runs(gh, (900, "completed", "failure", 1), (902, "in_progress", null, 1));
        var r = await Reads(gh).Read(Waiting(), Ct);
        Assert.Equal(MasterVerdict.Pending, r.Verdict);
        Assert.DoesNotContain(gh.Calls, c => c.Path.Contains("rerun-failed-jobs"));
        // a wait past the timeout re-runs what completed rather than waiting for a run that never ends
        gh.Write(HttpMethod.Post, "repos/acme/orders/actions/runs/900/rerun-failed-jobs", status: 201);
        var old = await Reads(gh).Read(Waiting(since: new DateTimeOffset(VerbFixture.Now.AddMinutes(-45))), Ct);
        Assert.Equal(900L, Assert.Single(old.Rerun!).Id);
    }

    [Fact]
    public async Task AReRunThatCouldNotStart_IsRed_AtOnce()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure"), ("unit", "completed", "failure")));
        Runs(gh, (900, "completed", "failure", 1));
        gh.On(HttpMethod.Post, "repos/acme/orders/actions/runs/900/rerun-failed-jobs", _ => new GhResult(403, new JsonObject { ["message"] = "no" }));
        var r = await Reads(gh).Read(Waiting(), Ct);
        Assert.Equal(MasterVerdict.Red, r.Verdict);
        Assert.Equal(["Build result", "unit"], r.Failed);
    }

    [Fact]
    public async Task TheVerdictWaitsForANewerAttemptOfEveryReRun_ThenItIsRed()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure")));
        var state = Waiting(rerunAt: new DateTimeOffset(VerbFixture.Now.AddMinutes(-5)), runs: new RerunRun { Id = 900, Attempt = 1 });
        Runs(gh, (900, "completed", "failure", 1));   // still the old attempt
        Assert.Equal(MasterVerdict.Pending, (await Reads(gh).Read(state, Ct)).Verdict);
        Runs(gh, (900, "in_progress", null, 2));
        Assert.Equal(MasterVerdict.Pending, (await Reads(gh).Read(state, Ct)).Verdict);
        Runs(gh, (900, "completed", "failure", 2));   // the new attempt completed and failed too
        var r = await Reads(gh).Read(state, Ct);
        Assert.Equal(MasterVerdict.Red, r.Verdict);
        Assert.Equal(["Build result"], r.Failed);
        Assert.DoesNotContain(gh.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ABrokenWait_GoesOnWhenALaterCommitThatContainsTheMergeIsGreen()
    {
        var gh = new FakeGh();
        gh.Get(ChecksPath + Merge, CheckRuns(("Build result", "completed", "failure")));
        gh.Get(ChecksPath + Later, CheckRuns(("Build result", "completed", "success")));
        gh.Get("repos/acme/orders/commits?sha=master&per_page=11", new JsonArray(new JsonObject { ["sha"] = Later }, new JsonObject { ["sha"] = Merge }));
        gh.Get($"repos/acme/orders/compare/{Merge}...{Later}?per_page=1", new JsonObject { ["status"] = "ahead" });
        var r = await Reads(gh).Read(Waiting("broken"), Ct);
        Assert.Equal((MasterVerdict.Green, Later), (r.Verdict, r.Via));
        // no green master yet: the wait goes on
        var none = new FakeGh();
        none.Get(ChecksPath + Merge, CheckRuns(("Build result", "in_progress", null)));
        none.Get("repos/acme/orders/commits?sha=master&per_page=11", new JsonArray(new JsonObject { ["sha"] = Merge }));
        Assert.Equal(MasterVerdict.Pending, (await Reads(none).Read(Waiting("timeout"), Ct)).Verdict);
    }

    // ---- the pull request ---------------------------------------------------------------------------------------------------------------------------

    static JsonObject Pr(string state, bool merged, string head, string author) => new() { ["number"] = 11, ["state"] = state, ["merged"] = merged, ["head"] = new JsonObject { ["sha"] = head }, ["user"] = new JsonObject { ["login"] = author } };

    [Fact]
    public async Task AClosedPullRequest_IsClosedWithoutAMerge_OnlyWhenItWasNotMerged()
    {
        var gh = new FakeGh();
        gh.Get("repos/acme/orders/pulls/11", Pr("closed", false, VerbFixture.Sha('a'), "bot"));
        gh.Get("repos/acme/orders/pulls/12", Pr("closed", true, VerbFixture.Sha('a'), "bot"));
        gh.Get("repos/acme/orders/pulls/13", Pr("open", false, VerbFixture.Sha('a'), "bot"));
        var reads = Reads(gh);
        Assert.True((await reads.State(11, Ct))!.ClosedWithoutMerge);
        Assert.False((await reads.State(12, Ct))!.ClosedWithoutMerge);
        Assert.False((await reads.State(13, Ct))!.ClosedWithoutMerge);
        Assert.Null(await reads.State(99, Ct));
    }

    [Fact]
    public async Task AnApproval_IsAHumansLatestReviewOfTheHead_NotTheAuthorsOrAnotherHeads()
    {
        var head = VerbFixture.Sha('a');
        JsonObject Review(string login, string state, string commit) => new() { ["user"] = new JsonObject { ["login"] = login }, ["state"] = state, ["commit_id"] = commit };
        var gh = new FakeGh();
        gh.Get("repos/acme/orders/pulls/11", Pr("open", false, head, "alice"));
        gh.Get("repos/acme/orders/pulls/11/reviews", new JsonArray(Review("alice", "APPROVED", head), Review("bob", "APPROVED", head), Review("carol", "APPROVED", VerbFixture.Sha('b'))));
        var config = VerbFixture.Config(c => c["humanApprovers"] = new JsonArray("alice", "carol", "dave"));
        // alice wrote the pull request, bob is no approver, carol approved another head
        Assert.Null(await Reads(gh, config).Approver(11, head, Ct));
        gh.Get("repos/acme/orders/pulls/11/reviews", new JsonArray(Review("dave", "APPROVED", head), Review("dave", "CHANGES_REQUESTED", head)));
        Assert.Null(await Reads(gh, config).Approver(11, head, Ct));   // (the latest review of a person counts)
        gh.Get("repos/acme/orders/pulls/11/reviews", new JsonArray(Review("dave", "CHANGES_REQUESTED", head), Review("DAVE".ToLowerInvariant(), "APPROVED", head)));
        Assert.Equal("dave", await Reads(gh, config).Approver(11, head, Ct));
        // nobody is named: nobody can approve
        Assert.Null(await Reads(gh).Approver(11, head, Ct));
    }
}

public class ItemStepsJobTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Commit = "abcdef0123456789abcdef0123456789abcdef01";

    sealed class FakeMaster : IMasterChecks
    {
        public MasterReading Reading = new(MasterVerdict.Pending, []);
        public int Reads;
        public bool Fail;
        public Task<MasterReading> Read(WorkItemState state, CancellationToken ct) { Reads++; return Fail ? throw new InvalidOperationException("GitHub is down") : Task.FromResult(Reading); }
    }

    sealed class FakePulls : IItemPullRequests
    {
        public PullRequestFacts? Facts;
        public string? By;
        public int Reads;
        public Task<PullRequestFacts?> State(int pr, CancellationToken ct) { Reads++; return Task.FromResult(Facts); }
        public Task<string?> Approver(int pr, string head, CancellationToken ct) => Task.FromResult(By);
    }

    static async Task<(SagaFixture Fx, FakeMaster Master, FakePulls Pulls, List<WorkItemInput> Sent, ItemStepsJob Job, Func<bool> Leader)> Make(StepKind step, bool masterWatch = true)
    {
        var fx = new SagaFixture(WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false), Sweep = SweepRules.Default });
        await fx.Start(); await fx.Review(); await fx.Done(); await fx.PullRequest(Head, 11, "src/a.cs"); await fx.Review(); await fx.Done();
        if (step >= StepKind.Merge) await fx.Verdict(true, Head);
        if (step == StepKind.Deliver) await fx.Merged(Commit);
        var master = new FakeMaster(); var pulls = new FakePulls(); var sent = new List<WorkItemInput>();
        var leader = true;
        var job = new ItemStepsJob(fx.Store, Item.Repository, master, pulls, async m => { sent.Add(m); await fx.Send(m); return true; }, () => leader, () => false, masterWatch, () => fx.Now);
        return (fx, master, pulls, sent, job, () => leader);
    }

    [Fact]
    public async Task AGreenMaster_IsToldOnce_AndTheSagaMovesOn()
    {
        var (fx, master, _, sent, job, _) = await Make(StepKind.Deliver);
        master.Reading = new MasterReading(MasterVerdict.Green, [], Commit);
        Assert.Equal("1 read, 0 to re-arm, 1 told", await job.Run(default));
        var seen = Assert.IsType<MasterObserved>(Assert.Single(sent));
        Assert.Equal((MasterVerdict.Green, Commit), (seen.Verdict, seen.Commit));
        Assert.Equal("green", fx.State.MasterState);
        // the item no longer waits for master: it is not read again
        await job.Run(default);
        Assert.Equal(1, master.Reads);
    }

    [Fact]
    public async Task APendingMaster_IsNotTold_ButAReRunIs()
    {
        var (fx, master, _, sent, job, _) = await Make(StepKind.Deliver);
        Assert.Contains("0 told", await job.Run(default));
        Assert.Empty(sent);
        master.Reading = new MasterReading(MasterVerdict.Pending, [], Rerun: [new RerunRun { Id = 900, Attempt = 1 }]);
        await job.Run(default);
        Assert.Equal(MasterVerdict.Pending, Assert.IsType<MasterObserved>(Assert.Single(sent)).Verdict);
        Assert.Equal([900L], fx.State.MasterRerunRuns.Select(r => r.Id));
    }

    [Fact]
    public async Task ARedMaster_ParksTheItem_AndAGreenOneLaterLiftsThePark()
    {
        var (fx, master, _, sent, job, _) = await Make(StepKind.Deliver);
        master.Reading = new MasterReading(MasterVerdict.Red, ["unit"]);
        await job.Run(default);
        Assert.True(fx.State.Parked);
        Assert.StartsWith("master-broken:", fx.State.ParkReason);
        // the park is watched on (a parked wait is read for a green master only): nothing is told while master is not green, a green one is
        master.Reading = new MasterReading(MasterVerdict.Pending, []);
        await job.Run(default);
        Assert.Single(sent);
        master.Reading = new MasterReading(MasterVerdict.Green, [], Commit);
        await job.Run(default);
        Assert.False(fx.State.Parked);
        Assert.Equal("green", fx.State.MasterState);
    }

    [Fact]
    public async Task AReadThatFails_IsTriedAgain_NotToldAsAState()
    {
        var (fx, master, _, sent, job, _) = await Make(StepKind.Deliver);
        master.Fail = true;
        await job.Run(default);
        Assert.Empty(sent);
        Assert.Equal("waiting", fx.State.MasterState);
        master.Fail = false; master.Reading = new MasterReading(MasterVerdict.Green, [], Commit);
        await job.Run(default);
        Assert.Equal("green", fx.State.MasterState);
    }

    [Fact]
    public async Task AMergedItemWithNoWatch_IsToldToRearm_AtOnce()
    {
        var (fx, _, _, sent, job, _) = await Make(StepKind.Deliver);
        var rec = (await fx.Store.Load(fx.Key, default))!; rec.State.MasterState = ""; rec.State.MasterDue = null; await fx.Store.Save(fx.Key, rec.State, rec.Revision, default);
        Assert.Equal("0 read, 1 to re-arm, 1 told", await job.Run(default));
        Assert.IsType<RearmSettle>(Assert.Single(sent));
        Assert.Equal("waiting", fx.State.MasterState);
    }

    [Fact]
    public async Task APassWhosePullRequestWasClosed_IsToldClosed()
    {
        var (fx, _, pulls, sent, job, _) = await Make(StepKind.Merge);
        pulls.Facts = new PullRequestFacts(11, "open", false, Head, "bot");
        await job.Run(default);
        Assert.Empty(sent);
        pulls.Facts = new PullRequestFacts(11, "closed", false, Head, "bot");
        await job.Run(default);
        Assert.IsType<PullRequestClosed>(Assert.Single(sent));
        Assert.Equal(StepKind.Develop, fx.State.Step);
    }

    [Fact]
    public async Task AnInspectorsHold_IsClearedWhenAPersonApprovedTheHead()
    {
        var (fx, _, pulls, sent, job, _) = await Make(StepKind.Merge);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "inspector-escalated: needs a person"));
        pulls.Facts = new PullRequestFacts(11, "open", false, Head, "bot");
        await job.Run(default);
        Assert.Empty(sent);
        pulls.By = "alice";
        await job.Run(default);
        var approval = Assert.IsType<ApprovalSeen>(Assert.Single(sent));
        Assert.Equal(("alice", Head), (approval.By, approval.Head));
        Assert.False(fx.State.Parked);
    }

    [Fact]
    public async Task ThePullRequestOfAMergedPass_OrOfAParkThatIsNotTheInspectors_IsLeftAlone()
    {
        var (fx, _, pulls, sent, job, _) = await Make(StepKind.Merge);
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "merge-blocked: something"));
        pulls.Facts = new PullRequestFacts(11, "open", false, Head, "bot"); pulls.By = "alice";
        await job.Run(default);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task ADocsOnlyEngine_WatchesNoMaster_AndOnlyTheLeaderReads()
    {
        var (fx, master, _, _, job, _) = await Make(StepKind.Deliver, masterWatch: false);
        Assert.Equal("none", await job.Run(default));
        Assert.Equal(0, master.Reads);
        var follower = new ItemStepsJob(fx.Store, Item.Repository, master, new FakePulls(), _ => Task.FromResult(true), () => false, () => false);
        Assert.Equal("not-leader", await follower.Run(default));
        Assert.Equal(0, master.Reads);
    }

    [Fact]
    public async Task BelowTheGitHubReserve_NothingIsRead()
    {
        var (fx, master, _, _, _, _) = await Make(StepKind.Deliver);
        var low = new ItemStepsJob(fx.Store, Item.Repository, master, new FakePulls(), _ => Task.FromResult(true), () => true, () => true);
        Assert.Equal("budget-low", await low.Run(default));
        Assert.Equal(0, master.Reads);
    }
}
