using Fworker.Contracts.Commands;

namespace Fworker.Workflows.WorkItem;

/// <summary>
/// The issue of a finished item is closed by the engine, as v1's settle closes it (the pull request of a docs item carries no closing keyword, and a person's merge closes nothing). It is one rule for every way
/// an item completes (the saga's own merge, a merge by a person that reaches the saga as MergeSeen, ItemMerged or PullRequestClosed, the end of the settle window): the <see cref="WorkItemActions.Completed"/> decision.
/// v1 closes a docs item after master is green and no release candidate is due (SettleVerb.Finish); v2 closes it at the merge, which is the saga's completion. An item handed back to v1
/// (<see cref="WorkItemActions.HandedBack"/>) is not completed: v1 settles it. v1 has no settle cap, so the completion that ends one (the release never reached prod) closes nothing. The command is idempotent on GitHub
/// (an issue that is closed already is left) and keyed per item and generation.
/// </summary>
public static class WorkItemIssueClose
{
    /// <summary>The words that start the reason of the completion at the end of the settle cap (WorkSteps.SettleEnded).</summary>
    public const string SettleCapReasonPrefix = "the settle cap of ";

    /// <summary>The command for a decision, or null for one that closes nothing. <paramref name="carriesFeature"/>: the engine carries the code plans, whose Done the card projection writes too.</summary>
    public static CloseWorkItemIssue? For(WorkItemDecided d, bool carriesFeature)
    {
        if (d.Action != WorkItemActions.Completed) return null;
        var docs = string.Equals(d.Plan, WorkPlans.Docs, StringComparison.OrdinalIgnoreCase);
        if (!docs && !(WorkPlans.IsCode(d.Plan) && carriesFeature)) return null;
        if (d.Reasons.Count > 0 && d.Reasons[0].StartsWith(SettleCapReasonPrefix, StringComparison.Ordinal)) return null;
        return new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(d.Item, d.Generation), d.Item, d.Generation);
    }
}
