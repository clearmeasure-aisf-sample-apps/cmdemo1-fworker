using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W9.3 (#355): `merge`, `approve-pr` and `clear-hold` for the items the WorkItemSaga owns. The merge itself is the saga's: its Merge step asks the repository's MergeTrainSaga for the turn and the merge port runs the C# direct merge
// (W6.2, W7.2). So the verb a session calls does not merge on its own: it applies the script's checks to the saga's state, asks the saga to make its merge call now (its retry timer, delivered at once), waits a short while for
// the answer and says what the saga did, in the shape of Verb_merge's answers (merged, already-merged, waiting, demoted, parked, or its refusals). An item the saga does not own, and a merge policy other than auto
// (the approval of the current head, the QC gate, the migration number collision), are the script's: `Merge` answers null and the caller runs the verb on v1.

/// <summary>Where a verb reads whether an item carries a label (the GitHub issue's labels).</summary>
public interface IItemLabels
{
    /// <summary>The labels of the item, or null when it does not exist. A fault throws.</summary>
    Task<IReadOnlyList<string>?> LabelsOf(ItemId item, CancellationToken ct);
}

/// <summary>How the merge verb waits for the saga: the pause between two looks at its state and how many looks (1 second apart by default, 20 at most).</summary>
public sealed record MergeVerbWait(TimeSpan Poll, int Looks)
{
    public static readonly MergeVerbWait Default = new(TimeSpan.FromSeconds(1), 20);
}

public sealed class MergeVerbService(RepoId home, IWorkItemStore store, IItemLabels labels, Func<MergeRetryDue, Task<bool>> publish, MergeVerbSettings settings,
    MergeVerbWait? wait = null, Func<TimeSpan, Task>? delay = null, Func<DateTimeOffset>? clock = null)
{
    readonly MergeVerbWait wait = wait ?? MergeVerbWait.Default;
    readonly Func<TimeSpan, Task> pause = delay ?? Task.Delay;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public MergeVerbSettings Settings => settings;

    /// <summary>Whether the saga knows the item (any instance): the verbs below decide for these and only these.</summary>
    public async Task<WorkItemState?> StateOf(int item, CancellationToken ct) => (await store.Load(new ItemId(home, item).ToString(), ct))?.State;

    /// <summary><c>merge</c> for an item the saga owns; null when the verb is the script's (not owned, or a policy other than auto).</summary>
    public async Task<VerbAnswer?> Merge(JsonObject body, CancellationToken ct)
    {
        if (!MergeVerbReplies.ItemNumber("merge", body, out var item, out var refusal)) return refusal;
        if (!settings.Auto) return null;
        var state = await StateOf(item, ct);
        if (state is null) return null;
        var pr = MergeVerbReplies.OptionalInt(body, "pr");
        if (MergeVerbRules.MergeFromState(item, pr, state, settings) is { } decided) return decided;
        var itemLabels = await labels.LabelsOf(new ItemId(home, item), ct);
        if (itemLabels is null) return MergeVerbReplies.Reject("merge", 12, "item-not-found", $"issue #{item} not found");
        if (MergeVerbRules.MergeFromLabels(item, pr, state, itemLabels, settings) is { } held) return held;
        return await MergeNow(item, state, ct);
    }

    /// <summary>
    /// The saga's merge call, asked for now. When a call is already out nothing is asked (its answer comes); when the saga waits for its next call (a blocked answer set the timer) the timer is delivered at once. Then the
    /// state is looked at until the merge happened, the item left the Merge step, was parked, or the looks ran out.
    /// </summary>
    async Task<VerbAnswer> MergeNow(int item, WorkItemState before, CancellationToken ct)
    {
        if (before.MergeRequest is null && before.MergeRetryAt is { } due)
        {
            if (!await publish(new MergeRetryDue(before.ItemId, before.Generation, before.ArmId, due)))
                return MergeVerbReplies.Reject("merge", 20, "merge-unavailable", "the merge could not be handed to the work item saga; ask again", new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        var after = before;
        for (var look = 0; look < wait.Looks; look++)
        {
            await pause(wait.Poll);
            after = await StateOf(item, ct) ?? after;
            if (Settled(after)) break;
        }
        return Outcome(item, after);
    }

    static bool Settled(WorkItemState s) => s.MergeCommit.Length > 0 || s.Step != StepKind.Merge || s.Parked || !s.IsOpen;

    // what the script's answers are, for what the saga did
    VerbAnswer Outcome(int item, WorkItemState s)
    {
        if (s.MergeCommit.Length > 0) return MergeVerbReplies.Ok("merge", new JsonObject { ["id"] = item, ["outcome"] = "merged", ["sha"] = s.MergeCommit, ["by"] = "gateway" });
        if (s.Parked)
            return MergeVerbReplies.Ok("merge", new JsonObject { ["id"] = item, ["outcome"] = "parked", ["reason"] = ParkCode(s.ParkReason) });
        // the head moved (a new verdict is needed) or the branch fell out of date (the owner merges master): the item went back, which the script calls a demotion
        if (s.Step is StepKind.Verify or StepKind.Develop)
            return MergeVerbReplies.Ok("merge", new JsonObject
            {
                ["id"] = item, ["outcome"] = "demoted", ["reason"] = s.Step == StepKind.Verify ? "stale-head" : "merge-out-of-date",
                ["detail"] = new JsonObject { ["outcome"] = "demoted", ["round_trip"] = s.Demotions + s.MechanicalFixes },
            });
        return MergeVerbReplies.Ok("merge", new JsonObject
        {
            ["id"] = item, ["outcome"] = "waiting", ["reason"] = s.MergeBlockedReason.Length > 0 ? s.MergeBlockedReason : "checks-pending", ["since"] = now().UtcDateTime.ToString("o"),
        });
    }

    // "merge-failed: not-mergeable: ..." is the code merge-failed; a reason without a detail is its own code
    static string ParkCode(string reason)
    {
        var colon = reason.IndexOf(':');
        return (colon > 0 ? reason[..colon] : reason).Trim();
    }

    /// <summary><c>approve-pr</c>: the script's answer under merge policy auto; null where the inspector's review is the script's.</summary>
    public VerbAnswer? ApprovePr(JsonObject body) => MergeVerbRules.ApprovePr(body, settings);

    /// <summary><c>clear-hold</c> for an item the saga knows, under merge policy auto; null where the hold is the script's.</summary>
    public async Task<VerbAnswer?> ClearHold(JsonObject body, CancellationToken ct)
    {
        if (!MergeVerbReplies.ItemNumber("clear-hold", body, out var item, out var refusal)) return refusal;
        if (!settings.Auto) return null;
        var state = await StateOf(item, ct);
        return state is null ? null : MergeVerbRules.ClearHold(item, state, settings);
    }
}
