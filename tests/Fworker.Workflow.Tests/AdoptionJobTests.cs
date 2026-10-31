using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.9b: when the adoption runs. Once per term of the lease and only after the plant is restored, again when the term changes, the break read from the engine's markers, and the engine's heartbeat written only once the term's
/// adoption is over.
/// </summary>
public class AdoptionJobTests
{
    static readonly RepoId Home = Item.Repository;

    sealed class Reads : IAdoptionReads
    {
        public int Runs;
        public bool Fail, Two, FailEight, Moved;   // (Moved: v1 moved item 7 to Verify while the engine was away)
        public Task<IReadOnlyList<BoardCard>> Cards(CancellationToken ct)
        {
            Runs++;
            if (Fail) throw new HttpRequestException("GitHub answered 502");
            return Task.FromResult<IReadOnlyList<BoardCard>>(Two ? [new BoardCard(Home, 7, Develop, true, "item"), new BoardCard(Home, 8, Develop, true, "item eight")] : [new BoardCard(Home, 7, Moved ? Verify : Develop, true, "item")]);
        }
        public Task<V1ItemRecord?> ReadItem(ItemId item, CancellationToken ct) => FailEight && item.Number == 8 ? throw new HttpRequestException("GitHub answered 502") : Task.FromResult<V1ItemRecord?>(new V1ItemRecord(true, ["factory worker"], "The body.", Now.AddDays(-2), Moved ? State(rev: 9, extra: Claim(Verify, true, TimeSpan.FromMinutes(25))) : State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))), false));
        public Task<AdoptionPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct) => Task.FromResult<AdoptionPullRequest?>(null);
        public Task<IReadOnlyList<OctopusSignOff>> PendingSignOffs(CancellationToken ct) => Task.FromResult<IReadOnlyList<OctopusSignOff>>([]);
        public Task<IReadOnlyList<MergedPullRequest>> RecentMergedPulls(int take, string? afterCommit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MergedPullRequest>>([]);
    }

    sealed class Projections : IProjectedSequences
    {
        public Task<(long Comment, long Card)> Of(string itemKey, CancellationToken ct) => Task.FromResult((0L, 0L));
        public Task Advance(string itemKey, long sequence, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class Rig
    {
        public readonly Reads Reads = new();
        public readonly InMemoryWorkItemStore Items = new();
        public readonly InMemoryEngineMarkers Markers = new();
        public readonly InMemoryCapacityStore Capacity = new();
        public bool MayAct = true, Ready = true;
        public long Epoch = 1;
        public DateTimeOffset Clock = Now;
        public readonly ReadoptionGate Gate = new();
        public AdoptionRunner Runner = null!;
        public AdoptionJob Job = null!;

        public Rig(int maxAttempts = 5)
        {
            var ports = new AdoptionPorts(Reads, Items, new Projections(), Capacity, new InMemoryMergeTrainStore(), new InMemoryTrainStore(), new InMemoryQcEnvironmentStore(), new InMemoryMergedItemStore(), new InMemoryDeliveredBackfill(), null,
                _ => Task.FromResult(true), (_, _) => Task.FromResult(true));
            Runner = new AdoptionRunner(new AdoptionOptions(Home, Settings(), () => ["cmdemo1-ui/tdd"]), ports, () => Clock, Gate);
            Job = new AdoptionJob(Runner, Markers, () => MayAct, () => Epoch, () => Ready, "node-a", () => Clock, maxAttempts, readoption: Gate);
        }
    }

    [Fact]
    public async Task J1_ItWaitsForThePlantsRestore_AndRunsWhenItIsOver()
    {
        var r = new Rig { Ready = false };
        Assert.Equal("waiting: plant-restore", await r.Job.Tick(default));
        Assert.Equal(0, r.Reads.Runs);
        Assert.Equal("plant-restore", r.Job.Block()["waitingFor"]!.GetValue<string>());
        r.Ready = true;
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.Single(r.Items.States);
    }

    [Fact]
    public async Task J2_ItRunsOnlyWhereTheProcessHoldsTheLease()
    {
        var r = new Rig { MayAct = false };
        Assert.Equal("not-leader", await r.Job.Tick(default));
        Assert.Equal((0, 0), (r.Reads.Runs, r.Markers.Rows.Count));
    }

    [Fact]
    public async Task J3_OncePerTerm_ThenOnlyTheHeartbeat_AndANewTermRunsAgain()
    {
        var r = new Rig();
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.Equal("heartbeat", await r.Job.Tick(default));
        Assert.Equal("heartbeat", await r.Job.Tick(default));
        Assert.Equal(1, r.Reads.Runs);
        // the lease was lost and taken again: a new epoch is a new term, and the second adoption finds everything in place
        r.Epoch = 2;
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.Equal(2, r.Reads.Runs);
        Assert.Equal(1, r.Runner.Last!.Kept);
    }

    [Fact]
    public async Task J4_TheHeartbeatIsTheActedMarker_WrittenOnlyAfterTheAdoptionOfTheTermIsOver()
    {
        var r = new Rig();
        r.Reads.Fail = true;
        Assert.Equal("partial", await r.Job.Tick(default));
        Assert.Empty(r.Markers.Rows);   // (a start whose adoption broke off finds the same evidence again)
        r.Reads.Fail = false;
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.Equal(("node-a", "epoch 1"), (r.Markers.Rows["acted"].Holder, r.Markers.Rows["acted"].Detail));
    }

    [Fact]
    public async Task J5_APartialRunIsTriedAgainAtTheNextMinute_AndGivenUpAfterTheAttemptsTheJobAllows()
    {
        var r = new Rig(maxAttempts: 3);
        r.Reads.Fail = true;
        Assert.Equal(["partial", "partial", "partial"], new[] { await r.Job.Tick(default), await r.Job.Tick(default), await r.Job.Tick(default) });
        Assert.Equal("heartbeat", await r.Job.Tick(default));   // (the term is over with its errors reported; the status shows them)
        Assert.Equal(3, r.Reads.Runs);
        Assert.True(r.Job.Block()["done"]!.GetValue<bool>());
    }

    [Fact]
    public async Task J6_ALeaseLostDuringTheRun_IsNotATermDone_ItIsLeftToTheNextHolder()
    {
        var r = new Rig();
        r.Reads.Fail = false;
        var epochAsked = 0;
        var job = new AdoptionJob(r.Runner, r.Markers, () => true, () => ++epochAsked == 1 ? 1 : 2, () => true, "node-a", () => r.Clock);
        Assert.Equal("aborted", await job.Tick(default));
        Assert.Empty(r.Markers.Rows);
    }

    [Fact]
    public async Task J7_TheKillSwitchMarkerNewerThanTheLastActMakesTheRunAfterABreak()
    {
        var r = new Rig();
        await r.Markers.Mark(EngineMarkerNames.Acted, Now.AddHours(-2).AddMinutes(-5), "node-a", "", default);
        await r.Markers.Mark(EngineMarkerNames.V1Ran, Now.AddHours(-2), "node-b", "FACTORY_V2_OFF", default);
        await r.Job.Tick(default);
        var last = r.Runner.Last!;
        Assert.True(last.AfterBreak);
        Assert.Contains("kill switch", last.BreakWhy);
        // the engine acted since: the next start is not after a break
        var next = new Rig();
        await next.Markers.Mark(EngineMarkerNames.V1Ran, Now.AddHours(-2), "node-b", "", default);
        await next.Markers.Mark(EngineMarkerNames.Acted, Now.AddMinutes(-1), "node-a", "", default);
        await next.Job.Tick(default);
        Assert.False(next.Runner.Last!.AfterBreak);
    }

    [Fact]
    public void J8_AGapLongerThanTheBreakGapIsABreak_AShortOneNot_AndTheFirstStartIsNone()
    {
        Assert.Equal(true, AdoptionBreak.Detect(new EngineMarks(Now.AddMinutes(-45), null), Now).Break);
        Assert.Equal(false, AdoptionBreak.Detect(new EngineMarks(Now.AddMinutes(-5), null), Now).Break);
        Assert.Equal(false, AdoptionBreak.Detect(new EngineMarks(null, null), Now).Break);
        Assert.Equal(true, AdoptionBreak.Detect(new EngineMarks(null, Now.AddMinutes(-1)), Now).Break);
        Assert.Equal(false, AdoptionBreak.Detect(new EngineMarks(Now.AddMinutes(-45), null), Now, TimeSpan.FromHours(1)).Break);
    }

    [Fact]
    public async Task J9_WithoutTheMarkersTheBreakCannotBeTold_TheRunWaitsInsteadOfGuessing()
    {
        var r = new Rig();
        var job = new AdoptionJob(r.Runner, new BrokenMarkers(), () => true, () => 1, () => true, "node-a", () => r.Clock);
        Assert.Equal("waiting: engine-markers", await job.Tick(default));
        Assert.Equal(0, r.Reads.Runs);
    }

    sealed class BrokenMarkers : IEngineMarkers
    {
        public Task<EngineMarks> Read(CancellationToken ct) => throw new InvalidOperationException("the database does not answer");
        public Task Mark(string name, DateTimeOffset at, string holder, string detail, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task J10_TheBlockCarriesTheTermAndTheLatestReport()
    {
        var r = new Rig();
        await r.Job.Tick(default);
        var block = r.Job.Block();
        Assert.Equal(("done", true), (block["state"]!.GetValue<string>(), block["done"]!.GetValue<bool>()));
        Assert.Equal(1, block["last"]!["adopted"]!.GetValue<int>());
    }

    // ---- the review of #602: a break is not forgotten by a partial run, and the old instance's inputs wait for the adoption ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task J11_AfterABreakAPartialRunAtTheLastAttemptWritesNoHeartbeat_TheNextStartStillSeesTheBreak_AndTheTriesGoOnWithPauses()
    {
        var r = new Rig(maxAttempts: 2);
        await r.Markers.Mark(EngineMarkerNames.Acted, Now.AddHours(-2), "node-a", "epoch 0", default);   // (the gateway was away for two hours)
        r.Reads.Fail = true;
        Assert.Equal(["partial", "partial"], new[] { await r.Job.Tick(default), await r.Job.Tick(default) });
        Assert.Equal(Now.AddHours(-2), r.Markers.Rows["acted"].At);   // (no heartbeat: the evidence of the break is still the old one)
        Assert.Equal("waiting: retry-backoff", await r.Job.Tick(default));   // (past the attempts: a pause, not an end)
        var block = r.Job.Block();
        Assert.Equal((true, false), (block["breakPending"]!.GetValue<bool>(), block["done"]!.GetValue<bool>()));
        Assert.Equal(2, r.Reads.Runs);
        // a process that starts now finds the same evidence and decides the same
        var next = new AdoptionJob(r.Runner, r.Markers, () => true, () => 2, () => true, "node-b", () => r.Clock, 2, readoption: r.Gate);
        r.Reads.Fail = false;
        Assert.Equal("done", await next.Tick(default));
        Assert.True(r.Runner.Last!.AfterBreak);
        // the original job tries again after its pause, as the pause grows to a cap
        r.Reads.Fail = true;
        r.Clock = r.Clock.AddMinutes(2);
        Assert.Equal("partial", await r.Job.Tick(default));
        r.Reads.Fail = false;
        r.Clock = r.Clock.AddMinutes(5);
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.True(r.Markers.Rows["acted"].At >= Now);
        Assert.False(r.Job.Block()["breakPending"]!.GetValue<bool>());
    }

    [Fact]
    public async Task J12_WithoutABreakAPartialRunAtTheLastAttemptStillEndsTheTerm_NothingIsHeld()
    {
        var r = new Rig(maxAttempts: 2);
        await r.Markers.Mark(EngineMarkerNames.Acted, Now.AddMinutes(-1), "node-a", "", default);
        r.Gate.Hold("the host has not started yet", newTerm: true);
        r.Reads.Fail = true;
        Assert.Equal("partial", await r.Job.Tick(default));
        Assert.False(r.Gate.Held);   // (the term is not after a break: the hold of the start is let go at the first look)
        Assert.Equal("partial", await r.Job.Tick(default));
        Assert.Equal("heartbeat", await r.Job.Tick(default));
        Assert.True(r.Markers.Rows["acted"].At >= Now);
    }

    [Fact]
    public async Task J13_AfterABreakTheGateHoldsUntilARunIsDone_AndAnItemTheRunExaminedIsOpenAtOnce()
    {
        var r = new Rig();
        await r.Markers.Mark(EngineMarkerNames.Acted, Now.AddHours(-2), "node-a", "", default);
        r.Gate.Hold("the host has not started yet", newTerm: true);
        r.Reads.Two = true; r.Reads.FailEight = true;
        Assert.Equal("partial", await r.Job.Tick(default));
        Assert.True(r.Gate.Held);
        Assert.False(r.Gate.Refuses("acme/orders#7"));   // (examined and adopted: its inputs pass)
        Assert.True(r.Gate.Refuses("acme/orders#8"));    // (its read failed: it is still the old instance)
        Assert.Equal(1, r.Gate.Block()["adoptedItems"]!.GetValue<int>());
        r.Reads.FailEight = false;
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.False(r.Gate.Held);
        Assert.False(r.Gate.Refuses("acme/orders#8"));
    }

    static ItemId Seven => ItemId.Parse("acme/orders#7");

    [Fact]
    public async Task J14_AnOverdueTimerOfTheOldInstanceAtTheHostStartAfterTheKillSwitchDoesNotAct_UntilTheAdoptionHasMovedTheItem()
    {
        var r = new Rig();
        Assert.Equal("done", await r.Job.Tick(default));   // (the engine adopted item 7: generation 1 with a lease timer)
        var row = r.Items.States["acme/orders#7"];
        var lease = Assert.IsType<PhaseLeaseExpired>(WorkItemSaga.TimersOf(row).Select(t => t.Message).Single(m => m is PhaseLeaseExpired));
        // the kill switch was on for an hour: v1 ran, and the lease timer of generation 1 is long overdue when the new process starts
        r.Clock = r.Clock.AddHours(1);
        await r.Markers.Mark(EngineMarkerNames.V1Ran, r.Clock.AddMinutes(-30), "node-b", "FACTORY_V2_OFF", default);
        r.Epoch = 2;
        r.Gate.Hold("the host has not started yet", newTerm: true);
        await r.Gate.Begin(r.Markers, r.Clock, default);
        Assert.True(r.Gate.Held);
        var service = new WorkItemService(r.Items, new WorkItemSaga(new FakeDocsPort(), Settings().Policy), () => r.Clock, r.Gate);
        var before = r.Items.States["acme/orders#7"].ToJson();
        var answer = await service.Handle(lease);
        Assert.Equal(WorkItemOutcome.NotFound, answer.Decision.Outcome);
        Assert.Contains("re-adoption", answer.Decision.Reason);
        Assert.Empty(answer.Decisions); Assert.Empty(answer.Timers); Assert.Empty(answer.Sent);
        Assert.Equal(before, r.Items.States["acme/orders#7"].ToJson());   // (the row is as it was: no relaunch, no new lease)
        // v1 moved the item to Verify meanwhile; the adoption fences it, and the timer of the old instance answers NotFound by its generation
        r.Reads.Moved = true;
        Assert.Equal("done", await r.Job.Tick(default));
        Assert.Equal(2, r.Items.States["acme/orders#7"].Generation);
        Assert.False(r.Gate.Held);
        var late = await service.Handle(lease);
        Assert.Equal(WorkItemOutcome.NotFound, late.Decision.Outcome);
        Assert.Contains("generation 2", late.Decision.Reason);
    }
}
