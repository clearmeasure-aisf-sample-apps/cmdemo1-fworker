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

sealed class RuntimeManager
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
    }
    public sealed record Ticket(string Name, int Item, string Phase, string Profile, string Nonce, string Role = "");

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
    int startFailuresInRow; bool healthy = true; string unhealthyWhy = "";
    bool relisted;

    public Plant? Plant { get; set; }
    // the gateway's own hands: park an item (rule verb park-item), end a claim the item still carries, revoke a name
    public Func<int, string, Task>? Park { get; set; }
    public Func<int, string, Task>? ReleaseClaim { get; set; }
    public Func<string, Task>? Revoke { get; set; }
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

    public (bool Ok, string Code) CanStart()
    {
        lock (gate)
        {
            Roll();
            if (!lim.IsOn()) return (false, "runtime-off");
            if (!relisted) return (false, "runtime-relisting");       // until the executions were read at start, a second one could be started
            if (!healthy) return (false, "runtime-unhealthy");
            if (lim.Blocked() is { Length: > 0 } why) return (false, why);
            if (execs.Values.Count(e => !e.Ended) >= lim.MaxConcurrent) return (false, "runtime-at-capacity");
            if (today >= lim.MaxExecutionsPerDay) return (false, "runtime-budget");
            return (true, "");
        }
    }

    public Ticket Begin(int item, string phase, string profile, string role = "")
    {
        var name = $"{lim.Prefix}-{item}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lim.TokenSeconds;
        var nonce = lim.MintCredential != null ? lim.MintCredential(name, item, phase, expires)
            : nonces.Mint(name, cae.Kind, expires, TimeSpan.FromSeconds(Math.Max(lim.AckSeconds, 60) + 600));
        lock (gate)
        {
            Roll(); today++; execs[name] = new Exec { Name = name, Item = item, Phase = phase };
            // another phase of the item started: the item made progress, the count in a row starts again
            foreach (var k in launches.Keys.Where(k => k.Item == item && k.Phase != phase).ToList()) launches.Remove(k);
            launches[(item, phase)] = launches.GetValueOrDefault((item, phase)) + 1;
        }
        return new Ticket(name, item, phase, profile, nonce, role);
    }

    public void Registered(string name) { lock (gate) { if (execs.TryGetValue(name, out var e) && e.RegisteredAt == null) { e.RegisteredAt = DateTime.UtcNow; startFailuresInRow = 0; idleFollowUps.Remove(e.Item); } } }

    // a session started outside the plant (the Copilot owner's fix-forward of a red master) counts against the daily cap
    public void CountExternal() { lock (gate) { Roll(); today++; } }

    void Roll() { var d = DateTime.UtcNow.Date; if (d != day) { day = d; today = 0; } }

    // ---- outside the plant's lock -----------------------------------------------------------------------------------------------------------
    public async Task Provision(Ticket t)
    {
        // the plant's place for the launched line is given up however the start call ends (RuntimeLaunched fills it first when it writes one)
        try { await ProvisionCore(t); }
        finally { try { Plant?.RuntimeLaunchDone(t.Name); } catch (Exception x) { Log("journal-failed", t.Name, t.Item, x.Message); } }
    }
    async Task ProvisionCore(Ticket t)
    {
        using var span = Source.StartActivity("runtime.provision");
        span?.SetTag("fworker.runtime", cae.Kind); span?.SetTag("fworker.worker", t.Name); span?.SetTag("fworker.item", t.Item); span?.SetTag("fworker.phase", t.Phase);
        ProvisionResult r;
        try { r = await cae.Provision(new ProvisionRequest(t.Name, t.Item, t.Phase, t.Profile, t.Nonce, t.Role), CancellationToken.None); }
        catch (Exception e) { r = new ProvisionResult(false, "", "start-error: " + (e is HttpRequestException { Message: var m } && m.StartsWith("octopus ", StringComparison.Ordinal) ? m : e.GetType().Name)); }
        Log("provision", t.Name, t.Item, r.Ok ? r.Execution : r.Error);
        if (r.Ok)
        {
            bool late; lock (gate) { late = !execs.TryGetValue(t.Name, out var e) || e.Ended || e.StopReason != ""; if (execs.TryGetValue(t.Name, out var e2)) { e2.Execution = r.Execution; e2.Url = r.Url; e2.Status = "Running"; } }
            span?.SetTag("fworker.execution", r.Execution);
            if (!late) { try { SessionStarted?.Invoke(t.Name, t.Item, t.Phase, cae.Kind); } catch (Exception x) { Log("transcript-failed", t.Name, t.Item, x.Message); } }
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
            lock (gate) { today = Math.Max(0, today - 1); if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            await End(t.Name, "no-capacity", failed: false, startFailure: false);
            return;
        }
        if (r.Error == "quota")
        {
            lock (gate) { if (launches.TryGetValue((t.Item, t.Phase), out var lc)) { if (lc <= 1) launches.Remove((t.Item, t.Phase)); else launches[(t.Item, t.Phase)] = lc - 1; } }
            await End(t.Name, "quota-blocked", failed: false, startFailure: false);
            return;
        }
        span?.SetStatus(ActivityStatusCode.Error, r.Error);
        lock (gate) failureErrors[(t.Item, t.Phase)] = r.Error;
        await End(t.Name, "start-failed", failed: true, startFailure: true);
    }

    public async Task Deprovision(string name, string reason)
    {
        using var span = Source.StartActivity("runtime.deprovision");
        span?.SetTag("fworker.runtime", cae.Kind); span?.SetTag("fworker.worker", name); span?.SetTag("fworker.reason", reason);
        lock (gate) { if (!execs.TryGetValue(name, out var e) || e.Ended) return; e.StopReason = reason; }
        nonces.Forget(name);
        try { await cae.Deprovision(name, reason, CancellationToken.None); Log("deprovision", name, 0, reason); }
        catch (Exception e) { span?.SetStatus(ActivityStatusCode.Error, e.Message); Log("deprovision-failed", name, 0, e.Message); }
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
        var settled = false;
        if (Ending != null && outcome is not ("settled" or "start-failed" or "quota-blocked" or "no-capacity")) { try { settled = await Ending(e); } catch (Exception x) { Log("ending-failed", name, e.Item, x.Message); } }
        // the phase is done pending checks: the next Development task (a fix-forward round) is counted afresh; fixForwardMaxRounds caps those
        if (settled) lock (gate) launches.Remove((e.Item, e.Phase));
        var tookBack = false; try { tookBack = Plant?.RuntimeEnded(name, e.Item, outcome, e.Url) ?? false; } catch (Exception x) { Log("take-back-failed", name, e.Item, x.Message); }
        // an execution that exited by itself while its worker still held the item did not finish it: a failure like any other
        if (tookBack && !failed && !settled && !pushed && e.StopReason == "" && outcome == "succeeded")
        {
            failed = true; outcome = "ended-with-item";
            lock (gate) { e.Outcome = outcome; var n1 = failures[(e.Item, e.Phase)] = failures.GetValueOrDefault((e.Item, e.Phase)) + 1; park = n1 == lim.MaxFailuresPerPhase; failureErrors[(e.Item, e.Phase)] = outcome; }
        }
        Executions.Add(1, new KeyValuePair<string, object?>("runtime", cae.Kind), new KeyValuePair<string, object?>("outcome", outcome));
        if (tookBack && ReleaseClaim != null) { try { await ReleaseClaim(e.Item, name); } catch (Exception x) { Log("release-claim-failed", name, e.Item, x.Message); } }
        // a failure costs the item one of its executions in this phase: the one that failed, or the one whose offer was lost with it
        if (Revoke != null && ((failed && e.RegisteredAt != null) || lim.RevokeOnEnd)) { try { await Revoke(name); } catch (Exception x) { Log("revoke-failed", name, e.Item, x.Message); } }
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
        lock (gate)
        {
            if (!relisted)
            {
                foreach (var i in list.Where(i => i.Status is "Running" or "Processing" && !execs.ContainsKey(i.WorkerName)))
                {
                    execs[i.WorkerName] = new Exec { Name = i.WorkerName, Item = i.Item, Phase = i.Phase, Execution = i.Execution, Status = i.Status, StartedAt = now };   // its window starts at adoption: the worker reconnects after the restart
                    if (i.Started is { } st && st.Date == day) today++;
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
