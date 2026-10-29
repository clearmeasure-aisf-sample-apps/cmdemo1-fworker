using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Capacity;

namespace Fworker.Workflow.Tests;

/// <summary>The scheduler on an in-memory ledger with a clock the test moves (W7.1): sends an input like the handler does and keeps every event and timer it asked for.</summary>
sealed class CapacityFixture
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The runtimes the tests use: octopus with hosts (2 guaranteed slots per host, 3 with headroom), cursor-cloud with 1 slot, copilot-cloud with 5; the file-overlap rule on; a slot lasts 600 minutes.</summary>
    public const string DefaultConfig = """
        {
          "runtimes": { "octopus": { "enabled": true, "maxConcurrent": 6, "maxExecutionsPerDay": 50, "perHostCapacity": 2, "perHostBurst": 3 } },
          "api": { "cursor": { "enabled": true, "maxConcurrent": 1, "maxExecutionsPerDay": 100 }, "copilot": { "enabled": true, "maxConcurrent": 5, "maxExecutionsPerDay": 100 } },
          "schedule": { "fileOverlap": { "enabled": true, "maxHoldMinutes": 120 }, "overlapIgnore": ["package-lock.json"] },
          "scheduler": { "slotMaxMinutes": 600 }
        }
        """;

    public static readonly HostMetrics Roomy = new(8000, 4, 0.5, 50000);

    public readonly InMemoryCapacityStore Store = new();
    public readonly CapacityScheduler Scheduler;
    public readonly CapacityService Svc;
    public readonly Dictionary<string, RuntimeOutside> Outside = [];
    public DateTimeOffset Now = T0;
    public readonly List<object> Messages = [];
    public readonly List<DateTimeOffset> Wakes = [];
    int keys;

    public CapacityFixture(Action<JsonObject>? edit = null, Func<InMemoryCapacityStore, ICapacityStore>? wrap = null)
    {
        var cfg = (JsonObject)JsonNode.Parse(DefaultConfig)!;
        edit?.Invoke(cfg);
        Scheduler = new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), kind => Outside.GetValueOrDefault(kind) ?? new RuntimeOutside(0, 0));
        Svc = new CapacityService(wrap?.Invoke(Store) ?? Store, Scheduler, () => Now);
    }

    public static ItemId I(int n, string repo = "orders") => new("acme", repo, n);
    public string K() => $"k{++keys}";
    public CapacityState State => Store.State(CapacityKeys.Factory)!;
    public IEnumerable<SlotGranted> Granted => Messages.OfType<SlotGranted>();
    public IEnumerable<Held> HeldMessages => Messages.OfType<Held>();

    public async Task<CapacityResult> Send(CapacityInput input)
    {
        var r = await Svc.Handle(input);
        Messages.AddRange(r.Messages);
        if (r.WakeAt is { } w) Wakes.Add(w);
        return r;
    }

    /// <summary>A request for a slot; the defaults are a Technical Design session of an item on cursor-cloud, requested now.</summary>
    public Task<CapacityResult> Request(int n, string runtime = "cursor-cloud", string phase = CapacityPhases.Design, string? priority = null, bool pinned = false, bool generated = false, bool resume = false,
        int depth = 1, DateTimeOffset? requested = null, string[]? files = null, string repo = "orders", string[]? toolchains = null, bool inIntake = false, string? key = null)
        => Send(new RequestSlot(I(n, repo), key ?? K(), 1, runtime, phase, new PlanRank(pinned, priority, inIntake), generated, resume, depth, requested ?? Now, toolchains ?? [],
            files is null ? Footprint.Empty : new Footprint(files, FootprintSource.Design)));

    public Task<CapacityResult> End(int n, string slot = "", int? peak = null, string repo = "orders") => Send(new SessionEnded(I(n, repo), K(), slot, peak));
    public Task<CapacityResult> Refuse(int n, string slot, string outcome, string repo = "orders") => Send(new StartRefused(I(n, repo), K(), slot, outcome));
    public Task<CapacityResult> Clear(int n, string repo = "orders") => Send(new ClearItem(I(n, repo), K(), "test"));
    public Task<CapacityResult> Report(string host, int running = 0, HostMetrics? metrics = null, string runtime = "octopus", DateTimeOffset? at = null)
        => Send(new HostReported(K(), host, runtime, running, metrics ?? Roomy, at ?? Now));
    public Task<CapacityResult> Wake(DateTimeOffset? due = null) => Send(new CapacityWake(due ?? Now));

    /// <summary>The slot of an item, or null.</summary>
    public LedgerSlot? SlotOf(int n, string repo = "orders") => State.Slots.GetValueOrDefault(I(n, repo).ToString());

    public int SlotsOnHost(string host) => State.Slots.Values.Count(s => s.Host == host);

    /// <summary>The numbers of the items that hold a slot, in order.</summary>
    public IEnumerable<int> Holders => State.Slots.Values.Select(s => ItemId.Parse(s.Item).Number).Order();

    /// <summary>The waiting text of an item in the queue, "" when none was published.</summary>
    public string Waiting(int n, string repo = "orders") => State.Queue.GetValueOrDefault(I(n, repo).ToString())?.Announced ?? "(not waiting)";

    /// <summary>Fills a host's footprint with ten sessions that peaked at <paramref name="peak"/> megabytes (burst needs ten samples).</summary>
    public async Task Warm(string host, int peak = 1000, int sessions = 10)
    {
        for (var i = 0; i < sessions; i++)
        {
            var id = 900 + i;
            var r = await Request(id, "octopus");
            var granted = r.Messages.OfType<SlotGranted>().Single();
            if (granted.Host != host) throw new InvalidOperationException($"the warm-up session ran on {granted.Host}, not {host}");
            await End(id, granted.SlotId, peak);
        }
    }
}

/// <summary>A ledger that fails the next calls: a database that does not answer.</summary>
sealed class FlakyCapacityStore(ICapacityStore inner) : ICapacityStore
{
    public int FailLoads, FailSaves;
    public int Loads, Saves;

    public Task<CapacityRecord?> Load(string key, CancellationToken ct)
    {
        Loads++;
        if (FailLoads > 0) { FailLoads--; throw new TimeoutException("the database does not answer"); }
        return inner.Load(key, ct);
    }

    public Task<bool> Save(string key, CapacityState state, int expectedRevision, CancellationToken ct)
    {
        Saves++;
        if (FailSaves > 0) { FailSaves--; throw new TimeoutException("the database does not answer"); }
        return inner.Save(key, state, expectedRevision, ct);
    }
}
