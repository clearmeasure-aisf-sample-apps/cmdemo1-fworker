using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class Runner(string entry)
{
    // runner.pool > 0: long-lived pwsh workers take the calls (RunnerPool.cs); null (the default): one process per call, as below.
    readonly RunnerPool? pool = RunnerPool.Create(entry);
    // at most runner.maxProcesses calls at once, pooled and spawned together, started by priority (RunnerGate.cs, issue 89)
    readonly RunnerGate limiter = RunnerGate.Create(entry);
    // the processes of calls that run now (not the pool's workers), for the process count and memory of /status
    readonly ConcurrentDictionary<long, Process> live = new();
    long liveKey;
    // the shared GitHub budget (GitHubBudget.cs, issue 92): every answer's _github field is recorded there and taken out of the answer
    public GitHubBudget? Budget { get; set; }
    public async Task<(int Code, string Output)> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        // a call of the gateway itself that can wait (a journal line, the back-merge check, the metrics) is not started while the budget is low
        if (Budget is { } b && GitHubBudget.DeferredCalls.Contains(verb) && b.Low() is { Low: true } low)
        {
            b.CountCallRefusal();
            return (20, JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({low.Resource}) is below its reserve; this call waits for the reset", retryAfterSeconds = Math.Max(1, (int)(low.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = low.ResetAt.ToString("o"), exit = 20 }));
        }
        var (code, output) = await RunProcessAsync(verb, stdin, timeout);
        return (code, Budget?.Take(output) ?? output);
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
        var key = Interlocked.Increment(ref liveKey); live[key] = p;
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
        finally { live.TryRemove(key, out _); }
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
