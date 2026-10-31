using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10j: the sessions of the items the saga schedules start from the saga and the scheduler. The ledger's executions are subtracted from what the plant's manager says it runs outside the ledger (concurrency and the
/// day's budget), the session starts on the runtime the slot is on, a start into a host that is down settles like "no capacity", a stop that failed is tried again, the saga's offer is made from its state, and the reaper
/// tells the scheduler which items are in flight.
/// </summary>
public sealed class SessionLedgerTests
{
    sealed class PlainRuntime : IWorkerRuntime
    {
        public string Kind => "cursor-cloud"; public int MaxConcurrent => 100;
        public int Provisions; public List<(string, string)> Stops = new();
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) { Provisions++; return Task.FromResult(new ProvisionResult(true, "exec-" + Provisions, "", "")); }
        public Task Deprovision(string workerName, string reason, CancellationToken ct) { lock (Stops) Stops.Add((workerName, reason)); return Task.CompletedTask; }
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>([]);
    }

    static async Task<RuntimeManager> Manager(PlainRuntime rt, int maxConcurrent = 100, int perDay = 100)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.Kind = "cursor-cloud"; limits.IsOn = () => true; limits.MaxConcurrent = maxConcurrent; limits.MaxExecutionsPerDay = perDay;
        var m = new RuntimeManager(cfg, rt, new RuntimeNonces(), limits) { MayAct = () => true };
        await m.Poll(CancellationToken.None);   // the executions are read: a start may be made
        return m;
    }

    static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(25);
        Assert.True(cond());
    }

    // ---- the ledger's executions are not counted twice -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLoad_LeavesOutTheExecutionsTheLedgerGranted_TheManagersOwnLimitsStillCountEveryOne()
    {
        var m = await Manager(new PlainRuntime(), maxConcurrent: 3, perDay: 30);
        m.Begin(5, "Development", ""); m.Begin(6, "Development", "");
        m.Begin(7, "Development", "", slot: "s1", arm: "1.2");
        Assert.Equal((2, 2, (string?)null), m.Load());   // the ledger counts #7 itself
        // the plant's own start is asked against all three (concurrency 3)
        Assert.Equal((false, "runtime-at-capacity"), m.CanStart());
        // a start for a slot of the ledger is not asked again: the scheduler admitted it against what the manager runs outside the ledger
        Assert.Equal((true, ""), m.CanStart(ledgerGranted: true));
    }

    [Fact]
    public async Task AnExecutionThatNeverStarted_IsRefundedFromBothCounts_AndTheDayRollsBothOver()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;
        m.MayAct = () => false;                                          // fenced: nothing is started and the execution is given back
        var t = m.Begin(7, "Development", "", slot: "s1");
        Assert.Equal((0, 0, (string?)null), m.Load());
        await m.Provision(t);
        Assert.Equal((0, 0, (string?)null), m.Load());
        m.MayAct = () => true;
        m.Begin(8, "Development", "", slot: "s2"); m.Begin(9, "Development", "");
        Assert.Equal((1, 1, (string?)null), m.Load());                   // #9 only
    }

    [Fact]
    public async Task AScenarioOfSessionsBeyondHalfTheDayBudget_RunsWithNoRuntimeBudgetHold()
    {
        // the day's budget is 10 executions. Before the manager left the ledger's executions out of what it runs outside the ledger, each one was counted by the ledger and by the manager, and the scheduler held the
        // sixth request with runtime-budget (half the budget). Now all ten run, and the eleventh waits for the budget, which is the only hold.
        var rt = new PlainRuntime(); var m = await Manager(rt, maxConcurrent: 100, perDay: 10);
        var cfg = JsonNode.Parse("""{ "api": { "cursor": { "enabled": true, "maxConcurrent": 100, "maxExecutionsPerDay": 10 } } }""");
        var svc = new CapacityService(new InMemoryCapacityStore(), new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), kind => kind == "cursor-cloud" ? new RuntimeOutside(m.Load().Running, m.Load().Today) : new RuntimeOutside(0, 0)));
        var held = new List<Held>();
        for (var n = 1; n <= 11; n++)
        {
            var r = await svc.Handle(new RequestSlot(new ItemId("acme", "orders", n), $"ask|{n}", 1, "cursor-cloud", CapacityPhases.Develop, new Fworker.Domain.PlanRank(false, null, false), false, false, 2, DateTimeOffset.UtcNow, [], Footprint.Empty));
            held.AddRange(r.Messages.OfType<Held>());
            // the plant starts the session of every grant on the slot's runtime, as RouteRuntime does
            foreach (var g in r.Messages.OfType<SlotGranted>())
            {
                Assert.True(m.CanStart(ledgerGranted: true).Ok);
                m.Begin(g.Item.Number, "Development", "", slot: g.SlotId);
            }
        }
        Assert.Equal("runtime-budget", Assert.Single(held).Code);          // ten requests, ten grants, no wait among them: the eleventh is over the budget, the only hold
        Assert.Equal(10, (await svc.Store.Load(CapacityKeys.Factory, default))!.State.Slots.Count);
        Assert.Equal((0, 0, (string?)null), m.Load());                     // and the manager reports nothing outside the ledger
    }

    // ---- a start into a host that is down ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStartDispatchedIntoAHostThatIsDown_SettlesLikeNoCapacity_NothingFailedAndTheSlotGoesBack()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        var refusals = new List<(int, string, string)>();
        m.OnStartRefused = (item, key, outcome) => refusals.Add((item, key, outcome));
        m.DispatchStart = _ => throw new WolverineHostDownException();
        var t = m.Begin(7, "Development", "", slot: "s1");
        await m.Provision(t);
        Assert.False(m.Alive(t.Name));
        Assert.Equal(0, rt.Provisions);
        var refusal = Assert.Single(refusals);
        Assert.Equal((7, "host-full"), (refusal.Item1, refusal.Item3));   // the scheduler frees the slot and the request waits again
        Assert.False(m.Exhausted(7, "Development"));                      // no failure of the item or the phase
        Assert.Equal((0, 0, (string?)null), m.Load());                    // the execution and its launch are given back
        Assert.True(m.CanStart().Ok);                                     // and the runtime is not taken for unhealthy
    }

    [Fact]
    public async Task AnyOtherFaultOfTheDispatch_IsStillAStartFailure()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        var refusals = new List<string>();
        m.OnStartRefused = (_, _, outcome) => refusals.Add(outcome);
        m.DispatchStart = _ => throw new InvalidOperationException("the outbox failed");
        await m.Provision(m.Begin(7, "Development", "", slot: "s1"));
        Assert.Equal(["start-failed"], refusals);
    }

    [Fact]
    public async Task AnOfferWaitsForTheHost_ThatCarriesItsStart()
    {
        var m = await Manager(new PlainRuntime());
        var up = false;
        m.DispatchStart = _ => Task.CompletedTask; m.DispatchReady = () => up;
        Assert.Equal((false, "host-not-running"), m.CanStart(ledgerGranted: true));
        Assert.Equal((false, "host-not-running"), m.CanStart());
        up = true;
        Assert.Equal((true, ""), m.CanStart(ledgerGranted: true));
        // v1 dispatches nothing: the host is not asked
        var v1 = await Manager(new PlainRuntime()); v1.DispatchReady = () => false;
        Assert.True(v1.CanStart().Ok);
    }

    // ---- the plant starts the session where the slot is ---------------------------------------------------------------------------------------

    static JsonObject Offer(int item, string slot, string runtime, string arm) => new() { ["item"] = item, ["column"] = "Development", ["kind"] = "own-item", ["reason"] = "handoff", ["v2"] = true, ["slot"] = slot, ["runtime"] = runtime, ["arm"] = arm };

    [Fact]
    public async Task ASlotsOffer_StartsTheSessionOnTheSlotsRuntime_UnderItsSlotAndArm_EvenWhereTheManagerIsFullAndAWorkerIsConnected()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt, maxConcurrent: 1);
        m.Begin(1, "Development", "");   // the plant's own session fills the manager's concurrency
        var tickets = new List<RuntimeManager.Ticket>();
        m.DispatchStart = t => { lock (tickets) tickets.Add(t); return Task.CompletedTask; };
        var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30) { Cursor = m };
        m.Plant = plant;
        plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);   // a connected session that would take an item the usual way
        plant.PlanSagaOffers(new JsonArray(Offer(7, "s4", "cursor-cloud", "1.3")), [7]);
        await Until(() => { lock (tickets) return tickets.Count == 1; });
        var t = tickets[0];
        Assert.Equal((7, "s4", "1.3"), (t.Item, t.Slot, t.Arm));
        Assert.True(m.Alive(t.Name));
        Assert.Equal((1, 1, (string?)null), m.Load());   // #1 only: the ledger counts the slot's execution itself
    }

    [Fact]
    public async Task ASlotsOffer_WaitsWhileTheHostIsDown_AndIsNotGivenToAConnectedWorker()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        var up = false; var started = 0;
        m.DispatchStart = _ => { started++; return Task.CompletedTask; }; m.DispatchReady = () => up;
        var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30) { Cursor = m };
        m.Plant = plant;
        plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        var offers = new JsonArray(Offer(7, "s4", "cursor-cloud", "1.3"));
        plant.PlanSagaOffers(offers, [7]);
        await Task.Delay(100);
        Assert.Equal(0, started);
        Assert.Equal("", m.ExistingFor(7));
        up = true;
        plant.PlanSagaOffers(new JsonArray(Offer(7, "s4", "cursor-cloud", "1.3")), [7]);
        await Until(() => started == 1);
        Assert.NotEqual("", m.ExistingFor(7));
    }

    [Fact]
    public async Task AnOfferWithNoSlot_IsRoutedAsBefore_TheLedgerIsNotInvolved()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt, maxConcurrent: 1);
        m.Begin(1, "Development", "");
        var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30) { Cursor = m };
        m.Plant = plant;
        var node = Offer(7, "s4", "cursor-cloud", "1.3"); node.Remove("slot"); node.Remove("runtime"); node.Remove("arm");
        plant.PlanSagaOffers(new JsonArray(node), [7]);
        await Task.Delay(100);
        Assert.Equal("", m.ExistingFor(7));   // the manager is full and no label asks for it: the item goes the local way
    }

    // ---- a stop that failed ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStopThatFailed_IsTriedAgain_AndEndsTheRecordAfterTheLastTry()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        m.StopRetryPause = TimeSpan.Zero;
        var stops = 0;
        m.DispatchStart = _ => Task.CompletedTask; m.DispatchStop = (_, _) => { stops++; return Task.CompletedTask; };
        var t = m.Begin(7, "Development", "", slot: "s1");
        await m.Provision(t);
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec", "", ""));
        await m.Deprovision(t.Name, "revoked");
        Assert.Equal(1, stops);
        // each failed answer is followed, at the next reading, by another stop; the last try ends the record
        for (var attempt = 1; attempt < RuntimeManager.MaxStopAttempts; attempt++)
        {
            m.CompleteStop(t.Name, false, "octopus 500");
            await m.Poll(CancellationToken.None);
            Assert.Equal(attempt + 1, stops);
            Assert.True(m.Alive(t.Name));
        }
        m.CompleteStop(t.Name, false, "octopus 500");
        await m.Poll(CancellationToken.None);
        Assert.False(m.Alive(t.Name));
        Assert.Equal(RuntimeManager.MaxStopAttempts, stops);
    }

    [Fact]
    public async Task AStopThatWasDone_IsNotTriedAgain_AndV1DoesNotRetryAtAll()
    {
        var rt = new PlainRuntime(); var m = await Manager(rt);
        m.StopRetryPause = TimeSpan.Zero;
        var stops = 0;
        m.DispatchStart = _ => Task.CompletedTask; m.DispatchStop = (_, _) => { stops++; return Task.CompletedTask; };
        var t = m.Begin(7, "Development", "");
        await m.Provision(t);
        await m.CompleteStart(t.Name, 7, "Development", new ProvisionResult(true, "exec", "", ""));
        await m.Deprovision(t.Name, "revoked");
        m.CompleteStop(t.Name, false, "x"); m.CompleteStop(t.Name, true, "");   // failed once, then the retry (a later answer) was done
        await m.Poll(CancellationToken.None);
        Assert.Equal(1, stops);

        var v1rt = new PlainRuntime(); var v1 = await Manager(v1rt);
        var u = v1.Begin(8, "Development", "");
        await v1.Provision(u);
        await v1.Deprovision(u.Name, "revoked");
        await v1.Poll(CancellationToken.None);
        Assert.Single(v1rt.Stops);
    }

    // ---- the saga's offer ----------------------------------------------------------------------------------------------------------------------

    static readonly RepoId Home = new("acme", "orders");
    static string? Column(StepKind k) => k switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null };

    [Fact]
    public async Task TheSagasOffer_IsMadeFromItsState_OnTheSlotItHolds_AndOnlyForTheSlotTheRequestNames()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(true) }));
        var item = new ItemId(Home, 7);
        await svc.Handle(new StartWorkItem(item, "intake"));
        var own = new V2Ownership(store, Home, Column);
        var planned = new List<(JsonArray Nodes, IReadOnlyCollection<int> Items)>();
        var port = new SessionStartPort(own.RefreshAsync, only => own.OfferNodes(_ => false, only), (nodes, items) => planned.Add((nodes, items)));
        var arm = (await store.Load(item.ToString(), default))!.State.ArmId;

        // no slot yet: the item waits for a grant and is offered nothing
        Assert.Equal("no-offer", await port.Run(new SessionStartRequested(item, 1, arm, "start|x|s1", "s1", "octopus", "host-a", "design"), default));
        await svc.Handle(new SlotAssigned(item, 1, "slot|s1", "s1", "octopus", "host-a", "design", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4)));
        // a request for another slot than the saga's current one is stale: its own request follows
        Assert.Equal("stale-slot", await port.Run(new SessionStartRequested(item, 1, arm, "start|x|s0", "s0", "octopus", "host-a", "design"), default));
        Assert.Empty(planned);

        Assert.Equal("offered", await port.Run(new SessionStartRequested(item, 1, arm, "start|x|s1", "s1", "octopus", "host-a", "design"), default));
        var (nodes, items) = Assert.Single(planned);
        Assert.Equal([7], items);
        var node = Assert.IsType<JsonObject>(Assert.Single(nodes));
        Assert.Equal(("Technical Design", "s1", "octopus", arm), (node["column"]!.ToString(), node["slot"]!.ToString(), node["runtime"]!.ToString(), node["arm"]!.ToString()));

        // an item a session holds is offered nothing: it goes on with its owner
        var owned = new SessionStartPort(own.RefreshAsync, only => own.OfferNodes(_ => true, only), (nodes2, items2) => planned.Add((nodes2, items2)));
        Assert.Equal("no-offer", await owned.Run(new SessionStartRequested(item, 1, arm, "start|x|s1", "s1", "octopus", "host-a", "design"), default));
        Assert.Single(planned);
    }

    [Fact]
    public async Task AfterABreak_TheSagasOffer_IsNotMadeForAnItemTheAdoptionHasNotExamined_UntilItIs()
    {
        // W9.9b (review): the row is still the old generation and v1 may have moved or merged the item; the plant must not start a session on it
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(true) }));
        var item = new ItemId(Home, 7);
        await svc.Handle(new StartWorkItem(item, "intake"));
        var arm = (await store.Load(item.ToString(), default))!.State.ArmId;
        await svc.Handle(new SlotAssigned(item, 1, "slot|s1", "s1", "octopus", "host-a", "design", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4)));
        var gate = new Fworker.Workflows.Adoption.ReadoptionGate();
        gate.Hold("v1 ran", newTerm: true);
        var own = new V2Ownership(store, Home, Column, gate);
        var planned = new List<(JsonArray Nodes, IReadOnlyCollection<int> Items)>();
        var port = new SessionStartPort(own.RefreshAsync, only => own.OfferNodes(_ => false, only), (nodes, items) => planned.Add((nodes, items)));
        var request = new SessionStartRequested(item, 1, arm, "start|x|s1", "s1", "octopus", "host-a", "design");

        Assert.Equal("no-offer", await port.Run(request, default));
        Assert.Empty(planned);
        Assert.True(own.Owns(7));   // (v1's sweep still leaves the item alone)
        gate.Adopted(item.ToString());
        Assert.Equal("offered", await port.Run(request, default));
        Assert.Single(planned);
    }

    [Fact]
    public async Task ASagaTableThatCannotBeRead_IsAnError_TheHostTriesTheRequestAgain()
    {
        var port = new SessionStartPort(_ => Task.FromResult(false), _ => [], (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => port.Run(new SessionStartRequested(new ItemId(Home, 7), 1, "1.1", "k", "s1", "octopus", null, "design"), default));
    }

    [Fact]
    public async Task AnItemOfAPlanThatUsesNoScheduler_IsOfferedWithNoSlot()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false) }));
        await svc.Handle(new StartWorkItem(new ItemId(Home, 7), "intake"));
        var own = new V2Ownership(store, Home, Column);
        await own.RefreshAsync();
        var node = Assert.Single(own.OfferNodes(_ => false));
        Assert.Null(node["slot"]); Assert.Null(node["runtime"]);
        Assert.Empty(own.Slots);
    }

    // ---- the reaper --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheReaper_TellsTheSchedulerTheItemsTheSagasHaveInFlight_AndNothingWhenItIsNotTheLeader_OrWhenTheTableCannotBeRead()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(true) }));
        await svc.Handle(new StartWorkItem(new ItemId(Home, 7), "intake"));   // asks for a slot: known to the scheduler
        await svc.Handle(new StartWorkItem(new ItemId(Home, 8), "intake"));
        await svc.Handle(new ParkWorkItem(new ItemId(Home, 8), 0, "park-1", "person"));   // parked: cleared from the scheduler
        var told = new List<ReapStale>(); var leader = false;
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var reaper = new CapacityReaper(store, m => { told.Add(m); return Task.FromResult(true); }, () => leader, () => at);
        Assert.Equal("not-leader", await reaper.Run(default));
        Assert.Empty(told);
        leader = true;
        Assert.StartsWith("told", await reaper.Run(default));
        var message = Assert.Single(told);
        Assert.Equal(["acme/orders#7"], message.Live);
        Assert.Equal((at, ReapStale.DefaultGrace), (message.ReadAt, message.Grace));

        var down = new CapacityReaper(store, _ => Task.FromResult(false), () => true, () => at);
        Assert.Equal("host-down", await down.Run(default));
        var broken = new CapacityReaper(new ThrowingStore(), _ => Task.FromResult(true), () => true, () => at);
        await Assert.ThrowsAsync<TimeoutException>(() => broken.Run(default));   // (the job reports it; nothing is sent)
    }

    sealed class ThrowingStore : IWorkItemStore
    {
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => throw new TimeoutException();
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => throw new TimeoutException();
    }
}
