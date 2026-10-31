using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10i: the state comment follows the saga, in the format v1 reads (the rollback path of FACTORY_V2_OFF). The edit is a pure function of the decision and the saga's state (V1StateFormat); the projection reads the saga's
/// state, applies the edit through the versioned StateCommentProjection and writes nothing when the comment says it already. The tests play the transitions that no verb of v1 writes: the parks of the saga, the lift of a park, the
/// merge the gateway makes, the settle's states and the end of the settle, the demotion; and what v1's own writes must keep (a park of the same cause, a merge record of the same commit).
/// </summary>
public sealed class StateProjectionTests
{
    static readonly RepoId Repo = new("o", "r");
    static readonly ItemId Item = new(Repo, 7);
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    static JsonNode Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json")))!;
        cfg["intake"]!["v2Plans"] = new JsonArray("docs", "feature");
        cfg["delivery"]!["finalEnvironment"] = "tdd";
        return cfg;
    }

    static readonly JsonNode Config = Source();
    internal static readonly DocsPlanSettings Plan = DocsPlanSettingsReader.From(Config);
    internal static readonly V1StateSettings Settings = V1StateSettings.From(Config, Plan);

    static WorkItemState Saga(string plan, StepKind step, Action<WorkItemState>? edit = null)
    {
        var s = new WorkItemState { Item = Item.ToString(), Generation = 1, Plan = plan, Step = step, StartedAt = T0.AddHours(-1), PullRequest = 2, Head = "h1", VerdictHead = "h1" };
        edit?.Invoke(s);
        return s;
    }

    static WorkItemDecided Decided(string action, WorkItemState saga, long sequence = 5, DateTimeOffset? at = null, params string[] reasons) =>
        new(Item, saga.Generation, sequence, action, saga.Plan, saga.Status == WorkItemStatus.Completed ? null : saga.Step.ToString(), saga.ArmId.Length == 0 ? null : saga.ArmId, reasons, at ?? T0);

    /// <summary>What a PASS leaves in the state comment (the verbs of v1 write it before the saga hears of the verdict).</summary>
    static JsonObject V1Pass() => new()
    {
        ["schema"] = 1, ["item"] = 7, ["rev"] = 3,
        ["claim"] = new JsonObject { ["owner"] = true, ["holder"] = "w1", ["column"] = "Functional Testing" },
        ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = 2, ["head_sha"] = "h1", ["processed"] = false },
        ["flow"] = new JsonArray(new JsonObject { ["c"] = "Technical Design", ["at"] = "2026-10-30T10:00:00.000Z" }, new JsonObject { ["c"] = "Functional Testing", ["at"] = "2026-10-30T11:00:00.000Z" }),
    };

    static JsonObject Applied(JsonObject state, WorkItemDecided d, WorkItemState saga)
    {
        var copy = (JsonObject)state.DeepClone();
        V1StateFormat.Apply(copy, d, saga, Plan, Settings);
        return copy;
    }

    static string Iso(DateTimeOffset at) => JournalJson.Iso(at.UtcDateTime);

    // ---- which decisions -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheDecisionsThatChangeWhatV1Reads_AreProjected()
    {
        foreach (var action in new[] { WorkItemActions.StepEntered, WorkItemActions.Parked, WorkItemActions.Resumed, WorkItemActions.ParkExpired, WorkItemActions.HoldCleared, WorkItemActions.MovedBack,
                     WorkItemActions.Merged, WorkItemActions.Completed, WorkItemActions.HandedBack, WorkItemActions.Settling, WorkItemActions.Undelivered, WorkItemActions.MasterGreen,
                     WorkItemActions.MasterBroken, WorkItemActions.MasterTimeout, WorkItemActions.SettleRearmed, WorkItemActions.Demoted, WorkItemActions.FixForward, WorkItemActions.ChecksGreen })
            Assert.True(V1StateFormat.Wants(action), action);
        // the verbs of v1 write these themselves, or they concern the saga alone
        foreach (var action in new[] { WorkItemActions.Started, WorkItemActions.Reviewed, WorkItemActions.Verdict, WorkItemActions.MergeRequested, WorkItemActions.MergeBlocked, WorkItemActions.SlotGranted,
                     WorkItemActions.LeaseExpired, WorkItemActions.SessionLost, WorkItemActions.OwnerReleased, WorkItemActions.ChecksRed, WorkItemActions.Held, WorkItemActions.PlanSwitched })
            Assert.False(V1StateFormat.Wants(action), action);
    }

    // ---- parks -----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMergeBlockedPark_IsV1sPark_WithTheCodeAndTheEnd_AndTheVerdictIsProcessed()
    {
        var until = T0.AddHours(24);
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: 60 merge calls without a merge (no-auto-merge-label)"; s.ParkUntil = until; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.Parked, saga), saga);
        Assert.Equal(("merge-blocked", Iso(T0), Iso(until)), (after["park"]!["reason"]!.ToString(), after["park"]!["at"]!.ToString(), after["park"]!["until"]!.ToString()));
        Assert.True((bool)after["verdict"]!["processed"]!);   // nothing merges the PASS again
        Assert.True((bool)after["claim"]!["owner"]!);          // the claim is the plant's, not this projection's
    }

    [Fact]
    public void AParkWithNoEndOfItsOwn_LastsTheParkHoursOfTheConfiguration()
    {
        var saga = Saga("feature", StepKind.Develop, s => { s.Parked = true; s.ParkReason = "launches-exhausted: 4 executions in a row"; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.Parked, saga), saga);
        Assert.Equal(Iso(T0.AddHours(24)), after["park"]!["until"]!.ToString());
    }

    [Fact]
    public void AParkV1sVerbWrote_ForTheSameCause_IsLeftAsItIs_EvenWhenTheSagasReasonHasMoreText()
    {
        var state = V1Pass();
        state["park"] = new JsonObject { ["reason"] = "worker-failure", ["at"] = "2026-10-30T11:30:00.000Z", ["until"] = "2026-10-31T11:30:00.000Z", ["launches"] = 3 };
        var saga = Saga("feature", StepKind.Develop, s => { s.Parked = true; s.ParkReason = "worker-failure: 3 failures in Develop"; s.ParkUntil = T0.AddHours(24); });
        var after = Applied(state, Decided(WorkItemActions.Parked, saga), saga);
        Assert.Equal(state.ToJsonString(), after.ToJsonString());
        // a reason with a colon of its own (v1's "fv:release-candidate-timeout") is the same park when the saga names it whole
        state["park"] = new JsonObject { ["reason"] = "fv:release-candidate-timeout", ["until"] = "2026-10-31T11:30:00.000Z" };
        saga = Saga("feature", StepKind.Deliver, s => { s.Parked = true; s.ParkReason = "fv:release-candidate-timeout"; });
        Assert.Equal(state.ToJsonString(), Applied(state, Decided(WorkItemActions.Parked, saga), saga).ToJsonString());
        // another cause is the saga's word, which is the later one
        saga = Saga("feature", StepKind.Develop, s => { s.Parked = true; s.ParkReason = "followups-idle: 3 follow-up sessions"; s.ParkUntil = T0.AddHours(24); });
        Assert.Equal("followups-idle", Applied(state, Decided(WorkItemActions.Parked, saga), saga)["park"]!["reason"]!.ToString());
    }

    [Fact]
    public void AFixForwardExhaustedPark_NamesThePullRequestAndTheHeadItParkedOn_AndKeepsTheRounds()
    {
        var saga = Saga("feature", StepKind.Verify, s => { s.Parked = true; s.ParkReason = "fix-forward-exhausted: CI stayed red after 3 fix-forward rounds (build)"; s.FixForwardRounds = 3; s.Demotions = 1; });
        var state = V1Pass(); state["fix_forward_rounds"] = 4;   // (v1's counter is one past the saga's when the sweep parked; it is never lowered)
        var after = Applied(state, Decided(WorkItemActions.Parked, saga), saga);
        Assert.Equal(("fix-forward-exhausted", 2, "h1"), (after["park"]!["reason"]!.ToString(), (int)after["park"]!["pr"]!, after["park"]!["head"]!.ToString()));
        Assert.Equal((4, 1), ((int)after["fix_forward_rounds"]!, (int)after["demotions"]!));
    }

    [Fact]
    public void AHeldItem_IsParkedWithTheCodeHold()
    {
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = SagaHoldPrefix + "hold:owner"; });
        Assert.Equal("hold", Applied(V1Pass(), Decided(WorkItemActions.Parked, saga), saga)["park"]!["reason"]!.ToString());
    }

    const string SagaHoldPrefix = WorkItemSaga.HoldParkPrefix;

    [Theory]
    [InlineData(WorkItemActions.Resumed)]
    [InlineData(WorkItemActions.ParkExpired)]
    [InlineData(WorkItemActions.HoldCleared)]
    [InlineData(WorkItemActions.HandedBack)]
    public void TheLiftOfAPark_TakesItOutOfTheStateComment_AndAResumeOrAnExpiryStartsTheCountsFromZero(string action)
    {
        var state = V1Pass();
        state["park"] = new JsonObject { ["reason"] = "merge-blocked", ["until"] = "2026-10-31T11:30:00.000Z" };
        state["fix_forward_rounds"] = 2; state["demotions"] = 1; state["master_defect"] = new JsonObject { ["sha"] = "x" };
        var saga = Saga("feature", StepKind.Merge);
        var after = Applied(state, Decided(action, saga), saga);
        Assert.Null(after["park"]);
        var reset = action is WorkItemActions.Resumed or WorkItemActions.ParkExpired;
        Assert.Equal(reset, after["fix_forward_rounds"] is null && after["demotions"] is null && after["master_defect"] is null);
    }

    [Fact]
    public void ASagaThatIsStillParked_IsNotUnparkedByALateDecision()
    {
        var state = V1Pass();
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: x"; s.ParkUntil = T0.AddHours(24); });
        var after = Applied(state, Decided(WorkItemActions.Resumed, saga, 4), saga);   // the resume of an earlier park, handled after the park that followed it
        Assert.Equal("merge-blocked", after["park"]!["reason"]!.ToString());
    }

    // ---- the merge -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADocsItemTheGatewayMerged_HasV1sMergeRecord_Done_AndAProcessedPass()
    {
        var done = T0.AddSeconds(2);
        var saga = Saga("docs", StepKind.Merge, s => { s.Status = WorkItemStatus.Completed; s.MergeCommit = "abc123"; s.CompletedAt = done; });
        var state = V1Pass(); state["awaiting_checks"] = new JsonObject { ["pr"] = 2 }; state["handoff"] = new JsonObject { ["column"] = "Development" };
        var after = Applied(state, Decided(WorkItemActions.Merged, saga), saga);
        var merge = after["merge"]!.AsObject();
        Assert.Equal(("abc123", 2, Iso(T0), Iso(done)), (merge["sha"]!.ToString(), (int)merge["pr"]!, merge["merged_at"]!.ToString(), merge["done_at"]!.ToString()));
        Assert.True(merge["awaiting"] is null || merge["awaiting"]!.ToString() == "");   // nothing is awaited: v1's settle has nothing to do
        Assert.True((bool)after["verdict"]!["processed"]!);
        Assert.Null(after["awaiting_checks"]); Assert.Null(after["handoff"]);
        Assert.Null(after["park"]);
    }

    [Fact]
    public void ACodeItemTheGatewayMerged_WaitsForMastersCi_AsV1sMergeVerbLeavesIt()
    {
        var saga = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "waiting"; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.Merged, saga), saga);
        Assert.Equal(("abc123", "master-green", Iso(T0)), (after["merge"]!["sha"]!.ToString(), after["merge"]!["awaiting"]!.ToString(), after["merge"]!["merged_at"]!.ToString()));
        Assert.Null(after["merge"]!["done_at"]);
        Assert.True((bool)after["verdict"]!["processed"]!);
    }

    [Fact]
    public void AMergeRecordV1Wrote_ForTheSameCommit_IsCompleted_NotReplaced()
    {
        var state = V1Pass();
        state["merge"] = new JsonObject { ["sha"] = "ABC123", ["awaiting"] = "master-green", ["merged_at"] = "2026-10-30T11:59:00.000Z", ["pr"] = 2, ["by"] = "person" };
        var saga = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "waiting"; });
        var after = Applied(state, Decided(WorkItemActions.Merged, saga), saga);
        Assert.Equal("2026-10-30T11:59:00.000Z", after["merge"]!["merged_at"]!.ToString());
        Assert.Equal("person", after["merge"]!["by"]!.ToString());
        Assert.Equal("master-green", after["merge"]!["awaiting"]!.ToString());
    }

    [Fact]
    public void TheMastersStates_AreV1sSettleStates()
    {
        var rerun = T0.AddMinutes(-5);
        string Awaiting(Action<WorkItemState> edit, string action, JsonObject? state = null)
        {
            var saga = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; edit(s); });
            return Applied(state ?? V1Pass(), Decided(action, saga), saga)["merge"]!["awaiting"]!.ToString();
        }
        Assert.Equal("master-green", Awaiting(s => s.MasterState = "waiting", WorkItemActions.SettleRearmed));
        Assert.Equal("master-green", Awaiting(s => s.MasterState = "", WorkItemActions.SettleRearmed));
        Assert.Equal("master-broken", Awaiting(s => { s.MasterState = "broken"; s.MasterFailed = ["build"]; s.Parked = true; s.ParkReason = "master-broken: master is BROKEN at abc1234 (build)"; }, WorkItemActions.MasterBroken));
        Assert.Equal("master-timeout", Awaiting(s => { s.MasterState = "timeout"; s.Parked = true; s.ParkReason = "master-green-timeout: master CI did not go green"; }, WorkItemActions.MasterTimeout));
        Assert.Equal("release-candidate", Awaiting(s => s.MasterState = "green", WorkItemActions.MasterGreen));
        // the park of a broken master carries what v1's auto-unpark judges it by
        var broken = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "broken"; s.MasterFailed = ["build", "acceptance"]; s.Parked = true; s.ParkReason = "master-broken: x"; s.MasterRerunAt = rerun; s.MasterRerunRuns = [new RerunRun { Id = 99, Attempt = 2 }]; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.MasterBroken, broken), broken);
        Assert.Equal(("build,acceptance", "abc123"), (string.Join(",", after["park"]!["failed"]!.AsArray().Select(x => x!.ToString())), after["park"]!["sha"]!.ToString()));
        Assert.Equal((true, Iso(rerun), 99L, 2), ((bool)after["merge"]!["rerun_attempted"]!, after["merge"]!["rerun_at"]!.ToString(), (long)after["merge"]!["rerun_runs"]![0]!["id"]!, (int)after["merge"]!["rerun_runs"]![0]!["attempt"]!));
    }

    [Fact]
    public void AGreenMaster_IsOnlyGivenThePointV1WouldHaveReached_AndV1sOwnProgressStays()
    {
        var state = V1Pass(); state["verdict"]!["processed"] = true;
        state["merge"] = new JsonObject { ["sha"] = "abc123", ["awaiting"] = "release-queue", ["release"] = "1.0.2", ["queued_at"] = "2026-10-30T11:00:00.000Z", ["merged_at"] = "2026-10-30T10:00:00.000Z", ["pr"] = 2 };
        var saga = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "green"; });
        Assert.Equal(state.ToJsonString(), Applied(state, Decided(WorkItemActions.MasterGreen, saga), saga).ToJsonString());
        var viaLater = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "green"; s.MasterGreenVia = "def456"; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.MasterGreen, viaLater), viaLater)["merge"]!;
        Assert.Equal(("release-candidate", "def456", Iso(T0), Iso(T0.AddMinutes(20))), (after["awaiting"]!.ToString(), after["green_via"]!.ToString(), after["green_at"]!.ToString(), after["rc_deadline"]!.ToString()));
    }

    [Fact]
    public void ADeliveredItemInItsSettleWindow_AwaitsTheReleaseQueue_AndAnUndeliveredOneIsNotDone()
    {
        var settling = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.Status = WorkItemStatus.Settling; s.MasterState = "green"; s.Delivered["tdd"] = "1.0.7"; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.Settling, settling), settling)["merge"]!;
        Assert.Equal(("release-queue", "1.0.7"), (after["awaiting"]!.ToString(), after["release"]!.ToString()));
        var done = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.Status = WorkItemStatus.Completed; s.CompletedAt = T0.AddMinutes(30); s.MasterState = "green"; });
        var finished = Applied(Applied(V1Pass(), Decided(WorkItemActions.Settling, settling), settling), Decided(WorkItemActions.Completed, done, 9), done);
        Assert.Equal((true, Iso(T0.AddMinutes(30))), (finished["merge"]!["awaiting"] is null, finished["merge"]!["done_at"]!.ToString()));
        // delivered, and then it was lost inside the window: the saga is open again and so is the settle
        var undelivered = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "green"; });
        var back = Applied(finished, Decided(WorkItemActions.Undelivered, undelivered, 10), undelivered)["merge"]!;
        Assert.Null(back["done_at"]);
        Assert.Equal("release-candidate", back["awaiting"]!.ToString());
    }

    [Fact]
    public void AnItemWithNoMerge_GetsNoMergeRecord_AndAClosedOneIsNotMadeDone()
    {
        var saga = Saga("feature", StepKind.Design, s => { s.Status = WorkItemStatus.Completed; s.CompletedAt = T0; });
        var after = Applied(V1Pass(), Decided(WorkItemActions.Completed, saga), saga);
        Assert.Null(after["merge"]);
        Assert.False((bool)after["verdict"]!["processed"]!);
    }

    // ---- the step and the demotion ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADemotion_VoidsThePass_CountsTheDemotion_AndMovesTheOwnersClaimWithTheItem()
    {
        var saga = Saga("feature", StepKind.Develop, s => { s.Demotions = 1; s.VerdictHead = ""; s.ArmId = "1.4"; });
        var state = V1Pass(); state["bot"] = new JsonObject { ["lease_until"] = "2026-10-30T12:30:00.000Z" }; state["awaiting_checks"] = new JsonObject { ["pr"] = 2 };
        var demoted = Applied(state, Decided(WorkItemActions.Demoted, saga, 7), saga);
        Assert.Null(demoted["verdict"]); Assert.Null(demoted["bot"]);
        Assert.Equal(1, (int)demoted["demotions"]!);
        var entered = Applied(demoted, Decided(WorkItemActions.StepEntered, saga, 8), saga);
        Assert.Equal("Development", entered["claim"]!["column"]!.ToString());
        Assert.Equal("Development", entered["flow"]!.AsArray().Last()!["c"]!.ToString());
        Assert.Equal(3, entered["flow"]!.AsArray().Count);
        Assert.Null(entered["awaiting_checks"]);
    }

    [Fact]
    public void TheFlowStamp_IsNotRepeatedForAStageTheSessionStamped_AndKeepsTheLast15()
    {
        var saga = Saga("feature", StepKind.Verify, s => s.ArmId = "1.3");
        var state = V1Pass();   // (the last stamp is Functional Testing, written by the verb of the session)
        Assert.Equal(state["flow"]!.ToJsonString(), Applied(state, Decided(WorkItemActions.StepEntered, saga), saga)["flow"]!.ToJsonString());
        var long15 = new JsonArray(); for (var i = 0; i < 15; i++) long15.Add(new JsonObject { ["c"] = i % 2 == 0 ? "Development" : "Technical Design", ["at"] = $"2026-10-30T0{i % 10}:00:00.000Z" });
        state["flow"] = long15;
        var after = Applied(state, Decided(WorkItemActions.StepEntered, saga), saga)["flow"]!.AsArray();
        Assert.Equal(15, after.Count);
        Assert.Equal("Functional Testing", after.Last()!["c"]!.ToString());
    }

    [Fact]
    public void AClaimThatIsNotTheOwners_OrAStepTheItemHasLeft_IsNotMovedByAStaleDecision()
    {
        var saga = Saga("feature", StepKind.Merge, s => s.ArmId = "1.5");
        var stale = new WorkItemDecided(Item, 1, 6, WorkItemActions.StepEntered, "feature", nameof(StepKind.Develop), "1.4", [], T0);   // (the item has moved on to Merge since)
        var state = V1Pass();
        Assert.Equal("Functional Testing", Applied(state, stale, saga)["claim"]!["column"]!.ToString());
        state["claim"]!["owner"] = false;
        var now = Saga("feature", StepKind.Develop, s => s.ArmId = "1.4");
        Assert.Equal("Functional Testing", Applied(state, Decided(WorkItemActions.StepEntered, now), now)["claim"]!["column"]!.ToString());
    }

    // ---- the whole path, idempotent --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameDecisionAppliedTwice_ChangesNothingTheSecondTime()
    {
        var saga = Saga("feature", StepKind.Deliver, s => { s.MergeCommit = "abc123"; s.MasterState = "broken"; s.MasterFailed = ["build"]; s.Parked = true; s.ParkReason = "master-broken: x"; s.ParkUntil = T0.AddHours(24); s.FixForwardRounds = 1; });
        foreach (var action in new[] { WorkItemActions.Parked, WorkItemActions.MasterBroken, WorkItemActions.Merged, WorkItemActions.StepEntered, WorkItemActions.FixForward })
        {
            var d = Decided(action, saga);
            var once = Applied(V1Pass(), d, saga);
            Assert.Equal(once.ToJsonString(), Applied(once, d, saga).ToJsonString());
        }
    }

    // ---- the projection: through the versioned comment projection ---------------------------------------------------------------------------------------

    internal sealed class MemoryJournal : IJournalStore
    {
        public JsonObject State = new() { ["schema"] = 1, ["item"] = 7 };
        public long? CommentId; public int Saves; public int Reads;
        public Func<Exception?>? Fault;

        public Task<ItemState> Read(int item, CancellationToken ct)
        {
            Reads++;
            if (Fault?.Invoke() is { } fault) throw fault;
            return Task.FromResult(new ItemState { CommentId = CommentId, State = (JsonObject)State.DeepClone(), Rev = CommentId is null ? null : ItemStateComment.RevOf(State) });
        }

        public Task Save(int item, ItemState holder, CancellationToken ct)
        {
            Saves++;
            State = (JsonObject)holder.State.DeepClone();
            State["rev"] = (holder.Rev ?? 0) + 1; CommentId ??= 1000;
            return Task.CompletedTask;
        }

        public Task<long?> Post(int item, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class Rig
    {
        public readonly MemoryJournal Journal = new();
        public readonly InMemoryWorkItemStore Store = new();
        public readonly InMemoryProjectionVersions Versions = new();
        public readonly List<TimeSpan> Pauses = [];
        public readonly SagaStateProjection Projection;

        public Rig(bool hasComment = true)
        {
            if (hasComment) { Journal.CommentId = 1000; foreach (var kv in V1Pass()) Journal.State[kv.Key] = kv.Value?.DeepClone(); }
            var comment = new StateCommentProjection(Journal, Versions, new ProjectionEcho(Versions));
            Projection = new SagaStateProjection(() => comment, Store, Plan, Settings, (d, _) => { Pauses.Add(d); return Task.CompletedTask; });
        }

        public async Task Put(WorkItemState saga) { var rec = await Store.Load(saga.Item, default); await Store.Save(saga.Item, saga, rec?.Revision ?? 0, default); }
    }

    [Fact]
    public async Task ADecisionIsProjectedFromTheSagasState_AWriteOnce_AndAnOlderOrRepeatedDecisionWritesNothing()
    {
        var r = new Rig();
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: x"; s.ParkUntil = T0.AddHours(24); s.Sequence = 6; });
        await r.Put(saga);
        var parked = Decided(WorkItemActions.Parked, saga, 6);
        await r.Projection.Project(parked, default);
        Assert.Equal((1, "merge-blocked"), (r.Journal.Saves, r.Journal.State["park"]!["reason"]!.ToString()));
        Assert.Equal(6, await r.Versions.Applied(Item.ToString(), ProjectionTargets.Comment));
        // handled again (a redelivery) and an older decision handled late: nothing is read or written
        r.Journal.Reads = 0;
        await r.Projection.Project(parked, default);
        await r.Projection.Project(Decided(WorkItemActions.Resumed, saga, 5), default);
        Assert.Equal((1, 0), (r.Journal.Saves, r.Journal.Reads));
    }

    [Fact]
    public async Task ADecisionThatLeavesTheCommentAsItIs_WritesNothing_AndKeepsV1sRevision()
    {
        var r = new Rig();
        var saga = Saga("feature", StepKind.Verify, s => { s.Sequence = 3; s.ArmId = "1.3"; });
        await r.Put(saga);
        await r.Projection.Project(Decided(WorkItemActions.StepEntered, saga, 3), default);   // Functional Testing is stamped and the claim is there already
        Assert.Equal(0, r.Journal.Saves);
        Assert.Equal(3, ItemStateComment.RevOf(r.Journal.State));
    }

    [Fact]
    public async Task ADecisionThatIsNotTheProjections_ReadsNothing()
    {
        var r = new Rig();
        var saga = Saga("feature", StepKind.Verify, s => s.Sequence = 3);
        await r.Put(saga);
        await r.Projection.Project(Decided(WorkItemActions.Verdict, saga, 3), default);
        Assert.Equal((0, 0), (r.Journal.Reads, r.Journal.Saves));
    }

    [Fact]
    public async Task ADecisionOfAnEarlierGeneration_OrOfAnItemWithNoSaga_IsLeftToItsOwnDecisions()
    {
        var r = new Rig();
        await r.Projection.Project(Decided(WorkItemActions.Parked, Saga("feature", StepKind.Merge)), default);   // no saga
        var reopened = Saga("feature", StepKind.Design, s => { s.Generation = 2; s.Sequence = 9; });
        await r.Put(reopened);
        var old = new WorkItemDecided(Item, 1, 4, WorkItemActions.Parked, "feature", "Merge", null, ["merge-blocked"], T0);
        await r.Projection.Project(old, default);
        Assert.Equal(0, r.Journal.Saves);
    }

    [Fact]
    public async Task AnItemWithNoStateComment_GetsOneWhenTheSagaHasSomethingV1MustRead_AndNoneWhenItHasNot()
    {
        var r = new Rig(hasComment: false);
        var saga = Saga("feature", StepKind.Merge, s => s.Sequence = 1);
        await r.Put(saga);
        await r.Projection.Project(Decided(WorkItemActions.Resumed, saga, 1), default);   // nothing parked, nothing to say: no comment is made for it
        Assert.Equal(0, r.Journal.Saves);
        saga.Parked = true; saga.ParkReason = "merge-blocked: x"; saga.Sequence = 2;
        await r.Put(saga);
        await r.Projection.Project(Decided(WorkItemActions.Parked, saga, 2), default);
        Assert.Equal(1, r.Journal.Saves);
        Assert.Equal("merge-blocked", r.Journal.State["park"]!["reason"]!.ToString());
    }

    [Fact]
    public async Task AFaultIsTriedAgain_AndOneThatOutlastsTheTriesIsThrown_AndNothingIsRecordedAsApplied()
    {
        var r = new Rig();
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: x"; s.Sequence = 2; });
        await r.Put(saga);
        var faults = 2;
        r.Journal.Fault = () => faults-- > 0 ? new HttpRequestException("503") : null;
        await r.Projection.Project(Decided(WorkItemActions.Parked, saga, 2), default);   // the third try goes through
        Assert.Equal(2, r.Pauses.Count);
        Assert.Equal(1, r.Journal.Saves);
        var down = new Rig();
        await down.Put(saga);
        down.Journal.Fault = () => new HttpRequestException("503");
        var thrown = await Assert.ThrowsAsync<StateProjectionRetryException>(() => down.Projection.Project(Decided(WorkItemActions.Parked, saga, 2), default));   // not given up: the handler's policy schedules it again
        Assert.IsType<HttpRequestException>(thrown.InnerException);
        Assert.Equal(3, down.Pauses.Count);
        Assert.Equal(0, await down.Versions.Applied(Item.ToString(), ProjectionTargets.Comment));
    }

    [Fact]
    public async Task AFaultThatOutlastsTheTries_IsProjectedLater_WithNoNewDecision()
    {
        // a docs item the gateway merged: its last decision is the merge; GitHub answers 5xx for longer than the tries and then recovers. No other decision comes.
        var r = new Rig();
        var saga = Saga("docs", StepKind.Merge, s => { s.Status = WorkItemStatus.Completed; s.MergeCommit = "abc123"; s.CompletedAt = T0.AddSeconds(2); s.Sequence = 4; });
        await r.Put(saga);
        var merged = Decided(WorkItemActions.Merged, saga, 4);
        var down = true;
        r.Journal.Fault = () => down ? new HttpRequestException("503") : null;
        await Assert.ThrowsAsync<StateProjectionRetryException>(() => r.Projection.Project(merged, default));
        Assert.Null(r.Journal.State["merge"]);   // v1, handed the item back now, would pick the merge up as a person's from this comment
        down = false;
        await r.Projection.Project(merged, default);   // the scheduled message: the same decision
        Assert.Equal("abc123", r.Journal.State["merge"]!["sha"]!.ToString());
        Assert.NotNull(r.Journal.State["merge"]!["done_at"]);
        Assert.True((bool)r.Journal.State["verdict"]!["processed"]!);
        var saves = r.Journal.Saves;
        await r.Projection.Project(merged, default);   // and a delivery twice writes nothing
        Assert.Equal(saves, r.Journal.Saves);
    }

    [Fact]
    public async Task ALateRun_NeverUndoesANewerDecision()
    {
        // the park's message failed and is scheduled; the resume was projected meanwhile; the late park finds the saga unparked and the version ahead
        var r = new Rig();
        var parked = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: x"; s.Sequence = 2; });
        await r.Put(parked);
        var parkDecision = Decided(WorkItemActions.Parked, parked, 2);
        var down = true;
        r.Journal.Fault = () => down ? new HttpRequestException("503") : null;
        await Assert.ThrowsAsync<StateProjectionRetryException>(() => r.Projection.Project(parkDecision, default));
        down = false;
        var resumed = Saga("feature", StepKind.Merge, s => { s.Sequence = 3; });
        await r.Put(resumed);
        await r.Projection.Project(Decided(WorkItemActions.Resumed, resumed, 3), default);
        await r.Projection.Project(parkDecision, default);   // the scheduled retry of the older message
        Assert.Null(r.Journal.State["park"]);
    }

    [Fact]
    public async Task TheMessageIsScheduledAgain_After1_5_15And60Minutes_ThenEveryHour_NeverTheDeadLetters()
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseWolverine(o =>
        {
            o.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SagaStateProjectionHandler));
            o.Durability.Mode = DurabilityMode.MediatorOnly;
        }).Build();
        await host.StartAsync();
        var graph = host.Services.GetRequiredService<Wolverine.Runtime.Handlers.HandlerGraph>();
        var chain = graph.Chains.Single(c => c.MessageType == typeof(ProjectStateComment));
        // (Wolverine applies Configure while it compiles a chain, which a host that has handled nothing has not done: it is called here, on the chain it would be called on)
        Assert.Empty(chain.Failures);
        SagaStateProjectionHandler.Configure(chain);
        var failure = new StateProjectionRetryException(Decided(WorkItemActions.Parked, Saga("feature", StepKind.Merge), 2), new HttpRequestException());
        var text = chain.Failures.Single(r => r.Match.Matches(failure)).ToString();
        Assert.Contains("attempt 1: Schedule Retry in 60 seconds; attempt 2: Schedule Retry in 300 seconds; attempt 3: Schedule Retry in 900 seconds; attempt 4: Schedule Retry in 3600 seconds", text);
        Assert.Contains("repeat: Schedule Retry in 3600 seconds", text);
        Assert.DoesNotContain("error queue", text);
        Assert.DoesNotContain(chain.Failures, r => r.Match.Matches(new InvalidOperationException("a bug")));   // anything else is not this policy's
    }

    [Fact]
    public async Task ARefusalForGood_IsLoggedAndLeft_NotRetried()
    {
        var r = new Rig();
        var saga = Saga("feature", StepKind.Merge, s => { s.Parked = true; s.ParkReason = "merge-blocked: x"; s.Sequence = 2; });
        await r.Put(saga);
        r.Journal.Fault = () => new JournalRefusedException("state-too-large");
        await r.Projection.Project(Decided(WorkItemActions.Parked, saga, 2), default);
        Assert.Empty(r.Pauses);
    }

    [Fact]
    public async Task AGatewayWithNoCredential_ProjectsNothing()
    {
        var store = new InMemoryWorkItemStore();
        var projection = new SagaStateProjection(() => null, store, Plan, Settings);
        await projection.Project(Decided(WorkItemActions.Parked, Saga("feature", StepKind.Merge)), default);
    }

    // ---- the rollback path: an item mid-flow, handed back to V1 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemMidFlow_HandedBackToV1_HasEverythingV1ReadsAndEveryDecisionAgainWritesNothing()
    {
        var r = new Rig();
        // the item was PASSed, the gateway merged it, the master watch parked it broken and then lifted the park, and it is delivered and settled; the decisions as the saga published them
        var steps = new List<(string Action, Action<WorkItemState> Edit)>
        {
            (WorkItemActions.StepEntered, s => { s.Step = StepKind.Merge; s.ArmId = "1.4"; }),
            (WorkItemActions.Merged, s => { s.Step = StepKind.Deliver; s.MergeCommit = "abc123"; s.MasterState = "waiting"; }),
            (WorkItemActions.MasterBroken, s => { s.MasterState = "broken"; s.MasterFailed = ["build"]; s.Parked = true; s.ParkReason = "master-broken: master is BROKEN at abc1234 (build)"; s.ParkUntil = T0.AddHours(24); }),
            (WorkItemActions.MasterGreen, s => { s.MasterState = "green"; s.MasterFailed = []; s.Parked = false; s.ParkReason = ""; s.ParkUntil = null; }),
            (WorkItemActions.Resumed, s => { }),
            (WorkItemActions.Settling, s => { s.Status = WorkItemStatus.Settling; s.Delivered["tdd"] = "1.0.7"; }),
            (WorkItemActions.Completed, s => { s.Status = WorkItemStatus.Completed; s.CompletedAt = T0.AddMinutes(40); }),
        };
        var saga = Saga("feature", StepKind.Verify, s => s.Sequence = 0);
        var decisions = new List<WorkItemDecided>();
        foreach (var (action, edit) in steps)
        {
            edit(saga); saga.Sequence++;
            decisions.Add(Decided(action, saga, saga.Sequence, T0.AddMinutes(decisions.Count)));
            await r.Put(saga);
            await r.Projection.Project(decisions[^1], default);
        }
        var s = r.Journal.State;
        Assert.Null(s["park"]);                                                 // v1 does not offer a parked item, and this one is not parked
        Assert.True((bool)s["verdict"]!["processed"]!);                         // v1 does not merge the PASS again
        Assert.Equal(("abc123", Iso(T0.AddMinutes(40))), (s["merge"]!["sha"]!.ToString(), s["merge"]!["done_at"]!.ToString()));   // v1 settles nothing: the item is done
        Assert.True(s["merge"]!["awaiting"] is null);
        // v1's sweep reads the comment: another pass over the same decisions (the handler runs again after a restart) writes nothing
        var saves = r.Journal.Saves; var rev = ItemStateComment.RevOf(s);
        foreach (var d in decisions) await r.Projection.Project(d, default);
        Assert.Equal((saves, rev), (r.Journal.Saves, ItemStateComment.RevOf(r.Journal.State)));
    }
}
