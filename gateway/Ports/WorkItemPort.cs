using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Persistence;
using Fworker.Workflows.Intake;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// W6.0 and W6.1 (docs/design/fworker-wolverine-implementation.md W6): the WorkItemSaga in the Wolverine host, in the hybrid composition the owner decided on (2026-10-28). Only where the engine acts
// (Engine.V2Acts; tdd stays in the shadow run, so nothing of this exists there or in v1): v1's plant still offers the sessions and runs every verb the v2 engine does not own yet, v2's saga decides the items it
// has started (the docs-only items the intake gave it), and v1's sweep leaves those items alone. The second half is V2Ownership and SweepV2Filter: the sweep's answer is read as before, and everything it says about
// an item that has a saga (an offer, a trigger, a hold, a park, an owner) is dropped before the plant sees it, so the plant offers no session for it and notifies no owner of it. The rule script itself is not
// changed: it still reads such an item, and the follow-up that passes it the owned items belongs to the integrator of Verbs.Sweep.ps1 (docs/design/fworker-wolverine-implementation.md, W6.0).
// Nothing starts a saga yet: the feeder of the intake (W6.6) publishes StartWorkItem, and intake.v2Plans is empty in config/instance.json.

/// <summary>
/// The items of the home repository that have a saga which has not completed, as the numbers v1's sweep knows them by. Read from the saga's table before each sweep (a point query by status,
/// bounded in time); a read that fails keeps the last answer, because a v2 item must not be taken by v1 for the length of an outage.
/// </summary>
sealed class V2Ownership(IWorkItemStore store, RepoId home)
{
    volatile HashSet<int> owned = [];
    public int Count => owned.Count;

    public bool Owns(int item) => owned.Contains(item);

    public IReadOnlyCollection<int> Items => owned;

    /// <summary>Reads the owned items; false when the read failed and the last answer stands.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(10));
            var keys = await store.OwnedItems(limit.Token);
            var set = new HashSet<int>();
            foreach (var key in keys)
                if (ItemId.TryParse(key, out var id) && id.Repository == home) set.Add(id.Number);
            owned = set;
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"v2 ownership: {e.GetType().Name}; the last answer stands ({owned.Count} items)");
            return false;
        }
    }

    /// <summary>For a test or a host without a database: the owned items as given.</summary>
    public void Set(IEnumerable<int> items) => owned = items.ToHashSet();
}

static class SweepV2Filter
{
    // arrays of objects with an "item", arrays of numbers, and objects keyed by the number of the item
    static readonly string[] ObjectLists = ["offers", "triggers", "held", "parks", "pass_unmerged", "flags", "release_queue"];
    static readonly string[] NumberLists = ["unconfirmed", "unparked", "holds", "settling"];
    static readonly string[] KeyedByNumber = ["owners", "owned", "pins", "priorities", "runtimes", "profiles"];

    /// <summary>Removes from the sweep's answer everything it says about the items <paramref name="owns"/> names; returns how many entries it removed. The answer's own log of what the script did (actions, errors) is left as it is.</summary>
    public static int Apply(JsonNode sweep, Func<int, bool> owns)
    {
        if (sweep is not JsonObject o) return 0;
        var removed = 0;
        foreach (var name in ObjectLists)
            if (o[name] is JsonArray a) removed += RemoveWhere(a, x => x is JsonObject xo && Number(xo["item"]) is { } n && owns(n));
        foreach (var name in NumberLists)
            if (o[name] is JsonArray a) removed += RemoveWhere(a, x => Number(x) is { } n && owns(n));
        foreach (var name in KeyedByNumber)
            if (o[name] is JsonObject d)
                foreach (var key in d.Select(kv => kv.Key).ToList())
                    if (int.TryParse(key, out var n) && owns(n)) { d.Remove(key); removed++; }
        return removed;
    }

    static int? Number(JsonNode? n) => n is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : null;

    static int RemoveWhere(JsonArray a, Func<JsonNode?, bool> drop)
    {
        var n = 0;
        for (var i = a.Count - 1; i >= 0; i--) if (drop(a[i])) { a.RemoveAt(i); n++; }
        return n;
    }
}

/// <summary>What the gateway builds for the saga: its service, the history writer, the ownership the sweep reads and the intake settings.</summary>
sealed record WorkItemParts(WorkItemService Service, WorkItemHistory History, IWorkItemStore Store, V2Ownership? Ownership, IntakeSettings Intake);

static class WorkItemWiring
{
    /// <summary>The saga's parts on the database of the host. The docs-only port is D4's adapter once D4 is on master (Fworker.Workflows.WorkItem.DocsOnlyPort); until then the recorded design decides.</summary>
    public static WorkItemParts Make(GatewayComposition c, string connectionString, IDocsOnlyPort? docs = null)
    {
        Func<FactoryDbContext> open = () => Open(connectionString);
        var store = new SqlWorkItemStore(open);
        var service = new WorkItemService(store, new WorkItemSaga(docs ?? new RecordedDesignDocsOnly()));
        var ownership = c.Instance.Home is { } home ? new V2Ownership(store, home) : null;
        return new WorkItemParts(service, new WorkItemHistory(open), store, ownership, IntakeSettings.From(c.Instance.Root, Environment.GetEnvironmentVariable));
    }

    public static FactoryDbContext Open(string connectionString) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString).Options);

    public static void Attach(WolverineHostService host, WorkItemParts parts)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(parts.Service);
            services.AddSingleton(parts.History);
            // for the feeder of the intake (W6.6), which finds the Todo items that are docs-only and hands them to the saga through the host's own queues
            services.AddSingleton(parts.Intake);
            services.AddSingleton(sp => new WorkItemIntake(parts.Store, parts.Intake, (m, _) => sp.GetRequiredService<IMessageBus>().PublishAsync(m).AsTask()));
            w.Discovery.IncludeType(typeof(WorkItemHandlers));
            w.Discovery.IncludeType(typeof(WorkItemRouting));
            w.Discovery.IncludeType(typeof(WorkItemHistoryHandler));
            WorkItemTopology.Route(w);
        };
    }
}
