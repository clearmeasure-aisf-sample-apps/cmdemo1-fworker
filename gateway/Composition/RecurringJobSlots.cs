// W4.0 made the recurring jobs of the durable ingress as slots (a name and an interval); W4.3 fills them. A slot is filled by naming its job in the registry; the
// scheduler (a hosted service of the gateway, leader lease only) publishes RecurringJobRun(name) to the durable local queue "recurring-jobs" when a slot is due, and the
// handler runs the job. Attached to the Wolverine host only where the engine acts (Engine.V2Acts), so shadow (tdd) and v1 register nothing. A restart loses no chain (there
// is none: the scheduler's clock is the process's, a queued run survives in the durable inbox), and a run that finds its job still running is dropped, so a slow pass never piles up.
sealed record RecurringJobSlot(string Name, TimeSpan Every);

sealed class RecurringJobSlots(IReadOnlyList<RecurringJobSlot> slots)
{
    public const string BoardReconciler = "board-reconciler", CatalogReread = "catalog-reread", IngressDedupPurge = "ingress-dedup-purge", DocsIntake = "docs-intake", FeatureChecks = "feature-checks", SagaRearm = "saga-rearm", LedgerPurge = "ledger-purge";
    public IReadOnlyList<RecurringJobSlot> Slots { get; } = slots;
    // W7.3: the watcher of the checks of the feature items runs every minute too, and is filled only when the engine carries the feature plan
    // W6.6: the intake of docs items is the one slot that runs every minute, as v1's sweep pulls an item when a session is free; it is filled only when intake names the docs plan
    public static RecurringJobSlots Default() => new([new(BoardReconciler, TimeSpan.FromMinutes(5)), new(CatalogReread, TimeSpan.FromMinutes(15)), new(IngressDedupPurge, TimeSpan.FromHours(6)), new(DocsIntake, TimeSpan.FromMinutes(1)), new(FeatureChecks, TimeSpan.FromMinutes(1)), new(SagaRearm, TimeSpan.FromMinutes(5)), new(LedgerPurge, TimeSpan.FromHours(6))]);

    /// <summary>Adds the slots and the registry of their jobs to the host's services and the queue the runs travel on. Returns the slots registered.</summary>
    public static RecurringJobSlots Attach(WolverineHostService host, RecurringJobRegistry? registry = null)
    {
        var slots = Default(); var previous = host.RuntimeCommands; registry ??= new RecurringJobRegistry();
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(slots); services.AddSingleton(registry);
            w.Discovery.IncludeType(typeof(RecurringJobHandler));
            w.PublishMessage<RecurringJobRun>().ToLocalQueue(RecurringJobHandler.Queue);
            w.LocalQueue(RecurringJobHandler.Queue).UseDurableInbox().MaximumParallelMessages(2);
        };
        return slots;
    }
}

/// <summary>A run of the job named. Public because Wolverine's generated code refers to it.</summary>
public sealed record RecurringJobRun(string Name);

/// <summary>The jobs by slot name. A job that throws is logged (type only) and tried again at its next interval; a run of a job that is still running is dropped.</summary>
public sealed class RecurringJobRegistry
{
    readonly Dictionary<string, Func<CancellationToken, Task>> jobs = new(StringComparer.Ordinal);
    readonly HashSet<string> running = [];
    public Dictionary<string, int> Runs { get; } = new(StringComparer.Ordinal);
    readonly Dictionary<string, JobRun> last = new(StringComparer.Ordinal);

    /// <summary>The latest run of each job that has run (W9.10o): when it started, how long it took and how it ended (the type of the exception, never its message). The v2 block of /api/v1/status shows it.</summary>
    public IReadOnlyDictionary<string, JobRun> LastRuns { get { lock (last) return new Dictionary<string, JobRun>(last); } }

    void Record(string name, DateTimeOffset started, System.Diagnostics.Stopwatch clock, string? error) { lock (last) last[name] = new JobRun(started, clock.ElapsedMilliseconds, error); }

    public RecurringJobRegistry Add(string name, Func<CancellationToken, Task> job) { jobs[name] = job; return this; }
    public bool Has(string name) => jobs.ContainsKey(name);
    /// <summary>The names of the jobs that are filled.</summary>
    public IReadOnlyCollection<string> Names => jobs.Keys.ToList();
    /// <summary>How many runs of the job finished without an error.</summary>
    public int RunCount(string name) { lock (Runs) return Runs.GetValueOrDefault(name); }

    public async Task Run(string name, CancellationToken ct)
    {
        if (!jobs.TryGetValue(name, out var job)) return;
        lock (running) if (!running.Add(name)) return;
        var started = DateTimeOffset.UtcNow; var clock = System.Diagnostics.Stopwatch.StartNew();
        try { await job(ct); lock (Runs) Runs[name] = Runs.GetValueOrDefault(name) + 1; Record(name, started, clock, null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Record(name, started, clock, e.GetType().Name); Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"recurringJob\":\"{name}\",\"error\":\"{e.GetType().Name}\"}}"); }
        finally { lock (running) running.Remove(name); }
    }
}

/// <summary>How the latest run of a recurring job ended: when it started, how long it took, and the type of the exception when it failed (null when it did not).</summary>
public sealed record JobRun(DateTimeOffset StartedAt, long Milliseconds, string? Error);

/// <summary>The handler of the durable queue "recurring-jobs". Public because Wolverine's generated code refers to it.</summary>
public static class RecurringJobHandler
{
    public const string Queue = "recurring-jobs";
    public static Task Handle(RecurringJobRun run, RecurringJobRegistry registry, CancellationToken ct) => registry.Run(run.Name, ct);
}

/// <summary>Publishes the runs of the slots that have a job, when due, while this process may act (the leader lease). The first run of each slot is after <c>firstDelay</c>.</summary>
sealed class RecurringJobScheduler(RecurringJobSlots slots, RecurringJobRegistry registry, Func<RecurringJobRun, Task<bool>> publish, Func<bool> mayAct, TimeProvider? time = null, TimeSpan? firstDelay = null) : BackgroundService
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Dictionary<string, DateTimeOffset> next = new(StringComparer.Ordinal);

    /// <summary>One look at the clock: publishes the runs due and returns their names. A slot's next time moves on even when this process does not act or the publish failed.</summary>
    internal async Task<IReadOnlyList<string>> Tick()
    {
        var now = clock.GetUtcNow(); var sent = new List<string>();
        foreach (var slot in slots.Slots)
        {
            if (!registry.Has(slot.Name)) continue;
            if (!next.TryGetValue(slot.Name, out var due)) { next[slot.Name] = now + (firstDelay ?? TimeSpan.FromSeconds(30)); continue; }
            if (now < due) continue;
            next[slot.Name] = now + slot.Every;
            if (!mayAct()) continue;
            try { if (await publish(new RecurringJobRun(slot.Name))) sent.Add(slot.Name); }
            catch (Exception e) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"recurringJob\":\"{slot.Name}\",\"publish\":\"{e.GetType().Name}\"}}"); }
        }
        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(); } catch (Exception e) when (!ct.IsCancellationRequested) { Console.Error.WriteLine($"recurring jobs: {e.GetType().Name}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
