using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W9.3 (#355): the rules of `merge`, `approve-pr` and `clear-hold` that are decisions and not GitHub calls, as pure functions of the item's saga state and the configuration. They are the checks of
// plugin/scripts/lib/Verbs.Merge.ps1 (Verb_merge) and Verbs.Inspect.ps1 (Verb_approve_pr, Verb_clear_hold), in the script's order, with its codes, exits and texts. What the script decides by reading the item's
// state comment, the saga's state answers here: the PASS verdict is the saga on its Merge step with the head the PASS covers, a merge that happened is the merge commit, a hold or park is the saga's park.

/// <summary>What the merge verbs read of config/instance.json.</summary>
/// <param name="MergePolicy"><c>mergePolicy</c>: auto (the gateway merges a PASS by itself) or peer-approved (an inspector's approval, then the merge; stays on v1).</param>
/// <param name="AutoMergeLabel"><c>autoMergeLabel</c>: only an item that carries it is merged under auto.</param>
/// <param name="HoldLabels"><c>holdLabels</c> (hold:owner when the configuration names none): a person holds the item while one is on it.</param>
/// <param name="ValidationColumn"><c>columns.validation</c>: the column a PASS is merged from.</param>
/// <param name="ColumnOf">The card column of a step (null when the step has none of its own: the merge happens in the validation column).</param>
public sealed record MergeVerbSettings(string MergePolicy, string AutoMergeLabel, IReadOnlyList<string> HoldLabels, string ValidationColumn, Func<StepKind, string?> ColumnOf)
{
    public bool Auto => string.Equals(MergePolicy, "auto", StringComparison.Ordinal);

    /// <summary><c>Get-HoldLabel</c>: the hold label on the item, or "" (case does not matter, as in the script).</summary>
    public string HoldLabelOn(IEnumerable<string> labels)
    {
        foreach (var h in HoldLabels)
            if (h.Length > 0 && labels.Any(l => string.Equals(l, h, StringComparison.OrdinalIgnoreCase))) return h;
        return "";
    }

    /// <summary>The column the item's card is in as far as the saga knows: the column of its step, the validation column for the merge, else the step's name.</summary>
    public string ColumnName(StepKind step) => ColumnOf(step) ?? (step is StepKind.Verify or StepKind.Merge ? ValidationColumn : step.ToString());
}

public static class MergeVerbRules
{
    /// <summary>
    /// The first half of <c>Verb_merge</c>: what the state alone decides, in the script's order. Null means the call goes on (to the label checks, then the merge itself). An item the saga has merged answers
    /// <c>already-merged</c> whatever column it is in (the merged pull request may have closed it, and the settle may have moved its card on).
    /// </summary>
    public static VerbAnswer? MergeFromState(int item, int? pullRequestArgument, WorkItemState state, MergeVerbSettings settings)
    {
        if (state.MergeCommit.Length > 0 && state.PullRequest > 0 && (pullRequestArgument is null || pullRequestArgument == state.PullRequest))
            return MergeVerbReplies.Ok("merge", new JsonObject { ["id"] = item, ["outcome"] = "already-merged", ["sha"] = state.MergeCommit });
        if (!state.IsOpen) return MergeVerbReplies.Reject("merge", 11, "item-closed", $"issue #{item} is closed");
        if (state.Step is not (StepKind.Verify or StepKind.Merge))
            return MergeVerbReplies.Reject("merge", 10, "wrong-column", $"merging happens only from '{settings.ValidationColumn}'; the item is in '{settings.ColumnName(state.Step)}'");
        // a PASS the saga has recorded sends the item to its Merge step with the head the PASS covers; before that (or after a FAIL) there is none to merge
        if (state.Step != StepKind.Merge || state.VerdictHead.Length == 0) return MergeVerbReplies.Reject("merge", 10, "no-pass-verdict", "no unprocessed PASS verdict is recorded for this item");
        return null;
    }

    /// <summary>
    /// The second half, after the item's labels were read: a hold label (a person holds the item: its PASS is not merged until the label is removed), the pull request the PASS names, the Auto Merge label,
    /// and the saga's own park (a parked saga merges nothing until a person unparks it; the script's session call ignores a park under auto, but it is the script that merges there, not a saga).
    /// </summary>
    public static VerbAnswer? MergeFromLabels(int item, int? pullRequestArgument, WorkItemState state, IReadOnlyList<string> labels, MergeVerbSettings settings)
    {
        if (settings.HoldLabelOn(labels) is { Length: > 0 } hold)
            return MergeVerbReplies.Reject("merge", 10, "held-by-label", $"item carries the '{hold}' label: a person holds it; the PASS is not merged until the label is removed", new JsonObject { ["label"] = hold });
        var pr = pullRequestArgument ?? state.PullRequest;
        if (pr != state.PullRequest) return MergeVerbReplies.Reject("merge", 10, "pr-mismatch", $"the verdict names PR #{state.PullRequest}, not #{pr}");
        if (!labels.Any(l => string.Equals(l, settings.AutoMergeLabel, StringComparison.OrdinalIgnoreCase)))
            return MergeVerbReplies.Reject("merge", 10, "no-auto-merge-label", $"item does not carry '{settings.AutoMergeLabel}'");
        if (state.Parked) return MergeVerbReplies.Reject("merge", 10, "held", $"item is held: {state.ParkReason}");
        return null;
    }

    /// <summary>
    /// <c>Verb_approve_pr</c> under merge policy auto: the arguments are checked first (the item, then the inspector's text), then the answer is the script's: no inspection is needed. Null under any other policy,
    /// where the inspector's review of the pull request on GitHub is the script's (it stays on v1 with the rest of peer-approved, W6.2).
    /// </summary>
    public static VerbAnswer? ApprovePr(JsonObject body, MergeVerbSettings settings)
    {
        if (!MergeVerbReplies.ItemNumber("approve-pr", body, out _, out var refusal)) return refusal;
        if (!MergeVerbReplies.Need("approve-pr", body, "text", out _, out refusal)) return refusal;
        if (settings.Auto) return MergeVerbReplies.Reject("approve-pr", 10, "not-required", "merge policy is 'auto'; no inspection is needed");
        return null;
    }

    /// <summary>
    /// <c>Verb_clear_hold</c> under merge policy auto: only an inspector's escalation (approve-pr, which auto never runs) is a hold this verb clears, so a saga item is never held by the inspector. A closed item is
    /// refused first, as the script does. Null for the item the saga does not know, and under any other policy (the hold is then the state comment's).
    /// </summary>
    public static VerbAnswer? ClearHold(int item, WorkItemState state, MergeVerbSettings settings)
    {
        if (!settings.Auto) return null;
        if (!state.IsOpen) return MergeVerbReplies.Reject("clear-hold", 11, "item-closed", $"issue #{item} is closed");
        return MergeVerbReplies.Reject("clear-hold", 10, "not-held", "the item is not held by the inspector");
    }
}
