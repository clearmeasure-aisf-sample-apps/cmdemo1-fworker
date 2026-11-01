using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class Runner(string entry) : IRuleScriptCalls
{
    // runner.pool > 0: long-lived pwsh workers take the calls (RunnerPool.cs); null (the default): one process per call, as below.
    readonly RunnerPool? pool = RunnerPool.Create(entry);
    // at most runner.maxProcesses calls at once, pooled and spawned together, started by priority (RunnerGate.cs, issue 89)
    readonly RunnerGate limiter = RunnerGate.Create(entry);
    // the processes of calls that run now (not the pool's workers), for the process count and memory of /status
    readonly ConcurrentDictionary<long, Process> live = new();
    readonly ConcurrentDictionary<long, string> liveVerb = new();
    long liveKey;
    // Fencing (W0.1, docs/design/delivery-speed.md 18.1.1). The rule scripts that act on the board get the lease epoch this gateway holds as "_lease"
    // ({ epoch, holder }); while it is set they check the lease ref before every write (plugin/scripts/lib/Leader.ps1 Assert-LeaseHeld) and refuse it
    // once a newer process holds it. Null (lease off): nothing is added. The verbs are those whose writes the board, the merge queue or a deployment
    // depend on; a verb that is only read, and the lease's and the snapshot's own, are not fenced (the final snapshot of a superseded gateway must land).
    public Func<(long Epoch, string Holder)?>? Lease { get; set; }
    public static readonly IReadOnlySet<string> FencedVerbs = new HashSet<string>(StringComparer.Ordinal)
    { "sync-board", "merge", "settle", "resettle-item", "copilot-settle", "base-update-branch", "base-watch", "qc-sync", "ci-release" };
    int fencedInFlight;
    public int FencedInFlight => Volatile.Read(ref fencedInFlight);

    static string StripLease(string stdin)
    {
        if (!stdin.Contains("\"_lease\"", StringComparison.Ordinal)) return stdin;
        try { if (JsonNode.Parse(stdin) is JsonObject o && o.Remove("_lease")) return o.ToJsonString(); } catch (JsonException) { }
        return stdin;
    }

    /// <summary>The rule script's stdin with "_lease" added for a fenced verb while the lease is on; anything else is returned unchanged.</summary>
    public string InjectLease(string verb, string stdin)
    {
        // a "_lease" in the input came from a caller, never from the gateway: it is taken out, as "_cause" is, so no client can fence or unfence a script
        if (!FencedVerbs.Contains(verb) || Lease?.Invoke() is not { } l) return StripLease(stdin);
        try
        {
            var o = string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject;
            if (o is null) return stdin;
            o["_lease"] = new JsonObject { ["epoch"] = l.Epoch, ["holder"] = l.Holder };
            return o.ToJsonString();
        }
        catch (JsonException) { return stdin; }
    }

    /// <summary>
    /// A superseded gateway lets the fenced rule scripts that are running finish for at most <paramref name="wait"/> (they stop at their next write by
    /// themselves: the lease is lost), then ends those still running, so the final snapshot is written after nothing else of this process acts.
    /// </summary>
    public async Task<(int WaitedMs, int Killed)> DrainFencedAsync(TimeSpan wait)
    {
        var sw = Stopwatch.StartNew();
        while (FencedInFlight > 0 && sw.Elapsed < wait) await Task.Delay(100);
        var killed = 0;
        if (FencedInFlight > 0)
        {
            foreach (var kv in liveVerb.ToList()) if (FencedVerbs.Contains(kv.Value) && live.TryGetValue(kv.Key, out var p)) { try { p.Kill(true); killed++; } catch { } }
            killed += pool?.KillBusy(v => FencedVerbs.Contains(v)) ?? 0;
            var grace = Stopwatch.StartNew();
            while (FencedInFlight > 0 && grace.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        }
        return ((int)sw.ElapsedMilliseconds, killed);
    }
    // the shared GitHub budget (GitHubBudget.cs, issue 92): every answer's _github field is recorded there and taken out of the answer
    public GitHubBudget? Budget { get; set; }
    // W9.9a: where the engine acts, the verbs that have a v2 route are answered there, whoever asks: a session (the verb endpoint reaches the same routes first) or the gateway itself (the plant's hooks, the
    // sweep, the endpoints, the runtimes, QcHub, BaseWatch, the journal lines of the ticker). Set once when the table is built (V2VerbRoutes.Build); null where the engine does not act (tdd today, uat,
    // prod), so there every call is the rule script, as before. A verb with no route (the leader lease, the plant snapshot) is the script's in both cases.
    public V2VerbRoutes? Routes { get; set; }
    public async Task<(int Code, string Output)> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        if (Routes is { } routes && routes.TryGet(verb, out var handler))
        {
            // the fence of the script path (FencedVerbs, RunScriptAsync) holds for the route too: a process that no longer holds the leader lease refuses a verb that writes to the board
            // (the script would answer lease-lost at its write; a route has no such check of its own, except those that made one), and the call is counted so that DrainFencedAsync waits for it
            var fenced = FencedVerbs.Contains(verb);
            if (fenced && MayAct?.Invoke() == false)
            {
                var lost = (20, JsonSerializer.Serialize(new { ok = false, verb, code = "lease-lost", reason = $"this gateway no longer holds the leader lease; the step was not performed: {verb}", exit = 20 }));
                NoteFenced(verb, lost.Item1, lost.Item2);
                return lost;
            }
            if (fenced) Interlocked.Increment(ref fencedInFlight);
            try
            {
                var answer = await handler(stdin, timeout);
                return (answer.Code, answer.Output);
            }
            finally { if (fenced) Interlocked.Decrement(ref fencedInFlight); }
        }
        return await RunScriptAsync(verb, stdin, timeout);
    }

    /// <summary>Whether this process may act now (the leader lease, and the SQL lock where the engine acts: <c>GatewayLeader.MayAct</c>); null (the default) is always. Set with <see cref="Lease"/>; only a routed fenced verb asks it here, the script path has the lease in its input.</summary>
    public Func<bool>? MayAct { get; set; }

    /// <summary>Cutover review: true while the gateway is inert for want of a database; set by the composition (EngineOptions.NoDatabase). See <see cref="InertVerbs"/>.</summary>
    public Func<bool>? Inert { get; set; }

    /// <summary>The rule script, whatever v2 answers: what a v2 route that cannot answer (no ledger, no translator, a merge for an item the saga does not own) falls back to. A route must call this and never <see cref="RunAsync"/>, which would ask the route again.</summary>
    public async Task<(int Code, string Output)> RunScriptAsync(string verb, string stdin, TimeSpan timeout)
    {
        // cutover review: v2 configured and no database (EngineOptions.NoDatabase): the rule script runs for the gateway's own housekeeping and for the verbs that read, and for nothing else, whoever asks
        if (Inert?.Invoke() == true && !InertVerbs.RunnerMayRun(verb)) { var refused = InertVerbs.Refusal(verb); return (refused.Code, refused.Output); }
        // a call of the gateway itself that can wait (a journal line, the back-merge check, the metrics) is not started while the budget is low
        if (Budget is { } b && GitHubBudget.DeferredCalls.Contains(verb) && b.Low() is { Low: true } low)
        {
            b.CountCallRefusal();
            return (20, JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({low.Resource}) is below its reserve; this call waits for the reset", retryAfterSeconds = Math.Max(1, (int)(low.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = low.ResetAt.ToString("o"), exit = 20 }));
        }
        var fenced = FencedVerbs.Contains(verb);
        if (fenced) Interlocked.Increment(ref fencedInFlight);
        int code; string output;
        try { (code, output) = await RunProcessAsync(verb, InjectLease(verb, stdin), timeout); }
        finally { if (fenced) Interlocked.Decrement(ref fencedInFlight); }
        if (fenced) NoteFenced(verb, code, output);
        return (code, Budget?.Take(output) ?? output);
    }

    // The gateway's own line when a fenced script refused a step or stopped its pass because the lease was lost (the script wrote its reason to stderr, which is
    // logged as "[verb] ..."; this line is the one to search for: gateway = fenced)
    void NoteFenced(string verb, int code, string output)
    {
        if (!output.Contains("\"lease-lost\"", StringComparison.Ordinal) && !output.Contains("\"fenced\":{", StringComparison.Ordinal)) return;
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "fenced", ["verb"] = verb, ["epoch"] = Lease?.Invoke() is { } l ? l.Epoch : null, ["exit"] = code }.ToJsonString());
    }

    async Task<(int Code, string Output)> RunProcessAsync(string verb, string stdin, TimeSpan timeout)
    {
        // test-only (Tier 1 golden writes, tests/scenarios/golden/README.md), inert unless FACTORY_TEST_CAUSE_HEADER=1: the serving request's cause id as "_cause"
        stdin = TestCause.Inject(stdin);
        var queuedAt = DateTime.UtcNow;
        using var slot = await limiter.EnterAsync(verb, RunnerGate.MaxWait(RunnerGate.ClassOf(verb), timeout));
        if (slot is null)
        {
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runner"] = "busy", ["verb"] = verb, ["limit"] = limiter.Limit }.ToJsonString());
            return (20, JsonSerializer.Serialize(new { ok = false, verb, code = "runner-busy", reason = $"the gateway runs {limiter.Limit} rule scripts at once and none came free in time; try again", retryAfterSeconds = 15, exit = 20 }));
        }
        // the wait for the slot is taken from the time the call may run, down to a quarter of it: a caller that set a timeout is not kept much longer
        var waited = DateTime.UtcNow - queuedAt;
        if (waited > TimeSpan.Zero) timeout = timeout - waited > timeout / 4 ? timeout - waited : timeout / 4;
        if (pool is not null && await pool.TryRunAsync(verb, stdin, timeout) is { } pooled) return pooled;
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", entry, "--server", verb }) psi.ArgumentList.Add(a);
        RunnerEnv.Apply(psi);
        using var p = Process.Start(psi)!;
        var key = Interlocked.Increment(ref liveKey); live[key] = p; liveVerb[key] = verb;
        try
        {
            await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (20, JsonSerializer.Serialize(new { ok = false, verb, code = "timeout", reason = "the rule script did not finish in time", retryAfterSeconds = 30, exit = 20 })); }
            var err = await errTask; if (!string.IsNullOrWhiteSpace(err)) Console.Error.WriteLine($"[{verb}] {err.Trim()}");
            return (p.ExitCode, (await outTask).Trim());
        }
        finally { live.TryRemove(key, out _); liveVerb.TryRemove(key, out _); }
    }

    // /status: the gate (limit, running and waiting by class, peaks, the last starts), the pool's size, and the processes and memory now
    public JsonObject Status()
    {
        var o = limiter.Status();
        o["pool"] = pool?.Size ?? 0;
        o["memory"] = RunnerMemory.Read(live.Values.Concat(pool?.Processes() ?? Enumerable.Empty<Process>()).ToList());
        return o;
    }
}
