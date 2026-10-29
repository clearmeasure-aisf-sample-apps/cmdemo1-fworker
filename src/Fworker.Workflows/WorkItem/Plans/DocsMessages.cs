using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.WorkItem;

// W6.6 (#335): the messages of the docs plan's steps. They join the inputs of the WorkItemSaga (WorkItemMessages.cs): addressed by the item, carrying the generation and, for what a session or a command answers, the arm
// of the step, and a business key where they change the state. The merge is the one step that asks something of the outside, so it has a command (MergeRequested) and the answer to it (MergeAttempted); the command is
// handled in the gateway (it needs GitHub), the saga only decides when to ask and what each answer means.

/// <summary>The rules of the docs plan, from config/instance.json (timing.maxReviewRounds, timing.docsOnlyMaxReviewRounds) and the merge retry the plan adds.</summary>
/// <param name="MaxReviewRounds">timing.maxReviewRounds (3): the review rounds of a column.</param>
/// <param name="DocsOnlyMaxReviewRounds">timing.docsOnlyMaxReviewRounds (2): the design of an item whose design recorded the docs-only marker gets fewer (Test-DocsOnlyDesignReview).</param>
/// <param name="MergeRetry">How long after a blocked or transient merge answer the merge is asked for again (the script's sweep asks every minute).</param>
/// <param name="MaxMergeAttempts">Merge calls of one arm of the Merge step before the item is parked: a head that stays blocked (checks that never pass, a hold) is a question for a person.</param>
/// <param name="ReviewedSteps">The steps that are left only after a REVIEW: COMPLETE (reviewVerdictColumns: the design and the development column); null is those two.</param>
public sealed record DocsPlanRules(int MaxReviewRounds = 3, int DocsOnlyMaxReviewRounds = 2, TimeSpan? MergeRetry = null, int MaxMergeAttempts = 60, IReadOnlyList<StepKind>? ReviewedSteps = null)
{
    public static readonly DocsPlanRules Default = new();

    /// <summary>Whether the step is left only after a complete review.</summary>
    public bool NeedsReview(StepKind step) => ReviewedSteps is null ? step is StepKind.Design or StepKind.Develop : ReviewedSteps.Contains(step);

    public TimeSpan Retry => MergeRetry ?? TimeSpan.FromSeconds(60);

    /// <summary>The review rounds of the design column: the fast path of a docs-only design (the latest design recorded the marker) has the smaller limit.</summary>
    public int DesignRounds(bool? designMarker) => designMarker == true ? Math.Min(MaxReviewRounds, DocsOnlyMaxReviewRounds) : MaxReviewRounds;
}

/// <summary>
/// The reviewer's verdict of the step the item is on (record-review): <paramref name="Complete"/> is REVIEW: COMPLETE, otherwise REVIEW: FINDINGS. A round is used either way; past the limit of the column the review is
/// refused. The design and the development step leave only after a COMPLETE review (config reviewVerdictColumns).
/// </summary>
[MessageIdentity("workitem.review-recorded")]
public sealed record ReviewRecorded(ItemId Item, int Generation, string ArmId, string Key, bool Complete) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>
/// The Functional Testing verdict (validation-verdict) on the pull request's head <paramref name="Head"/>. A PASS covers that head and no other: the merge sends exactly it. A FAIL sends the item back to Development
/// while D5's FailVerdict budget lasts, and parks it past that.
/// </summary>
[MessageIdentity("workitem.verdict-recorded")]
public sealed record VerdictRecorded(ItemId Item, int Generation, string ArmId, string Key, bool Passed, int PullRequest, string Head, string Summary) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>How a merge call ended (<c>MergeStatus</c> of Fworker.Integrations.GitHub.MergeDirect, without the dependency).</summary>
public enum MergeResultKind { Merged, Blocked, HeadMoved, Failed }

/// <summary>The answer of the merge command: the outcome, its stable reason code, the commit it made, the head GitHub's own update made, and whether asking again can help.</summary>
public sealed record MergeResult(MergeResultKind Kind, string Reason, string Detail = "", string Commit = "", string NewHead = "", bool Transient = false, TimeSpan? RetryAfter = null, int UpdatesAdded = 0, bool Updated = false);

/// <summary>The saga asks the gateway to merge the pull request with the PASS head. The attempt names the answer; sending it again is safe (the merge command is idempotent on its key).</summary>
[MessageIdentity("workitem.merge-requested")]
public sealed record MergeRequested(ItemId Item, int Generation, string ArmId, int Attempt, int PullRequest, string Head, int UpdatesSoFar, string UpdatedFrom) : ItemCommand(Item);

/// <summary>The answer to <see cref="MergeRequested"/>. A merge is also a fact the delivery train derives (ItemMerged → MergeSeen); whichever arrives first ends the step and the other finds the item past its merge.</summary>
[MessageIdentity("workitem.merge-attempted")]
public sealed record MergeAttempted(ItemId Item, int Generation, string ArmId, string Key, int Attempt, MergeResult Result) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>The timer of the next merge call (a Wolverine scheduled message): it counts only when the merge is still waiting for it at that time.</summary>
[MessageIdentity("workitem.merge-retry-due")]
public sealed record MergeRetryDue(ItemId Item, int Generation, string ArmId, DateTimeOffset Due) : WorkItemInput(Item), IArmedInput;
