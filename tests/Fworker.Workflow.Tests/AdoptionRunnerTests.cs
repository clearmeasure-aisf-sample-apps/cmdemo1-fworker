using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Persistence;
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
/// W9.9b: the adoption wired. The run reads every open item of the home repository, maps it (W9.10m, W9.10n) and applies the results to the saga's rows and the rows beside them; here with the in-memory stores and a fake GitHub and Octopus.
/// Rows of the plan: A (adoption at a start: the items, the skips by reason), B (idempotent across starts and safe with two processes), C (after a break: fenced), D (Sign-offs), E (DeliveredTo and the merged items), F (the trains' rows
/// validated), G (the lease and the faults).
/// </summary>
public class AdoptionRunnerTests
{
    static readonly RepoId Home = Item.Repository;
    const string TrainKey = "cmdemo1-ui/tdd";

    internal sealed class FakeReads : IAdoptionReads
    {
        public readonly List<BoardCard> Board = [];
        public readonly Dictionary<int, V1ItemRecord> Records = [];
        public readonly Dictionary<int, AdoptionPullRequest> Pulls = [];
        public readonly List<OctopusSignOff> SignOffs = [];
        public readonly List<MergedPullRequest> Merged = [];
        public readonly HashSet<int> Failing = [];
        public int ItemReads, PullReads;
        public Func<Task>? OnRead;

        public Task<IReadOnlyList<BoardCard>> Cards(CancellationToken ct) => Task.FromResult<IReadOnlyList<BoardCard>>([.. Board]);

        public async Task<V1ItemRecord?> ReadItem(ItemId item, CancellationToken ct)
        {
            ItemReads++;
            if (OnRead is { } hook) await hook();
            if (Failing.Contains(item.Number)) throw new HttpRequestException("GitHub answered 502");
            return Records.GetValueOrDefault(item.Number);
        }

        public Task<AdoptionPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct) { PullReads++; return Task.FromResult(Pulls.GetValueOrDefault(number)); }
        public Task<IReadOnlyList<OctopusSignOff>> PendingSignOffs(CancellationToken ct) => Task.FromResult<IReadOnlyList<OctopusSignOff>>([.. SignOffs]);
        public Task<IReadOnlyList<MergedPullRequest>> RecentMergedPulls(int take, string? afterCommit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MergedPullRequest>>(Merged.Take(take).ToList());
    }

    internal sealed class Projected : IProjectedSequences
    {
        public readonly Dictionary<string, (long Comment, long Card)> Versions = [];
        public Task<(long Comment, long Card)> Of(string itemKey, CancellationToken ct) => Task.FromResult(Versions.GetValueOrDefault(itemKey));
        public Task Advance(string itemKey, long sequence, DateTimeOffset at, CancellationToken ct)
        {
            var (comment, card) = Versions.GetValueOrDefault(itemKey);
            Versions[itemKey] = (Math.Max(comment, sequence), Math.Max(card, sequence));
            return Task.CompletedTask;
        }
    }

    sealed class World
    {
        public readonly FakeReads Reads = new();
        public readonly InMemoryWorkItemStore Items = new();
        public readonly Projected Projections = new();
        public readonly InMemoryCapacityStore Capacity = new();
        public readonly InMemoryMergeTrainStore MergeTrains = new();
        public readonly InMemoryTrainStore Trains = new();
        public readonly InMemoryQcEnvironmentStore Qc = new();
        public readonly InMemoryMergedItemStore Merged = new();
        public readonly InMemoryDeliveredBackfill Delivered = new();
        public readonly ItemMergedDeriverTests.FakeReader DeriverReads = new();
        public readonly List<object> Published = [];
        public readonly List<(object Message, DateTimeOffset Due)> Scheduled = [];
        public bool HostUp = true;
        public bool WithDeriver = true;
        public int MergeQueueDepth = 2;

        public AdoptionRunner Runner(Func<DateTimeOffset>? clock = null)
        {
            var deriver = WithDeriver ? new ItemMergedDeriver(DeriverReads, Merged, Home, () => "master", new ItemGateConfig("Auto Merge", "video", ["hold:owner"]), clock ?? (() => Now)) : null;
            var ports = new AdoptionPorts(Reads, Items, Projections, Capacity, MergeTrains, Trains, Qc, Merged, Delivered, deriver,
                m => { if (!HostUp) return Task.FromResult(false); lock (Published) Published.Add(m); return Task.FromResult(true); },
                (m, due) => { if (!HostUp) return Task.FromResult(false); lock (Scheduled) Scheduled.Add((m, due)); return Task.FromResult(true); });
            return new AdoptionRunner(new AdoptionOptions(Home, Settings(), () => [TrainKey], MergeQueueDepth: MergeQueueDepth), ports, clock ?? (() => Now));
        }

        public Task<AdoptionReport> Run(bool afterBreak = false, Func<bool>? acting = null, AdoptionRunner? runner = null) => (runner ?? Runner()).Run(afterBreak, afterBreak ? "test" : "", acting ?? (() => true), default);

        /// <summary>An item v1 has in flight: its card, its issue with the state comment.</summary>
        public World Put(int n, string column, JsonObject? state, bool open = true, bool issueOpen = true)
        {
            Reads.Board.RemoveAll(c => c.Number == n);
            Reads.Board.Add(new BoardCard(Home, n, column, open, $"item {n}"));
            Reads.Records[n] = new V1ItemRecord(issueOpen, ["factory worker", "Auto Merge"], "The body.", Now.AddDays(-2), state, false);
            return this;
        }

        public WorkItemState Row(int n) => Items.States[new ItemId(Home, n).ToString()];
        public IEnumerable<T> Of<T>() => Published.OfType<T>();
    }

    static string Owner(string column = Develop) => Claim(column, true, TimeSpan.FromMinutes(25));

    /// <summary>A world with the items a start finds in v1's hands: #7 in Development with a live owner, #8 in Technical Design, #9 a Todo card.</summary>
    static World InFlight() => new World().Put(7, Develop, State(extra: Owner())).Put(8, Design, State(extra: "\"design\": { \"docs_only\": false }")).Put(9, "Todo", null);

    // ---- A: the adoption at a start ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A1_EveryOpenItemOfTheBoardIsAdopted_TheOwnerKeepsItsSlot_TheOtherAsksForOne()
    {
        var w = InFlight();
        var report = await w.Run();
        Assert.Equal(("done", 2, 2, 2), (report.Outcome, report.Adopted, report.Upserted, w.Items.States.Count));
        var developing = w.Row(7); var designing = w.Row(8);
        Assert.Equal((StepKind.Develop, true, "adopted|acme/orders#7|1"), (developing.Step, developing.SlotKnown, developing.SlotId));
        Assert.Equal((StepKind.Design, true), (designing.Step, designing.SlotAsked));
        // the ledger holds the live owner's slot, so that the scheduler does not grant a second one
        Assert.Equal(["acme/orders#7"], w.Capacity.State(CapacityKeys.Factory)!.Slots.Keys);
        // what the rows ask for leaves after they were saved: the decision, the timers (scheduled for their time), the request for a slot
        Assert.Equal(2, w.Of<WorkItemDecided>().Count(d => d.Action == WorkItemActions.Adopted));
        Assert.NotEmpty(w.Scheduled);
        Assert.Contains(w.Of<SlotWanted>(), a => a.Key.StartsWith("ask|acme/orders#8|1"));
    }

    [Fact]
    public async Task A2_ACardInAColumnOfNoStepIsCounted_AndNotRead()
    {
        var w = InFlight();
        var report = await w.Run();
        Assert.Equal((1, "column-of-no-step"), (report.Skipped, Assert.Single(report.SkippedBy).Key));
        Assert.Equal(2, w.Reads.ItemReads);   // (#9 was not read)
        Assert.Equal(3, report.Cards);
    }

    [Fact]
    public async Task A3_TheItemsTheMappingSkips_AreCountedByTheirReasonCode()
    {
        var w = InFlight()
            .Put(10, Verify, State(extra: Pass()), issueOpen: false)
            .Put(11, Develop, null)
            .Put(12, Verify, State(extra: Merged("release-queue", "\"done_at\": \"" + Iso(TimeSpan.FromMinutes(1)) + "\"")))
            .Put(13, Release, State());
        var report = await w.Run();
        Assert.Equal(2, report.Adopted);
        Assert.Equal(new Dictionary<string, int> { ["column-of-no-step"] = 1, ["issue-closed"] = 1, ["no-state-comment"] = 1, ["settled"] = 1, ["hold-column"] = 1 }, report.SkippedBy.ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(5, report.Skipped);
        Assert.DoesNotContain(w.Items.States.Keys, k => k is "acme/orders#10" or "acme/orders#11" or "acme/orders#12" or "acme/orders#13");
    }

    [Fact]
    public async Task A4_TheStatusJsonCarriesTheCountsAndNoText()
    {
        var w = InFlight();
        var json = (await w.Run()).ToJson();
        Assert.Equal((2, 2, 1, "done"), (json["adopted"]!.GetValue<int>(), json["upserted"]!.GetValue<int>(), json["skipped"]!.GetValue<int>(), json["outcome"]!.GetValue<string>()));
        Assert.Equal(1, json["skippedBy"]!["column-of-no-step"]!.GetValue<int>());
        Assert.False(json["afterBreak"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A5_APullRequestOfTheItemIsReadForItsHeadAndFiles()
    {
        var w = new World().Put(7, Develop, State(extra: Join(Owner(), "\"pr\": { \"number\": 11 }")));
        w.Reads.Pulls[11] = new AdoptionPullRequest(11, "open", Head, null, ["src/a.cs"]);
        await w.Run();
        var row = w.Row(7);
        Assert.Equal((11, Head, 1), (row.PullRequest, row.Head, w.Reads.PullReads));
        Assert.Equal(["src/a.cs"], row.Footprint);
    }

    [Fact]
    public async Task A6_TheQcRecordsOfTheCommentAreSavedToTheTable()
    {
        var w = new World().Put(7, Verify, State(extra: Join(Owner(Verify), "\"qc\": { \"11\": { \"state\": \"ready\", \"attempt\": 1, \"rev\": 3 } }")));
        var report = await w.Run();
        Assert.Equal(1, report.QcRows);
        Assert.Equal("acme/orders#7", w.Qc.Rows["acme/orders!11"].ItemKey);
        Assert.Equal(0, (await w.Run()).QcRows);   // (a record the table holds is not written again)
    }

    // ---- B: idempotent across starts, safe with two processes ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B1_TheSecondStartAdoptsNothingAndWritesNothing()
    {
        var w = InFlight();
        await w.Run();
        var revisions = new[] { 7, 8 }.Select(n => w.Items.RevisionOf($"acme/orders#{n}")).ToArray();
        var ledger = w.Capacity.State(CapacityKeys.Factory)!.ToJson(); var ledgerRevision = w.Capacity.RevisionOf(CapacityKeys.Factory);
        var published = w.Published.Count; var scheduled = w.Scheduled.Count;
        var again = await w.Run();
        Assert.Equal(("done", 0, 0, 2), (again.Outcome, again.Adopted, again.Upserted, again.Kept));
        Assert.Equal(revisions, new[] { 7, 8 }.Select(n => w.Items.RevisionOf($"acme/orders#{n}")));
        Assert.Equal((ledger, ledgerRevision), (w.Capacity.State(CapacityKeys.Factory)!.ToJson(), w.Capacity.RevisionOf(CapacityKeys.Factory)));
        Assert.Equal((published, scheduled), (w.Published.Count, w.Scheduled.Count));
    }

    [Fact]
    public async Task B2_TheRowsTheSagaRanAreKept_AndTheNewerCommentOfAnItemWinsOnce()
    {
        var w = InFlight();
        await w.Run();
        // the comment moves on (a verb of v1 or the saga's own projection wrote it): the same state only moves the revision it was read at
        w.Put(7, Develop, State(rev: 6, extra: Owner()));
        var same = await w.Run();
        Assert.Equal((0, 1, 1), (same.Updated, same.Unchanged, same.Kept));
        Assert.Equal(6, w.Row(7).CommentRev);
        // a comment that says something else (the owner's work went into Functional Testing) wins: a new arm of the same instance
        w.Put(7, Verify, State(rev: 7, extra: Join(Owner(Verify), "\"verdict\": { \"passed\": false, \"pr\": 11, \"head_sha\": \"" + Head + "\", \"summary\": \"x\", \"round\": 0 }")));
        var moved = await w.Run();
        Assert.Equal((1, StepKind.Verify, 1), (moved.Updated, w.Row(7).Step, w.Row(7).Generation));
        Assert.Equal(0, (await w.Run()).Updated);
    }

    [Fact]
    public async Task B3_TwoProcessesAtOnce_LeaveOneRowPerItem_AndTheLoserOfEveryRaceReadsAgain()
    {
        var w = InFlight();
        var first = w.Runner(); var second = w.Runner();
        // both read #7 and then wait for each other: whichever saves second finds the row there and its revision stale
        var gate = new TaskCompletionSource(); var arrived = 0;
        w.Reads.OnRead = async () => { if (Interlocked.Increment(ref arrived) <= 2) { if (arrived == 2) gate.SetResult(); await gate.Task; } };
        var both = await Task.WhenAll(w.Run(runner: first), w.Run(runner: second));
        Assert.All(both, r => Assert.Equal("done", r.Outcome));
        Assert.Equal(2, w.Items.States.Count);
        Assert.Equal(2, both.Sum(r => r.Adopted));   // (every item adopted once, by whichever process was first)
        Assert.True(both.Sum(r => r.Conflicts + r.Kept) >= 1);
        // the ledger holds the one slot, whatever order they ran in
        Assert.Single(w.Capacity.State(CapacityKeys.Factory)!.Slots);
    }

    // ---- C: after a break (FACTORY_V2_OFF, or a long absence) -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C1_AfterABreak_AnItemV1Moved_IsTheNextGeneration_AndTheLedgerIsFenced()
    {
        var w = InFlight();
        await w.Run();
        Assert.Contains("acme/orders#7", w.Capacity.State(CapacityKeys.Factory)!.Slots.Keys);
        // while the engine was off v1 took the item to Functional Testing and ran its own session there
        w.Put(7, Verify, State(rev: 9, extra: Join(Claim(Verify, false, TimeSpan.FromMinutes(-5)), "\"verdict\": { \"passed\": false, \"pr\": 11, \"head_sha\": \"" + Head + "\", \"summary\": \"x\", \"round\": 0 }")));
        var report = await w.Run(afterBreak: true);
        Assert.Equal(("done", 1, true), (report.Outcome, report.Updated, report.AfterBreak));
        Assert.Equal((2, StepKind.Verify), (w.Row(7).Generation, w.Row(7).Step));
        Assert.Equal(1, w.Row(8).Generation);   // (the item the comment did not change keeps its instance)
        // the old instance's slot is gone from the ledger: the new instance asks again
        Assert.DoesNotContain("acme/orders#7", w.Capacity.State(CapacityKeys.Factory)!.Slots.Keys);
        Assert.Contains(w.Of<SlotWanted>(), a => a.Key.StartsWith("ask|acme/orders#7|2"));
        // and a second start after the break finds the same: nothing more happens
        Assert.Equal(0, (await w.Run(afterBreak: true)).Updated);
        Assert.Equal(2, w.Row(7).Generation);
    }

    [Fact]
    public async Task C2_AfterABreak_AnOpenRowOfAnItemV1EndedIsClosed_EvenWhenItsCardIsClosedAndAway()
    {
        var w = InFlight();
        await w.Run();
        w.Put(8, Done, State(rev: 12), open: false, issueOpen: false);
        var report = await w.Run(afterBreak: true);
        Assert.Equal(1, report.Closed);
        Assert.Equal(WorkItemStatus.Completed, w.Row(8).Status);
        Assert.Contains(w.Of<WorkItemDecided>(), d => d.Action == WorkItemActions.AdoptedClosed && d.Item.Number == 8);
    }

    [Fact]
    public async Task C3_WithoutABreak_ARowIsNeverClosedByTheAdoption()
    {
        var w = InFlight();
        await w.Run();
        w.Put(8, Done, State(rev: 12), open: false, issueOpen: false);
        var report = await w.Run();
        Assert.Equal((0, WorkItemStatus.Open), (report.Closed, w.Row(8).Status));
    }

    // ---- D: the pending Sign-offs --------------------------------------------------------------------------------------------------------------------------------------

    static JsonObject PromotedItem(string task = "T-1", string release = "1.0.7") =>
        State(extra: Merged("release-queue", $"\"promoted\": {{ \"tdd\": {{ \"task\": \"{task}\", \"at\": \"{Iso(TimeSpan.FromMinutes(40))}\", \"release\": \"{release}\", \"signoff_wait_since\": \"{Iso(TimeSpan.FromMinutes(25))}\" }} }}"));

    [Fact]
    public async Task D1_ASignOffAtTheGatewaysOwnDeployment_BecomesTheTrainsToAnswer_WaitingSinceV1RecordedIt()
    {
        var w = new World().Put(7, Verify, PromotedItem());
        w.Reads.SignOffs.Add(new OctopusSignOff("tdd", new PendingSignOff("I-1", "D-1", "T-1", "1.0.7", Now - TimeSpan.FromMinutes(5))));
        var report = await w.Run();
        var train = (await w.Trains.Load(TrainKey, default))!.State;
        var so = Assert.Single(train.SignOffs);
        Assert.Equal((Now - TimeSpan.FromMinutes(25), true, 1), (so.Since, train.Requested.ContainsKey("1.0.7"), report.SignOffs));
        Assert.Equal(["acme/orders#7"], train.RequestItems["1.0.7"]);
    }

    [Fact]
    public async Task D2_ASignOffTheWatcherPolledFirst_LearnsWhoseItIs_ASecondRunChangesNothing()
    {
        var w = new World().Put(7, Verify, PromotedItem());
        var known = TrainState.New(TrainKey);
        known.SignOffs.Add(new TrainSignOff { InterruptionId = "I-1", DeploymentId = "D-1", TaskId = "T-1", Version = "1.0.7", Since = Now });   // (the train stamped it with its own clock)
        await w.Trains.Save(TrainKey, known, 0, default);
        w.Reads.SignOffs.Add(new OctopusSignOff("tdd", new PendingSignOff("I-1", "D-1", "T-1", "1.0.7", Now - TimeSpan.FromMinutes(5))));
        await w.Run();
        var train = (await w.Trains.Load(TrainKey, default))!.State;
        Assert.Equal((Now - TimeSpan.FromMinutes(25), true), (Assert.Single(train.SignOffs).Since, train.Requested.ContainsKey("1.0.7")));
        var revision = (await w.Trains.Load(TrainKey, default))!.Revision;
        await w.Run();
        Assert.Equal(revision, (await w.Trains.Load(TrainKey, default))!.Revision);
    }

    [Fact]
    public async Task D4_ThePromotionsAreReadFromTheItemsTheTrainKnows_WhoseIssuesClosedWithTheMerge()
    {
        // the item of the release is merged and its card is gone from the steps (a closed issue): it is on no card the run reads, only in the train's candidates and the items of the release it asked for
        var w = new World();
        w.Reads.Records[7] = new V1ItemRecord(false, ["factory worker"], "The body.", Now.AddDays(-2), PromotedItem(), false);
        var train = TrainState.New(TrainKey);
        train.Candidates["acme/orders#7"] = new TrainCandidate { Item = "acme/orders#7", Commit = Sha, PullRequest = 11, MergedAt = Now };
        await w.Merged.TryRecordAll([new MergedItem { ItemKey = "acme/orders#7", Commit = Sha, PullRequest = 11, GatesJson = "{}", DependenciesJson = "[]", Source = "webhook", MergedAt = Now, MessageId = Guid.NewGuid() }], default);
        await w.Trains.Save(TrainKey, train, 0, default);
        w.Reads.SignOffs.Add(new OctopusSignOff("tdd", new PendingSignOff("I-1", "D-1", "T-1", "1.0.7", Now - TimeSpan.FromMinutes(5))));
        var report = await w.Run();
        var next = (await w.Trains.Load(TrainKey, default))!.State;
        Assert.Equal((1, Now - TimeSpan.FromMinutes(25), true), (report.SignOffs, Assert.Single(next.SignOffs).Since, next.Requested.ContainsKey("1.0.7")));
        Assert.Equal(1, w.Reads.ItemReads);   // (read once, for the promotions: the card is not on the board)
        Assert.Empty(w.Items.States);
    }

    [Fact]
    public async Task D3_ASignOffOfSomebodyElsesDeploymentIsLeftAlone()
    {
        var w = new World().Put(7, Verify, PromotedItem());
        w.Reads.SignOffs.Add(new OctopusSignOff("tdd", new PendingSignOff("I-9", "D-9", "T-other", "1.0.9", Now)));
        await w.Run();
        Assert.Null(await w.Trains.Load(TrainKey, default));
    }

    // ---- E: DeliveredTo and the merged items ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task E1_AMergeV1RecordedBecomesAMergedItemsRow_AndTheTrainHearsOfIt()
    {
        var w = new World().Put(7, Verify, State(extra: Merged("release-queue")));
        var report = await w.Run();
        var row = Assert.Single(w.Merged.Rows);
        Assert.Equal(("acme/orders#7", Sha, "adopted"), (row.ItemKey, row.Commit, row.Source));
        var message = Assert.Single(w.Of<ItemMerged>());
        Assert.Equal(("acme/orders#7", Sha, 11, "adopted"), (message.Item.ToString(), message.Commit, message.PullRequest, message.Source));
        Assert.Equal((1, 1), (report.MergedRows, report.MergedPublished));
        // a second start finds the row the adoption's own still, and sends the message again (the train takes a repeat as it takes the first); the table has the one row
        Assert.Equal(1, (await w.Run()).MergedRows);
        Assert.Single(w.Merged.Rows);
    }

    [Fact]
    public async Task E2_AMergeTheWebhookRecordedFirst_IsTheirs_NothingIsSentForItAgain()
    {
        var w = new World().Put(7, Verify, State(extra: Merged("release-queue")));
        await w.Merged.TryRecordAll([new MergedItem { ItemKey = "acme/orders#7", Commit = Sha, PullRequest = 11, GatesJson = "{}", DependenciesJson = "[]", Source = "webhook", MergedAt = Now, MessageId = Guid.NewGuid() }], default);
        var report = await w.Run();
        Assert.Equal((0, 0), (report.MergedRows, w.Of<ItemMerged>().Count()));
        Assert.Equal("webhook", Assert.Single(w.Merged.Rows).Source);
    }

    [Fact]
    public async Task E3_TheItemMergedForARowIsLeftOutWhenTheSagaKnowsItReachedTheTrainsEnvironment()
    {
        var w = new World().Put(7, Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")));
        var train = TrainState.New(TrainKey);
        train.Releases["1.0.7"] = new TrainRelease { ReleaseId = "R-7", Version = "1.0.7", Commit = Sha, Assembled = Now };
        train.Deployments["1.0.7"] = new Dictionary<string, TrainDeployment> { ["tdd"] = new() { DeploymentId = "D-1", Phase = "Succeeded", StartedAt = Now, FinishedAt = Now } };
        train.DeliveredItems.Add($"acme/orders#7|{Sha}"); train.Current = "1.0.7";
        await w.Trains.Save(TrainKey, train, 0, default);
        await w.Run();
        Assert.Empty(w.Of<ItemMerged>());
    }

    [Fact]
    public async Task E4_TheNewestMergedPullRequests_GiveTheMergedItemsOfMergesNoRowHolds()
    {
        var w = new World();
        w.Reads.Merged.Add(w.DeriverReads.Pulls[9] = new MergedPullRequest(Home, 9, true, "master", "bot/21-add-thing", "Add the thing (#9)", "Closes #21", ItemMergedDeriverTests.Sha));
        w.DeriverReads.Items[21] = ItemMergedDeriverTests.Facts(21, labels: ["Auto Merge"], body: "The body", passed: true);
        var report = await w.Run();
        Assert.Equal((1, 1), (report.MergedFromPulls, report.MergedPublished));
        var message = Assert.Single(w.Of<ItemMerged>());
        Assert.Equal(("acme/orders#21", "adoption"), (message.Item.ToString(), message.Source));
        Assert.Equal("adoption", Assert.Single(w.Merged.Rows).Source);
        // the second start derives the same row once and sends the message again; the table has the one row
        await w.Run();
        Assert.Single(w.Merged.Rows);
    }

    [Fact]
    public async Task E5_DeliveredToIsBackfilledFromTheSagasRowsAndTheTrainsHistory_NeverOverTheTrainsOwnRow()
    {
        var w = new World().Put(7, Verify, State(extra: Merged("release-queue")));
        var train = TrainState.New(TrainKey);
        train.Releases["1.0.7"] = new TrainRelease { ReleaseId = "R-7", Version = "1.0.7", Commit = Sha, Assembled = Now };
        train.Deployments["1.0.7"] = new Dictionary<string, TrainDeployment> { ["tdd"] = new() { DeploymentId = "D-1", Phase = "Succeeded", StartedAt = Now, FinishedAt = Now } };
        train.Current = "1.0.7";
        train.DeliveredItems.Add($"acme/orders#7|{Sha}");
        train.DeliveredBy["1.0.7"] = [new TrainCandidate { Item = "acme/orders#7", Commit = Sha, PullRequest = 11, MergedAt = Now }, new TrainCandidate { Item = "acme/orders#3", Commit = new string('c', 40), PullRequest = 3, MergedAt = Now }];
        await w.Trains.Save(TrainKey, train, 0, default);
        await w.Delivered.RecordIfAbsent(new DeliveredTo(new ItemId(Home, 3), "tdd", "1.0.6"), Now.AddDays(-1), default);   // (the table's own row for #3)
        var report = await w.Run();
        Assert.Equal("1.0.6", w.Delivered.Rows[("acme/orders#3", "tdd")].Release);
        Assert.Equal("1.0.7", w.Delivered.Rows[("acme/orders#7", "tdd")].Release);
        Assert.Equal(1, report.DeliveredToWritten);
        Assert.Equal(0, (await w.Run()).DeliveredToWritten);
    }

    // ---- F: the rows the shadow built ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task F1_ACandidateNoMergedItemsRowBacks_IsRemoved_OneItDoesHold_Stays()
    {
        var w = new World().Put(7, Verify, State(extra: Merged("release-queue")));
        var train = TrainState.New(TrainKey);
        train.Candidates["acme/orders#7"] = new TrainCandidate { Item = "acme/orders#7", Commit = Sha, PullRequest = 11, MergedAt = Now };
        train.Candidates["acme/orders#5"] = new TrainCandidate { Item = "acme/orders#5", Commit = new string('d', 40), PullRequest = 5, MergedAt = Now };
        await w.Trains.Save(TrainKey, train, 0, default);
        var report = await w.Run();
        Assert.Equal(1, report.CandidatesRemoved);
        Assert.Equal(["acme/orders#7"], (await w.Trains.Load(TrainKey, default))!.State.Candidates.Keys);
    }

    [Fact]
    public async Task F2_WithoutADeriverTheMergedItemsAreNotTheEvidenceOfTheTrain_NoCandidateIsRemoved()
    {
        var w = new World { WithDeriver = false };
        var train = TrainState.New(TrainKey);
        train.Candidates["acme/orders#5"] = new TrainCandidate { Item = "acme/orders#5", Commit = new string('d', 40), PullRequest = 5, MergedAt = Now };
        await w.Trains.Save(TrainKey, train, 0, default);
        var report = await w.Run();
        Assert.Equal((0, "done"), (report.CandidatesRemoved, report.Outcome));
        Assert.Single((await w.Trains.Load(TrainKey, default))!.State.Candidates);
    }

    [Fact]
    public async Task F3_TheMergeLineKeepsTheEntriesOfOpenRequests_AndDropsTheRest()
    {
        var w = new World().Put(7, Verify, State(extra: Join(Owner(Verify), Pass(), "\"design\": { \"docs_only\": true }")));
        await w.Run();
        // a request of the saga (it is on Merge and holds its place) and one of an item that has no row
        var line = MergeTrainState.New("acme/orders");
        line.Line.Add(new MergeTrainEntry { Item = "acme/orders#7", PullRequest = 11, Order = 1, Generation = 1 });
        line.Line.Add(new MergeTrainEntry { Item = "acme/orders#40", PullRequest = 40, Order = 2, Generation = 1 });
        await w.MergeTrains.Save("acme/orders", line, 0, default);
        Assert.Equal(StepKind.Merge, w.Row(7).Step);
        var report = await w.Run();
        Assert.Equal(1, report.MergeLineRemoved);
        Assert.Equal(["acme/orders#7"], (await w.MergeTrains.Load("acme/orders", default))!.State.Line.Select(e => e.Item));
    }

    // ---- G: the lease and the faults -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task G1_ARunWhoseLeaseIsGone_EndsAborted_AndLeavesTheRestToTheNextHolder()
    {
        var w = InFlight();
        var asked = 0;
        var report = await w.Run(acting: () => ++asked <= 1);   // (the lease is lost after the first item)
        Assert.Equal("aborted", report.Outcome);
        Assert.True(w.Items.States.Count < 2);
        var next = await w.Run();
        Assert.Equal(("done", 2), (next.Outcome, w.Items.States.Count));
    }

    [Fact]
    public async Task G2_AnItemThatCannotBeRead_IsAnErrorOfTheRun_TheOthersAreAdopted_ThePartialRunIsFinishedByTheNext()
    {
        var w = InFlight();
        w.Reads.Failing.Add(7);
        var report = await w.Run();
        Assert.Equal(("partial", 1, 1), (report.Outcome, report.Errors, report.Adopted));
        Assert.Equal(1, report.ErrorKinds["HttpRequestException"]);
        w.Reads.Failing.Clear();
        var next = await w.Run();
        Assert.Equal(("done", 1, 1), (next.Outcome, next.Adopted, next.Kept));
    }

    [Fact]
    public async Task G3_AHostThatIsNotUp_LosesNoRow_TheReArmRepairsWhatNeverLeft()
    {
        var w = InFlight();
        w.HostUp = false;
        var report = await w.Run();
        Assert.Equal(("done", 2), (report.Outcome, w.Items.States.Count));
        Assert.True(report.PublishFailed > 0);
        Assert.Empty(w.Published);
        // the rows carry what they ask for (the timers' times, the request for a slot): WorkItemRearm sends what is overdue and silent
        Assert.NotEmpty(WorkItemSaga.TimersOf(w.Row(7)));
        Assert.NotEmpty(WorkItemSaga.CommandsOf(w.Row(8)));
    }

    [Fact]
    public async Task G4_ACancelledRunIsAborted_AndRethrows()
    {
        var w = InFlight();
        using var cts = new CancellationTokenSource();
        w.Reads.OnRead = () => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Runner().Run(false, "", () => true, cts.Token));
    }

    [Fact]
    public void G5_TheMessageIdOfAPullRequestIsFixedByItAndItsCommit()
    {
        Assert.Equal(AdoptionRunner.MessageIdOf(Home, 9, Sha.ToUpperInvariant()), AdoptionRunner.MessageIdOf(Home, 9, Sha));
        Assert.NotEqual(AdoptionRunner.MessageIdOf(Home, 9, Sha), AdoptionRunner.MessageIdOf(Home, 10, Sha));
    }
}
