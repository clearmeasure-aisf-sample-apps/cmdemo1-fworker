using System.Text.RegularExpressions;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W9.3 (#355): the standing back-merge trigger's verbs (plugin/scripts/lib/BaseUpdate.ps1, docs/design/plant-manager.md 6a) for the items the WorkItemSaga owns: `base-watch` (the head of the default branch, and per owned
// item its pull request, how far the branch is behind and which files master changed that the pull request changes too) and `base-update-branch` (GitHub's "update branch" for one pull request, only when nothing
// of the item would be made stale by it). These are the pure rules; BaseUpdateService has the calls.

/// <summary>What the saga knows of an item that decides whether the gateway may move its branch (the script reads the same from the state comment).</summary>
/// <param name="VerdictRecorded">A verdict is on record (a PASS waiting for its merge, or the one that was merged; the script keeps it until the item is settled): the verdict covers one head, and moving it would void it.</param>
/// <param name="PassUnmerged">The script's <c>verdict.passed and not verdict.processed</c>: a PASS waiting for its merge, which is the gateway's next step.</param>
/// <param name="Merged">The pull request is merged and the item still settles (the script's <c>merge.awaiting</c>).</param>
/// <param name="InValidation">The item is in Functional Testing (the saga's Verify or Merge step).</param>
/// <param name="PullRequest">The pull request the item has recorded (0: none).</param>
/// <param name="Branch">The branch of the pull request when no open pull request names it (the saga records only the number: empty).</param>
public sealed record BaseItemFacts(bool VerdictRecorded, bool PassUnmerged, bool Merged, bool InValidation, int PullRequest, string Branch)
{
    public static BaseItemFacts From(WorkItemState s) => new(
        VerdictRecorded: s.VerdictHead.Length > 0 || s.MergeCommit.Length > 0,   // (the verdict of a merged item stays on record, as processed)
        PassUnmerged: s.IsOpen && s.VerdictHead.Length > 0 && s.MergeCommit.Length == 0,
        Merged: s.IsOpen && s.MergeCommit.Length > 0,
        InValidation: s.IsOpen && s.Step is StepKind.Verify or StepKind.Merge,
        PullRequest: s.PullRequest, Branch: "");
}

public static class BaseUpdateRules
{
    /// <summary>
    /// <c>Get-BackmergeHold</c>: why the gateway must not move this item's branch ("" when it may). Moving the head would make the verdict's sha (the merge-after-PASS path) or the QC run stale.
    /// <paramref name="qcRecord"/> is the JSON of the QC record of the pull request (null: none); it holds the head when a QC environment was made for it.
    /// </summary>
    public static string Hold(BaseItemFacts item, int pullRequest, string head, string? qcRecord)
    {
        if (item.VerdictRecorded) return "verdict-recorded";
        if (item.Merged) return "merge-awaiting";
        if (item.InValidation) return "in-validation";
        if (pullRequest > 0 && head.Length > 0 && qcRecord is not null && qcRecord.Contains(head, StringComparison.OrdinalIgnoreCase)) return "qc-for-head";
        return "";
    }

    /// <summary>The pull request of an item among the open ones: its branch is <c>{name}/{item}-...</c>, or a copilot/* branch of the pull request the item recorded.</summary>
    public static BasePull? PullRequestOf(IEnumerable<BasePull> open, string instanceName, int item, int recordedPullRequest)
    {
        var branch = new Regex($"^{Regex.Escape(instanceName)}/{item}-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var p in open)
            if (branch.IsMatch(p.HeadRef) || (p.HeadRef.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) && recordedPullRequest > 0 && recordedPullRequest == p.Number)) return p;
        return null;
    }

    /// <summary>The conflict the row reports for a pull request that was read: GitHub answers mergeable null until it has computed it (a later look gets it).</summary>
    public static bool? Conflict(BasePull p) => p.Mergeable == false || string.Equals(p.MergeableState, "dirty", StringComparison.Ordinal) ? true : p.Mergeable == true ? false : null;
}

/// <summary>A pull request as the back-merge trigger reads it.</summary>
public sealed record BasePull(int Number, string State, string HeadRef, string HeadSha, bool? Mergeable, string MergeableState);
