using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The set of the saga's items (V2Ownership) is read before each sweep, and the sweep reads the board after it; the intake starts a saga between the two, the card moves, and a session that is offered the item
/// is faster than the next read. So every path that is told of an item by someone else looks the item up when the set lacks it (one point read of its key), the sweep reads the set again after the script, and
/// a read is published as one value and only when it began after the published one.
/// </summary>
public sealed class OwnershipLookupTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    const string Section = "## Technical Design\nthe design";

    static DocsPlanSettings Settings() => DocsPlanSettingsReader.From(JsonNode.Parse(
        """{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"},"intake":{"v2Plans":["docs","feature"]}}"""));

    static string? ColumnOf(StepKind step) => step switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null };

    /// <summary>The store with hooks: what a point read and a full read do, so that a test can hold one back, fail it, or count it.</summary>
    sealed class Store : IWorkItemStore
    {
        public readonly InMemoryWorkItemStore Inner = new();
        public int Loads, Reads;
        public Func<string, CancellationToken, Task>? OnLoad;
        public Func<int, Task>? AfterRead;   // (after the rows were read: the answer is as old as that)
        public Func<Task>? AfterLoad;        // (after a row was read by its key)
        public async Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) { Interlocked.Increment(ref Loads); if (OnLoad is { } h) await h(itemKey, ct); var r = await Inner.Load(itemKey, ct); if (AfterLoad is { } a) await a(); return r; }
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => Inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => Inner.OwnedItems(ct);
        public async Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Reads);
            var rows = await Inner.OwnedStates(ct);
            if (AfterRead is { } h) await h(n);
            return rows;
        }
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => Inner.InFlightByRepository(ct);
    }

    sealed class Rig
    {
        public readonly Store Store = new();
        public readonly WorkItemService Svc;
        public readonly V2Ownership Own;
        public readonly List<WorkItemInput> Published = [];
        public DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

        public Rig(TimeSpan? findLimit = null)
        {
            Svc = new WorkItemService(Store.Inner, new WorkItemSaga(new DocsPort(), Settings().Policy));
            Own = new V2Ownership(Store, Home, ColumnOf, null, findLimit);
        }

        /// <summary>A saga that started after the set was read: the set has not been read since.</summary>
        public async Task StartedAfterTheRead(int n = 7, string plan = "docs") { await Own.RefreshAsync(); await Svc.Handle(new StartWorkItem(new ItemId(Home, n), "intake", plan)); }

        public V2VerbFeed Feed(IVerbRouter inner) => new(inner, Own, Store, Home, Settings(), m => { Published.Add(m); return Task.FromResult(true); }, null, _ => Task.CompletedTask, () => Now);
        public IEnumerable<WorkItemInput> Inputs => Published.Where(m => m is not LeaseRenewed);
    }

    sealed class Inner(int code, string output) : IVerbRouter
    {
        public readonly List<(string Verb, string Stdin)> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls.Add((verb, stdin)); return Task.FromResult(new VerbAnswer(code, output)); }
    }

    static string Body(object? extra = null)
    {
        var o = new JsonObject { ["item"] = 7 };
        if (extra is not null) foreach (var kv in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o.ToJsonString();
    }

    static WorkItemState Completed(WorkItemState state) { state.Status = WorkItemStatus.Completed; return state; }

    // ---- the lookup ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASagaThatStartedAfterTheRead_IsFoundByItsKey_WithoutReadingTheTable_AndIsOfferedFromThen()
    {
        var rig = new Rig(); await rig.StartedAfterTheRead();
        Assert.False(rig.Own.Owns(7)); Assert.Empty(rig.Own.Offers);
        Assert.Equal(1, rig.Store.Reads);
        Assert.True(await rig.Own.OwnsOrFindAsync(7));
        Assert.Equal((1, 1), (rig.Store.Loads, rig.Store.Reads));      // (one point read, and the table was not read)
        Assert.True(rig.Own.Owns(7));
        Assert.Equal([(7, "Technical Design")], rig.Own.Offers);
        Assert.Single(rig.Own.States);
        Assert.True(await rig.Own.OwnsOrFindAsync(7));
        Assert.Equal(1, rig.Store.Loads);                              // (known now: no second read)
    }

    [Fact]
    public async Task AnItemWithoutASaga_IsNotOwned_AndCostsOnePointRead_EachTime()
    {
        var rig = new Rig();
        Assert.False(await rig.Own.OwnsOrFindAsync(99)); Assert.False(await rig.Own.OwnsOrFindAsync(99));
        Assert.Equal((2, 0), (rig.Store.Loads, rig.Store.Reads));
        Assert.Equal(0, rig.Own.Count);
    }

    [Fact]
    public async Task ASagaThatIsCompleted_IsNotOwned()
    {
        var rig = new Rig();
        await rig.Store.Save(Item.ToString(), new WorkItemState { Item = Item.ToString(), Generation = 1, Status = WorkItemStatus.Completed, Plan = "docs", Step = StepKind.Merge }, 0, default);
        Assert.False(await rig.Own.OwnsOrFindAsync(7));
        Assert.Equal(0, rig.Own.Count);
    }

    [Fact]
    public async Task CallersThatComeWhileTheReadOfAnItemIsInFlight_WaitForIt_AndGetItsAnswer()
    {
        var rig = new Rig(); await rig.StartedAfterTheRead();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.OnLoad = (_, _) => gate.Task;
        var first = rig.Own.OwnsOrFindAsync(7); var second = rig.Own.OwnsOrFindAsync(7); var third = rig.Own.OwnsOrFindAsync(7);
        await Task.Delay(100);
        Assert.False(first.IsCompleted || second.IsCompleted || third.IsCompleted);   // (they wait; they do not answer "not owned")
        gate.SetResult();
        Assert.All(await Task.WhenAll(first, second, third), Assert.True);
        Assert.Equal(1, rig.Store.Loads);
    }

    [Fact]
    public async Task ALookupThatFails_LeavesTheAnswerAsItWas_AndPublishesNothing()
    {
        var rig = new Rig(); await rig.StartedAfterTheRead();
        rig.Store.OnLoad = (_, _) => throw new TimeoutException("the table");
        Assert.False(await rig.Own.OwnsOrFindAsync(7));
        Assert.Equal(0, rig.Own.Count); Assert.Empty(rig.Own.Offers); Assert.Empty(rig.Own.States);
        // the table is not asked again at once: each call would wait for the limit
        rig.Store.OnLoad = null;
        Assert.False(await rig.Own.OwnsOrFindAsync(7));
        Assert.Equal(1, rig.Store.Loads);
    }

    [Fact]
    public async Task ALookupThatHangs_ReturnsAtTheLimit_WithTheAnswerAsItWas()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(150)); await rig.StartedAfterTheRead();
        rig.Store.OnLoad = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await rig.Own.OwnsOrFindAsync(7));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, rig.Own.Count); Assert.Empty(rig.Own.Offers);
    }

    // ---- the published value ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALookupThatIsOlderThanAFullRead_DoesNotBringBackWhatThatReadLeftOut()
    {
        var rig = new Rig(); await rig.StartedAfterTheRead();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.AfterLoad = () => held.Task;                              // (the lookup has read the open saga and is held back)
        var lookup = rig.Own.OwnsOrFindAsync(7);
        await Task.Delay(50);
        var s = (await rig.Store.Inner.Load(Item.ToString(), default))!;
        await rig.Store.Inner.Save(Item.ToString(), Completed(s.State), s.Revision, default);   // (the saga completed)
        Assert.True(await rig.Own.RefreshAsync());                          // (a read that began after the lookup: the saga is not in it)
        held.SetResult();
        Assert.False(await lookup);
        Assert.False(rig.Own.Owns(7)); Assert.Empty(rig.Own.Offers); Assert.Empty(rig.Own.States);
    }

    [Fact]
    public async Task TheReadThatFollowsALookup_IsPublished_NotTakenForAnOlderOne()
    {
        var rig = new Rig(); await rig.StartedAfterTheRead();
        Assert.True(await rig.Own.OwnsOrFindAsync(7));
        var s = (await rig.Store.Inner.Load(Item.ToString(), default))!;
        await rig.Store.Inner.Save(Item.ToString(), Completed(s.State), s.Revision, default);
        Assert.True(await rig.Own.RefreshAsync());
        Assert.False(rig.Own.Owns(7));                                      // (the sweep's read after the script sees what is there now)
    }

    [Fact]
    public async Task TheSetIsNotLoaded_UntilAFullReadIsPublished_ALookupDoesNotLoadIt()
    {
        var rig = new Rig();
        await rig.Svc.Handle(new StartWorkItem(Item, "intake", "docs"));
        await rig.Svc.Handle(new StartWorkItem(new ItemId(Home, 8), "intake", "docs"));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.AfterRead = n => n == 1 ? first.Task : Task.CompletedTask;
        var startup = rig.Own.RefreshAsync();                               // (the first read of the table, held back)
        await Task.Delay(50);
        Assert.True(await rig.Own.OwnsOrFindAsync(7));                      // (a lookup finds one item meanwhile)
        Assert.False(rig.Own.Loaded);                                       // (one item is not the set: the sweep must not act on it)
        first.SetResult(); await startup;
        Assert.False(rig.Own.Loaded);                                       // (the held read began before the lookup and is not published)
        Assert.True(await rig.Own.RefreshAsync());
        Assert.True(rig.Own.Loaded); Assert.True(rig.Own.Owns(8)); Assert.True(rig.Own.Owns(7));
    }

    [Fact]
    public async Task ALateOlderRead_CannotTakeAwayWhatANewerOneSaw()
    {
        var rig = new Rig();
        var older = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.AfterRead = n => n == 1 ? older.Task : Task.CompletedTask;   // (the first read has read an empty table and is held back)
        var late = rig.Own.RefreshAsync();
        await Task.Delay(50);
        await rig.Svc.Handle(new StartWorkItem(Item, "intake", "docs"));
        Assert.True(await rig.Own.RefreshAsync());                           // (the newer read)
        Assert.True(rig.Own.Owns(7)); Assert.Equal([(7, "Technical Design")], rig.Own.Offers);
        older.SetResult();
        Assert.True(await late);                                             // (it read well; its answer is not the published one)
        Assert.True(rig.Own.Owns(7)); Assert.Equal([(7, "Technical Design")], rig.Own.Offers); Assert.Single(rig.Own.States);
    }

    [Fact]
    public async Task AReadThatBeganBeforeALookup_CannotTakeTheFoundSagaOut()
    {
        var rig = new Rig();
        var older = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.AfterRead = n => n == 1 ? older.Task : Task.CompletedTask;
        var late = rig.Own.RefreshAsync();                                   // (reads an empty table, held back)
        await Task.Delay(50);
        await rig.Svc.Handle(new StartWorkItem(Item, "intake", "docs"));
        Assert.True(await rig.Own.OwnsOrFindAsync(7));
        older.SetResult(); await late;
        Assert.True(rig.Own.Owns(7));
    }

    // ---- the feed ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFlagOfAFeatureCompletion_IsGivenToAnItemWhoseSagaStartedAfterTheRead()
    {
        var rig = new Rig();
        await rig.StartedAfterTheRead(plan: "feature");
        var s = (await rig.Store.Inner.Load(Item.ToString(), default))!.State;
        await rig.Svc.Handle(new ReviewRecorded(Item, 1, s.ArmId, "r1", true)); await rig.Svc.Handle(new StepCompleted(Item, 1, s.ArmId, "d1"));   // (the design is over: the saga is in Develop)
        Assert.False(rig.Own.Owns(7));
        var inner = new Inner(0, """{"ok":true,"completed":true,"moved_to":"Functional Testing"}""");
        await rig.Feed(inner).RunAsync("complete", Body(new { column = "Development" }), TimeSpan.FromSeconds(1));
        var call = JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject();
        Assert.True(call["ftWithPendingChecks"]!.GetValue<bool>());     // (given before the script ran, from the lookup)
    }

    [Fact]
    public async Task TheFlagOfAVerdict_IsGivenToAnItemWhoseSagaStartedAfterTheRead()
    {
        var rig = new Rig();
        await rig.StartedAfterTheRead();
        var inner = new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"merge"}""");
        await rig.Feed(inner).RunAsync("validation-verdict", Body(new { passed = true, pr = 11 }), TimeSpan.FromSeconds(1));
        var call = JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject();
        Assert.True(call["sagaMerges"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheFlags_AreNotGivenToAnItemWithoutASaga_AndTheCallerIsNotHeldUpByATableThatFails()
    {
        var rig = new Rig();
        rig.Store.OnLoad = (_, _) => throw new TimeoutException("the table");
        var inner = new Inner(0, """{"ok":true,"recorded":true,"passed":true}""");
        var answer = await rig.Feed(inner).RunAsync("validation-verdict", Body(new { passed = true, pr = 11 }), TimeSpan.FromSeconds(1));
        Assert.Equal(0, answer.Code);
        Assert.False(JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject().ContainsKey("sagaMerges"));
        Assert.Empty(rig.Published);
    }

    [Fact]
    public async Task ASignOfLifeAnsweredByAV2Route_IsToldToASagaThatStartedAfterTheRead()
    {
        var rig = new Rig();
        await rig.StartedAfterTheRead();
        await rig.Feed(new Inner(0, "{}")).Observe("open-pr", Body(), """{"ok":true}""");
        Assert.IsType<LeaseRenewed>(Assert.Single(rig.Published));
        Assert.True(rig.Own.Owns(7));
    }

    [Fact]
    public async Task ASagaThatStartedAfterTheRead_HearsTheDesign_FromTheFeedOfTheScript()
    {
        var rig = new Rig();
        await rig.StartedAfterTheRead();
        await rig.Feed(new Inner(0, """{"ok":true,"appended":true}""")).RunAsync("append-body", Body(new { content = Section + "\n<!-- aisf:docs-only -->" }), TimeSpan.FromSeconds(1));
        Assert.IsType<DesignRecorded>(Assert.Single(rig.Inputs));
    }

    // ---- the other paths that are told of an item ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APark_ByAPersonOrTheRuntime_InTheFirstSecondsOfASaga_ReachesTheSaga()
    {
        var rig = new Rig();
        await rig.StartedAfterTheRead();
        var inner = new Inner(0, """{"ok":true,"park":{"at":"2026-10-31T10:00:00Z","reason":"by a person"}}""");
        var admin = new ItemAdminFeed(inner, rig.Own, Home, m => { rig.Published.Add(m); return Task.FromResult(true); });
        await admin.RunAsync("park-item", Body(), TimeSpan.FromSeconds(1));
        Assert.IsType<ParkWorkItem>(Assert.Single(rig.Published));

        var other = new Rig(); await other.StartedAfterTheRead();
        var inputs = new SagaInputs(other.Own, other.Store, Home, m => { other.Published.Add(m); return Task.FromResult(true); }, _ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.True(await inputs.Parked(7, "runtime-failed", "park|k"));
        Assert.IsType<ParkWorkItem>(Assert.Single(other.Published));
        Assert.True(await inputs.Budget(7, BudgetKind.Launch, "launch|x", "cursor"));
        Assert.Equal(2, other.Published.Count);
    }

    [Fact]
    public async Task APark_OfAnItemWithoutASaga_IsNotToldToTheSaga()
    {
        var rig = new Rig();
        var inputs = new SagaInputs(rig.Own, rig.Store, Home, m => { rig.Published.Add(m); return Task.FromResult(true); }, _ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.False(await inputs.Parked(8, "runtime-failed", "park|k"));
        Assert.Empty(rig.Published);
    }

    // ---- the sweep ---------------------------------------------------------------------------------------------------------------------------------

    static readonly SagaLabelRules Rules = SagaLabelRules.From(JsonNode.Parse("""{ "priorityLabels": { "top": "priority:top", "high": "priority:high" }, "testLabel": "factory test", "ownerHoldLabel": "hold:owner" }"""));

    // The script reads the board after the set was read. A saga that started meanwhile has its card in a working column, and the script offers the item as v1's: the sweep reads the set again before it drops v1's word about
    // the saga's items, so the plant gets no offer of v1's for an item that has a saga (the saga's own offer, with its slot, is what the plant is given).
    [Fact]
    public async Task ASagaThatStartsWhileTheScriptRuns_IsNotOfferedToThePlantAsV1sItem()
    {
        var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30) { Msg = new MessagingSettings { Configured = true, Enabled = true, OwnedGraceSeconds = 0 } };
        var rig = new Rig();
        await rig.Own.RefreshAsync();   // (the set read before the script: empty)
        var runner = new Runner("/nonexistent/factory.ps1");
        var routes = new V2VerbRoutes();
        var calls = 0;
        routes.Add(BoardRoutes.Verbs, "sync-board", async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await rig.Svc.Handle(new StartWorkItem(Item, "intake", "feature"));   // (the saga has its row, its card moves, the script reads the board; a feature item waits for a slot of the scheduler, so the saga offers nothing yet)
            return new VerbAnswer(0, new JsonObject
            {
                ["ok"] = true, ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["unconfirmed"] = new JsonArray(), ["owned"] = new JsonObject(), ["owners"] = new JsonObject(), ["pins"] = new JsonObject(),
                ["parks"] = new JsonArray(), ["release_queue"] = new JsonArray(), ["held"] = new JsonArray(),
                ["offers"] = new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Technical Design", ["kind"] = "own-item", ["reason"] = "new" }, new JsonObject { ["item"] = 8, ["column"] = "Technical Design", ["kind"] = "own-item", ["reason"] = "new" }),
            }.ToJsonString());
        });
        runner.Routes = routes;
        var hub = new EventHub();
        var qc = new QcHub(runner, plant, hub, new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));
        var sweeper = new Sweeper(runner, hub, plant, qc, 3600, true, null, Task.CompletedTask) { MayAct = () => true, PlantJobs = true, V2 = rig.Own, Feed = new SagaPlantFeed(rig.Own, plant.OwnerNameOf, _ => null, Rules) };
        await sweeper.StartAsync(default);
        try
        {
            var waiting = new List<int>();
            for (var i = 0; i < 200 && !waiting.Contains(8); i++)
            {
                waiting = plant.PlantData()["waiting"]!.AsArray().Select(r => r!["item"]!.GetValue<int>()).ToList();
                if (!waiting.Contains(8)) await Task.Delay(50);
            }
            Assert.Contains(8, waiting);                 // (v1's offer of an item with no saga reached the plant: the observation works)
            Assert.DoesNotContain(7, waiting);           // (and the one of the item that has a saga did not)
            Assert.True(rig.Own.Owns(7));
        }
        finally { await sweeper.StopAsync(default); }
    }
}
