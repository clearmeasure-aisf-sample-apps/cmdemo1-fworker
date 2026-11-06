using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class Runner(string entry)
{
    // one pwsh process per call. At most runner.maxProcesses calls at once, started by priority (RunnerGate.cs, issue 89)
    readonly RunnerGate limiter = RunnerGate.Create(entry);
    // the processes of calls that run now, for the process count and memory of /status
    readonly ConcurrentDictionary<long, Process> live = new();
    readonly ConcurrentDictionary<long, string> liveVerb = new();
    long liveKey;
    // The verbs whose writes the board, the merge queue or a deployment depend on: a process that no longer holds the leader lock refuses them (MayAct), and a superseded gateway waits for those
    // that run and then ends them (DrainFencedAsync). The GitHub-ref lease the scripts once checked themselves ("_lease") is gone (V3.2): the SQL lock is the only fence.
    public static readonly IReadOnlySet<string> FencedVerbs = new HashSet<string>(StringComparer.Ordinal)
    { "sync-board", "merge", "settle", "resettle-item", "copilot-settle", "base-update-branch", "base-watch", "qc-sync", "ci-release" };
    int fencedInFlight;
    public int FencedInFlight => Volatile.Read(ref fencedInFlight);

    /// <summary>The rule script's stdin without a "_lease": one in the input came from a caller, never from the gateway, and it is taken out, as "_cause" is, so no client can fence or unfence a script.</summary>
    public static string StripLease(string stdin)
    {
        if (!stdin.Contains("\"_lease\"", StringComparison.Ordinal)) return stdin;
        try { if (JsonNode.Parse(stdin) is JsonObject o && o.Remove("_lease")) return o.ToJsonString(); } catch (JsonException) { }
        return stdin;
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
            var grace = Stopwatch.StartNew();
            while (FencedInFlight > 0 && grace.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        }
        return ((int)sw.ElapsedMilliseconds, killed);
    }
    // the shared GitHub budget (GitHubBudget.cs, issue 92): every answer's _github field is recorded there and taken out of the answer
    public GitHubBudget? Budget { get; set; }
    // W9.9a: where the engine acts, the verbs that have a v2 route are answered there, whoever asks: a session (the verb endpoint reaches the same routes first) or the gateway itself (the plant's hooks, the
    // sweep, the endpoints, the runtimes, QcHub, BaseWatch, the journal lines of the ticker). Set once when the table is built (V2VerbRoutes.Build); null where the engine does not act (uat, prod), where
    // RunScriptAsync refuses every verb but the housekeeping (InertVerbs). A verb with no route is the script's in both cases, and rule.ps1 answers only the housekeeping (V3.3).
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

    /// <summary>Whether this process may act now (the SQL lock: <c>GatewayLeader.MayAct</c>); null (the default) is always. A fenced verb asks it before it starts, on the route and on the script path.</summary>
    public Func<bool>? MayAct { get; set; }

    /// <summary>V3.3: the engine options while the gateway is inert (EngineOptions.Inert), null where it acts; set by the composition. An inert gateway runs no rule script (<see cref="InertVerbs"/>).</summary>
    public Func<EngineOptions?>? Inert { get; set; }

    /// <summary>The rule script, whatever v2 answers: the gateway's housekeeping (InertVerbs.Housekeeping), and where the engine acts the read-only load of a transcript from the ref store (DatabaseTranscriptBackend, until 2026-11-13). No other route falls back to it (V3.3). A caller of the script must call this and never <see cref="RunAsync"/>, which would ask the route again.</summary>
    public async Task<(int Code, string Output)> RunScriptAsync(string verb, string stdin, TimeSpan timeout)
    {
        // V3.3: an inert gateway (the engine does not act in its environment) runs its own housekeeping only (InertVerbs.Housekeeping), whoever asks
        if (Inert?.Invoke() is { } inert && !InertVerbs.Housekeeping.Contains(verb)) { var refused = InertVerbs.Refusal(verb, inert); return (refused.Code, refused.Output); }
        // a call of the gateway itself that can wait (a journal line, the back-merge check, the metrics) is not started while the budget is low
        if (Budget is { } b && GitHubBudget.DeferredCalls.Contains(verb) && b.Low() is { Low: true } low)
        {
            b.CountCallRefusal();
            return (20, JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({low.Resource}) is below its reserve; this call waits for the reset", retryAfterSeconds = Math.Max(1, (int)(low.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = low.ResetAt.ToString("o"), exit = 20 }));
        }
        var fenced = FencedVerbs.Contains(verb);
        // (counted first, then the check: a drain that ran between the two finds the call and waits for it. The check is made again where the call really starts - after the wait for a runner slot, once the process or
        // the process is registered where DrainFencedAsync finds it - because a call can wait for a slot longer than the drain does)
        if (fenced) Interlocked.Increment(ref fencedInFlight);
        if (fenced && MayAct?.Invoke() == false)
        {
            Interlocked.Decrement(ref fencedInFlight);
            var lost = (20, JsonSerializer.Serialize(new { ok = false, verb, code = "lease-lost", reason = $"this gateway no longer holds the leader lock; the step was not performed: {verb}", exit = 20 }));
            NoteFenced(verb, lost.Item1, lost.Item2);
            return lost;
        }
        int code; string output;
        try { (code, output) = await RunProcessAsync(verb, StripLease(stdin), timeout, fenced); }
        finally { if (fenced) Interlocked.Decrement(ref fencedInFlight); }
        if (fenced) NoteFenced(verb, code, output);
        return (code, Budget?.Take(output) ?? output);
    }

    // The gateway's own line when a fenced script refused a step or stopped its pass because the lease was lost (the script wrote its reason to stderr, which is
    // logged as "[verb] ..."; this line is the one to search for: gateway = fenced)
    void NoteFenced(string verb, int code, string output)
    {
        if (!output.Contains("\"lease-lost\"", StringComparison.Ordinal) && !output.Contains("\"fenced\":{", StringComparison.Ordinal)) return;
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "fenced", ["verb"] = verb, ["exit"] = code }.ToJsonString());
    }

    bool MayStart(bool fenced) => !fenced || MayAct?.Invoke() != false;

    (int, string) LeaseLost(string verb) => (20, JsonSerializer.Serialize(new { ok = false, verb, code = "lease-lost", reason = $"this gateway no longer holds the leader lock; the step was not performed: {verb}", exit = 20 }));

    async Task<(int Code, string Output)> RunProcessAsync(string verb, string stdin, TimeSpan timeout, bool fenced = false)
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
        if (!MayStart(fenced)) return LeaseLost(verb);
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", entry, "--server", verb }) psi.ArgumentList.Add(a);
        RunnerEnv.Apply(psi);
        using var p = Process.Start(psi)!;
        var key = Interlocked.Increment(ref liveKey); live[key] = p; liveVerb[key] = verb;
        try
        {
            // registered where the drain finds it: if the lock went before that, the drain did not see this process, so it ends here and nothing of it has run
            if (!MayStart(fenced)) { try { p.Kill(true); } catch { } return LeaseLost(verb); }
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

    // /status: the gate (limit, running and waiting by class, peaks, the last starts), and the processes and memory now
    public JsonObject Status()
    {
        var o = limiter.Status();
        o["memory"] = RunnerMemory.Read(live.Values.ToList());
        return o;
    }
}
