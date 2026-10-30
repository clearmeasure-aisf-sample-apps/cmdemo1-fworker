// W4.0 made the recurring jobs of the durable ingress as slots (a name and an interval); W4.3 fills them. A slot is filled by naming its job in the registry; the
// scheduler (a hosted service of the gateway, leader lease only) publishes RecurringJobRun(name) to the durable local queue "recurring-jobs" when a slot is due, and the
// handler runs the job. Attached to the Wolverine host only where the engine acts (Engine.V2Acts), so shadow (tdd) and v1 register nothing. A restart loses no chain (there
// is none: the scheduler's clock is the process's, a queued run survives in the durable inbox), and a run that finds its job still running is dropped, so a slow pass never piles up.
// W9.10g: the plant-wide steps of v1's sweep (claim-ref reap, board guard, QC sync, replenish, the keeper's backlog count) are slots of their own, and so are the retention prune and the faster board reconciler.
// Their intervals come from <see cref="RecurringJobIntervals"/>; a slot may carry the delay of its first run (<c>First</c>), which defaults to the scheduler's.
sealed record RecurringJobSlot(string Name, TimeSpan Every, TimeSpan? First = null);

/// <summary>
/// The intervals of the recurring jobs that are not fixed (W9.10g). The board reconciler runs every 2 minutes (owner default of 2026-10-30, instead of a projects_v2_item webhook; recurringJobs.boardReconcilerSeconds, 30 to 600). The plant-wide
/// steps run at the sweep's own interval (FACTORY_SWEEP_SECONDS, 180; not faster than every 5 seconds) as they did inside it, the QC sync at most every minute (a sweep asked for by a webhook ran after 60 s at the earliest), and the retention prune once a day.
/// </summary>
sealed record RecurringJobIntervals(TimeSpan BoardReconciler, TimeSpan PlantSteps, TimeSpan QcSync, TimeSpan Retention)
{
    public static RecurringJobIntervals Default => From(null, 180);

    public static RecurringJobIntervals From(System.Text.Json.Nodes.JsonNode? root, int sweepSeconds)
    {
        var block = root?["recurringJobs"];
        int Num(string key, int fallback, int min, int max) => block?[key] is { } n && int.TryParse(n.ToString(), out var v) ? Math.Clamp(v, min, max) : fallback;
        // (never faster than every 5 seconds: the Tier 1 tests sweep every 2 seconds, and five script passes every 2 seconds on top of the sweep would load a CI machine for nothing)
        var plant = Num("plantStepsSeconds", Math.Max(5, sweepSeconds), 5, 3600);
        return new(TimeSpan.FromSeconds(Num("boardReconcilerSeconds", 120, 30, 600)), TimeSpan.FromSeconds(plant), TimeSpan.FromSeconds(Math.Min(plant, Num("qcSyncSeconds", 60, 1, 3600))), TimeSpan.FromHours(Num("retentionHours", 24, 1, 168)));
    }
}

sealed class RecurringJobSlots(IReadOnlyList<RecurringJobSlot> slots)
{
    public const string BoardReconciler = "board-reconciler", CatalogReread = "catalog-reread", IngressDedupPurge = "ingress-dedup-purge", DocsIntake = "docs-intake", FeatureChecks = "feature-checks", SagaRearm = "saga-rearm", LedgerPurge = "ledger-purge", IncidentHealth = "incident-health";
    // W9.10g: the plant-wide steps of the sweep, the retention prune
    public const string ClaimRefReap = "claim-ref-reap", BoardGuard = "board-guard", QcSync = "qc-sync", Replenish = "replenish", BacklogLow = "backlog-low", RetentionPrune = "retention-prune";
    public IReadOnlyList<RecurringJobSlot> Slots { get; } = slots;
    // W7.3: the watcher of the checks of the feature items runs every minute too, and is filled only when the engine carries the feature plan
    // W6.6: the intake of docs items is the one slot that runs every minute, as v1's sweep pulls an item when a session is free; it is filled only when intake names the docs plan
    public static RecurringJobSlots Default(RecurringJobIntervals? intervals = null)
    {
        var i = intervals ?? RecurringJobIntervals.Default;
        TimeSpan Soon(TimeSpan every) => TimeSpan.FromSeconds(Math.Min(every.TotalSeconds, 15));   // the first run of a plant-wide step: as soon as the leader may act (the job itself waits for the restore)
        return new([new(BoardReconciler, i.BoardReconciler), new(CatalogReread, TimeSpan.FromMinutes(15)), new(IngressDedupPurge, TimeSpan.FromHours(6)), new(DocsIntake, TimeSpan.FromMinutes(1)), new(FeatureChecks, TimeSpan.FromMinutes(1)), new(SagaRearm, TimeSpan.FromMinutes(5)), new(LedgerPurge, TimeSpan.FromHours(6)), new(IncidentHealth, TimeSpan.FromMinutes(1)),
            new(ClaimRefReap, i.PlantSteps, Soon(i.PlantSteps)), new(BoardGuard, i.PlantSteps, Soon(i.PlantSteps)), new(QcSync, i.QcSync, Soon(i.QcSync)), new(Replenish, i.PlantSteps, Soon(i.PlantSteps)), new(BacklogLow, i.PlantSteps, Soon(i.PlantSteps)),
            new(RetentionPrune, i.Retention, TimeSpan.FromMinutes(10))]);
    }

    /// <summary>Adds the slots and the registry of their jobs to the host's services and the queue the runs travel on. Returns the slots registered.</summary>
    public static RecurringJobSlots Attach(WolverineHostService host, RecurringJobRegistry? registry = null, RecurringJobIntervals? intervals = null)
    {
        var slots = Default(intervals); var previous = host.RuntimeCommands; registry ??= new RecurringJobRegistry();
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(slots); services.AddSingleton(registry);
            w.Discovery.IncludeType(typeof(RecurringJobHandler));
            w.PublishMessage<RecurringJobRun>().ToLocalQueue(RecurringJobHandler.Queue);
            w.LocalQueue(RecurringJobHandler.Queue).UseDurableInbox().MaximumParallelMessages(4);   // (W9.10g: four, since the plant-wide steps are jobs now and a pass of one can take a minute)
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
    // the look at the clock: every 5 seconds, or half the shortest interval of a slot that has a job where that is shorter (the sweep of the Tier 1 tests runs every 2 seconds), never faster than once a second
    TimeSpan TickEvery => TimeSpan.FromSeconds(Math.Clamp(slots.Slots.Where(s => registry.Has(s.Name)).Select(s => s.Every.TotalSeconds / 2).DefaultIfEmpty(5).Min(), 1, 5));

    /// <summary>One look at the clock: publishes the runs due and returns their names. A slot's next time moves on even when this process does not act or the publish failed.</summary>
    internal async Task<IReadOnlyList<string>> Tick()
    {
        var now = clock.GetUtcNow(); var sent = new List<string>();
        foreach (var slot in slots.Slots)
        {
            if (!registry.Has(slot.Name)) continue;
            if (!next.TryGetValue(slot.Name, out var due)) { next[slot.Name] = now + (firstDelay ?? slot.First ?? TimeSpan.FromSeconds(30)); continue; }
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
            try { await Task.Delay(TickEvery, clock, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
