using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.WorkItem;

// V1.4 (docs/design/fworker-wolverine-implementation.md, row V1.4): the steps that only v1's sweep did, as recurring jobs of the engine, leader only. Where the engine acts the sweep no longer does them for the saga's items.
//   gap-renew            the owners' claims that ran out while no gateway was up (Invoke-GapRenew, issue 94): once per gap, after the restore and the adoption of the term, before the first offer of the plant;
//                        SagaGapRenew gives each saga item whose lease ended inside the gap a new lease (LeaseRenewed). The sweep keeps its own gap for the items that have no saga (SweepGap.Take);
//   base-watch-fallback  the base update after a missed webhook (BaseWatch.AfterSweep): master's head against the last one seen, at recurringJobs.baseWatchSeconds (5 minutes); the sweep no longer calls it;
//   release-queue-flag   a Release Queue card of an open item with no merge record (issue 117): a journal line once and the operator's plant event, ReleaseQueueFlags.
// A job does nothing while this process may not act, before the plant is restored and the adoption is over, or (the base update and the flag, which read GitHub) while the GitHub budget is low.

/// <summary>The three jobs. One instance for the gateway; each method is one job and answers how the run ended.</summary>
sealed class SweepLeftoverJobs(Func<bool> mayAct, Func<bool> ready, V2Ownership ownership, SweepGap gap, Func<int, string?> ownerOf, Action<string, int, string> operatorEvent, GitHubBudget budget, Func<LeaseRenewed, Task> send,
    Func<Task> baseWatchPass, Func<IReleaseQueueFlags?> flags, Func<IReadOnlyList<BoardCard>> cards, RepoId home, IReadOnlyCollection<string> holdColumns, TimeSpan? readyWait = null, Func<DateTime>? clock = null)
{
    readonly TimeSpan readyWait = readyWait ?? TimeSpan.FromSeconds(30);
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    readonly HashSet<int> flagged = [];

    /// <summary>The runs by outcome, for the tests.</summary>
    public Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);

    string Note(string job, string outcome) { lock (Outcomes) Outcomes[$"{job}:{outcome}"] = Outcomes.GetValueOrDefault($"{job}:{outcome}") + 1; return outcome; }

    // The first run comes a few seconds after the leader may act, often before the restore, the adoption or the first read of the saga's items: wait for them, a little, and then do nothing until the next interval.
    async Task<string?> NotNowAsync(bool needsBudget, CancellationToken ct)
    {
        var until = DateTime.UtcNow + readyWait;
        while (true)
        {
            var why = NotNow(needsBudget);
            if (why is not "waiting" || DateTime.UtcNow >= until) return why;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return why; }
        }
    }

    string? NotNow(bool needsBudget)
    {
        if (!mayAct()) return "not-leader";
        if (!ready()) return "waiting";
        if (needsBudget && budget.Low().Low) return "budget-low";
        return null;
    }

    // the saga's items as the table has them now (a read that fails leaves the pass for the next tick: an item must not be taken for one without a saga)
    async Task<bool> ReadStates(CancellationToken ct)
    {
        try { await ownership.RefreshAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { return false; }
        return ownership.Loaded;
    }

    /// <summary>The gap renewal. Closes the gap once its items are renewed; a pass the lease ended leaves it open for the next holder.</summary>
    public async Task<string> GapRenew(CancellationToken ct)
    {
        if (gap.PeekRenew() is not { } since) return Note("gap-renew", "no-gap");
        if (await NotNowAsync(false, ct) is { } why) return Note("gap-renew", why);
        if (!await ReadStates(ct)) return Note("gap-renew", "unread");
        var until = new DateTimeOffset(now(), TimeSpan.Zero);
        var renewed = await SagaGapRenew.Run(ownership.States, new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)), until, ownerOf, send);
        if (!mayAct()) return Note("gap-renew", "fenced");
        gap.RenewDone(since);
        foreach (var n in renewed) Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gapRenewed"] = n, ["worker"] = ownerOf(n), ["since"] = since.ToString("o") }.ToJsonString());
        return Note("gap-renew", renewed.Count > 0 ? "renewed" : "none");
    }

    /// <summary>The base update fallback: BaseWatch looks at master's head (it does nothing before its own interval, and in every mode a trigger is the webhook's first).</summary>
    public async Task<string> BaseWatchFallback(CancellationToken ct)
    {
        if (await NotNowAsync(true, ct) is { } why) return Note("base-watch-fallback", why);
        await baseWatchPass();
        return Note("base-watch-fallback", "ok");
    }

    /// <summary>The flag of a Release Queue card without a merge record.</summary>
    public async Task<string> ReleaseQueueFlag(CancellationToken ct)
    {
        if (await NotNowAsync(true, ct) is { } why) return Note("release-queue-flag", why);
        if (flags() is not { } step) return Note("release-queue-flag", "no-github");
        if (!await ReadStates(ct)) return Note("release-queue-flag", "unread");
        // (only the saga's items: the sweep still flags the others, and the operator would hear of one twice)
        var sagaItems = ownership.States.ToDictionary(s => s.ItemId.Number);
        var merged = sagaItems.Values.Where(s => s.MergeCommit.Length > 0 || s.Status == WorkItemStatus.Completed).Select(s => s.ItemId.Number).ToHashSet();
        var candidates = cards()
            .Where(c => c.Repository == home && c.Open && sagaItems.ContainsKey(c.Number) && c.Column is { Length: > 0 } col && holdColumns.Contains(col, StringComparer.OrdinalIgnoreCase) && !merged.Contains(c.Number))
            .Where(c => { lock (flagged) return !flagged.Contains(c.Number); })
            .Select(c => (c.Number, c.Column!)).ToList();
        if (candidates.Count == 0) return Note("release-queue-flag", "none");
        var lacking = await step.Flag(candidates);
        if (!mayAct()) return Note("release-queue-flag", "fenced");
        foreach (var c in candidates.Where(c => !lacking.Contains(c.Number))) lock (flagged) flagged.Add(c.Number);   // (a merge record in the state comment: nothing to flag, and not read again)
        foreach (var n in lacking)
        {
            bool first; lock (flagged) first = flagged.Add(n);
            if (first) operatorEvent(ReleaseQueueFlags.Code, n, candidates.First(c => c.Number == n).Item2);
        }
        return Note("release-queue-flag", lacking.Count > 0 ? "flagged" : "none");
    }
}
