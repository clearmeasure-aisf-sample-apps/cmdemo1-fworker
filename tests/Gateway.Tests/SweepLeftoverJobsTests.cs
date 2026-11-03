using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.WorkItem;
using Xunit;

/// <summary>
/// V1.4: the steps that only v1's sweep did, as recurring jobs of the engine (gateway/Ports/SweepLeftoverJobs.cs): the gap renewal of the saga's owner leases, the base update fallback and the flag of a Release Queue card
/// without a merge record. The rules themselves (SagaGapRenew, ReleaseQueueFlags) are tested in Fworker.Workflow.Tests (SweepLeftoversTests); here they are stand-ins, and what is checked is when a job runs, what it reads and
/// what it does with the answer, and the slots.
/// </summary>
public class SweepLeftoverJobsTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly DateTime Since = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    static readonly DateTime Now = Since.AddMinutes(20);

    sealed class FakeFlags : IReleaseQueueFlags
    {
        public readonly List<(int Item, string Column)> Asked = [];
        public HashSet<int> Lacking = [];
        public Task<IReadOnlyList<int>> Flag(IEnumerable<(int Item, string Column)> candidates)
        {
            var list = candidates.ToList(); Asked.AddRange(list);
            return Task.FromResult<IReadOnlyList<int>>(list.Where(c => Lacking.Contains(c.Item)).Select(c => c.Item).ToList());
        }
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly V2Ownership Ownership;
        public readonly SweepGap Gap = new();
        public readonly GitHubBudget Budget = new(300);
        public readonly List<LeaseRenewed> Sent = [];
        public readonly List<(string Code, int Item, string Column)> Events = [];
        public readonly FakeFlags Flags = new();
        public readonly Dictionary<int, string> Owners = [];
        public List<BoardCard> Cards = [];
        public int WatchPasses;
        public bool May = true, Ready = true;
        public SweepLeftoverJobs Jobs;

        public Rig()
        {
            Ownership = new V2Ownership(Store, Home);
            Jobs = new SweepLeftoverJobs(() => May, () => Ready, Ownership, Gap, n => Owners.GetValueOrDefault(n), (code, item, column) => Events.Add((code, item, column)), Budget,
                m => { Sent.Add(m); return Task.CompletedTask; }, () => { WatchPasses++; return Task.CompletedTask; }, () => Flags, () => Cards, Home, ["Release Queue"], TimeSpan.Zero, () => Now);
        }

        public async Task Seed(int n, DateTimeOffset? lease, string status = WorkItemStatus.Open, bool parked = false)
        {
            var state = new WorkItemState { Item = new ItemId(Home, n).ToString(), Generation = 1, Status = status, Step = StepKind.Develop, ArmId = $"1.{n}", LeaseDue = lease, Parked = parked };
            Assert.True(await Store.Save(state.Item, state, 0, default));
        }
    }

    // ---- gap-renew -----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGapRenewalRenewsTheOwnersWhoseLeaseEndedInTheGap_OncePerGap()
    {
        var r = new Rig();
        await r.Seed(1, Since.AddMinutes(5)); await r.Seed(2, Since.AddMinutes(-3)); await r.Seed(3, Now.AddMinutes(10));
        r.Owners[1] = "alice"; r.Owners[2] = "bob"; r.Owners[3] = "carol";
        r.Gap.Set(Since);
        Assert.Equal("renewed", await r.Jobs.GapRenew(default));
        Assert.Equal(["gap|" + Since.AddMinutes(5).Ticks], r.Sent.Select(m => m.Key));
        Assert.Equal(1, r.Sent[0].Item.Number);
        Assert.Equal("no-gap", await r.Jobs.GapRenew(default));   // the gap is closed: a later tick finds nothing
        Assert.Single(r.Sent);
        r.Gap.Set(Since);                                         // (the sweep putting its share back after a failed pass does not open it again)
        Assert.Equal("no-gap", await r.Jobs.GapRenew(default));
        r.Gap.Set(Since.AddHours(1));                             // a new gap does
        Assert.Equal("none", await r.Jobs.GapRenew(default));
    }

    [Fact]
    public async Task TheGapRenewalIsIndependentOfTheSweepTakingTheGap()
    {
        var r = new Rig();
        await r.Seed(1, Since.AddMinutes(5)); r.Owners[1] = "alice";
        r.Gap.Set(Since);
        Assert.Equal(Since, r.Gap.Take());   // the first full sweep took it (for the items that have no saga)
        Assert.Equal("renewed", await r.Jobs.GapRenew(default));
        Assert.Single(r.Sent);
    }

    [Fact]
    public async Task TheGapRenewalWaitsForTheLeaseTheRestoreTheAdoptionAndTheSagaTable()
    {
        var r = new Rig();
        await r.Seed(1, Since.AddMinutes(5)); r.Owners[1] = "alice";
        r.Gap.Set(Since);
        r.May = false;
        Assert.Equal("not-leader", await r.Jobs.GapRenew(default));
        r.May = true; r.Ready = false;
        Assert.Equal("waiting", await r.Jobs.GapRenew(default));
        Assert.Empty(r.Sent);
        Assert.NotNull(r.Gap.PeekRenew());   // (the gap is still open)
        r.Ready = true;
        Assert.Equal("renewed", await r.Jobs.GapRenew(default));
    }

    [Fact]
    public async Task ATransitionThatCannotBeSentLeavesTheGapOpen()
    {
        var r = new Rig();
        await r.Seed(1, Since.AddMinutes(5)); r.Owners[1] = "alice";
        r.Gap.Set(Since);
        var failing = new SweepLeftoverJobs(() => true, () => true, r.Ownership, r.Gap, n => r.Owners.GetValueOrDefault(n), (_, _, _) => { }, r.Budget, _ => throw new InvalidOperationException("host down"), () => Task.CompletedTask, () => r.Flags, () => [], Home, ["Release Queue"], TimeSpan.Zero, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.GapRenew(default));
        Assert.Equal(Since, r.Gap.PeekRenew());
        Assert.Equal("renewed", await r.Jobs.GapRenew(default));
    }

    // ---- base-watch-fallback ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBaseUpdateFallbackLooksAtMastersHeadOnTheLeader_AndNotOnALowBudget()
    {
        var r = new Rig();
        Assert.Equal("ok", await r.Jobs.BaseWatchFallback(default));
        Assert.Equal(1, r.WatchPasses);
        r.May = false;
        Assert.Equal("not-leader", await r.Jobs.BaseWatchFallback(default));
        Assert.Equal(1, r.WatchPasses);
        r.May = true;
        r.Budget.Observe(new JsonObject { ["core"] = new JsonObject { ["remaining"] = 10, ["reset"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), ["limit"] = 5000 } });
        Assert.Equal("budget-low", await r.Jobs.BaseWatchFallback(default));   // (the sweep did not call it on a low budget either)
        Assert.Equal(1, r.WatchPasses);
    }

    // ---- release-queue-flag ----------------------------------------------------------------------------------------------------------------------------------

    static BoardCard Card(int n, string column, bool open = true) => new(Home, n, column, open, $"Item {n}");

    [Fact]
    public async Task ACardInTheReleaseQueueWithoutAMergeIsFlaggedAndReportedOnce()
    {
        var r = new Rig();
        await r.Seed(9, null); await r.Seed(10, null);                               // saga items with no merge commit
        var merged = new WorkItemState { Item = new ItemId(Home, 11).ToString(), Generation = 1, Step = StepKind.Deliver, ArmId = "1.1", MergeCommit = "abc" };
        Assert.True(await r.Store.Save(merged.Item, merged, 0, default));
        r.Cards = [Card(9, "Release Queue"), Card(10, "Release Queue"), Card(11, "Release Queue"), Card(12, "Development"), Card(15, "Release Queue"), Card(13, "Release Queue", open: false), new BoardCard(new RepoId("acme", "other"), 14, "Release Queue", true, "x")];
        r.Flags.Lacking = [9, 10];
        Assert.Equal("flagged", await r.Jobs.ReleaseQueueFlag(default));
        Assert.Equal([9, 10], r.Flags.Asked.Select(c => c.Item));   // (not the merged one, the other column, the closed card, the other repository or an item with no saga: the sweep flags that one)
        Assert.Equal([("release-queue-without-merge", 9, "Release Queue"), ("release-queue-without-merge", 10, "Release Queue")], r.Events);
        // the next pass reads nothing again for the cards it has dealt with
        Assert.Equal("none", await r.Jobs.ReleaseQueueFlag(default));
        Assert.Equal(2, r.Flags.Asked.Count);
        Assert.Equal(2, r.Events.Count);
    }

    [Fact]
    public async Task ACardWhoseStateCommentHasAMergeRecordIsNotReportedAndNotReadAgain()
    {
        var r = new Rig();
        await r.Seed(9, null);
        r.Cards = [Card(9, "Release Queue")];
        r.Flags.Lacking = [];   // (the state comment has the merge record)
        Assert.Equal("none", await r.Jobs.ReleaseQueueFlag(default));
        Assert.Empty(r.Events);
        Assert.Equal("none", await r.Jobs.ReleaseQueueFlag(default));
        Assert.Single(r.Flags.Asked);
    }

    [Fact]
    public async Task TheTimerOfAnExpiredLeaseWaitsWhileTheGapRenewalIsOpen()
    {
        var store = new InMemoryWorkItemStore();
        var hold = true;
        var svc = new WorkItemService(store, new WorkItemSaga(new Gateway.Tests.DocsPort(), WorkItemPolicy.Default), holdLeaseTimers: () => hold);
        var due = DateTimeOffset.UtcNow.AddMinutes(-5);
        var state = new WorkItemState { Item = new ItemId(Home, 3).ToString(), Generation = 1, Step = StepKind.Develop, ArmId = "1.1", LeaseDue = due };
        Assert.True(await store.Save(state.Item, state, 0, default));
        var held = await svc.Handle(new PhaseLeaseExpired(new ItemId(Home, 3), 1, "1.1", due));
        Assert.Equal(WorkItemOutcome.NotFound, held.Decision.Outcome);
        Assert.Equal(due, (await store.Load(state.Item, default))!.State.LeaseDue);   // (nothing changed: no relaunch)
        hold = false;
        var done = await svc.Handle(new PhaseLeaseExpired(new ItemId(Home, 3), 1, "1.1", due));
        Assert.NotEqual(WorkItemOutcome.NotFound, done.Decision.Outcome);   // (once the renewal has run for the term the timer counts as before)
    }

    // ---- the slots ---------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSlotsHaveTheirIntervals_AndTheBaseUpdateIsEveryFiveMinutesUnlessTheConfigurationSaysOtherwise()
    {
        var slots = RecurringJobSlots.Default(RecurringJobIntervals.From(null, 180)).Slots;
        Assert.Equal(TimeSpan.FromMinutes(5), slots.Single(s => s.Name == RecurringJobSlots.BaseWatchFallback).Every);
        Assert.Equal(TimeSpan.FromMinutes(1), slots.Single(s => s.Name == RecurringJobSlots.GapRenew).Every);
        Assert.Equal(TimeSpan.FromSeconds(180), slots.Single(s => s.Name == RecurringJobSlots.ReleaseQueueFlag).Every);
        Assert.Equal(TimeSpan.FromSeconds(3), slots.Single(s => s.Name == RecurringJobSlots.GapRenew).First);   // (the first look as soon as the leader may act: the gap is renewed before the first offer)
        var fast = RecurringJobSlots.Default(RecurringJobIntervals.From(JsonNode.Parse("""{"recurringJobs":{"baseWatchSeconds":5}}"""), 2)).Slots;
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Single(s => s.Name == RecurringJobSlots.BaseWatchFallback).Every);
        var low = RecurringJobIntervals.From(JsonNode.Parse("""{"recurringJobs":{"baseWatchSeconds":1}}"""), 180);
        Assert.Equal(TimeSpan.FromSeconds(5), low.BaseWatch);   // never faster than every 5 seconds
    }
}
