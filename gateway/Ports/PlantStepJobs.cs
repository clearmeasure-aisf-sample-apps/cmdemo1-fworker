using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Workflows.Reconcilers;

// W9.10g (docs/design/fworker-wolverine-implementation.md, row W9.10g): the plant-wide steps of v1's sweep as recurring jobs of the engine, leader only. Where the engine acts the sweep (Sweeper.cs) leaves these steps to the jobs
// (sync-board skip_steps) and the jobs run each of them on the leader at the sweep's own interval:
//   claim-ref-reap  the claim refs with no live claim behind them (issue 47);            PlantSteps.ClaimRefReap
//   board-guard     open labelled issues without a card are put on the board;           PlantSteps.BoardGuard
//   qc-sync         the PR QC environments' offers and teardowns, through QcService:    the verb qc-sync (v2's own answer), whose records QcHub.Reconcile takes
//   replenish       the keeper's replenishment of the backlog;                          PlantSteps.Replenish
//   backlog-low     the count of the keeper's column, and the keeper's backlog-low job;  PlantSteps.DesignWaiting, Plant.KeeperBacklog
// The first pull request of the row made the jobs a call of the rule script (sync-board plant_steps); the second one is the C# port (src/Fworker.Workflows/Reconcilers/PlantSteps.cs), so no job runs a script any more. The script keeps
// the same steps for the gateway that does not act (tdd while it only shadows) until W9.11b.
// The gap renewal (issue 94) of the saga's items is the job gap-renew of SweepLeftoverJobs (V1.4), which waits for the adoption of the term; the sweep keeps the gap for the items that have no saga.
// A job does nothing while this process may not act, before the plant is restored, while the saga table has not been read once (the steps leave the saga's items alone), or while the GitHub budget is below its reserve (the steps are
// the ones the sweep skipped on a low budget). A pass the lease ended is dropped: the steps stop before their next write and what they read is not applied.

/// <summary>The plant-wide steps of the sweep as jobs. One instance for the gateway; each method is one job and answers how the run ended.</summary>
sealed class PlantStepJobs(Func<IPlantSteps?> steps, Func<Plant?> plant, Func<QcHub?> qc, GitHubBudget budget, V2Ownership? ownership, Func<bool> mayAct, Func<bool> restored, bool ownerMode, TimeSpan? readyWait = null, TimeSpan? passTimeout = null, Func<IReadOnlyCollection<int>>? excluded = null)
{
    /// <summary>The steps the sweep leaves to the jobs (sync-board skip_steps). The keeper's count is not a step of an ordinary pass: the gateway simply does not apply it.</summary>
    public static readonly string[] SweepSkips = ["claim-ref-reap", "board-guard", "qc-sync", "replenish"];

    readonly TimeSpan passTimeout = passTimeout ?? TimeSpan.FromSeconds(280);

    // The C# step cannot be killed on the timeout the way the script's pwsh was (Runner.RunProcessAsync): WaitAsync only stops this job waiting. So the Task of each job is kept, and a run that finds the previous one of its job
    // still going answers "busy" and starts nothing: two copies of a step (replenish reads the item and saves it without the item lock, as the script did) would create the same issue twice.
    readonly Dictionary<string, Task> inflight = new(StringComparer.Ordinal);

    /// <summary>The runs by outcome, for the status block and the tests.</summary>
    public Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);

    void Note(string job, string outcome) { lock (Outcomes) Outcomes[$"{job}:{outcome}"] = Outcomes.GetValueOrDefault($"{job}:{outcome}") + 1; }

    // The first run of a job comes a few seconds after the leader may act, often before the plant is restored or the saga's items are read once. The sweep waited for both and then ran at once; a job that gave up would lose a whole
    // interval (180 s, an hour on a gateway that sweeps rarely) and the keeper would not hear the backlog before it. So a job waits for them, at most this long, and then does nothing as before.
    readonly TimeSpan readyWait = readyWait ?? TimeSpan.FromSeconds(30);

    async Task<string?> NotNowAsync(bool needsBudget, CancellationToken ct)
    {
        var until = DateTime.UtcNow + readyWait;
        while (true)
        {
            var why = NotNow(needsBudget);
            if (why is not ("waiting-for-restore" or "waiting-for-ownership") || DateTime.UtcNow >= until) return why;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return why; }
        }
    }

    /// <summary>Why a job does not run now, or null: not the leader, not restored, the saga's items not known yet, or the budget low.</summary>
    string? NotNow(bool needsBudget = true)
    {
        if (!mayAct()) return "not-leader";
        if (!restored()) return "waiting-for-restore";
        if (ownership is { Loaded: false }) return "waiting-for-ownership";
        if (needsBudget && budget.Low().Low) return "budget-low";
        return null;
    }

    // (the sweep skipped the claim-ref reap, the board guard and the QC sync on a low budget; the replenishment and the keeper's count it ran whatever the budget)
    public Task<string> ClaimRefReap(CancellationToken ct) => Pass("claim-ref-reap", ct, budgetGated: true, run: (s, skip, _) => s.ClaimRefReap(skip));
    public Task<string> BoardGuard(CancellationToken ct) => Pass("board-guard", ct, budgetGated: true, run: (s, _, _) => s.BoardGuard());

    public Task<string> Replenish(CancellationToken ct) => Pass("replenish", ct, budgetGated: false,
        // the owner mode's plant hands the free sessions and the last plan's waiting count to the backpressure; without it the replenishment holds back or not on its own (no plan counted: 0 free, nothing known)
        run: (s, _, p) => s.Replenish(ownerMode && p is not null ? p.LastPlanWaiting() : null, ownerMode && p is not null ? p.FreeOwnerSessions() : 0),
        apply: (j, p) =>
        {
            if (!ownerMode || p is null || j["replenish"] is not JsonObject rp) return;
            p.SetReplenish(rp);
            p.NoteGenerated((rp["created"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var g) ? g : 0));
        });

    public Task<string> BacklogLow(CancellationToken ct) => Pass("backlog-low", ct, budgetGated: false, run: (s, _, _) => s.DesignWaiting(), apply: (j, p) =>
    {
        // (the keeper's count is the plant's whether or not owner sessions are on, as the sweep applied it: the keeper's job needs no owner)
        if (p is not null && j["design_waiting"] is JsonValue dw && dw.TryGetValue<int>(out var waiting)) p.KeeperBacklog(waiting);
    });

    public async Task<string> QcSync(CancellationToken ct)
    {
        if (await NotNowAsync(true, ct) is { } why) { Note("qc-sync", why); return why; }
        if (qc() is not { } hub) { Note("qc-sync", "no-qc"); return "no-qc"; }
        var outcome = await hub.Sync(mayAct, ct);
        Note("qc-sync", outcome);
        return outcome;
    }

    async Task<string> Pass(string job, CancellationToken ct, bool budgetGated, Func<IPlantSteps, IReadOnlySet<int>, Plant?, Task<PlantStepResult>> run, Action<JsonNode, Plant?>? apply = null)
    {
        lock (inflight) if (inflight.TryGetValue(job, out var previous) && !previous.IsCompleted) { Note(job, "busy"); return "busy"; }
        if (await NotNowAsync(budgetGated, ct) is { } why) { Note(job, why); return why; }
        if (steps() is not { } plantSteps) { Note(job, "no-github"); return "no-github"; }
        // the saga's items are not v1's: the reap leaves their refs alone
        if (ownership is not null) { try { await ownership.RefreshAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { } }
        var skip = ownership is null ? new HashSet<int>() : ownership.Items.ToHashSet();
        if (excluded?.Invoke() is { } left) skip.UnionWith(left);   // (cutover review: after a break the claim refs of an item the adoption could not fence are not reaped either)
        var clock = Stopwatch.StartNew();
        PlantStepResult result;
        Task<PlantStepResult> started;
        lock (inflight)
        {
            if (inflight.TryGetValue(job, out var previous) && !previous.IsCompleted) { Note(job, "busy"); return "busy"; }   // (a second run that passed the first look while this one waited for the restore)
            var p = plant();
            started = Task.Run(() => run(plantSteps, skip, p));
            inflight[job] = started;
        }
        _ = started.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);   // (a step that ends after its timeout is not unobserved)
        try { result = await started.WaitAsync(passTimeout, ct); }
        catch (PlantStepFenced) { Note(job, "fenced"); Console.WriteLine(Line(job, "fenced")); return "fenced"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Note(job, "failed"); Console.WriteLine(Line(job, "failed", ("error", e.GetType().Name), ("out", e.Message.Length > 300 ? e.Message[..300] : e.Message))); return "failed"; }
        // fencing as the sweep does it: this process lost the lease while the step ran; what the pass read is dropped
        if (!mayAct()) { Note(job, "fenced"); Console.WriteLine(Line(job, "fenced")); return "fenced"; }
        apply?.Invoke(result.ToJson(), plant());
        Console.WriteLine(Line(job, "ok", ("actions", result.Actions.Count), ("errors", result.Errors.Count), ("elapsedMs", clock.ElapsedMilliseconds)));
        foreach (var a in result.Actions) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepAction\":{a!.ToJsonString()}}}");
        foreach (var e in result.Errors) Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"sweepError\":{e!.ToJsonString()}}}");
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
