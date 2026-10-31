using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

// The runtime adapter's bookkeeping (docs/design/cloud-worker-runtimes.md 4.2 to 4.5, 2.11): which executions run for which item and phase, the
// capacity, the daily budget and the health of each runtime, and the failures per item and phase. The plant decides that an item goes to a runtime
// (Plant.Runtimes.cs) and asks here for a worker name and a nonce; the provisioning itself, the stop and the reading of the executions run outside
// the plant's lock. Lock order: the plant's gate, then this one; nothing here calls the plant while holding its own lock.
// The limits of one runtime kind: the caeJob block for cae-job, api.cursor for cursor-cloud (CursorCloudRuntime.cs).
sealed class RuntimeLimits
{
    public string Kind = "cae-job", Prefix = "cae";
    public Func<bool> IsOn = () => false;
    // a reason the runtime cannot start anything now, or null (the Copilot seat's quota: quota-exhausted until the next probe)
    public Func<string?> Blocked = () => null;
    public int MaxConcurrent, MaxExecutionsPerDay, MaxFailuresPerPhase = 2, UnhealthyAfterFailures = 2;
    // executions of one item in one phase in a row (no other phase of the item started between them), however each ended: at the limit the
    // item is parked instead of launched again (a task that ends without finishing its phase and without counting as failed would otherwise be
    // relaunched without end); 0 = no limit
    public int MaxLaunchesPerPhase;
    public double AckSeconds = 300, ListSeconds = 30;
    public long TokenSeconds = 14400 + 900;
    // the credential the execution is started with: null mints a one-time nonce (cae-job); cursor-cloud mints the worker's r1 (name, item, phase, expiry)
    public Func<string, int, string, long, string>? MintCredential;
    // the credential leaves Azure (an r1 stored at Cursor): the name is revoked at every end, not only after a failure
    public bool RevokeOnEnd;

    public static RuntimeLimits Of(CaeJobSettings c) => new()
    {
        Kind = "cae-job", Prefix = "cae", IsOn = () => c.On, MaxConcurrent = c.MaxConcurrent, MaxExecutionsPerDay = c.MaxExecutionsPerDay, MaxFailuresPerPhase = c.MaxFailuresPerPhase,
        UnhealthyAfterFailures = c.UnhealthyAfterFailures, AckSeconds = c.AckSeconds, ListSeconds = c.ListSeconds, TokenSeconds = c.ReplicaTimeoutSeconds + 900
    };
}

// What an adapter can tell of an ended execution beyond its state (the Copilot runtime: a follow-up session started by a comment on the item's pull
// request, and whether it pushed to that pull request while it ran)
interface IRuntimeWork
{
    bool FollowUp(string worker);
    // the follow-up ended without registering or pushing: the next one of the item is told to push first
    void Idle(string worker);
    Task<bool?> Pushed(string worker, CancellationToken ct);
}

// A runtime whose one agent owns an item until it is in production (Copilot: one task resumed; Cursor: one agent with follow-up runs): the
// gateway's owner watch (Program.cs) reads what each owned item waits on and closes the ownership when it is done.
interface IItemOwners
{
    string Kind { get; }
    List<CopilotOwner> Owners();
    bool Running(int item);
    void Waiting(int item, string column, string waitingOn);
    void SetPr(int item, int pr, string branch = "");
    Task CloseOwner(int item);
}

// The runtime manager as the v2 side sees it (W3.1): the handlers of the result messages call these, and the plant's start and stop go through them.
// Under the v1 engine RuntimeManager calls the adapters itself; under v2 Provision and Deprovision dispatch a command through the outbox and the
// result message comes back as CompleteStart or CompleteStop.
interface IRuntimeManager
{
    string Kind { get; }
    bool On { get; }
    (bool Ok, string Code) CanStart();
    RuntimeManager.Ticket Begin(int item, string phase, string profile, string role = "");
    Task Provision(RuntimeManager.Ticket t);
    Task Deprovision(string name, string reason);
    void Registered(string name);
    bool IsRuntimeWorker(string name);
    // v2: the start command of the ticket ended (the result message of the handler): the same settling as a v1 start that returned this result
    Task CompleteStart(string name, int item, string phase, ProvisionResult result);
    void CompleteStop(string name, bool ok, string error);
    // v2: the start command made the execution (its handler has the answer of the adapter) and its result message is still on its way to CompleteStart: the
    // records know the execution and its page from now on, as they do in v1 the moment the adapter answers
    void NoteStarted(string name, string execution, string url);
}

sealed class RuntimeManager : IRuntimeManager
{
    public static readonly ActivitySource Source = new("FactoryWorker.Gateway.Runtimes");
    static readonly Meter MeterOf = new("FactoryWorker.Gateway.Runtimes");
    static readonly Counter<long> Executions = MeterOf.CreateCounter<long>("fworker.runtime.executions", description: "Executions of runtime workers by runtime and outcome");

    public sealed class Exec
    {
        public string Name = "", Execution = "", Url = "", Phase = "", Status = "provisioning", Outcome = "", StopReason = "";
        public int Item;
        public DateTime StartedAt = DateTime.UtcNow;
        public DateTime? RegisteredAt, EndedAt;
        public bool Ended;
        // W9.10j: the slot of the scheduler's ledger this execution was started for ("" for a session the plant offered on its own) and the arm of the saga that asked for it
        public string Slot = "", Arm = "";
        // W9.10j: the stop of this execution failed this many times; the next try is due at StopRetryAt (Poll)
        public int StopFailures;
        public DateTime StopRetryAt;
    }
    public sealed record Ticket(string Name, int Item, string Phase, string Profile, string Nonce, string Role = "", string Slot = "", string Arm = "");

    readonly object gate = new();
    readonly RuntimeSettings cfg;
    readonly IWorkerRuntime cae;
    readonly RuntimeNonces nonces;
    readonly Dictionary<string, Exec> execs = new(StringComparer.Ordinal);
    readonly Dictionary<(int Item, string Phase), int> failures = new();
    readonly Dictionary<(int Item, string Phase), int> launches = new();   // executions in a row of an item in one phase (RuntimeLimits.MaxLaunchesPerPhase)
    // the error of the last failure of an item and phase (its outcome; for a failed start the error, where a non-2xx launch carries the runtime's answer, at most 1 KB), shown in /status
    readonly Dictionary<(int Item, string Phase), string> failureErrors = new();
    // follow-up sessions of an item in a row, in any phase, that ended without registering and without pushing (IRuntimeWork): at
    // MaxFailuresPerPhase the item is parked instead of resumed again (#415, #416: seven sessions that never connected, alternating phases)
    readonly Dictionary<int, int> idleFollowUps = new();
    readonly List<JsonObject> last = new();
    DateTime day = DateTime.UtcNow.Date; int today;
    // W9.10j: the executions begun today for a slot of the scheduler's ledger. The ledger counts its grants itself (what runs and the day's), so what this manager tells the scheduler it runs outside the ledger (Load) is its
    // own count less these; the manager's own limits (CanStart) still count every execution, as the plant sees all of them. Counted since this process started: after a restart the day's earlier grants are in both counts again.
    int ledgerToday;
    int startFailuresInRow; bool healthy = true; string unhealthyWhy = "";
    bool relisted;
    // W0.2: the UTC day for which `today` was last counted from the runtime's task history (IRunHistory); `today` is a cache of that count plus the
    // launches since. A runtime without a history never sets it and counts in memory only.
    DateTime countedDay = DateTime.MinValue;

    public Plant? Plant { get; set; }
    // the leader lease (W0.1): a start is not made, and an end it caused is not settled, claimed or revoked, once this says false; defaults to the plant's
    public Func<bool>? MayAct { get; set; }
    // the gateway's own hands: park an item (rule verb park-item), end a claim the item still carries, revoke a name
    public Func<int, string, Task>? Park { get; set; }
    public Func<int, string, Task>? ReleaseClaim { get; set; }
    public Func<string, Task>? Revoke { get; set; }
    // W9.10h (only where the engine acts): what the item's saga and the scheduler are told of this runtime's sessions. The budget of the item's step (a launch, a failure, an idle follow-up, a session that
    // connected or pushed: item, kind, business key, reason) and a start that was refused (item, business key, outcome: "host-full" is a wait, anything else frees the slot and drops the request). Both are
    // fire and forget: a fault in them never reaches the plant or the start.
    public Action<int, Fworker.Workflows.WorkItem.BudgetKind, string, string>? OnBudget { get; set; }
    public Action<int, string, string>? OnStartRefused { get; set; }
    void Spend(int item, Fworker.Workflows.WorkItem.BudgetKind kind, string key, string reason) { try { OnBudget?.Invoke(item, kind, key, reason); } catch (Exception x) { Log("budget-feed-failed", "", item, x.GetType().Name); } }
    void Refused(int item, string key, string outcome) { try { OnStartRefused?.Invoke(item, key, outcome); } catch (Exception x) { Log("start-refused-feed-failed", "", item, x.GetType().Name); } }
    // before the plant takes back what an ended execution held: the gateway settles what the execution left (the Copilot runtime: a Development
    // task's pull request waits for its checks in the gateway, rule verb copilot-settle). Not for an execution that never started.
    public Func<Exec, Task<bool>>? Ending { get; set; }   // true: the item was settled (the end is no failure of the phase)
    // the items an execution's owner holds between its sessions (the Copilot runtime: one task per item, resumed), for /status
    public Func<JsonArray>? Owned { get; set; }
    public Func<JsonObject>? Quota { get; set; }

    readonly RuntimeLimits lim;
    public RuntimeManager(RuntimeSettings cfg, IWorkerRuntime cae, RuntimeNonces nonces, RuntimeLimits? limits = null)
    { this.cfg = cfg; this.cae = cae; this.nonces = nonces; lim = limits ?? RuntimeLimits.Of(cfg.CaeJob); }

    public RuntimeSettings Settings => cfg;
    // the session transcript store (gateway/Transcripts.cs): told when an execution of a runtime whose container posts its lines starts and ends
    public Action<string, int, string, string>? SessionStarted { get; set; }
    public Action<string>? SessionEnded { get; set; }
    public RuntimeLimits Limits => lim;
    public string Kind => cae.Kind;
    public bool On => lim.IsOn();
    public double AckSeconds => lim.AckSeconds;

    // ---- called by the plant, under its gate: no I/O ----------------------------------------------------------------------------------------
    public bool IsRuntimeWorker(string name) { lock (gate) return execs.ContainsKey(name); }
    public string ExistingFor(int item) { lock (gate) return execs.Values.FirstOrDefault(e => e.Item == item && !e.Ended)?.Name ?? ""; }
    // the items this runtime ran or runs an execution for since the process started (the CI release's candidates)
    public HashSet<int> ItemsSeen() { lock (gate) { var s = execs.Values.Select(e => e.Item).Concat(launches.Keys.Select(k => k.Item)).Concat(failures.Keys.Select(k => k.Item)).ToHashSet(); foreach (var x in last) if (x["item"]?.GetValue<int>() is int i) s.Add(i); return s; } }
    public string UrlOf(string name) { lock (gate) return execs.TryGetValue(name, out var e) ? e.Url : ""; }
    // the execution of this worker was started and has not ended
    public bool Alive(string name) { lock (gate) return execs.TryGetValue(name, out var e) && !e.Ended; }
    public string ExecutionOf(string name) { lock (gate) return execs.TryGetValue(name, out var e) ? e.Execution : ""; }
    public bool Exhausted(int item, string phase)
    {
        lock (gate)
        {
            if (failures.GetValueOrDefault((item, phase)) >= lim.MaxFailuresPerPhase) return true;
            if (idleFollowUps.GetValueOrDefault(item) >= lim.MaxFailuresPerPhase)
            {
                failureErrors[(item, phase)] = $"followups-idle: {idleFollowUps[item]} follow-up sessions in a row ended without connecting to the gateway or pushing";
                return true;
            }
            if (lim.MaxLaunchesPerPhase > 0 && launches.GetValueOrDefault((item, phase)) >= lim.MaxLaunchesPerPhase)
            {
                failureErrors[(item, phase)] = $"launches-exhausted: {lim.MaxLaunchesPerPhase} executions in a row without finishing the phase";
                return true;
            }
            return false;
        }
    }

    public (bool Ok, string Code) CanStart() => CanStart(false);

    /// <summary>
    /// Whether a session may be started now. <paramref name="ledgerGranted"/> (W9.10j): the session is for a slot the scheduler granted. The scheduler has admitted it against the runtime's concurrency and the day's budget
    /// (counting what this manager runs outside the ledger), so those two are not asked again here: asking would count the ledger's own sessions twice, and the item would wait for a place the scheduler already gave it.
    /// What is no shortage of slots still holds: the runtime is off, its executions are not counted yet, it is unhealthy, its quota is spent, and the Wolverine host that carries the start is not running (the offer
    /// waits for the host; a start dispatched into a host that is down would be lost).
    /// </summary>
    public (bool Ok, string Code) CanStart(bool ledgerGranted)
    {
        lock (gate)
        {
            Roll();
            if (!lim.IsOn()) return (false, "runtime-off");
            if (!relisted) return (false, "runtime-relisting");       // until the executions were read at start, a second one could be started
            if (cae is IRunHistory && countedDay != day) return (false, "runtime-relisting");   // a new UTC day: until the day's runs are counted from the history
            if (!healthy) return (false, "runtime-unhealthy");
            if (lim.Blocked() is { Length: > 0 } why) return (false, why);
            if (DispatchStart != null && DispatchReady?.Invoke() == false) return (false, "host-not-running");
            if (ledgerGranted) return (true, "");
            if (execs.Values.Count(e => !e.Ended) >= lim.MaxConcurrent) return (false, "runtime-at-capacity");
            if (today >= lim.MaxExecutionsPerDay) return (false, "runtime-budget");
            return (true, "");
        }
    }

    // W7.1: what this manager runs and has started today, for the v2 CapacityScheduler, which shares the runtime's limits with the plant (the hybrid composition), and the reason CanStart would refuse that is no shortage of
    // slots (the same order: not counted yet, unhealthy, quota). A short read under the lock; called only where the engine acts.
    public (int Running, int Today, string? Blocked) Load()
    {
        lock (gate)
        {
            Roll();
            string? blocked = null;
            if (!relisted || (cae is IRunHistory && countedDay != day)) blocked = "runtime-relisting";
            else if (!healthy) blocked = "runtime-unhealthy";
            else if (lim.Blocked() is { Length: > 0 } why) blocked = why;
            // the executions begun for the ledger's slots are the ledger's own: it counts them itself (W9.10j)
            return (execs.Values.Count(e => !e.Ended && e.Slot == ""), Math.Max(0, today - ledgerToday), blocked);
        }
    }

    Ticket IRuntimeManager.Begin(int item, string phase, string profile, string role) => Begin(item, phase, profile, role);

    /// <summary>Reserves a worker name and a nonce for a session. <paramref name="slot"/> and <paramref name="arm"/> (W9.10j): the slot of the scheduler's ledger the session is started for, and the arm of the saga that asked for it.</summary>
    public Ticket Begin(int item, string phase, string profile, string role = "", string slot = "", string arm = "")
    {
        var name = $"{lim.Prefix}-{item}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lim.TokenSeconds;
        var nonce = lim.MintCredential != null ? lim.MintCredential(name, item, phase, expires)
            : nonces.Mint(name, cae.Kind, expires, TimeSpan.FromSeconds(Math.Max(lim.AckSeconds, 60) + 600));
        lock (gate)
        {
            Roll(); today++; if (slot != "") ledgerToday++;
            execs[name] = new Exec { Name = name, Item = item, Phase = phase, Slot = slot, Arm = arm };
            // another phase of the item started: the item made progress, the count in a row starts again
            foreach (var k in launches.Keys.Where(k => k.Item == item && k.Phase != phase).ToList()) launches.Remove(k);
            launches[(item, phase)] = launches.GetValueOrDefault((item, phase)) + 1;
        }
        return new Ticket(name, item, phase, profile, nonce, role, slot, arm);
    }

    public void Registered(string name)
    {
        int item = 0;
        lock (gate) { if (execs.TryGetValue(name, out var e) && e.RegisteredAt == null) { e.RegisteredAt = DateTime.UtcNow; startFailuresInRow = 0; idleFollowUps.Remove(e.Item); item = e.Item; } }
        if (item > 0) Spend(item, Fworker.Workflows.WorkItem.BudgetKind.Registered, $"registered|{name}", "the session connected");
    }

    // a session started outside the plant (the Copilot owner's fix-forward of a red master) counts against the daily cap
    public void CountExternal() { lock (gate) { Roll(); today++; } }

    void Roll() { var d = DateTime.UtcNow.Date; if (d != day) { day = d; today = 0; ledgerToday = 0; } }

    // Under the lock: the execution of this name never started and is refunded: the day's count goes back by one, and the ledger's share with it when the execution was begun for a slot
    void RefundToday(string name)
    {
        today = Math.Max(0, today - 1);
        if (execs.TryGetValue(name, out var e) && e.Slot != "") ledgerToday = Math.Max(0, ledgerToday - 1);
    }

    // ---- outside the plant's lock -----------------------------------------------------------------------------------------------------------
    // v2 (W3.1): set where the engine mode is V2. The start and the stop are then commands dispatched through the Wolverine outbox, and their results come
    // back as CompleteStart and CompleteStop; null is v1, which calls the adapter here and is unchanged.
    public Func<Ticket, Task>? DispatchStart { get; set; }
    public Func<string, string, Task>? DispatchStop { get; set; }
    // W9.10j: whether the host that carries the dispatched commands is running; a start is not offered while it is not (CanStart). Null: always.
    public Func<bool>? DispatchReady { get; set; }

    public async Task Provision(Ticket t) => await ProvisionDispatched(t);

    // true: the start was dispatched as a command (v2) and CompleteStart settles it
    async Task<bool> ProvisionDispatched(Ticket t)
    {
        // the plant's place for the launched line is given up however the start call ends (RuntimeLaunched fills it first when it writes one)
        var dispatched = false;
        try { dispatched = await ProvisionCore(t); }
        finally { if (!dispatched) LaunchDone(t); }
        return dispatched;
    }

    // #522: v1's Provision answers when the adapter has answered, so its caller reads the execution at once. A caller that needs that under v2 (the test hook of the
    // octopus runtime) starts here: the start is dispatched, and the call answers when the result message has settled it (CompleteStart), or when the bound has passed.
    // v1's call is bounded by the adapters' HTTP timeouts (30 s a request; a start makes up to four), so the bound is the same order. False: the bound passed and the
    // result is still on its way (a transient fault is retried with pauses of up to a minute); the start settles when it arrives, nothing is lost. Without a dispatch
    // (v1, a fenced process, a dispatch that failed) this is Provision.
    public static readonly TimeSpan StartAnswerBound = TimeSpan.FromSeconds(120);

    public async Task<bool> ProvisionAndWait(Ticket t, TimeSpan bound)
    {
        TaskCompletionSource? done = null;
        if (DispatchStart != null)
        {
            // registered before the dispatch: the result can come back before the dispatch call returns
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) startWaiters[t.Name] = done;
        }
        bool dispatched;
        try { dispatched = await ProvisionDispatched(t); }
        catch { ForgetWaiter(t.Name); throw; }
        if (!dispatched || done == null) { ForgetWaiter(t.Name); return true; }
        try { await done.Task.WaitAsync(bound); return true; }
        catch (TimeoutException) { ForgetWaiter(t.Name); return false; }
    }

    readonly Dictionary<string, TaskCompletionSource> startWaiters = new(StringComparer.Ordinal);
    void ForgetWaiter(string name) { lock (gate) startWaiters.Remove(name); }
    void StartAnswered(string name) { TaskCompletionSource? w; lock (gate) startWaiters.Remove(name, out w); w?.TrySetResult(); }

    public void NoteStarted(string name, string execution, string url)
    {
        // only into a live execution that has none yet: CompleteStart (Settle) sets the same values and the status, and decides about an execution that ended meanwhile
        lock (gate) { if (execs.TryGetValue(name, out var e) && !e.Ended && e.Execution == "") { e.Execution = execution; e.Url = url; } }
    }
    void LaunchDone(Ticket t) { try { Plant?.RuntimeLaunchDone(t.Name); } catch (Exception x) { Log("journal-failed", t.Name, t.Item, x.Message); } }

    // v2: the result of the start command. The launched line's place is given up as in v1, however it ends.
    public async Task CompleteStart(string name, int item, string phase, ProvisionResult r)
    {
        try { await CompleteStartCore(name, item, phase, r); }
        finally { StartAnswered(name); }   // however it ended: a caller of ProvisionAndWait stops waiting
    }

    async Task CompleteStartCore(string name, int item, string phase, ProvisionResult r)
    {
        Ticket? t; lock (gate) t = execs.TryGetValue(name, out var e) && !e.Ended ? new Ticket(name, item, phase, "", "") : null;
        // the execution was settled meanwhile (stopped, lapsed, a restart): a start that succeeded is stopped, one that failed has nothing to settle
        if (t == null) { if (r.Ok) { try { await cae.Deprovision(name, "ended-during-start", CancellationToken.None); } catch (Exception x) { Log("deprovision-failed", name, item, x.Message); } } return; }
        try { using var span = Source.StartActivity("runtime.provision.result"); await Settle(t, r, span); }
        finally { LaunchDone(t); }
    }

    public void CompleteStop(string name, bool ok, string error)
    {
        Log(ok ? "deprovision" : "deprovision-failed", name, 0, error);
        if (!ok) StopFailed(name); else StopDone(name);
    }

    void StopDone(string name) { lock (gate) { if (execs.TryGetValue(name, out var e)) e.StopFailures = 0; } }

    // W9.10j: a stop that failed is tried again (Poll) with a pause that doubles, MaxStopAttempts times in all; the runtime's own reports (the list that shows the execution ended, the host report that shows the container
    // gone) end the record in Poll before that. After the last try the record is ended here as "stop-failed": the container may still run, and then the host's report counts it outside the ledger.
    public const int MaxStopAttempts = 4;
    public TimeSpan StopRetryPause { get; set; } = TimeSpan.FromSeconds(30);
    void StopFailed(string name)
    {
        lock (gate)
        {
            if (!execs.TryGetValue(name, out var e) || e.Ended) return;
            e.StopFailures++;
            e.StopRetryAt = DateTime.UtcNow + TimeSpan.FromTicks(StopRetryPause.Ticks << Math.Min(e.StopFailures - 1, 6));
        }
    }

    // true: the start was dispatched as a command, the rest happens in CompleteStart
    async Task<bool> ProvisionCore(Ticket t)
    {
        using var span = Source.StartActivity("runtime.provision");
        span?.SetTag("fworker.runtime", cae.Kind); span?.SetTag("fworker.worker", t.Name); span?.SetTag("fworker.item", t.Item); span?.SetTag("fworker.phase", t.Phase);
        // fencing (W0.1): a session is not started by a process that lost the lease after it decided to (the successor decides again from its own sweep).
        // The slot and the launch count are given back as for "no capacity": no failure of the item, no launch.
        if ((MayAct ?? Plant?.MayAct)?.Invoke() == false)
        {
            nonces.Forget(t.Name);
            lock (gate) { RefundToday(t.Name); if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            Log("provision-fenced", t.Name, t.Item, "lease-lost: this process no longer holds the leader lease; nothing was started");
            await End(t.Name, "fenced", failed: false, startFailure: false);
            return false;
        }
        if (DispatchStart != null)
        {
            // the command carries the ticket's nonce and the idempotency key of the worker name; the outbox holds it until the transaction commits
            try { await DispatchStart(t); Log("provision-dispatched", t.Name, t.Item, cae.Kind); return true; }
            // W9.10j: the Wolverine host that carries the command is not running (a restart, a hand-over): nothing was started and nothing failed. It settles like "no capacity": the execution and the launch are given back, the
            // scheduler's slot goes back and the request waits again; the offer waits for the host (CanStart)
            catch (WolverineHostDownException) { await Settle(t, new ProvisionResult(false, "", "no capacity"), span); return false; }
            catch (Exception e) { await Settle(t, new ProvisionResult(false, "", "start-error: " + e.GetType().Name), span); return false; }
        }
        ProvisionResult r;
        try { r = await cae.Provision(new ProvisionRequest(t.Name, t.Item, t.Phase, t.Profile, t.Nonce, t.Role, cfg.Launch), CancellationToken.None); }
        catch (Exception e) { r = new ProvisionResult(false, "", "start-error: " + (e is HttpRequestException { Message: var m } && m.StartsWith("octopus ", StringComparison.Ordinal) ? m : e.GetType().Name)); }
        await Settle(t, r, span);
        return false;
    }

    // what a start's result does to the records, the plant and the item: the same for v1's direct call and v2's result message
    async Task Settle(Ticket t, ProvisionResult r, Activity? span)
    {
        Log("provision", t.Name, t.Item, r.Ok ? r.Execution : r.Error);
        if (r.Ok)
        {
            bool late; lock (gate) { late = !execs.TryGetValue(t.Name, out var e) || e.Ended || e.StopReason != ""; if (execs.TryGetValue(t.Name, out var e2)) { e2.Execution = r.Execution; e2.Url = r.Url; e2.Status = "Running"; } }
            span?.SetTag("fworker.execution", r.Execution);
            if (!late) { try { SessionStarted?.Invoke(t.Name, t.Item, t.Phase, cae.Kind); } catch (Exception x) { Log("transcript-failed", t.Name, t.Item, x.Message); } }
            // the launch counts against the step's launches in a row once the execution exists (a refused or failed start is not one: it is a refusal or a failure below)
            if (!late) Spend(t.Item, Fworker.Workflows.WorkItem.BudgetKind.Launch, $"launch|{t.Name}", cae.Kind);
            // the item's journal names the execution and its page (a Copilot task: "Copilot session: <url>"); a runtime without a page adds no line
            if (!late && r.Url != "") { try { Plant?.RuntimeLaunched(t.Name, t.Item, t.Phase, cae.Kind, r.Execution, r.Url); } catch (Exception x) { Log("journal-failed", t.Name, t.Item, x.Message); } }
            // ended or stopped while the start call ran (a revoke, a lapse): stop it now, or it would run unattended
            if (late) { try { await cae.Deprovision(t.Name, "ended-during-start", CancellationToken.None); } catch (Exception x) { Log("deprovision-failed", t.Name, t.Item, x.Message); } }
            return;
        }
        nonces.Forget(t.Name);
        // the adapter settled the item instead of starting a task (copilot-settle: its pull request waits for its checks): no failure, no launch
        if (r.Error == "settled")
        {
            lock (gate) { if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            await End(t.Name, "settled", failed: false, startFailure: false);
            return;
        }
        // the runtime refused the launch for its account's quota or billing: no failure of the item, no launch counted, the item keeps waiting
        // the runtime has no free host now (the octopus runtime): the item queues, no failure, no launch counted
        if (r.Error == "no capacity")
        {
            lock (gate) { RefundToday(t.Name); if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            Refused(t.Item, $"refused|{t.Name}", "host-full");   // (the scheduler's slot goes back and the request waits again: no host had room)
            await End(t.Name, "no-capacity", failed: false, startFailure: false);
            return;
        }
        // W9.10c (v2 only: the Copilot launch port answers it): a launch or a resume of this item and phase has an outcome nobody knows yet (its call timed out, or another one is reserved), and the reconciler
        // decides. Not a failure of the item and no launch counted; the item is offered again and the reservation answers then.
        if (r.Error == "reserved")
        {
            lock (gate) { if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            Refused(t.Item, $"refused|{t.Name}", "reserved");
            await End(t.Name, "reserved", failed: false, startFailure: false);
            return;
        }
        if (r.Error == "quota")
        {
            lock (gate) { if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            Refused(t.Item, $"refused|{t.Name}", "quota");
            await End(t.Name, "quota-blocked", failed: false, startFailure: false);
            return;
        }
        span?.SetStatus(ActivityStatusCode.Error, r.Error);
        lock (gate) failureErrors[(t.Item, t.Phase)] = r.Error;
        Refused(t.Item, $"refused|{t.Name}", "start-failed");   // (the slot is free and the request dropped; the failure is counted by End, and the saga's budget with it)
        await End(t.Name, "start-failed", failed: true, startFailure: true);
    }

    public async Task Deprovision(string name, string reason)
    {
        using var span = Source.StartActivity("runtime.deprovision");
        span?.SetTag("fworker.runtime", cae.Kind); span?.SetTag("fworker.worker", name); span?.SetTag("fworker.reason", reason);
        lock (gate) { if (!execs.TryGetValue(name, out var e) || e.Ended) return; e.StopReason = reason; }
        nonces.Forget(name);
        if (DispatchStop != null)
        {
            try { await DispatchStop(name, reason); Log("deprovision-dispatched", name, 0, reason); }
            catch (Exception e) { span?.SetStatus(ActivityStatusCode.Error, e.Message); Log("deprovision-failed", name, 0, e.Message); StopFailed(name); }
            return;
        }
        try { await cae.Deprovision(name, reason, CancellationToken.None); Log("deprovision", name, 0, reason); }
        catch (Exception e) { span?.SetStatus(ActivityStatusCode.Error, e.Message); Log("deprovision-failed", name, 0, e.Message); }   // (v1 stops and does not retry: only the commands of the v2 engine do, StopFailed)
    }

    // The execution has ended (or was never started): the plant takes back what the worker still held, a failure is counted against the item and
    // phase (the item is parked at the limit), an execution that never registered counts against the runtime's health.
    async Task End(string name, string outcome, bool failed, bool startFailure)
    {
        Exec? e; bool park = false; bool unhealthyNow = false;
        lock (gate)
        {
            if (!execs.TryGetValue(name, out e) || e.Ended) return;
            e.Ended = true; e.EndedAt = DateTime.UtcNow; e.Outcome = outcome;
            try { SessionEnded?.Invoke(name); } catch { }
            // W9.10h: the host had no room (the start refused with exit 75): a wait like "no capacity", not a failure. The execution and its launch are given back, nothing counts against the item or the runtime's health
            if (outcome == "host-full")
            {
                RefundToday(e.Name);
                if (launches.TryGetValue((e.Item, e.Phase), out var lc)) { if (lc <= 1) launches.Remove((e.Item, e.Phase)); else launches[(e.Item, e.Phase)] = lc - 1; }
            }
            // a failure is counted before the plant takes the item back (RuntimeEnded): a plan in between would otherwise see the phase not yet
            // exhausted and start one execution more than maxFailuresPerPhase
            if (failed) { var n0 = failures[(e.Item, e.Phase)] = failures.GetValueOrDefault((e.Item, e.Phase)) + 1; park = n0 == lim.MaxFailuresPerPhase; if (outcome != "start-failed") failureErrors[(e.Item, e.Phase)] = outcome; }
            if (e.RegisteredAt == null && (failed || startFailure))
            {
                startFailuresInRow++;
                if (healthy && startFailuresInRow >= lim.UnhealthyAfterFailures) { healthy = false; unhealthyNow = true; unhealthyWhy = $"{startFailuresInRow} executions in a row ended without their worker registering"; }
            }
            last.Add(new JsonObject { ["worker"] = e.Name, ["item"] = e.Item, ["phase"] = e.Phase, ["execution"] = e.Execution, ["url"] = e.Url, ["outcome"] = outcome, ["error"] = failed ? failureErrors.GetValueOrDefault((e.Item, e.Phase)) : null, ["started"] = e.StartedAt.ToString("o"), ["ended"] = e.EndedAt?.ToString("o"), ["detail"] = Detail(e.Name) });
            if (last.Count > 10) last.RemoveAt(0);
            foreach (var k in execs.Where(kv => kv.Value.Ended && kv.Value.EndedAt < DateTime.UtcNow.AddHours(-1)).Select(kv => kv.Key).ToList()) execs.Remove(k);
        }
        if (unhealthyNow) Log("unhealthy", name, e.Item, unhealthyWhy);
        // the saga's budget of the step follows the runtime's (a failure here is a failure there), and a start the host refused for room frees the scheduler's slot
        if (failed) Spend(e.Item, Fworker.Workflows.WorkItem.BudgetKind.Failure, $"failure|{name}", outcome);
        if (outcome == "host-full") Refused(e.Item, $"refused|{name}", "host-full");
        // a follow-up session is judged by its end and the work it pushed, not only by its registration: one that succeeded and pushed to the
        // item's pull request without connecting did its step (Development: settled below, its pull request waits for its checks); one that
        // neither connected nor pushed did nothing, and such sessions in a row park the item (Exhausted) instead of resuming it again
        bool pushed = false, idle = false;
        if (outcome == "succeeded" && e.RegisteredAt == null && cae is IRuntimeWork rw && rw.FollowUp(name))
        {
            bool? pu = null;
            try { pu = await rw.Pushed(name, CancellationToken.None); } catch (Exception x) { Log("pushed-read-failed", name, e.Item, x.Message); }
            // not known (the head unreadable): neither, the end is judged as before
            pushed = pu == true; idle = pu == false;
            if (idle) rw.Idle(name);
            Log(pushed ? "followup-pushed" : idle ? "followup-idle" : "followup-unknown", name, e.Item, e.Phase);
        }
        lock (gate) { if (idle) idleFollowUps[e.Item] = idleFollowUps.GetValueOrDefault(e.Item) + 1; else if (pushed) idleFollowUps.Remove(e.Item); }
        if (idle) Spend(e.Item, Fworker.Workflows.WorkItem.BudgetKind.IdleFollowUp, $"idle|{name}", "the follow-up ended without connecting or pushing");
        else if (pushed) Spend(e.Item, Fworker.Workflows.WorkItem.BudgetKind.Registered, $"pushed|{name}", "the follow-up pushed");
        var settled = false;
        if (Ending != null && outcome is not ("settled" or "start-failed" or "quota-blocked" or "no-capacity" or "host-full" or "fenced" or "stop-failed")) { try { settled = await Ending(e); } catch (Exception x) { Log("ending-failed", name, e.Item, x.Message); } }
        // the phase is done pending checks: the next Development task (a fix-forward round) is counted afresh; fixForwardMaxRounds caps those
        if (settled) lock (gate) launches.Remove((e.Item, e.Phase));
        var tookBack = false; try { tookBack = Plant?.RuntimeEnded(name, e.Item, outcome, e.Url) ?? false; } catch (Exception x) { Log("take-back-failed", name, e.Item, x.Message); }
        // an execution that exited by itself while its worker still held the item did not finish it: a failure like any other
        if (tookBack && !failed && !settled && !pushed && e.StopReason == "" && outcome == "succeeded")
        {
            failed = true; outcome = "ended-with-item";
            lock (gate) { e.Outcome = outcome; var n1 = failures[(e.Item, e.Phase)] = failures.GetValueOrDefault((e.Item, e.Phase)) + 1; park = n1 == lim.MaxFailuresPerPhase; failureErrors[(e.Item, e.Phase)] = outcome; }
            Spend(e.Item, Fworker.Workflows.WorkItem.BudgetKind.Failure, $"failure|{name}", outcome);
        }
        Executions.Add(1, new KeyValuePair<string, object?>("runtime", cae.Kind), new KeyValuePair<string, object?>("outcome", outcome));
        if (tookBack && outcome != "fenced" && ReleaseClaim != null) { try { await ReleaseClaim(e.Item, name); } catch (Exception x) { Log("release-claim-failed", name, e.Item, x.Message); } }
        // a failure costs the item one of its executions in this phase: the one that failed, or the one whose offer was lost with it
        if (Revoke != null && outcome != "fenced" && ((failed && e.RegisteredAt != null) || lim.RevokeOnEnd)) { try { await Revoke(name); } catch (Exception x) { Log("revoke-failed", name, e.Item, x.Message); } }
        // at the limit the next plan of the item parks it (Plant.RouteRuntime), so the park happens once, on the plant's side
        if (park) Log("failures-exhausted", name, e.Item, e.Phase);
    }

    // An offer to a runtime worker lapsed (Plant.Tick): its execution is stopped and the lapse counts as a failed execution of the item's phase.
    public void OfferLapsed(string name) => _ = Task.Run(async () => { await Deprovision(name, "offer-lapsed"); await End(name, "offer-lapsed", failed: true, startFailure: false); });

    // Under the plant's gate: the park runs on the pool (rule verb park-item), and the plant may queue it again once it has run.
    public void QueuePark(int item, string phase) => _ = Task.Run(async () =>
    {
        Log("park", "", item, "runtime-failed " + phase);
        // after the park the item starts afresh: an unpark by the operator gives its phase maxFailuresPerPhase executions again
        try { if (Park != null) { await Park(item, phase); lock (gate) { failures.Remove((item, phase)); failureErrors.Remove((item, phase)); launches.Remove((item, phase)); idleFollowUps.Remove(item); } } } catch (Exception x) { Log("park-failed", "", item, x.Message); }
        finally { Plant?.RuntimeParked(item, phase); }
    });

    public void ResetHealth() { lock (gate) { healthy = true; startFailuresInRow = 0; unhealthyWhy = ""; } }

    // One reading of the executions: ended ones are settled, and a running one is stopped when its worker never registered, or registered and was
    // never given an item, within the acknowledgement window. The first reading after the start adopts the executions found (restart recovery).
    public async Task Poll(CancellationToken ct)
    {
        IReadOnlyList<RuntimeInstance> list;
        try { list = await cae.List(ct); }
        catch (Exception e) { Log("list-failed", "", 0, e.Message); return; }
        var ends = new List<(string Name, string Outcome, bool Failed)>(); var stops = new List<(string Name, string Why)>();
        var now = DateTime.UtcNow;
        // W0.2: the day budget is counted from the runtime's task history at the start and after every UTC midnight (outside the lock: it reads the server)
        int? fromHistory = null; var historyDay = now.Date;
        if (cae is IRunHistory history && countedDay != historyDay)
        {
            try { fromHistory = await history.LaunchesSince(historyDay, ct); }
            catch (Exception e) { Log("history-failed", "", 0, e.Message); if (!relisted) return; }   // the first reading waits for the history: retried at the next poll
        }
        lock (gate)
        {
            if (fromHistory is int counted)
            {
                // Roll first: a midnight that passed while the history was read leaves the count for the next poll; otherwise the count replaces what the
                // process had (nothing before the first reading; after a midnight, the launches since it, which the history includes)
                Roll();
                if (day == historyDay) { today = Math.Max(today, counted); countedDay = historyDay; Log("budget-counted", "", 0, $"{counted} since {historyDay:yyyy-MM-dd} 00:00 UTC"); }
            }
            if (!relisted)
            {
                foreach (var i in list.Where(i => i.Status is "Running" or "Processing" && !execs.ContainsKey(i.WorkerName)))
                {
                    execs[i.WorkerName] = new Exec { Name = i.WorkerName, Item = i.Item, Phase = i.Phase, Execution = i.Execution, Status = i.Status, StartedAt = now };   // its window starts at adoption: the worker reconnects after the restart
                    if (cae is not IRunHistory && i.Started is { } st && st.Date == day) today++;   // a runtime with a history counts the whole day from it
                    Log("relisted", i.WorkerName, i.Item, i.Execution);
                }
                relisted = true;
            }
            else
                foreach (var i in list.Where(i => i.Adopted && i.Status is "Running" or "Processing" && !execs.ContainsKey(i.WorkerName)))
                {
                    execs[i.WorkerName] = new Exec { Name = i.WorkerName, Item = i.Item, Phase = i.Phase, Execution = i.Execution, Status = i.Status, StartedAt = now };
                    Log("adopted", i.WorkerName, i.Item, i.Execution);
                }
            foreach (var e in execs.Values.Where(e => !e.Ended && e.Execution != ""))
            {
                var i = list.FirstOrDefault(x => x.Execution == e.Execution);
                if (i == null) { if ((now - e.StartedAt).TotalSeconds > 60) ends.Add((e.Name, "gone", false)); continue; }
                e.Status = i.Status;
                if (i.Status == "Succeeded") ends.Add((e.Name, e.StopReason != "" ? "stopped:" + e.StopReason : "succeeded", false));
                else if (i.Status is "Failed" or "Degraded") ends.Add((e.Name, "failed", true));
                // the runtime refused the session for a reason of its own account (quota): no failure of the item, no launch counted
                else if (i.Status == "Blocked") { ends.Add((e.Name, "blocked", false)); launches.Remove((e.Item, e.Phase)); }
                // the host refused the start for room (the Octopus runtime, exit 75): no failure, the execution is refunded (End)
                else if (i.Status == "HostFull") ends.Add((e.Name, "host-full", false));
                else if (i.Status == "Stopped") ends.Add((e.Name, e.StopReason != "" ? "stopped:" + e.StopReason : "stopped", e.StopReason == ""));
            }
        }
        foreach (var (n, o, f) in ends) await End(n, o, f, false);
        List<Exec> running; lock (gate) running = execs.Values.Where(e => !e.Ended && e.StopReason == "").ToList();
        foreach (var e in running)
        {
            var (registered, owns, offered) = Plant?.RuntimeWorkerState(e.Name) ?? (false, false, false);
            if (registered) Registered(e.Name);
            DateTime? regAt; lock (gate) regAt = e.RegisteredAt;
            if (regAt == null && (now - e.StartedAt).TotalSeconds > lim.AckSeconds) stops.Add((e.Name, "never-registered"));
            else if (regAt != null && !owns && !offered && (now - regAt.Value).TotalSeconds > lim.AckSeconds) stops.Add((e.Name, "no-item"));
        }
        foreach (var (n, why) in stops)
        {
            await Deprovision(n, why);
            if (why == "never-registered") await End(n, why, failed: true, startFailure: true);
        }
        // W9.10j: the stops that failed are tried again when their pause is over; after the last try the record ends (the host's report counts a container that still runs as outside the ledger)
        List<(string Name, string Reason, bool GiveUp)> again;
        lock (gate) again = execs.Values.Where(e => !e.Ended && e.StopReason != "" && e.StopFailures > 0 && (e.StopFailures >= MaxStopAttempts || DateTime.UtcNow >= e.StopRetryAt)).Select(e => (e.Name, e.StopReason, e.StopFailures >= MaxStopAttempts)).ToList();
        foreach (var (n, reason, giveUp) in again)
        {
            if (giveUp) { Log("stop-given-up", n, 0, reason); await End(n, "stop-failed", failed: false, startFailure: false); }
            else
            {
                Log("stop-retry", n, 0, reason);
                lock (gate) { if (execs.TryGetValue(n, out var re)) re.StopRetryAt = DateTime.UtcNow + StopRetryPause; }   // (a dispatched stop answers later: no second try before then)
                await Deprovision(n, reason);
            }
        }
    }

    JsonNode? Detail(string name) { try { return (cae as IRuntimeDetail)?.Detail(name); } catch { return null; } }

    public JsonObject Status()
    {
        lock (gate)
        {
            Roll();
            return new JsonObject
            {
                ["preferConnected"] = cfg.PreferConnected,
                ["routing"] = new JsonArray(cfg.Routing.Select(r => (JsonNode)new JsonObject { ["phase"] = r.Phase, ["runtime"] = r.Runtime }).ToArray()),
                [cae.Kind] = new JsonObject
                {
                    ["enabled"] = lim.IsOn(), ["maxConcurrent"] = lim.MaxConcurrent, ["running"] = execs.Values.Count(e => !e.Ended), ["today"] = today, ["maxExecutionsPerDay"] = lim.MaxExecutionsPerDay,
                    ["healthy"] = healthy, ["unhealthyReason"] = unhealthyWhy, ["startFailuresInRow"] = startFailuresInRow, ["ackSeconds"] = lim.AckSeconds, ["relisted"] = relisted,
                    ["executions"] = new JsonArray(execs.Values.Where(e => !e.Ended).Select(e => (JsonNode)new JsonObject { ["worker"] = e.Name, ["item"] = e.Item, ["phase"] = e.Phase, ["execution"] = e.Execution, ["url"] = e.Url, ["status"] = e.Status, ["started"] = e.StartedAt.ToString("o"), ["registered"] = e.RegisteredAt?.ToString("o"), ["detail"] = Detail(e.Name) }).ToArray()),
                    ["owned"] = Owned?.Invoke(), ["quota"] = Quota?.Invoke(),
                    ["launches"] = new JsonArray(launches.Select(kv => (JsonNode)new JsonObject { ["item"] = kv.Key.Item, ["phase"] = kv.Key.Phase, ["count"] = kv.Value }).ToArray()), ["maxLaunchesPerPhase"] = lim.MaxLaunchesPerPhase,
                    ["failures"] = new JsonArray(failures.Select(kv => (JsonNode)new JsonObject { ["item"] = kv.Key.Item, ["phase"] = kv.Key.Phase, ["count"] = kv.Value, ["error"] = failureErrors.GetValueOrDefault(kv.Key) }).ToArray()),
                    // the detail is read again while the runtime still knows the worker: its usage arrives after the end (a Cursor agent's reads zero at first)
                    ["last"] = new JsonArray(last.Select(x => { var c = (JsonObject)x.DeepClone(); if (Detail(c["worker"]?.ToString() ?? "") is { } d) c["detail"] = d; return (JsonNode)c; }).ToArray())
                }
            };
        }
    }

    static void Log(string what, string worker, int item, string detail) =>
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = what, ["worker"] = worker, ["item"] = item, ["detail"] = detail }.ToJsonString());
}

// Reads the executions every runtimes.caeJob.listSeconds; the first reading at start is the restart recovery (2.11).
sealed class RuntimeLoop(RuntimeManager m) : BackgroundService
{
    // the leader lease (Leader.cs): only the acting gateway parks items and ends claims for the executions it reads
    public Func<bool> MayAct { get; init; } = () => true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (MayAct()) await m.Poll(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } catch (Exception e) { Console.Error.WriteLine($"runtime poll: {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(m.Limits.ListSeconds), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
