using Fworker.Workflows.Capacity;

// W7.0 and W7.1 (docs/design/fworker-wolverine-implementation.md W7): the CapacityScheduler in the Wolverine host, in the hybrid composition the owner decided on (2026-10-28). Only where the engine acts (Engine.V2Acts;
// tdd stays in the shadow run, so nothing of this exists there or in v1): the scheduler is the slot ledger of the sessions the v2 engine starts, and v1's plant keeps the ledger of the sessions it offers. The two share
// the limits of a runtime: what the plant's manager runs and has started today is counted as outside the scheduler's ledger (RuntimeManager.Load), and a host's report counts the containers it runs that the
// scheduler did not grant. The Octopus runtime's host reports (W7.4) are its HostReported input. Nothing sends a RequestSlot yet: the feature plan (W7.3) and the docs plan's steps do, and the scheduler is idle until they do. There is no flag; the settings are config/instance.json's
// (schedule.fileOverlap, scheduler, runtimes.octopus).
// W9.10j: the executions the manager started for a slot of this ledger are not "outside" it (RuntimeManager.Load leaves them out: the ledger counts each session once), and the sessions of the slots it grants are started
// by the saga (SessionStartRequested, Ports/SessionStartPort.cs) on the slot's runtime; the reaper (CapacityReaper, ReapStale) drops what the ledger keeps of an item no saga has in flight.

/// <summary>What the gateway builds for the scheduler: its service, the history writer and the settings it reads.</summary>
sealed record CapacityParts(CapacityService Service, CapacityHistory History, CapacitySettings Settings);

static class CapacityWiring
{
    /// <summary>
    /// The scheduler's parts on the database of the host. <paramref name="outside"/> answers what a runtime runs outside the ledger (the plant's manager); without it nothing runs outside.
    /// <paramref name="isOn"/> says which runtimes are on in this process; without it the configuration decides.
    /// </summary>
    public static CapacityParts Make(GatewayComposition c, string connectionString, Func<string, RuntimeOutside>? outside = null, Func<string, bool>? isOn = null)
    {
        Func<Fworker.Persistence.FactoryDbContext> open = () => WorkItemWiring.Open(connectionString);
        var settings = CapacitySettings.From(c.Instance.Root, c.Engine.Environment);
        // the configuration says which runtimes may be on; the plant's managers know which are (a runtime also needs its key, its gateway URL, its job)
        if (isOn != null) settings = settings.Only(RuntimeKinds.All.Where(isOn));
        var service = new CapacityService(new SqlCapacityStore(open), new CapacityScheduler(settings, outside));
        return new CapacityParts(service, new CapacityHistory(open), settings);
    }

    /// <summary>Which runtimes the plant's managers have on, by the kind the scheduler names.</summary>
    public static Func<string, bool> OnOf(RuntimeRegistry registry) => kind => registry.ByKind(kind)?.On == true;

    /// <summary>What the plant's managers run, by the kind of runtime the scheduler names.</summary>
    public static Func<string, RuntimeOutside> OutsideOf(RuntimeRegistry registry) => kind =>
    {
        if (registry.ByKind(kind) is not { } manager) return new RuntimeOutside(0, 0);
        var (running, today, blocked) = manager.Load();
        return new RuntimeOutside(running, today, blocked);
    };

    /// <summary>
    /// W9.10j: what each runtime's manager reads of the scheduler's persisted ledger (<see cref="RuntimeManager.ReadLedger"/>): the day's count of grants of that runtime and the slots it holds, by item number. The manager reads it
    /// when it counts the day again (after a restart, at midnight) and adopts the executions it finds, so the grants made before a restart are subtracted from what it reports as outside the ledger once, not counted twice.
    /// Only the items of <paramref name="home"/> (the work-orders repository the plant's items belong to) are matched by number.
    /// </summary>
    public static void ConnectLedger(RuntimeRegistry registry, ICapacityStore store, Fworker.Contracts.Ids.RepoId? home)
    {
        foreach (var manager in registry.Managers)
        {
            var kind = manager.Kind;
            manager.ReadLedger = async ct => LedgerDayOf(await store.Load(CapacityKeys.Factory, ct), kind, home);
        }
    }

    /// <summary>The manager's view of the ledger for one runtime; null when the ledger has no row yet (it has counted nothing).</summary>
    internal static RuntimeManager.LedgerDay? LedgerDayOf(CapacityRecord? record, string kind, Fworker.Contracts.Ids.RepoId? home)
    {
        if (record?.State is not { } state) return null;
        var slots = new List<RuntimeManager.LedgerSlotRef>();
        foreach (var slot in state.Slots.Values.Where(s => s.Runtime == kind).OrderBy(s => s.SlotId, StringComparer.Ordinal))
            if (Fworker.Contracts.Ids.ItemId.TryParse(slot.Item, out var id) && (home is null || id.Repository == home)) slots.Add(new RuntimeManager.LedgerSlotRef(id.Number, slot.SlotId));
        return new RuntimeManager.LedgerDay(state.Day, state.ExecutionsToday.GetValueOrDefault(kind), slots);
    }

    /// <summary>
    /// W7.4: every host report of the Octopus runtime becomes the scheduler's <see cref="HostReported"/> input (the containers running on the host and its metrics). Called only where the engine acts, so v1 and the
    /// shadow run of tdd never send one. A report is not sent while the Wolverine host is down (PublishAsync answers false): the host reports again within five minutes, and a report holds the whole state.
    /// </summary>
    public static void ConnectHostReports(OctopusRunbookRuntime? octopus, WolverineHostService host)
    {
        if (octopus == null) return;
        octopus.HostReported = e => host.PublishAsync(ToMessage(e));
    }

    /// <summary>The scheduler's input for a host report. The key is per host and moment, so a redelivered message does nothing and a later report is never taken for it.</summary>
    internal static HostReported ToMessage(HostReportEvent e)
        => new($"host-report:{e.Host}:{e.At.UtcTicks}", e.Host, RuntimeKinds.Octopus, e.Running, e.Facts.ToMetrics(), e.At);

    public static void Attach(WolverineHostService host, CapacityParts parts)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(parts.Service);
            services.AddSingleton(parts.History);
            services.AddSingleton(parts.Settings);
            w.Discovery.IncludeType(typeof(CapacityHandlers));
            w.Discovery.IncludeType(typeof(CapacityHistoryHandler));
            CapacityTopology.Route(w);
        };
    }
}
