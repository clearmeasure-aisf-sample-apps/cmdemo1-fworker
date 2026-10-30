using System.Text.Json;
using System.Text.Json.Nodes;

// W9.10g (docs/design/fworker-wolverine-implementation.md, row W9.10g): the plant-wide steps of v1's sweep as recurring jobs of the engine, leader only. Where the engine acts the sweep (Sweeper.cs) leaves these steps to the jobs
// (sync-board skip_steps) and the jobs run each of them on the leader at the sweep's own interval:
//   claim-ref-reap  the claim refs with no live claim behind them (issue 47);            sync-board plant_steps ["claim-ref-reap"]
//   gap-renew       the owners' claims that ran out while no gateway was up (issue 94);  sync-board plant_steps ["gap-renew"] with the gap the restore noted (once per start)
//   board-guard     open labelled issues without a card are put on the board;           sync-board plant_steps ["board-guard"]
//   qc-sync         the PR QC environments' offers and teardowns, through QcService:    the verb qc-sync (v2's own answer), whose records QcHub.Reconcile takes
//   replenish       the keeper's replenishment of the backlog (Replenish.ps1);           sync-board plant_steps ["replenish"]
//   backlog-low     the count of the keeper's column, and the keeper's backlog-low job;  sync-board plant_steps ["design-waiting"], Plant.KeeperBacklog
// A plant pass reads the board list once and runs only the steps it names: no item loop, no intake, no seen state (Verbs.Sweep.ps1). The steps are still the script's rules until W9.11, which is why each job is a call of
// sync-board that the inventory lists (docs/design/verb-inventory.json "plant-steps"); the QC sync is not: it is the verb v2 answers itself. A job does nothing while this process may not act, before the plant is restored,
// while the saga table has not been read once (the steps leave the saga's items alone), or while the GitHub budget is below its reserve (the steps are the ones the sweep skipped on a low budget). A pass the lease ended is dropped.

/// <summary>The plant-wide steps of the sweep as jobs. One instance for the gateway; each method is one job and answers how the run ended.</summary>
sealed class PlantStepJobs(Runner runner, Func<Plant?> plant, Func<QcHub?> qc, GitHubBudget budget, SweepGap gap, V2Ownership? ownership, Func<bool> mayAct, Func<bool> restored, bool ownerMode)
{
    /// <summary>The steps the sweep leaves to the jobs (sync-board skip_steps). The gap and the keeper's count are not steps of an ordinary pass: the gateway simply does not hand them to it.</summary>
    public static readonly string[] SweepSkips = ["claim-ref-reap", "board-guard", "qc-sync", "replenish"];

    static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(280);

    /// <summary>The runs by outcome, for the status block and the tests.</summary>
    public Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);

    void Note(string job, string outcome) { lock (Outcomes) Outcomes[$"{job}:{outcome}"] = Outcomes.GetValueOrDefault($"{job}:{outcome}") + 1; }

    /// <summary>Why a job does not run now, or null: not the leader, not restored, the saga's items not known yet, or the budget low.</summary>
    string? NotNow(bool needsBudget = true)
    {
        if (!mayAct()) return "not-leader";
        if (!restored()) return "waiting-for-restore";
        if (ownership is { Loaded: false }) return "waiting-for-ownership";
        if (needsBudget && budget.Low().Low) return "budget-low";
        return null;
    }

    // (the sweep skipped the claim-ref reap, the board guard and the QC sync on a low budget; the gap renewal, the replenishment and the keeper's count it ran whatever the budget)
    public Task<string> ClaimRefReap(CancellationToken ct) => Pass("claim-ref-reap", ["claim-ref-reap"], ct, budgetGated: true);
    public Task<string> BoardGuard(CancellationToken ct) => Pass("board-guard", ["board-guard"], ct, budgetGated: true);

    public async Task<string> GapRenew(CancellationToken ct)
    {
        if (NotNow(false) is { } why) { Note("gap-renew", why); return why; }
        if (gap.Take() is not { } since) { Note("gap-renew", "no-gap"); return "no-gap"; }
        var outcome = await Pass("gap-renew", ["gap-renew"], ct, new JsonObject { ["gap"] = new JsonObject { ["since"] = since.ToString("o"), ["until"] = DateTime.UtcNow.ToString("o") } }, budgetGated: false, onFailure: () => gap.Set(since));
        return outcome;
    }

    public async Task<string> Replenish(CancellationToken ct)
    {
        var extra = new JsonObject();
        if (ownerMode && plant() is { } p)
        {
            extra["free_workers"] = p.FreeOwnerSessions();
            if (p.LastPlanWaiting() is { } waiting) extra["replenish_waiting"] = waiting;
        }
        return await Pass("replenish", ["replenish"], ct, extra, budgetGated: false, apply: (j, p) =>
        {
            if (!ownerMode || p is null || j["replenish"] is not JsonObject rp) return;
            p.SetReplenish(rp);
            p.NoteGenerated((rp["created"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var g) ? g : 0));
        });
    }

    public Task<string> BacklogLow(CancellationToken ct) => Pass("backlog-low", ["design-waiting"], ct, budgetGated: false, apply: (j, p) =>
    {
        if (ownerMode && p is not null && j["design_waiting"] is JsonValue dw && dw.TryGetValue<int>(out var waiting)) p.KeeperBacklog(waiting);
    });

    public async Task<string> QcSync(CancellationToken ct)
    {
        if (NotNow() is { } why) { Note("qc-sync", why); return why; }
        if (qc() is not { } hub) { Note("qc-sync", "no-qc"); return "no-qc"; }
        var outcome = await hub.Sync(mayAct, ct);
        Note("qc-sync", outcome);
        return outcome;
    }

    async Task<string> Pass(string job, string[] steps, CancellationToken ct, JsonObject? extra = null, bool budgetGated = false, Action<JsonNode, Plant?>? apply = null, Action? onFailure = null)
    {
        // (gap-renew has asked already: its gap is taken before the pass and given back when the pass failed)
        if (job != "gap-renew" && NotNow(budgetGated) is { } why) { Note(job, why); return why; }
        var args = new JsonObject { ["plant_steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()) };
        if (extra is not null) foreach (var kv in extra) args[kv.Key] = kv.Value?.DeepClone();
        if (ownership is not null) { try { await ownership.RefreshAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { } }
        Sweeper.AddV2Arguments(args, ownership, []);   // skip_items: the saga's items are not v1's
        int code; string output;
        try { (code, output) = await runner.RunAsync("sync-board", args.ToJsonString(), PassTimeout); }
        catch { onFailure?.Invoke(); throw; }
        if (code != 0) { onFailure?.Invoke(); Note(job, "failed"); Console.WriteLine(Line(job, "failed", ("exit", code), ("out", output.Length > 300 ? output[..300] : output))); return "failed"; }
        JsonNode? j;
        try { j = JsonNode.Parse(output); } catch (JsonException) { j = null; }
        if (j is not JsonObject answer) { onFailure?.Invoke(); Note(job, "failed"); return "failed"; }
        // fencing as the sweep does it: the script found the lease lost, or this process lost it while the script ran; what the pass read is dropped
        if (answer["fenced"] is JsonObject || !mayAct()) { onFailure?.Invoke(); Note(job, "fenced"); Console.WriteLine(Line(job, "fenced")); return "fenced"; }
        apply?.Invoke(answer, plant());
        var actions = answer["actions"] as JsonArray ?? new JsonArray(); var errors = answer["errors"] as JsonArray ?? new JsonArray();
        Console.WriteLine(Line(job, "ok", ("actions", actions.Count), ("errors", errors.Count), ("elapsedMs", answer["elapsed_ms"]?.ToString() ?? "")));
        foreach (var a in actions) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepAction\":{a!.ToJsonString()}}}");
        foreach (var e in errors) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepError\":{e!.ToJsonString()}}}");
        Note(job, "ok");
        return "ok";
    }

    static string Line(string job, string outcome, params (string Key, object Value)[] facts)
    {
        var o = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["plantStep"] = job, ["outcome"] = outcome };
        foreach (var (k, v) in facts) o[k] = JsonSerializer.SerializeToNode(v);
        return o.ToJsonString();
    }
}
