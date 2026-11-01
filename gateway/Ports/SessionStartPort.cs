using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.WorkItem;

// W9.10j (docs/design/fworker-wolverine-implementation.md, W9.10 rows; wolverine-factory.md 3.2 "who starts a session"): the sessions of the items the saga schedules start from the saga and the scheduler, not from the sweep.
//  - SessionStartPort: after the scheduler's SlotGranted the saga sends SessionStartRequested (its arm id and the slot). The gateway reads the saga's state (the offers of V2Ownership are the saga's, nothing of the sweep's
//    answer) and gives the plant the offer of that one item at once, carrying the slot, the runtime the slot is on and the arm: the plant starts the session on that runtime (Plant.RouteRuntime), the manager counts the
//    execution as the ledger's (RuntimeManager.Begin with a slot), and the start command carries the arm and the slot (StartRuntimeSession). An item that has an owner, is parked or has moved on is offered nothing. The
//    offer is idempotent: a second request for the same slot finds the assignment waiting for its answer and leaves it. The sweep's full pass still repeats the same offers from the same state, so an offer the plant lost
//    (a restart) is made again; W9.11 takes that repeat over when it removes the sweep.
//  - CapacityReaper: a recurring job (leader only) that tells the scheduler which items the sagas have in flight, so what the ledger keeps of an item whose saga never sent ClearItem is reaped (ReapStale).

/// <summary>Starts the session of a slot the scheduler granted. Never throws for an item that needs nothing; a saga table that cannot be read throws, and the host tries the message again.</summary>
public sealed class SessionStartPort
{
    readonly Func<CancellationToken, Task<bool>> refresh;
    readonly Func<IReadOnlyCollection<int>, List<JsonObject>> offersOf;
    readonly Action<JsonArray, IReadOnlyCollection<int>> plan;
    readonly Func<ItemId, CancellationToken, Task<string?>>? pinOf;

    /// <summary>
    /// <paramref name="pinOf"/> (V1.3) reads the worker an item's <c>worker:</c> label pins it to, from the issue: the labels of the reconciler's cards (V2Ownership.PinOf) may be older than the label, and the offer made here is
    /// the first the plant sees of the item. A read that fails offers the item without the pin (the sweep's full pass repeats the offer with the cards' pins).
    /// </summary>
    internal SessionStartPort(V2Ownership ownership, Func<Plant?> plant, Func<ItemId, CancellationToken, Task<string?>>? pinOf = null)
    {
        this.pinOf = pinOf;
        refresh = ownership.RefreshAsync;
        offersOf = only => plant() is { } p ? ownership.OfferNodes(p.HasOwner, only) : [];
        plan = (nodes, items) => (plant() ?? throw new InvalidOperationException("the gateway has no plant yet")).PlanSagaOffers(nodes, items);
    }

    /// <summary>For a test: the three seams as given.</summary>
    internal SessionStartPort(Func<CancellationToken, Task<bool>> refresh, Func<IReadOnlyCollection<int>, List<JsonObject>> offersOf, Action<JsonArray, IReadOnlyCollection<int>> plan, Func<ItemId, CancellationToken, Task<string?>>? pinOf = null)
    { this.refresh = refresh; this.offersOf = offersOf; this.plan = plan; this.pinOf = pinOf; }

    public async Task<string> Run(SessionStartRequested m, CancellationToken ct)
    {
        var item = m.Item.Number;
        if (!await refresh(ct)) throw new InvalidOperationException("the saga table could not be read");
        var nodes = offersOf([item]);
        if (nodes.Count == 0) return "no-offer";   // the item has an owner, is parked, or its saga moved on: the slot is the saga's to end
        // (a slot that is no longer the item's: the saga holds a newer grant, whose own request follows)
        if (nodes[0]["slot"]?.ToString() != m.SlotId) return "stale-slot";
        if (pinOf is not null && nodes[0]["pin"] is null)
        {
            try { if (await pinOf(m.Item, ct) is { Length: > 0 } pin) nodes[0]["pin"] = pin; }
            catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"session start for {m.Item}: the pin could not be read ({e.GetType().Name}); offered without it"); }
        }
        plan(new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()), [item]);
        return "offered";
    }
}

static class SessionStartWiring
{
    /// <summary>Adds the session start and its handler to the host's services (where the engine acts).</summary>
    public static void Attach(WolverineHostService host, SessionStartPort port)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(port);
            w.Discovery.IncludeType(typeof(SessionStartHandler));
        };
    }
}

/// <summary>The handler of the saga's request to start a session. Public because Wolverine's generated code refers to it.</summary>
public static class SessionStartHandler
{
    public static Task Handle(SessionStartRequested m, SessionStartPort port, CancellationToken ct) => port.Run(m, ct);
}

/// <summary>
/// The reaper: for the items the sagas have in flight (open, and known to the scheduler: SlotKnown) it tells the scheduler (ReapStale), which drops what it holds of any other item that has been in its ledger longer
/// than the grace. Runs on the leader only; a read of the sagas that fails sends nothing (the scheduler must never be told that nothing is in flight because the table could not be read).
/// </summary>
public sealed class CapacityReaper(IWorkItemStore store, Func<ReapStale, Task<bool>> publish, Func<bool> mayAct, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<string> Run(CancellationToken ct)
    {
        if (!mayAct()) return "not-leader";
        var readAt = now();
        // (every repository's items: the ledger is the factory's)
        var live = (await store.OwnedStates(ct)).Where(s => s.IsOpen && s.SlotKnown).Select(s => s.Item).ToList();
        return await publish(new ReapStale(readAt, live, ReapStale.DefaultGrace)) ? $"told ({live.Count} in flight)" : "host-down";
    }
}
