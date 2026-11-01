using Fworker.Contracts.Commands;

namespace Fworker.Workflows.WorkItem;

/// <summary>
/// The issue of a finished item is closed by the engine, as v1's settle closes it at Done (the pull request of a docs item carries no closing keyword, and a person's merge closes nothing). It is one rule for every way
/// an item completes (the saga's own merge, a merge by a person that reaches the saga as MergeSeen, ItemMerged or PullRequestClosed, the end of the settle window): the <see cref="WorkItemActions.Completed"/> decision.
/// An item handed back to v1 (<see cref="WorkItemActions.HandedBack"/>) is not completed: v1 settles it. The command is idempotent on GitHub (an issue that is closed already is left) and keyed per item and generation.
/// </summary>
public static class WorkItemIssueClose
{
    /// <summary>The command for a decision, or null for one that closes nothing. <paramref name="carriesFeature"/>: the engine carries the code plans, whose Done the card projection writes too.</summary>
    public static CloseWorkItemIssue? For(WorkItemDecided d, bool carriesFeature)
    {
        if (d.Action != WorkItemActions.Completed) return null;
        var docs = string.Equals(d.Plan, WorkPlans.Docs, StringComparison.OrdinalIgnoreCase);
        if (!docs && !(WorkPlans.IsCode(d.Plan) && carriesFeature)) return null;
        return new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(d.Item, d.Generation), d.Item, d.Generation);
    }
}
