using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.PlantState;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Gateway.Tests.V2ItemViewsTests;

namespace Gateway.Tests;

/// <summary>
/// W9.10k: the plant survives a restart (its assignments, owners, bot holders and drains come back from the store, corrected from the saga's table and the claims) and is fed from saga state (owners' leases, parks, pins, priorities,
/// runtime labels, the release queue), the patrol digest reads the parked items of the views, the release check counts what the saga has in flight, and the plant manager's release-owner and requeue reach the saga. No database: the store
/// of these tests is in memory (the SQL one is tested in Fworker.Workflow.Tests, PlantStateStoreTests).
/// </summary>
public sealed class PlantStateGatewayTests
{
    static readonly RepoId Home = new("acme", "orders");

    // ---- the store, the plant and the coordinator --------------------------------------------------------------------------------------------------

    sealed class MemoryStore : IPlantStateStore
    {
        public PlantStateRead? Row; public int Saves; public bool FailLoad;
        public Task<bool> Save(JsonObject snapshot, long epoch, string holder, CancellationToken ct = default)
        {
            if (Row is { } r && r.Epoch > epoch) return Task.FromResult(false);
            // through the real split and join: what is stored is what the rows and the document make
            var rows = PlantStateSplit.Split(snapshot);
            Row = new PlantStateRead(PlantStateSplit.Join(rows.Assignments, rows.Holders, rows.Drains, rows.Document), epoch, holder, DateTimeOffset.UtcNow); Saves++;
            return Task.FromResult(true);
        }
        public Task<PlantStateRead?> Load(CancellationToken ct = default) => FailLoad ? throw new InvalidOperationException("database down") : Task.FromResult(Row);
    }

    static Plant NewPlant() => new("secret", (_, _, _, _) => Task.CompletedTask, 30) { Msg = new MessagingSettings { Configured = true, Enabled = true, OwnedGraceSeconds = 0 } };

    static JsonObject Seed() => (JsonObject)JsonNode.Parse($$"""
        {
          "v": 1, "at": "{{DateTime.UtcNow:o}}",
          "assignments": [
            { "id": "a-1f2b", "item": 12, "kind": "own-item", "role": "owner", "column": "Development", "mode": "resume", "reason": "relaunch", "worker": "alice", "pr": 41, "pin": "alice",
              "accepted": true, "acceptedAt": "{{DateTime.UtcNow.AddMinutes(-2):o}}", "createdAt": "{{DateTime.UtcNow.AddMinutes(-9):o}}", "slot": "slot-7", "runtime": "cursor-cloud", "arm": "1.3" },
            { "id": "a-0c01", "item": 9, "kind": "own-item", "role": "owner", "column": "Technical Design", "mode": "first", "reason": "new", "worker": "", "pr": 0, "pin": "",
              "accepted": false, "acceptedAt": "", "createdAt": "{{DateTime.UtcNow.AddMinutes(-1):o}}" }
          ],
          "owners": { "12": "alice", "30": "bob" }, "bots": { "12": "bot-1" }, "drained": ["carol"], "managerDrained": ["dave"], "managerMode": "advise"
        }
        """)!;

    static (SnapshotCoordinator Coordinator, List<string> Verbs) Coordinator(Plant plant, IPlantStateStore? store, bool leaderOn = false)
    {
        var verbs = new List<string>();
        var leader = new GatewayLeader(new Runner("/nonexistent/factory.ps1"), new LeaderSettings { On = leaderOn }, "0.0.0");
        var c = new SnapshotCoordinator(true, 30, leader, (verb, _, _) => { lock (verbs) verbs.Add(verb); return Task.FromResult((0, verb == "plant-snapshot-load" ? """{"found":false}""" : "{}")); }, plant, new SweepGap()) { Sql = store };
        return (c, verbs);
    }

    static string[] Ids(Plant p) => p.Snapshot()["assignments"]!.AsArray().Select(a => a!["id"]!.ToString()).Order().ToArray();

    // ---- the restart ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlantsAssignmentsOwnersHoldersAndDrains_ComeBackFromTheStore_InANewProcess()
    {
        var store = new MemoryStore();
        var before = NewPlant();
        var (na, no, skipped) = before.Restore(Seed(), 30);
        Assert.Equal((2, 2, ""), (na, no, skipped));
        var (a, aVerbs) = Coordinator(before, store);
        await a.LoadOnceAsync();   // (the first start: the store holds nothing and the ref nothing)
        Assert.True(await a.SaveAsync(true, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, store.Saves);
        Assert.DoesNotContain("plant-snapshot-save", aVerbs);   // the plant is in the database, not in the ref

        // the process ends; a new one starts with the same database
        var after = NewPlant();
        var (b, bVerbs) = Coordinator(after, store);
        await b.RestoreFromPredecessorAsync(null, "start");
        Assert.True(b.Restored.IsCompleted);
        Assert.Equal(Ids(before), Ids(after));
        var restored = after.Snapshot();
        Assert.Equal("alice", restored["owners"]!["12"]!.ToString()); Assert.Equal("bob", restored["owners"]!["30"]!.ToString()); Assert.Equal("bot-1", restored["bots"]!["12"]!.ToString());
        Assert.Equal(["carol"], restored["drained"]!.AsArray().Select(x => x!.ToString())); Assert.Equal(["dave"], restored["managerDrained"]!.AsArray().Select(x => x!.ToString()));
        // the accepted assignment keeps the id the worker's item token names, its worker and its scheduler slot; the offer that waited has none
        var accepted = restored["assignments"]!.AsArray().Single(x => x!["id"]!.ToString() == "a-1f2b")!;
        Assert.True(accepted["accepted"]!.GetValue<bool>()); Assert.Equal("alice", accepted["worker"]!.ToString()); Assert.Equal("slot-7", accepted["slot"]!.ToString()); Assert.Equal("cursor-cloud", accepted["runtime"]!.ToString()); Assert.Equal("1.3", accepted["arm"]!.ToString());
        Assert.False(restored["assignments"]!.AsArray().Single(x => x!["id"]!.ToString() == "a-0c01")!["accepted"]!.GetValue<bool>());
        Assert.Equal("sql", b.Status()["restore"]!["how"]!.ToString());
        Assert.DoesNotContain("plant-snapshot-load", bVerbs);   // nothing is read from the ref when the database has the plant
        Assert.Equal("advise", restored["managerMode"]!.ToString());   // the operator's run-time mode comes back too
    }

    [Fact]
    public async Task AnEmptyStoreReadsTheRefOnce_AndAStoreThatCannotBeReadStartsAnEmptyPlant()
    {
        var empty = new MemoryStore();
        var (c, verbs) = Coordinator(NewPlant(), empty);
        await c.RestoreFromPredecessorAsync(null, "start");
        Assert.Contains("plant-snapshot-load", verbs);   // the first start of this version: the ref may hold the previous version's plant
        Assert.True(c.Restored.IsCompleted);

        var down = new MemoryStore { FailLoad = true };
        var plant = NewPlant();
        var (d, dVerbs) = Coordinator(plant, down);
        await d.RestoreFromPredecessorAsync(null, "start");
        Assert.True(d.Restored.IsCompleted);   // (the sweep is not held back for ever by a database that is down)
        Assert.Equal("load-failed", d.Status()["restore"]!["skipped"]!.ToString());
        Assert.Empty(plant.Snapshot()["assignments"]!.AsArray());
        Assert.DoesNotContain("plant-snapshot-load", dVerbs);
    }

    [Fact]
    public async Task TheRestoreIsCorrected_FromTheSagasTableAndTheClaims_BeforeTheFirstSweep()
    {
        var store = new MemoryStore();
        var before = NewPlant(); before.Restore(Seed(), 30);
        var (a, _) = Coordinator(before, store);
        await a.LoadOnceAsync(); await a.SaveAsync(true, TimeSpan.FromSeconds(5));

        // item 9's saga completed while the plant was down; item 12's is open and its claim now says bob; item 30 has no saga (v1's)
        var sagas = new InMemoryWorkItemStore();
        var done = State(9); done.Status = WorkItemStatus.Completed; done.CompletedAt = DateTimeOffset.UtcNow;
        await sagas.Save(done.Item, done, 0, default);
        var open = State(12); await sagas.Save(open.Item, open, 0, default);
        var asked = new List<int>();
        var after = NewPlant();
        var (b, _) = Coordinator(after, store);
        var correction = new PlantRestoreCorrection(after, sagas, Home, (item, _) => { asked.Add(item); return Task.FromResult<string?>("bob"); });
        b.Correct = () => correction.Run();
        await b.RestoreFromPredecessorAsync(null, "start");

        Assert.Equal([12], asked);   // (only an open saga's owner is checked against its claim; 30 is the sweep's)
        var snap = after.Snapshot();
        Assert.DoesNotContain("a-0c01", Ids(after));                       // the offer of the completed item is gone
        Assert.Equal("bob", snap["owners"]!["12"]!.ToString());            // the claim's holder is the owner
        Assert.DoesNotContain(snap["assignments"]!.AsArray(), x => x!["id"]!.ToString() == "a-1f2b" && x["accepted"]!.GetValue<bool>());   // alice's accepted assignment went with her ownership
        Assert.Equal("bob", snap["owners"]!["30"]!.ToString());            // an item without a saga is left to the sweep
        var block = b.Status()["restore"]!["corrected"]!;
        Assert.Equal([9], block["forgotten"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal("bob", block["owners"]!["12"]!.ToString());
    }

    [Fact]
    public async Task AClaimThatIsGone_DropsTheOwner_AndOneThatCannotBeRead_LeavesIt()
    {
        var sagas = new InMemoryWorkItemStore();
        foreach (var n in new[] { 12, 30 }) { var s = State(n); await sagas.Save(s.Item, s, 0, default); }
        var plant = NewPlant(); plant.Restore(Seed(), 30);
        var correction = new PlantRestoreCorrection(plant, sagas, Home, (item, _) => Task.FromResult<string?>(item == 12 ? "" : null));
        var result = await correction.Run();
        Assert.Null(plant.OwnerNameOf(12));                    // no claim: released, taken back or never made
        Assert.Equal("bob", plant.OwnerNameOf(30));            // unreadable: as restored
        Assert.Equal([30], result["unread"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal("dropped", result["owners"]!["12"]!.ToString());
        Assert.DoesNotContain(plant.Snapshot()["assignments"]!.AsArray(), x => x!["id"]!.ToString() == "a-1f2b");
    }

    [Fact]
    public async Task TheClaimsHolder_IsReadFromGetItem_AClaimTakenBackIsNone_AnItemThatIsNotThereIsNone()
    {
        static IVerbRouter Router(int code, string output) => new FakeRouter((_, _) => (code, output));
        Assert.Equal("alice", await PlantRestoreCorrection.HolderOf(Router(0, """{"state":{"claim":{"holder":"alice","lease_until":"2026-10-30T11:00:00Z"}}}"""), 12, default));
        Assert.Equal("", await PlantRestoreCorrection.HolderOf(Router(0, """{"state":{"claim":{"holder":"alice","taken_back":"released-by-plant-manager"}}}"""), 12, default));
        Assert.Equal("", await PlantRestoreCorrection.HolderOf(Router(0, """{"state":{}}"""), 12, default));
        Assert.Equal("", await PlantRestoreCorrection.HolderOf(Router(12, "{}"), 12, default));
        Assert.Null(await PlantRestoreCorrection.HolderOf(Router(20, "{}"), 12, default));
        Assert.Null(await PlantRestoreCorrection.HolderOf(null, 12, default));
    }

    sealed class FakeRouter(Func<string, string, (int, string)> answer) : IVerbRouter
    {
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { var (c, o) = answer(verb, stdin); return Task.FromResult(new VerbAnswer(c, o)); }
    }

    // ---- the plant fed from saga state ---------------------------------------------------------------------------------------------------------------

    static WorkItemState State(int n, StepKind step = StepKind.Develop, Action<WorkItemState>? edit = null)
    {
        var s = new WorkItemState { Item = new ItemId(Home, n).ToString(), Generation = 1, Status = WorkItemStatus.Open, Plan = "feature", Step = step, ArmId = "1.1", StartedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        edit?.Invoke(s);
        return s;
    }

    static string? ColumnOf(StepKind step) => step switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null };

    static readonly SagaLabelRules Rules = SagaLabelRules.From(JsonNode.Parse("""{ "priorityLabels": { "top": "priority:top", "high": "priority:high" }, "testLabel": "factory test", "ownerHoldLabel": "hold:owner" }"""));

    static async Task<V2Ownership> Owned(params WorkItemState[] states)
    {
        var store = new InMemoryWorkItemStore();
        foreach (var s in states) await store.Save(s.Item, s, 0, default);
        var o = new V2Ownership(store, Home, ColumnOf);
        Assert.True(await o.RefreshAsync());
        return o;
    }

    [Fact]
    public async Task TheFeedBuildsWhatTheSweepWouldHaveSaid_FromTheStatesAndTheLabels()
    {
        var due = DateTimeOffset.UtcNow.AddMinutes(20);
        var ownership = await Owned(
            State(12, StepKind.Develop, s => s.LeaseDue = due),
            State(13, StepKind.Verify, s => { s.Parked = true; s.ParkReason = "merge-blocked: checks red on abc"; s.ParkUntil = due; }),
            State(14, StepKind.Deliver, s => { s.MergeCommit = "abc1234"; s.Delivered["tdd"] = "1.0.77"; }),
            State(15, StepKind.Deliver),   // not merged: nothing waits for a release
            State(16, StepKind.Develop));
        var labels = new Dictionary<int, IReadOnlyList<string>>
        {
            [12] = ["factory worker", "worker:alice", "priority:high", "runtime:copilot-cloud"],
            [13] = ["factory test", "hold:owner"],
            [14] = ["priority:top", "priority:high"],
        };
        var feed = new SagaPlantFeed(ownership, n => n == 12 ? "alice" : null, n => labels.GetValueOrDefault(n), Rules);
        var f = feed.Build();

        // the owner of 12 with the column of its step, its step's lease and its park state
        var owned = f["owned"]!.AsObject();
        Assert.Equal(["12"], owned.Select(kv => kv.Key));
        Assert.Equal(("alice", "Development", false), (owned["12"]!["holder"]!.ToString(), owned["12"]!["column"]!.ToString(), owned["12"]!["parked"]!.GetValue<bool>()));
        Assert.Equal(due.UtcDateTime, DateTime.Parse(owned["12"]!["lease_until"]!.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal("alice", f["owners"]!["12"]!.ToString());
        // the pin, the priority (top wins), the runtime and the test profile come from the labels
        Assert.Equal("alice", f["pins"]!["12"]!.ToString());
        Assert.Equal("high", f["priorities"]!["12"]!.ToString()); Assert.Equal("top", f["priorities"]!["14"]!.ToString());
        Assert.Equal("copilot-cloud", f["runtimes"]!["12"]!.ToString());
        Assert.Equal("testing", f["profiles"]!["13"]!.ToString());
        Assert.Equal([13], f["holds"]!.AsArray().Select(x => x!.GetValue<int>()));
        // the park, by its code
        var park = Assert.Single(f["parks"]!.AsArray())!;
        Assert.Equal((13, "Functional Testing", "merge-blocked"), (park["item"]!.GetValue<int>(), park["column"]!.ToString(), park["code"]!.ToString()));
        // the Release Queue: the merged item on its Deliver step, with the release it reached
        var queued = Assert.Single(f["release_queue"]!.AsArray())!;
        Assert.Equal((14, "1.0.77"), (queued["item"]!.GetValue<int>(), queued["release"]!.ToString()));
    }

    [Fact]
    public async Task NoLabelsRead_MeansNoPinOrPriorityIsClaimed_AndAnOwnerUnknownToThePlantHasNoOwnedEntry()
    {
        var ownership = await Owned(State(12), State(13));
        var f = new SagaPlantFeed(ownership, _ => null, _ => null, Rules).Build();
        Assert.Empty(f["owned"]!.AsObject()); Assert.Empty(f["owners"]!.AsObject()); Assert.Empty(f["pins"]!.AsObject()); Assert.Empty(f["priorities"]!.AsObject()); Assert.Empty(f["runtimes"]!.AsObject());
    }

    [Fact]
    public async Task TheFeedIsAddedToTheSweepsAnswer_AndAOneEntryListThePowerShellSentAsAnObjectBecomesAList()
    {
        var ownership = await Owned(State(14, StepKind.Deliver, s => s.MergeCommit = "abc"), State(13, StepKind.Verify, s => { s.Parked = true; s.ParkReason = "x"; }));
        var feed = new SagaPlantFeed(ownership, n => n == 14 ? "alice" : null, _ => null, Rules);
        var answer = (JsonObject)JsonNode.Parse("""{ "offers": [], "owned": { "5": { "holder": "bob" } }, "parks": [ { "item": 5, "code": "y" } ], "release_queue": { "item": 6, "release": "1.0.1" } }""")!;
        var added = feed.Apply(answer);
        Assert.True(added >= 4);
        Assert.Equal(["14", "5"], answer["owned"]!.AsObject().Select(kv => kv.Key).Order());   // v1's entry stays, the saga's is added
        Assert.Equal([5, 13], answer["parks"]!.AsArray().Select(x => x!["item"]!.GetValue<int>()).Order());
        var queue = Assert.IsType<JsonArray>(answer["release_queue"]);
        Assert.Equal([6, 14], queue.Select(x => x!["item"]!.GetValue<int>()).Order());
        // an answer without the fields gets them; a null or non-object answer is left alone
        var bare = (JsonObject)JsonNode.Parse("""{ "offers": [] }""")!;
        feed.Apply(bare); Assert.NotNull(bare["owned"]);
        Assert.Equal(0, feed.Apply(JsonValue.Create("x")!));
    }

    [Fact]
    public async Task WithoutTheFeed_ThePlantDropsTheOwnerOfASagaItem_WithTheFeed_ItKeepsIt()
    {
        // two full plans in a row that do not list an owner drop it (Plant.ReplaceOwned): that is what the sweep's filter did to every saga item
        static void Plans(Plant p, JsonObject owned) { for (var i = 0; i < 3; i++) p.Plan(new JsonArray(), null, null, owned, []); }
        var without = NewPlant(); without.Restore(Seed(), 30);
        Plans(without, new JsonObject());
        Assert.Null(without.OwnerNameOf(12));

        var ownership = await Owned(State(12, StepKind.Develop, s => s.LeaseDue = DateTimeOffset.UtcNow.AddMinutes(30)));
        var with = NewPlant(); with.Restore(Seed(), 30);
        var feed = new SagaPlantFeed(ownership, with.OwnerNameOf, _ => null, Rules);
        Plans(with, feed.Build()["owned"]!.AsObject());
        Assert.Equal("alice", with.OwnerNameOf(12));
    }

    [Fact]
    public async Task ASweepWhereTheEngineActs_ShowsTheSagasParksAndKeepsItsOwner()
    {
        var plant = NewPlant(); plant.Restore(Seed(), 30);
        var ownership = await Owned(State(12, StepKind.Develop, s => s.LeaseDue = DateTimeOffset.UtcNow.AddMinutes(30)), State(13, StepKind.Verify, s => { s.Parked = true; s.ParkReason = "master-broken: x"; }));
        var runner = new Runner("/nonexistent/factory.ps1");
        var routes = new V2VerbRoutes();   // (W9.9a: the Runner answers a verb that has a route from the table)
        routes.Add(BoardRoutes.Verbs, "sync-board", (_, _) => Task.FromResult(new VerbAnswer(0, new JsonObject { ["ok"] = true, ["offers"] = new JsonArray(), ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["unconfirmed"] = new JsonArray(),
            ["owned"] = new JsonObject(), ["owners"] = new JsonObject(), ["pins"] = new JsonObject(), ["parks"] = new JsonArray(), ["release_queue"] = new JsonArray(),
            // (what v1 said of the saga items: the filter takes it out, the feed puts the saga's own in)
            ["held"] = new JsonArray() }.ToJsonString())));
        runner.Routes = routes;
        var hub = new EventHub();
        var qc = new QcHub(runner, plant, hub, new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));
        var feed = new SagaPlantFeed(ownership, plant.OwnerNameOf, _ => null, Rules);
        var sweeper = new Sweeper(runner, hub, plant, qc, 3600, true, null, Task.CompletedTask) { MayAct = () => true, PlantJobs = true, V2 = ownership, Feed = feed };
        await sweeper.StartAsync(default);
        try
        {
            JsonArray unowned = [];
            for (var i = 0; i < 200; i++)
            {
                unowned = plant.Status()["unowned"]!.AsArray();
                if (unowned.Any(r => r!["item"]!.GetValue<int>() == 13)) break;
                await Task.Delay(50);
            }
            var row = Assert.Single(unowned, r => r!["item"]!.GetValue<int>() == 13)!;
            Assert.Equal(("parked", "master-broken"), (row["state"]!.ToString(), row["code"]!.ToString()));
            Assert.Equal("alice", plant.OwnerNameOf(12));
        }
        finally { await sweeper.StopAsync(default); }
    }

    // ---- the digest and the patrol, the release check -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePatrolDigestReports_TheParkedItemsOfTheViews_AndTheViewsLeaveOutWhatIsNotParkedOrNotOpen()
    {
        var source = new Source { Set = () => new WorkItemViewSet([
            Item("acme/orders", 13, parked: true, parkReason: "merge-blocked: checks red", history: [Line(DateTimeOffset.UtcNow.AddMinutes(-50), WorkItemActions.Parked, "Merge")]),
            Item("acme/orders", 14), Item("acme/orders", 15, status: WorkItemStatus.Completed, parked: true, parkReason: "old")], false) };
        var views = new V2ItemViews(source);
        var ownership = await Owned(State(13, StepKind.Merge));
        var feed = new SagaPlantFeed(ownership, _ => null, _ => null, Rules, views);
        Assert.Empty(feed.Attention());   // before the first read there is nothing (the digest never waits on the database)
        await feed.RefreshViewsAsync();
        var a = Assert.Single(feed.Attention());
        Assert.Equal((13, "parked", "merge-blocked"), (a.Item, a.Kind, a.Code));
        Assert.InRange((DateTime.UtcNow - a.Since).TotalMinutes, 49, 52);   // since the park, from the newest "parked" decision

        var plant = NewPlant(); plant.SagaAttentionSource = feed.Attention;
        var digest = plant.DigestNow().ToJsonString();
        Assert.Contains("\"kind\":\"parked\"", digest); Assert.Contains("\"item\":13", digest); Assert.Contains("\"code\":\"merge-blocked\"", digest);
        Assert.DoesNotContain("\"item\":15", digest);
        // one entry per park: the owners' trigger of the same park and the views' are one key (parked|item|code)
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(digest, "\"kind\":\"parked\"").Count);
    }

    [Fact]
    public async Task TheReleaseCheck_CountsAMergeTheSagaAskedFor_AndASlotWhoseSessionThePlantDoesNotHoldYet()
    {
        var ownership = await Owned(
            State(20, StepKind.Merge, s => s.MergeRequest = new MergeRequestState { Attempt = 1, PullRequest = 5, Head = "abc" }),
            State(21, StepKind.Develop, s => { s.SlotId = "slot-1"; }),
            State(12, StepKind.Develop, s => { s.SlotId = "slot-2"; }),       // the plant holds alice's session for 12
            State(22, StepKind.Develop, s => { s.SlotId = "slot-3"; s.Parked = true; }),
            State(23, StepKind.Develop));
        var plant = NewPlant(); plant.Restore(Seed(), 30);
        var feed = new SagaPlantFeed(ownership, plant.OwnerNameOf, _ => null, Rules);
        plant.SagaFlightSource = feed.InFlight;
        var r = plant.ReleaseCheck();
        Assert.False(r["ok"]!.GetValue<bool>()); Assert.Equal("not-quiet", r["code"]!.ToString());
        var reasons = r["reasons"]!.AsArray().Select(x => (x!["code"]!.ToString(), x["item"]!.GetValue<int>())).ToList();
        Assert.Contains(("saga-merge-in-flight", 20), reasons);
        Assert.Contains(("saga-session-starting", 21), reasons);
        Assert.DoesNotContain(reasons, x => x.Item2 is 12 or 22 or 23);   // held by a session / parked / nothing in flight

        // nothing in flight: the check is what it was
        plant.SagaFlightSource = () => [];
        Assert.DoesNotContain(plant.ReleaseCheck()["reasons"]!.AsArray(), x => x!["code"]!.ToString().StartsWith("saga-", StringComparison.Ordinal));
        Assert.Equal(["merge-requested", "slot-granted"], feed.InFlight().Select(f => f.Code).Distinct().Order());
    }

    // ---- the manager's actions reach the saga ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheManagersReleaseOfAnOwnerAndItsRequeue_AreSentToTheSagaOfTheItem()
    {
        var sent = new List<object>();
        var ownership = await Owned(State(12));
        var port = new KeeperSagaPort(ownership, Home, m => { sent.Add(m); return Task.FromResult(true); });
        Assert.True(port.Owns(12)); Assert.False(port.Owns(99));
        await port.OwnerReleased(12, "stalled owner", "manager-release|1");
        await port.Requeued(12, "stuck offer", "manager-requeue|2");
        var release = Assert.IsType<OwnerReleaseAsked>(sent[0]); var requeue = Assert.IsType<RequeueWorkItem>(sent[1]);
        Assert.Equal((new ItemId(Home, 12), 0, "manager-release|1", "stalled owner"), (release.Item, release.Generation, release.Key, release.Reason));
        Assert.Equal((new ItemId(Home, 12), 0, "manager-requeue|2", "stuck offer"), (requeue.Item, requeue.Generation, requeue.Key, requeue.Reason));
    }
}
