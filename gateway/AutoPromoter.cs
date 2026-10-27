using System.Text.Json;
using System.Text.Json.Nodes;

// The automatic promotion of a release to uat and prod (rule script verb auto-promote, plugin/scripts/lib/Verbs.Promote.ps1, docs/design/auto-promote.md)
// as a loop of its own, apart from the board sweep (Sweeper.cs). Before, the promotion was the last step of every full sweep and a slow one (a release
// queue of many items, an old release that never passes) held the sweep back, up to the gateway's cap of 280 s for it. Now the sweep never waits for it
// and it never waits for the sweep: each has its own call, its own timeout and its own turn. Only the acting gateway promotes (the leader lease,
// Leader.cs), and only where delivery.autoPromote names this environment: elsewhere the loop does not run at all.
sealed class AutoPromoter(Runner runner, TimeSpan interval, bool enabled) : BackgroundService
{
    // the gateway's cap for one pass; the rule script ends its own work earlier (budget_seconds, 200), so this is the backstop, not the plan
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(240);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    public Func<bool>? MayAct { get; init; }
    public TimeSpan Interval { get; } = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!enabled) return;
        // after the sweep's first pass has had its start: the promotion reads the board and the state comments the sweep keeps current
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Interval.TotalSeconds)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (MayAct?.Invoke() != false) await PassOnce();
            }
            // only the host's own stop ends the loop: a failure of one pass (a timeout, a refusal) is that pass's, the next one tries again
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"promote\":\"error\",\"type\":{JsonSerializer.Serialize(e.GetType().Name)},\"message\":{JsonSerializer.Serialize(e.Message)}}}"); }
            catch (Exception) { return; }
            try { await Task.Delay(MayAct?.Invoke() == false ? TimeSpan.FromSeconds(1) : Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // One pass: the verb, and its actions and errors in the gateway log, as the sweep's were (sweepAction, sweepError)
    async Task PassOnce()
    {
        var (code, output) = await runner.RunAsync("auto-promote", new JsonObject().ToJsonString(), CallTimeout);
        if (code != 0) { Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"promote\":\"failed\",\"exit\":{code},\"out\":{JsonSerializer.Serialize(output.Length > 300 ? output[..300] : output)}}}"); return; }
        var j = JsonNode.Parse(output)!;
        if (j["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && !on) return;
        var acts = j["actions"] as JsonArray ?? new JsonArray();
        var errs = j["errors"] as JsonArray ?? new JsonArray();
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"promote\":{{\"actions\":{acts.Count},\"errors\":{errs.Count},\"elapsedMs\":{(j["elapsed_ms"]?.ToString() ?? "null")}}}}}");
        foreach (var a in acts) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepAction\":{a!.ToJsonString()}}}");
        foreach (var er in errs) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepError\":{er!.ToJsonString()}}}");
    }
}
