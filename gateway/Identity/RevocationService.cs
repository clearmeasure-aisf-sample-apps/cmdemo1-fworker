using System.Text.Json;
using System.Text.Json.Nodes;

// The revoked credentials of this gateway (Auth.cs, gateway-composition.md 3 E): the read of the generations at start, its state for /ready, the
// middleware and /api/v1/status, and the revocation itself. Moved out of Program.cs unchanged (C4a): the same rule scripts, waits and answers.
interface IRevocationService
{
    // completes once the generations are read, or at once where there is nothing to read; never fails
    Task Loaded { get; }
    bool IsLoaded { get; }
    // failed or timed-out reads so far, and when the first read started (read by /ready)
    int FailedAttempts { get; }
    DateTime Since { get; }
    // FACTORY_TEST_SKIP_REVOCATIONS: nothing is read, and revoke is refused
    bool Off { get; }
    // why nothing is read on this gateway ("" when it reads): owner-sessions-off, github-not-configured
    string SkipReason { get; }
    // the "revocations" block of /api/v1/status (owner mode)
    JsonObject Status();
    // the new generation is written to GitHub, then raised here; the name's open stream and pending nonces end with it
    Task<(bool Ok, int Generation, bool Closed)> RevokeAsync(string name, string by);
}

// Revocations: the generations are read from GitHub once at start. An attempt has a time limit; a failed or timed-out attempt is tried again after a
// growing pause (5 s doubling to 60 s), for as long as it takes. Until the read succeeds no worker, supervisor or item credential is accepted (never
// fail open: a restart must not let a revoked credential back in): a request waits up to 20 s for the read and is then answered 503. The operator
// token does not wait. A start that cannot read is never silent: /ready says what it waits for, and once the attempts have failed for the alarm time
// one error line names the read. /health stays 200 throughout.
// A hosted service only to start the read with the host: the read is never gated by leadership (the leader's start waits for it) and, as before,
// is not stopped with the host.
sealed class RevocationService : IRevocationService, IHostedService
{
    readonly Revocations generations;
    readonly Func<string, string, TimeSpan, Task<(int Code, string Output)>> run;
    readonly RuntimeNonces nonces;
    readonly Func<string, bool> closeStream;
    readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly SemaphoreSlim revokeGate = new(1, 1);
    readonly TimeSpan attemptLimit, alarmAfter;
    readonly bool ownerMode;
    readonly DateTime since = DateTime.UtcNow;
    int attempts;   // failed or timed-out attempts so far (read by /ready)
    int started;

    // the first pause after a failed read (5 s, doubling to 60 s); a test shortens it
    internal double FirstPauseSeconds { get; init; } = 5.0;

    // run: the rule-script call (Runner.RunAsync); closeStream: the plant ends the name's open stream (Plant.Revoke); sessionsIssued: owner sessions,
    // messaging or the operator is on in the configuration (a session credential can be issued)
    public RevocationService(RevocationOptions options, bool ownerMode, string tokenSecret, bool testHooks, bool sessionsIssued, Revocations generations,
        Func<string, string, TimeSpan, Task<(int Code, string Output)>> run, RuntimeNonces nonces, Func<string, bool> closeStream)
    {
        this.generations = generations; this.run = run; this.nonces = nonces; this.closeStream = closeStream; this.ownerMode = ownerMode;
        // FACTORY_TEST_SKIP_REVOCATIONS=1 (the scenario suites' second gateways, whose GitHub is a port nothing listens on): nothing is read, and revoke is refused.
        Off = options.SkipRevocations;
        if (Off && !testHooks) throw new InvalidOperationException("FACTORY_TEST_SKIP_REVOCATIONS is for test gateways only: it needs messaging.testHooks in the configuration");
        if (Off && ownerMode) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revocations\":\"off\",\"warning\":\"FACTORY_TEST_SKIP_REVOCATIONS is set: revoked credentials are not read and cannot be revoked\"}}");
        // FACTORY_REVOCATIONS_ATTEMPT_SECONDS (45): the time limit of one read. FACTORY_REVOCATIONS_ALARM_SECONDS (180): failing for this long logs the error line.
        attemptLimit = TimeSpan.FromSeconds(options.AttemptSeconds);
        alarmAfter = TimeSpan.FromSeconds(options.AlarmSeconds);
        // Nothing to read: owner sessions, messaging, the supervisor and the operator are all off in the configuration (no session credential is issued), or the gateway
        // has no GitHub identity (no App key, or no GH_TOKEN; the vault placeholder 'unset' counts as none). Such a gateway becomes ready at once instead of
        // retrying a read that can never succeed; /api/v1/status names the reason under "revocations", and revoke is refused. A configured identity whose
        // read fails (a wrong key, a GitHub error) still keeps the gateway un-ready: that is never failed open.
        SkipReason = SkipReasonOf(ownerMode, sessionsIssued, options.GitHubConfigured);
        if (SkipReason != "") Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revocations\":\"skipped\",\"reason\":\"{SkipReason}\",\"warning\":\"revoked credentials are not read and cannot be revoked\"}}");
        if (!ownerMode || tokenSecret.Length == 0 || Off || SkipReason != "") loaded.SetResult();
    }

    public static string SkipReasonOf(bool ownerMode, bool sessionsIssued, bool gitHubConfigured) =>
        !ownerMode ? "" : !sessionsIssued ? "owner-sessions-off" : !gitHubConfigured ? "github-not-configured" : "";

    public Task Loaded => loaded.Task;
    public bool IsLoaded => loaded.Task.IsCompleted;
    public int FailedAttempts => Volatile.Read(ref attempts);
    public DateTime Since => since;
    public bool Off { get; }
    public string SkipReason { get; }

    public JsonObject Status() => SkipReason != "" ? new JsonObject { ["state"] = "skipped", ["reason"] = SkipReason }
        : Off ? new JsonObject { ["state"] = "off", ["reason"] = "test-switch" }
        : new JsonObject { ["state"] = IsLoaded ? "loaded" : "loading", ["failedAttempts"] = FailedAttempts };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!loaded.Task.IsCompleted && Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(ReadLoop);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task ReadLoop()
    {
        var alarmed = false; var pause = FirstPauseSeconds;
        while (true)
        {
            var why = "";
            try
            {
                // a read that timed out gets longer the next time (the limit, then twice, then at most 120 s): a slow GitHub must not keep every attempt just short
                var limit = TimeSpan.FromSeconds(Math.Min(120, attemptLimit.TotalSeconds * Math.Min(3, 1 + Volatile.Read(ref attempts))));
                var (code, output) = await run("revocations", "{}", limit);
                if (code == 0 && JsonNode.Parse(output)?["revoked"] is JsonObject list)
                {
                    foreach (var kv in list) if (Auth.ValidName(kv.Key) && kv.Value is JsonValue gv && gv.TryGetValue<int>(out var g) && g > 0) generations.Raise(kv.Key, g);
                    if (Volatile.Read(ref attempts) > 0) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revocations\":\"loaded\",\"afterFailedAttempts\":{Volatile.Read(ref attempts)},\"since\":\"{since:o}\"}}");
                    loaded.SetResult(); return;
                }
                string? rc = null; try { rc = JsonNode.Parse(output)?["code"]?.ToString(); } catch { }
                why = rc == "timeout" ? $"no answer within {attemptLimit.TotalSeconds:0} s" : $"exit {code}";
                Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revocations\":\"not-loaded\",\"exit\":{code},\"attempt\":{Volatile.Read(ref attempts) + 1}}}");
            }
            catch (Exception e) { why = e.GetType().Name; Console.Error.WriteLine($"revocations: {e.Message}"); }
            var n = Interlocked.Increment(ref attempts);
            if (!alarmed && DateTime.UtcNow - since >= alarmAfter)
            {
                alarmed = true;   // once: the line an operator searches for
                Console.WriteLine(JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("o"), level = "error", gateway = "not-ready", waitingFor = "revocations", attempts = n, since = since.ToString("o"), last = why,
                    reason = "the read of the revoked credentials at start keeps failing or timing out (rule script 'revocations': GET git/matching-refs of the work-orders repository); /ready stays 503 and no session credential is accepted until it succeeds; the gateway keeps retrying" }));
            }
            await Task.Delay(TimeSpan.FromSeconds(pause)); pause = Math.Min(60, pause * 2);
        }
    }

    public async Task<(bool Ok, int Generation, bool Closed)> RevokeAsync(string name, string by)
    {
        if (Off || SkipReason != "" || !loaded.Task.IsCompleted) return (false, 0, false);
        await revokeGate.WaitAsync();
        try
        {
            var next = generations.Of(name) + 1;
            var (code, _) = await run("revoke-worker", new JsonObject { ["name"] = name, ["generation"] = next }.ToJsonString(), TimeSpan.FromSeconds(120));
            if (code != 0) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revoke\":\"{name}\",\"failed\":{code}}}"); return (false, 0, false); }
            generations.Raise(name, next);
            nonces.Forget(name);   // a nonce of the name not yet redeemed is spent with it
            var closed = closeStream(name);
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"revoke\":\"{name}\",\"generation\":{next},\"streamClosed\":{(closed ? "true" : "false")},\"by\":\"{by}\"}}");
            return (true, next, closed);
        }
        finally { revokeGate.Release(); }
    }
}
