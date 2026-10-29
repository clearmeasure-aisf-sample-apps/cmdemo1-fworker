using Fworker.Workflows.Capacity;

// W7.0 and W7.1 (docs/design/fworker-wolverine-implementation.md W7): the CapacityScheduler in the Wolverine host, in the hybrid composition the owner decided on (2026-10-28). Only where the engine acts (Engine.V2Acts;
// tdd stays in the shadow run, so nothing of this exists there or in v1): the scheduler is the slot ledger of the sessions the v2 engine starts, and v1's plant keeps the ledger of the sessions it offers. The two share
// the limits of a runtime: what the plant's manager runs and has started today is counted as outside the scheduler's ledger (RuntimeManager.Load), and a host's report counts the containers it runs that the
// scheduler did not grant. Nothing sends a RequestSlot yet: the feature plan (W7.3) and the docs plan's steps do, and the scheduler is idle until they do. There is no flag; the settings are config/instance.json's
// (schedule.fileOverlap, scheduler, runtimes.octopus).

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
