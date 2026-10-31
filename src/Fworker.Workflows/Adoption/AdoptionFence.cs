using Fworker.Contracts.Ids;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// W9.10n: the fencing of a re-adoption after FACTORY_V2_OFF (docs/design/fworker-wolverine-implementation.md, "W9.10n as built"). While the kill switch is on, v1 acts on the items from their state comments and v2 does nothing, but
// what v2 left behind is still there: its timers in the message store, its commands in the outbox, the slots and requests in the scheduler's ledger, the places in the merge line. A re-adoption that took the comment's word for
// the step and went on with the same instance would let all of that act on an item v1 has moved, merged or finished. The fence is the instance itself: an item v1 touched gets a new generation, every timer and command carries
// the generation of the instance that made it, and the saga answers an older one with NotFound (the scheduler and the merge line answer it Stale and leave the new instance's entries alone). The rest of this file is what is
// cleaned beside the saga: the ledger's entries of the instances that ended, and the merge line's and the delivery train's rows that were built before the interval.

/// <summary>What the scheduler's ledger is to keep of one item after its instance changed: the entries of <paramref name="KeepGeneration"/> (null: none, the item has ended).</summary>
public sealed record LedgerFence(ItemId Item, int? KeepGeneration);

public static class AdoptionFence
{
    /// <summary>
    /// The generation to map an item for: 1 when the saga has no row for it; the row's own when the engine ran without a break; the next one when v1 acted in between (<paramref name="afterInterval"/>), because the instance
    /// the row stands for may be changed by v1 and the mapping then opens a new one. Whether a break happened is W9.9b's to say (the kill switch was on at the last stop, or the gateway ran v1 since the last time v2 acted);
    /// the item is fenced only when the comment shows that v1 touched it (<see cref="AdoptionKind.Updated"/>), the rest keep their generation.
    /// </summary>
    public static int GenerationFor(WorkItemState? existing, bool afterInterval) => existing is null ? 1 : afterInterval ? existing.Generation + 1 : existing.Generation;

    /// <summary>
    /// Whether the reason the mapping skipped an item means that v1 ended it, so that an open saga row for it is closed: the settle of v1 (<c>merge.done_at</c>), a docs item that was merged (the docs plan ends at the merge), a
    /// release that reached the final environment and passed its settle window, a card moved to Done, and an issue that was closed while the saga had not merged it (a person, or the merge of v1: the merge closes the issue). The
    /// other reasons (a hold column, no state comment, a column of no step, a plan this engine does not carry) leave the row to the saga's own inputs: they say where the item is, not that it is over.
    /// </summary>
    public static bool V1Ended(string skipReason, WorkItemState open) => skipReason switch
    {
        "settled" or "docs-merged" or "delivered-and-settled" or "card-done" => true,
        // an issue that the merge closed belongs to the merge step of the saga that made it (the delivery and the settle window are its own)
        "issue-closed" => open.MergeCommit.Length == 0 && open.Status == WorkItemStatus.Open,
        _ => false,
    };
}

public static class AdoptionTrainRows
{
    /// <summary>What a validation of rows removed and why: one line per row.</summary>
    public sealed record Removal(string Row, string Reason);

    /// <summary>
    /// The repository's merge line with the entries that no longer stand for a request of an open saga removed. An entry stays when the saga row of its item is open, is the instance (generation) that made the entry, is on the Merge
    /// step with its place in the line recorded (<see cref="WorkItemState.TrainEntered"/>), has not merged, and asks for the pull request the entry holds. Anything else was built before the interval (the item was merged,
    /// finished, parked, moved or reopened by v1, or the saga has no row for it) and would hold the line, or be granted a turn, for a request nobody makes any more: the saga that still wants its merge asks again (its request is
    /// published with its row). A copy; the state given is not changed.
    /// </summary>
    public static (MergeTrainState State, IReadOnlyList<Removal> Removed) ValidateMergeLine(MergeTrainState line, IReadOnlyDictionary<string, WorkItemState> sagas)
    {
        var next = MergeTrainState.FromJson(line.ToJson());
        var removed = new List<Removal>();
        foreach (var e in line.Line.OrderBy(x => x.Order).ToList())
        {
            var why = Why(e, sagas.GetValueOrDefault(e.Item));
            if (why is null) continue;
            next.Line.RemoveAll(x => x.Item == e.Item);
            removed.Add(new Removal(e.Item, why));
        }
        return (next, removed);
    }

    static string? Why(MergeTrainEntry e, WorkItemState? saga)
    {
        if (saga is null) return "the saga has no row for the item";
        if (!saga.IsOpen) return "the item's instance ended";
        if (e.Generation != 0 && e.Generation != saga.Generation) return $"the entry is generation {e.Generation}, the item is on {saga.Generation}";
        if (saga.Step != StepKind.Merge) return $"the item is on {saga.Step}, not Merge";
        if (saga.MergeCommit.Length > 0) return "the item was merged";
        if (!saga.TrainEntered) return "the saga holds no place in the line";
        if (saga.PullRequest != 0 && e.PullRequest != 0 && saga.PullRequest != e.PullRequest) return $"the entry is for pull request #{e.PullRequest}, the item's is #{saga.PullRequest}";
        return null;
    }

    /// <summary>
    /// The delivery train with the candidates that no merged-items row backs removed: a candidate stands for a merge the gateway recorded (<paramref name="merged"/>, "owner/repo#N|commit" with the commit in lower case, from the
    /// merged-items table after W9.9b's backfill), and one for another commit of the item (it was reopened and merged again), or for a merge the table does not hold, is not a delivery to wait for. The next merge seen for the item
    /// makes it a candidate again. Candidates already delivered here are removed too. A copy.
    /// </summary>
    public static (TrainState State, IReadOnlyList<Removal> Removed) ValidateCandidates(TrainState train, IReadOnlySet<string> merged)
    {
        var next = TrainState.FromJson(train.ToJson());
        var removed = new List<Removal>();
        foreach (var kv in train.Candidates.OrderBy(k => k.Key, StringComparer.Ordinal).ToList())
        {
            var key = $"{kv.Value.Item}|{kv.Value.Commit.ToLowerInvariant()}";
            string? why = train.DeliveredItems.Contains(key) ? "the item was delivered here already" : merged.Contains(key) ? null : "no merged-items row holds the merge";
            if (why is null) continue;
            next.Candidates.Remove(kv.Key);
            removed.Add(new Removal(kv.Key, why));
        }
        return (next, removed);
    }
}
