using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// W9.9b (docs/design/fworker-wolverine-implementation.md "W9.9b as built"): the adoption wired. One Run reads every open item of the home repository from GitHub (the board, the issue, the state comment, the pull request), maps it with W9.10m's
// mapping and W9.10n's fencing, and applies the results: the ledger (the fence of an instance that ended, the adopted slot), the QC rows, the merged-items row, then the saga's row itself (a save of the revision that was read, so a
// writer that was first is read again) and last what the row asks for (the decision, the timers, the commands). Then, beside the items: the Sign-offs Octopus shows are the delivery trains' to answer, the trains' candidates that no merged-items
// row backs are removed after the merged items were backfilled from the merged pull requests, the merge line's entries that stand for no open request are removed, and DeliveredTo is backfilled from the saga's rows and the trains.
// Every step is idempotent: the rule of the upsert leaves a row the comment has not moved past, the ledger and the stores take what they hold already as the newer truth, and the order (rows beside the state first, the saga's row
// last) means a run that broke off finds the same result again. Only the acting holder of the lease runs it, after the plant is restored (AdoptionJob).

/// <summary>The saga's row kept changing under the adoption: another writer moved it more often than the run's tries.</summary>
public sealed class AdoptionConflictException(string item) : Exception($"the work item {item} kept changing under the adoption");

public sealed class AdoptionRunner(AdoptionOptions options, AdoptionPorts ports, Func<DateTimeOffset>? clock = null)
{
    const int Attempts = 4;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    volatile AdoptionReport? last;

    /// <summary>The report of the latest run (null before the first).</summary>
    public AdoptionReport? Last => last;

    /// <summary>
    /// One run. <paramref name="afterBreak"/> says that v1 may have acted since the engine last did (W9.10n); <paramref name="acting"/> says that this process still holds the lease (asked between items: a run whose lease is gone ends
    /// as "aborted" and leaves the rest to the next holder). A fault of one item is counted and the run goes on; the run is "partial" then and is tried again.
    /// </summary>
    public async Task<AdoptionReport> Run(bool afterBreak, string breakWhy, Func<bool> acting, CancellationToken ct)
    {
        var report = new AdoptionReport { StartedAt = now(), AfterBreak = afterBreak, BreakWhy = breakWhy };
        last = report;
        try
        {
            var trains = await LoadTrains(ct);
            var promotions = new Dictionary<string, IReadOnlyList<ItemPromotion>>(StringComparer.Ordinal);
            if (!await Items(report, trains, promotions, afterBreak, acting, ct)) { report.Outcome = "aborted"; return report; }
            var backfilled = false;
            var mergedOk = await Guarded(report, async () => backfilled = await BackfillMerged(report, trains, ct)) && backfilled;
            await Guarded(report, () => Trains(report, promotions, mergedOk, ct));
            await Guarded(report, () => MergeLine(report, ct));
            await Guarded(report, () => DeliveredTo(report, ct));
            report.Outcome = !acting() ? "aborted" : report.Errors == 0 ? "done" : "partial";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Outcome = "aborted"; throw; }
        catch (Exception e) { report.Error(e); report.Outcome = "partial"; }
        finally { report.FinishedAt = now(); }
        return report;
    }

    // a part of the run that fails is an error of the run, not the end of it
    static async Task<bool> Guarded(AdoptionReport report, Func<Task> part)
    {
        try { await part(); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { report.Error(e); return false; }
    }

    async Task<IReadOnlyList<TrainState>> LoadTrains(CancellationToken ct)
    {
        var trains = new List<TrainState>();
        foreach (var key in options.TrainKeys) if (await ports.Trains.Load(key, ct) is { } rec) trains.Add(rec.State);
        return trains;
    }

    // ---- the items --------------------------------------------------------------------------------------------------------------------------------------------------------

    async Task<bool> Items(AdoptionReport report, IReadOnlyList<TrainState> trains, Dictionary<string, IReadOnlyList<ItemPromotion>> promotions, bool afterBreak, Func<bool> acting, CancellationToken ct)
    {
        var columns = options.Mapping.Columns;
        var rows = (await ports.Items.OwnedStates(ct)).Where(s => ItemId.TryParse(s.Item, out var id) && id.Repository == options.Home).ToDictionary(s => s.Item, StringComparer.Ordinal);
        var cards = (await ports.Reads.Cards(ct)).Where(c => c.Repository == options.Home && c.Number > 0).ToList();
        var targets = new SortedDictionary<int, BoardCard?>();
        foreach (var card in cards.Where(c => c.Open))
        {
            report.Cards++;
            // a card in a column of no step (Todo, the intake's) has no state comment of v1's to adopt: it is counted, and not read
            if (columns.StepOf(card.Column) is not null || columns.IsHold(card.Column) || columns.IsDone(card.Column) || rows.ContainsKey(new ItemId(options.Home, card.Number).ToString())) targets[card.Number] = card;
            else Skip(report, $"column-of-no-step: {card.Column}");
        }
        // an open row of the saga whose card is closed, or gone from the board, is read too: v1 may have ended it (W9.10n closes it after a break)
        foreach (var row in rows.Values) if (ItemId.TryParse(row.Item, out var id) && !targets.ContainsKey(id.Number)) targets[id.Number] = cards.FirstOrDefault(c => c.Number == id.Number);
        foreach (var (number, card) in targets)
        {
            if (!acting()) return false;
            ct.ThrowIfCancellationRequested();
            try { await One(report, new ItemId(options.Home, number), card, trains, promotions, afterBreak, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.Error(e); }
        }
        return acting();
    }

    async Task One(AdoptionReport report, ItemId id, BoardCard? card, IReadOnlyList<TrainState> trains, Dictionary<string, IReadOnlyList<ItemPromotion>> promotions, bool afterBreak, CancellationToken ct)
    {
        var key = id.ToString();
        var read = await ports.Reads.ReadItem(id, ct);
        report.Read++;
        var state = read?.State;
        if (state is not null) promotions[key] = AdoptionTrain.Promotions(key, state);
        AdoptionPullRequest? pull = null;
        if (state is not null && PullRequestOf(state) is > 0 and var number) pull = await ports.Reads.ReadPullRequest(id.Repository, number, ct);
        var snapshot = new AdoptionSnapshot(id, read?.IssueOpen ?? false, card?.Column, read?.Labels ?? card?.Labels ?? [], read?.Body, read?.CreatedAt, state, pull, null, trains, read?.DemoVideoPublished ?? false);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await ports.Items.Load(key, ct);
            var (comment, cardSeq) = await ports.Projected.Of(key, ct);
            var ledger = (await ports.Capacity.Load(CapacityKeys.Factory, ct))?.State;
            var at = now();
            var mapping = AdoptionMapper.Map(snapshot with { HeldSlot = ledger?.Slots.GetValueOrDefault(key) }, options.Mapping, at, AdoptionFence.GenerationFor(rec?.State, afterBreak));
            var result = AdoptionReconciler.Reconcile(rec?.State, comment, cardSeq, mapping, at, afterBreak);
            if (await Apply(report, key, result, rec?.Revision ?? 0, ct)) { Tally(report, result, mapping); return; }
            report.Conflicts++;
        }
        throw new AdoptionConflictException(key);
    }

    // the item's pull request as the mapping takes it from the comment: pr.number, else the awaiting-checks record's, the verdict's, the merge's
    static int PullRequestOf(JsonObject state)
    {
        foreach (var (o, name) in new[] { (V1State.Obj(state, "pr"), "number"), (V1State.Obj(state, "awaiting_checks"), "pr"), (V1State.Obj(state, "verdict"), "pr"), (V1State.Obj(state, "merge"), "pr") })
            if (V1State.Int(o, name) is > 0 and var n) return n;
        return 0;
    }

    static void Skip(AdoptionReport report, string reason)
    {
        report.Skipped++;
        var code = AdoptionReport.Code(reason);
        report.SkippedBy[code] = report.SkippedBy.GetValueOrDefault(code) + 1;
    }

    static void Tally(AdoptionReport report, AdoptionResult r, AdoptionMapping mapping)
    {
        switch (r.Kind)
        {
            case AdoptionKind.Adopted: report.Adopted++; break;
            case AdoptionKind.Updated: report.Updated++; break;
            case AdoptionKind.Unchanged: report.Unchanged++; break;
            case AdoptionKind.Kept: report.Kept++; break;
            case AdoptionKind.Closed: report.Closed++; break;
            case AdoptionKind.Reopened: report.Reopened++; break;
            default: Skip(report, mapping.Skipped ? mapping.SkipReason! : "instance-completed"); break;
        }
    }

    // The rows beside the state first, the saga's row last: a run that broke off in between finds the same result again (the row is not saved, so the comment is still newer), and every one of the writes below is idempotent.
    async Task<bool> Apply(AdoptionReport report, string key, AdoptionResult r, int expectedRevision, CancellationToken ct)
    {
        await Ledger(r, ct);
        foreach (var q in r.QcRows) if (await SaveQc(q, ct)) report.QcRows++;
        if (r.MergedRow is { } merged) await Merged(report, merged, r, ct);
        if (!r.Save || r.State is null) return true;
        if (!await ports.Items.Save(key, r.State, expectedRevision, ct)) return false;
        // The decision of the adoption is never projected (the comment is where it came from): the projections' versions are brought to it, or the rule of the upsert would find "decisions the projection has not written" for ever
        // and keep the row against every later comment. Decisions the saga makes afterwards are numbered after it.
        if (r.Decision is { } decision) await ports.Projected.Advance(key, decision.Sequence, now(), ct);
        await Publish(report, r);
        return true;
    }

    async Task Ledger(AdoptionResult r, CancellationToken ct)
    {
        if (r.Fence is null && r.Slot is null && r.Flight is null) return;
        for (var attempt = 0; attempt < Attempts * 2; attempt++)
        {
            var rec = await ports.Capacity.Load(CapacityKeys.Factory, ct);
            var before = rec?.State ?? new CapacityState();
            var next = r.Fence is { } fence ? AdoptionLedger.Fence(before, fence) : before;
            next = AdoptionLedger.Apply(next, r.Slot, r.Flight);
            if (next.ToJson() == before.ToJson()) return;
            if (await ports.Capacity.Save(CapacityKeys.Factory, next, rec?.Revision ?? 0, ct)) return;
        }
        throw new AdoptionConflictException("the scheduler's ledger");
    }

    async Task<bool> SaveQc(AdoptionQcRow q, CancellationToken ct)
    {
        // (the store keeps the newer revision of a record; a row that holds the record already is not written again)
        if (await ports.Qc.Load(q.Key, ct) is { } held && held.State.ToJsonString() == q.Record.ToJsonString()) return false;
        await ports.Qc.Save(q.Key, q.Repository, q.ItemKey, q.Record, ct);
        return true;
    }

    async Task Merged(AdoptionReport report, MergedItem row, AdoptionResult r, CancellationToken ct)
    {
        // a row another message holds (the webhook's, the reconciler's) is theirs: the train heard of the merge from it
        var mine = await ports.Merged.TryRecordAll([row], ct);
        if (mine.Count == 0) return;
        report.MergedRows++;
        // the train makes the merge a candidate, unless the saga already knows the release that reached the train's environment
        if (r.State is { } state && state.Delivered.ContainsKey(options.TrainEnvironment)) return;
        if (!ItemId.TryParse(row.ItemKey, out var item)) return;
        if (await ports.Publish(ItemMergedOf(row, item, "adopted"))) report.MergedPublished++; else report.PublishFailed++;
    }

    static ItemMerged ItemMergedOf(MergedItem row, ItemId item, string source)
    {
        var gates = JsonSerializer.Deserialize<ItemMergedGates>(row.GatesJson) ?? new ItemMergedGates(false, "", false, false, false, []);
        var dependencies = new List<ItemId>();
        foreach (var d in JsonSerializer.Deserialize<List<string>>(row.DependenciesJson) ?? []) if (ItemId.TryParse(d, out var id)) dependencies.Add(id);
        return new ItemMerged(item, row.Commit, row.PullRequest, gates, dependencies, source);
    }

    // the decision (the history), the commands and the timers of the row; the host not taking one is counted, and the re-arm job repairs a timer or a command that never left (WorkItemRearm)
    async Task Publish(AdoptionReport report, AdoptionResult r)
    {
        async Task<bool> Try(Func<Task<bool>> send)
        {
            try { return await send(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return false; }
        }
        if (r.Decision is { } d && !await Try(() => ports.Publish(d))) report.PublishFailed++;
        foreach (var c in r.Commands) if (!await Try(() => ports.Publish(c))) report.PublishFailed++;
        foreach (var t in r.Timers) if (!await Try(() => ports.Schedule(t.Message, t.Due))) report.PublishFailed++;
    }

    // ---- the merged items from the merged pull requests ---------------------------------------------------------------------------------------------------------------

    // False when there is nothing to derive with (no GitHub credential): the merged-items table is then not the train's evidence and its candidates are not validated against it
    async Task<bool> BackfillMerged(AdoptionReport report, IReadOnlyList<TrainState> trains, CancellationToken ct)
    {
        if (ports.Deriver is not { } deriver) return false;
        var environment = options.TrainEnvironment.ToLowerInvariant();
        var running = trains.FirstOrDefault(t => string.Equals(t.Environment, environment, StringComparison.OrdinalIgnoreCase))?.RunningIn(environment)?.Commit;
        foreach (var pull in await ports.Reads.RecentMergedPulls(options.MergedWindow, running, ct))
        {
            if (string.IsNullOrEmpty(pull.MergeCommit)) continue;
            var ev = new JsonObject { ["event"] = "base.merged", ["pr"] = pull.Number, ["sha"] = pull.MergeCommit, ["repository"] = options.Home.ToString() }.ToJsonString();
            var derivation = await deriver.Derive("adoption", ev, MessageIdOf(options.Home, pull.Number, pull.MergeCommit), ct);
            foreach (var merged in derivation.Merged)
            {
                report.MergedFromPulls++;
                if (await ports.Publish(merged)) report.MergedPublished++; else report.PublishFailed++;
            }
        }
        return true;
    }

    /// <summary>The message id that claims the rows of one merged pull request: fixed by the pull request and its commit, so the second start derives the same rows once.</summary>
    public static Guid MessageIdOf(RepoId repository, int pullRequest, string commit) => new(MD5.HashData(Encoding.UTF8.GetBytes($"adopt-pr|{repository}|{pullRequest}|{commit.ToLowerInvariant()}")));

    // ---- the delivery trains ------------------------------------------------------------------------------------------------------------------------------------------

    async Task Trains(AdoptionReport report, Dictionary<string, IReadOnlyList<ItemPromotion>> promotions, bool candidatesMayBeValidated, CancellationToken ct)
    {
        var pending = await ports.Reads.PendingSignOffs(ct);
        foreach (var key in options.TrainKeys)
        {
            for (var attempt = 0; attempt < Attempts + 1; attempt++)
            {
                var rec = await ports.Trains.Load(key, ct);
                var before = rec?.State ?? TrainState.New(key);
                var next = before;
                var here = pending.Where(p => string.Equals(p.Environment, before.Environment, StringComparison.OrdinalIgnoreCase)).Select(p => p.SignOff).ToList();
                if (here.Count > 0) next = AdoptionTrain.SignOffs(next, here, await PromotionsFor(next, promotions, ct));
                var removed = 0;
                // the candidates are validated against the merged-items table only once it has been backfilled, or it would not hold the merges a candidate stands for
                if (candidatesMayBeValidated && next.Candidates.Count > 0)
                {
                    var held = await ports.Merged.Held([.. next.Candidates.Values.Select(c => (c.Item, c.Commit.ToLowerInvariant()))], ct);
                    var (validated, gone) = AdoptionTrainRows.ValidateCandidates(next, held);
                    next = validated; removed = gone.Count;
                }
                if (next.ToJson() == before.ToJson()) break;
                if (await ports.Trains.Save(key, next, rec?.Revision ?? 0, ct))
                {
                    report.CandidatesRemoved += removed;
                    report.SignOffs += Claimed(before, next);
                    break;
                }
                report.Conflicts++;
                if (attempt == Attempts) throw new AdoptionConflictException($"the delivery train {key}");
            }
        }
    }

    // The promotions v1 recorded (merge.promoted) are on the state comments of the items of the release, and those are mostly not on the board any more: the issue closed with the merge. The items the train knows (its candidates and the items
    // of the releases it asked for) are read for them, besides the items the run read; at most MaxPromotionReads items per train.
    const int MaxPromotionReads = 40;

    async Task<IReadOnlyList<ItemPromotion>> PromotionsFor(TrainState train, Dictionary<string, IReadOnlyList<ItemPromotion>> known, CancellationToken ct)
    {
        var reads = 0;
        foreach (var item in train.Candidates.Values.Select(c => c.Item).Concat(train.RequestItems.Values.SelectMany(i => i)).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal))
        {
            if (known.ContainsKey(item) || reads >= MaxPromotionReads || !ItemId.TryParse(item, out var id)) continue;
            reads++;
            var record = await ports.Reads.ReadItem(id, ct);
            known[item] = record?.State is { } state ? AdoptionTrain.Promotions(item, state) : [];
        }
        return [.. known.Values.SelectMany(p => p)];
    }

    // the Sign-offs the train answers after the run and did not before: waiting, and for a release it asked for
    static int Claimed(TrainState before, TrainState after) =>
        after.SignOffs.Count(s => !s.Answered && after.Requested.ContainsKey(s.Version) && !(before.SignOffs.Any(b => b.InterruptionId == s.InterruptionId) && before.Requested.ContainsKey(s.Version)));

    async Task MergeLine(AdoptionReport report, CancellationToken ct)
    {
        var sagas = (await ports.Items.OwnedStates(ct)).ToDictionary(s => s.Item, StringComparer.Ordinal);
        for (var attempt = 0; attempt < Attempts + 1; attempt++)
        {
            var rec = await ports.MergeTrains.Load(options.MergeLineKey, ct);
            if (rec is null) return;
            var (next, gone) = AdoptionTrainRows.ValidateMergeLine(rec.State, sagas);
            if (gone.Count == 0) return;
            if (await ports.MergeTrains.Save(options.MergeLineKey, next, rec.Revision, ct)) { report.MergeLineRemoved += gone.Count; return; }
            report.Conflicts++;
        }
        throw new AdoptionConflictException("the merge line");
    }

    // ---- DeliveredTo ---------------------------------------------------------------------------------------------------------------------------------------------------

    // what the table lacks: the releases the saga's rows know their items reached (from v1's state and the trains) and the releases the trains record as having carried an item (their history is Octopus's, replayed by the watcher)
    async Task DeliveredTo(AdoptionReport report, CancellationToken ct)
    {
        var at = now();
        foreach (var s in await ports.Items.OwnedStates(ct))
        {
            if (!ItemId.TryParse(s.Item, out var item)) continue;
            foreach (var (environment, release) in s.Delivered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (release.Length > 0 && await ports.Delivered.RecordIfAbsent(new DeliveredTo(item, environment, release), at, ct)) report.DeliveredToWritten++;
        }
        foreach (var key in options.TrainKeys)
        {
            if (await ports.Trains.Load(key, ct) is not { } rec) continue;
            foreach (var (version, carried) in rec.State.DeliveredBy.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                foreach (var candidate in carried.OrderBy(c => c.Item, StringComparer.Ordinal))
                    if (ItemId.TryParse(candidate.Item, out var item) && await ports.Delivered.RecordIfAbsent(new DeliveredTo(item, rec.State.Environment, version), at, ct)) report.DeliveredToWritten++;
        }
    }
}
