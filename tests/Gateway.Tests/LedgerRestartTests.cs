using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10j review: a gateway restart must not bring the double count back. The scheduler's day count and its slots are durable (SQL); the manager rebuilds its own count from the runtime's history and adopts the executions it
/// finds, and both include the sessions the ledger granted before the restart. The manager reads the ledger when it counts the day again and when it adopts, and leaves those sessions out of what it reports as outside it.
/// </summary>
public sealed class LedgerRestartTests
{
    static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>A runtime with a task history (like Octopus): its history counts every launch of the day, and it lists the executions that still run.</summary>
    sealed class HistoryRuntime(int launches, params RuntimeInstance[] running) : IWorkerRuntime, IRunHistory
    {
        public string Kind => "cursor-cloud"; public int MaxConcurrent => 100;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "x", "", ""));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(running);
        public Task<int> LaunchesSince(DateTime sinceUtc, CancellationToken ct) => Task.FromResult(launches);
    }

    static RuntimeInstance Running(int item) => new($"exec-{item}", $"cursor-{item}-abc", item, "Development", "Running", DateTime.UtcNow, null);

    static RuntimeManager NewManager(IWorkerRuntime rt, int maxConcurrent = 100, int perDay = 100)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.Kind = "cursor-cloud"; limits.IsOn = () => true; limits.MaxConcurrent = maxConcurrent; limits.MaxExecutionsPerDay = perDay;
        return new RuntimeManager(cfg, rt, new RuntimeNonces(), limits) { MayAct = () => true };
    }

    static RuntimeManager.LedgerDay Ledger(int granted, params (int Item, string Slot)[] held)
        => new(Today, granted, held.Select(h => new RuntimeManager.LedgerSlotRef(h.Item, h.Slot)).ToList());

    [Fact]
    public async Task AfterARestartMidDay_TheHistoryAndTheLedgerCountTheSameTenSessions_TheLoadReportsNothingOutsideTheLedger()
    {
        // ten sessions ran for the ledger (ten slots held, ten grants counted) and the gateway restarted: the runtime's history has the ten launches and lists the ten executions that still run
        var m = NewManager(new HistoryRuntime(10, Enumerable.Range(1, 10).Select(Running).ToArray()));
        m.ReadLedger = _ => Task.FromResult<RuntimeManager.LedgerDay?>(Ledger(10, Enumerable.Range(1, 10).Select(n => (n, $"s{n}")).ToArray()));
        Assert.Equal((0, 0, "runtime-relisting"), m.Load());
        await m.Poll(CancellationToken.None);
        Assert.Equal((0, 0, (string?)null), m.Load());   // Today == 0 and Running == 0: the ledger counts all ten itself
        // the manager's own limits still see every execution: the plant's start is asked against all ten
        Assert.Equal((true, ""), m.CanStart());
        Assert.Equal(10, m.Status()["cursor-cloud"]!["running"]!.GetValue<int>());
    }

    [Fact]
    public async Task AfterARestart_WhatThePlantRanOnItsOwnIsStillOutsideTheLedger()
    {
        // the history has 13 launches: 10 of the ledger's, 3 of the plant's own offers (one of them still runs, on item 50, for which the ledger holds no slot)
        var running = Enumerable.Range(1, 10).Select(Running).Append(Running(50)).ToArray();
        var m = NewManager(new HistoryRuntime(13, running));
        m.ReadLedger = _ => Task.FromResult<RuntimeManager.LedgerDay?>(Ledger(10, Enumerable.Range(1, 10).Select(n => (n, $"s{n}")).ToArray()));
        await m.Poll(CancellationToken.None);
        Assert.Equal((1, 3, (string?)null), m.Load());
    }

    [Fact]
    public async Task ASlotTheLedgerHoldsThatNoSessionHasBegun_IsNotTakenForOneOfTheDaysLaunches()
    {
        // 5 launches in the history: 4 of the ledger's, 1 of the plant's. The ledger counted 5 grants, one of them (item 9, slot s9) is granted and not started yet: its launch is not in the history
        var m = NewManager(new HistoryRuntime(5, Running(1), Running(2), Running(3), Running(4), Running(50)));
        m.ReadLedger = _ => Task.FromResult<RuntimeManager.LedgerDay?>(Ledger(5, (1, "s1"), (2, "s2"), (3, "s3"), (4, "s4"), (9, "s9")));
        await m.Poll(CancellationToken.None);
        Assert.Equal((1, 1, (string?)null), m.Load());
        // the session of s9 then begins: both counts move by one and the plant's own stays at one
        m.Begin(9, "Development", "", slot: "s9");
        Assert.Equal((1, 1, (string?)null), m.Load());
    }

    [Fact]
    public async Task ALedgerOfAnotherDay_CountedNothingForToday()
    {
        var m = NewManager(new HistoryRuntime(4, Running(1), Running(2)));
        m.ReadLedger = _ => Task.FromResult<RuntimeManager.LedgerDay?>(new RuntimeManager.LedgerDay(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), 40, []));   // nothing has asked it since midnight
        await m.Poll(CancellationToken.None);
        Assert.Equal((2, 4, (string?)null), m.Load());
    }

    [Fact]
    public async Task ALedgerThatCannotBeRead_HoldsTheFirstReadingBack_AndTheDayIsNeverCountedWithoutIt()
    {
        var fail = true;
        var m = NewManager(new HistoryRuntime(10, Enumerable.Range(1, 10).Select(Running).ToArray()));
        m.ReadLedger = _ => fail ? throw new InvalidOperationException("the database is not there") : Task.FromResult<RuntimeManager.LedgerDay?>(Ledger(10, Enumerable.Range(1, 10).Select(n => (n, $"s{n}")).ToArray()));
        await m.Poll(CancellationToken.None);
        Assert.Equal("runtime-relisting", m.Load().Blocked);
        Assert.Equal((false, "runtime-relisting"), m.CanStart());   // the plant starts nothing on a day it cannot count yet
        fail = false;
        await m.Poll(CancellationToken.None);
        Assert.Equal((0, 0, (string?)null), m.Load());
    }

    [Fact]
    public async Task AnExecutionAdoptedLater_ThatTheLedgerHoldsASlotFor_IsNotOutsideTheLedger()
    {
        // a host report finds an execution after the first reading (Adopted): the ledger holds item 7's slot, not item 8's
        var running = new List<RuntimeInstance>();
        var m = NewManager(new ListRuntime(running));
        m.ReadLedger = _ => Task.FromResult<RuntimeManager.LedgerDay?>(Ledger(1, (7, "s7")));
        await m.Poll(CancellationToken.None);
        running.Add(Running(7) with { Adopted = true }); running.Add(Running(8) with { Adopted = true });
        await m.Poll(CancellationToken.None);
        Assert.Equal(1, m.Load().Running);   // item 8 only
    }

    sealed class ListRuntime(List<RuntimeInstance> running) : IWorkerRuntime
    {
        public string Kind => "cursor-cloud"; public int MaxConcurrent => 100;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "x", "", ""));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(running.ToList());
    }

    [Fact]
    public async Task TheViewOfTheLedger_IsTheDaysCountAndTheSlotsOfTheRuntime_ByItemNumber_ForTheHomeRepositoryOnly()
    {
        var store = new InMemoryCapacityStore();
        var cfg = JsonNode.Parse("""{ "api": { "cursor": { "enabled": true, "maxConcurrent": 100, "maxExecutionsPerDay": 100 } } }""");
        var svc = new CapacityService(store, new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), _ => new RuntimeOutside(0, 0)));
        await svc.Handle(Request(new ItemId("acme", "orders", 7)));
        await svc.Handle(Request(new ItemId("acme", "other", 8)));
        var record = await store.Load(CapacityKeys.Factory, default);
        var view = CapacityWiring.LedgerDayOf(record, "cursor-cloud", new RepoId("acme", "orders"))!;
        Assert.Equal((Today, 2), (view.Day, view.ExecutionsToday));
        var held = Assert.Single(view.Slots);
        Assert.Equal(7, held.Item);
        Assert.Equal(2, CapacityWiring.LedgerDayOf(record, "cursor-cloud", null)!.Slots.Count);
        Assert.Empty(CapacityWiring.LedgerDayOf(record, "octopus", null)!.Slots);
        Assert.Null(CapacityWiring.LedgerDayOf(null, "cursor-cloud", null));
    }

    static RequestSlot Request(ItemId id) => new(id, $"ask|{id}", 1, "cursor-cloud", CapacityPhases.Develop, new Fworker.Domain.PlanRank(false, null, false), false, false, 2, DateTimeOffset.UtcNow, [], Footprint.Empty);

    [Fact]
    public async Task ADayBudgetOfThirty_AfterARestartWithTenSessionsRunning_StillGrantsTwentyMore_AndNoMore()
    {
        // the audit's scenario: day budget 30; ten ledger sessions ran; the gateway restarts. Before the manager read the ledger back, the scheduler saw 10 (its own) + 10 (the rebuilt history) = 20 and granted ten more.
        var store = new InMemoryCapacityStore();
        var cfg = JsonNode.Parse("""{ "api": { "cursor": { "enabled": true, "maxConcurrent": 100, "maxExecutionsPerDay": 30 } } }""");
        var first = new CapacityService(store, new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), _ => new RuntimeOutside(0, 0)));
        for (var n = 1; n <= 10; n++) await first.Handle(Request(new ItemId("acme", "orders", n)));
        Assert.Equal(10, store.State(CapacityKeys.Factory)!.Slots.Count);

        // the restart: a new manager, its history has the ten launches and its list the ten executions; a new scheduler on the same store (SQL)
        var m = NewManager(new HistoryRuntime(10, Enumerable.Range(1, 10).Select(Running).ToArray()), maxConcurrent: 100, perDay: 30);
        m.ReadLedger = async ct => CapacityWiring.LedgerDayOf(await store.Load(CapacityKeys.Factory, ct), "cursor-cloud", new RepoId("acme", "orders"));
        await m.Poll(CancellationToken.None);
        var second = new CapacityService(store, new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), kind => { var l = m.Load(); return new RuntimeOutside(l.Running, l.Today, l.Blocked); }));

        var granted = 0; var held = new List<Held>();
        for (var n = 11; n <= 35; n++)
        {
            var r = await second.Handle(Request(new ItemId("acme", "orders", n)));
            granted += r.Messages.OfType<SlotGranted>().Count(); held.AddRange(r.Messages.OfType<Held>());
            foreach (var g in r.Messages.OfType<SlotGranted>()) m.Begin(g.Item.Number, "Development", "", slot: g.SlotId);   // the plant starts the session of every grant
        }
        Assert.Equal(20, granted);                                         // 10 + 20 = the budget of 30
        Assert.Equal(5, held.Count(h => h.Code == "runtime-budget"));      // and the rest wait for the budget, the only hold
        Assert.Equal((0, 0, (string?)null), m.Load());
    }
}
