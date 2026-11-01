using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.WorkItem;

// W6.1: the messages of the WorkItemSaga (docs/design/wolverine-factory.md 3.1). Everything that reaches an item is a WorkItemInput addressed by the item (its qualified id is the partition key, so one
// item's messages run one at a time) and, where the sender knows them, the generation and the arm id of the step it answers: a message from an earlier instance of the item (a reopened one) or from an earlier
// arming of a step (a session whose lease ran out) names a generation or an arm that is not the current one and is ignored. A generation of 0 means "the current instance": events from outside name only
// the item (the open-instance index answers which instance that is). What the saga publishes is WorkItemDecided, once per decision, with a sequence number that consumers (the projections of W6.3, the
// history) use to skip what they already applied. Delivery is at least once, so every input that changes the state carries a business key (phase, round, head, session...) and a repeated key does nothing.

/// <summary>Any input of a work item's saga.</summary>
public abstract record WorkItemInput(ItemId Item) : ItemCommand(Item);

/// <summary>An input that names the instance it answers; 0 means the current one.</summary>
public interface IGenerationInput { int Generation { get; } }

/// <summary>An input that answers one arming of a step.</summary>
public interface IArmedInput : IGenerationInput { string ArmId { get; } }

/// <summary>An input with a business key: the same key is applied once.</summary>
public interface IKeyedInput { string Key { get; } }

/// <summary>
/// Starts the item's saga: the first generation, or, for an item whose last instance completed, the next. A start for an item that is open does nothing (the answer says so). <paramref name="Plan"/> names
/// the plan (the docs plan for an item the intake knows to be docs-only); without one the hotfix flag chooses between the hotfix and the feature plan. V1.1: <paramref name="Step"/> is the step the item
/// begins at, for a card that stands in the column of a step already (a person moved it there, or the replenish placed it): the plan's first step when null or when the plan has no such step.
/// </summary>
[MessageIdentity("workitem.start")]
public sealed record StartWorkItem(ItemId Item, string Source, string? Plan = null, bool Hotfix = false, StepKind? Step = null) : WorkItemInput(Item);

/// <summary>A closed item was reopened (a person moved it back, or a delivered item was rolled back): a completed saga starts the next generation on the plan it had; an open one ignores it.</summary>
[MessageIdentity("workitem.reopen")]
public sealed record ReopenWorkItem(ItemId Item, string Reason, string Key) : WorkItemInput(Item), IKeyedInput;

/// <summary>
/// The design step recorded its design (the latest design section decides): <paramref name="DocsOnlyMarker"/> is whether it carries the docs-only marker. On the feature plan a marker switches the item to
/// the docs plan, and the absence of one on the docs plan switches it back; the judgment is the D4 port's. <paramref name="Files"/> is the design's <c>Files:</c> line (D6's footprint; null or empty: none), which the feature plan keeps.
/// </summary>
[MessageIdentity("workitem.design-recorded")]
public sealed record DesignRecorded(ItemId Item, int Generation, string ArmId, string Key, bool DocsOnlyMarker, IReadOnlyList<string>? Files = null) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>The session that holds the arm reports the step done. The saga moves to the next step of the plan (or completes), under a new arm.</summary>
[MessageIdentity("workitem.step-completed")]
public sealed record StepCompleted(ItemId Item, int Generation, string ArmId, string Key) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>What a budget message counts.</summary>
public enum BudgetKind
{
    /// <summary>A session or start of the step failed: against the failures of the phase.</summary>
    Failure,
    /// <summary>An execution was launched for the step: against the launches in a row of the phase (another phase starting begins the count again).</summary>
    Launch,
    /// <summary>A follow-up session ended without connecting or pushing: against the idle follow-ups of the item, in any phase.</summary>
    IdleFollowUp,
    /// <summary>A session connected or pushed: the idle follow-ups of the item start again.</summary>
    Registered,
}

/// <summary>
/// A count against one of the item's budgets (failures and launches per phase, idle follow-ups per item; v1 keeps them in memory, delivery-speed.md 18.1.3). The count that reaches its limit parks the item. Sent
/// under the arm of the step it concerns.
/// </summary>
[MessageIdentity("workitem.budget-spent")]
public sealed record BudgetSpent(ItemId Item, int Generation, string ArmId, string Key, BudgetKind Kind, string Reason) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>The lease of the arm is renewed (a heartbeat, a push): the step has that much longer before it counts as stalled.</summary>
[MessageIdentity("workitem.lease-renewed")]
public sealed record LeaseRenewed(ItemId Item, int Generation, string ArmId, string Key) : WorkItemInput(Item), IArmedInput, IKeyedInput;

/// <summary>The item's pull request has these changed files at this head. On the docs plan a file that is not documentation switches the item to the feature plan.</summary>
[MessageIdentity("workitem.pr-changed")]
public sealed record PullRequestChanged(ItemId Item, int Generation, string Key, int PullRequest, string Head, IReadOnlyList<string> Files) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>A check run of the merge commit's own Build: its name, status and conclusion (null while it has none).</summary>
public sealed record BuildCheck(string Name, string? Status, string? Conclusion);

/// <summary>
/// What was read of the merge commit after the merge: its Build's checks and the pull request's files (null when they could not be read). On the feature plan, when the build skipped its code checks or the files
/// are documentation, the item is settled as docs: there is no release to deliver.
/// </summary>
[MessageIdentity("workitem.merge-facts")]
public sealed record MergeFactsRead(ItemId Item, int Generation, string Key, IReadOnlyList<BuildCheck> Checks, IReadOnlyList<string>? PullRequestFiles) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>The item's pull request was merged (the delivery train's ItemMerged, routed by <see cref="WorkItemRouting"/>): the merge step is done whatever step the saga was on.</summary>
[MessageIdentity("workitem.merge-seen")]
public sealed record MergeSeen(ItemId Item, string Key, string Commit, int PullRequest) : WorkItemInput(Item), IKeyedInput;

/// <summary>The item reached an environment (the delivery train's DeliveredTo). Reaching the final environment starts the settle window.</summary>
[MessageIdentity("workitem.delivery-seen")]
public sealed record DeliverySeen(ItemId Item, string Key, string Environment, string Release) : WorkItemInput(Item), IKeyedInput;

/// <summary>The item is no longer in an environment (a rollback or a failed deployment): inside the settle window it returns to its Deliver step.</summary>
[MessageIdentity("workitem.delivery-lost")]
public sealed record DeliveryLost(ItemId Item, string Key, string Environment) : WorkItemInput(Item), IKeyedInput;

/// <summary>A person or the operator parks the item (park-item): the saga stops its lease and takes no step message until <see cref="ResumeWorkItem"/> or, where the engine expires parks (W9.10f), until the park ends (<paramref name="Minutes"/>, else <c>timing.parkHours</c>).</summary>
[MessageIdentity("workitem.park")]
public sealed record ParkWorkItem(ItemId Item, int Generation, string Key, string Reason, int Minutes = 0) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>Takes the item out of its park (unpark-item): the budgets of the phase start again and the step is armed anew.</summary>
[MessageIdentity("workitem.resume")]
public sealed record ResumeWorkItem(ItemId Item, int Generation, string Key) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// W9.10h: the item's issue was closed (a person closed it, or the board shows the card closed). Honoured only while no merge has happened and the merge step does not decide: an issue closed by the merge itself
/// ("Closes #N") reaches the saga as the merge, not as this. The instance then completes, so its slot, its place in the merge line and its card go.
/// </summary>
[MessageIdentity("workitem.item-closed")]
public sealed record ItemClosed(ItemId Item, string Key, string Source) : WorkItemInput(Item), IKeyedInput;

/// <summary>
/// W9.10h: the hold label the item carries now (the first label that is a hold: <c>hold:*</c> or a configured one), or null when it carries none. A hold parks the item (the label's name in the reason) and the label's
/// removal lifts that park, and only that one: a park for another reason stays a person's or the saga's.
/// </summary>
[MessageIdentity("workitem.hold-observed")]
public sealed record HoldObserved(ItemId Item, int Generation, string Key, string? Hold) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// W9.10h: a person moved the item's card (the projection's own moves are echoes and never reach the saga). <paramref name="Step"/> is the step whose column that is, null for a column of no step (Todo, Done, a hold column).
/// A move back to the column of an earlier step sends the item back to that step; a move to the step's own column or forward changes nothing (the saga follows the work, not the card).
/// </summary>
[MessageIdentity("workitem.card-moved")]
public sealed record CardMoved(ItemId Item, int Generation, string Key, string Column, StepKind? Step) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// The timer of a step's lease (a Wolverine scheduled message): <paramref name="Due"/> is the time it was scheduled for. It counts only when it names the current arm and the lease still ends at that time;
/// a renewed lease has a timer of its own.
/// </summary>
[MessageIdentity("workitem.lease-expired")]
public sealed record PhaseLeaseExpired(ItemId Item, int Generation, string ArmId, DateTimeOffset Due) : WorkItemInput(Item), IArmedInput;

/// <summary>The timer of the settle window: the item stayed delivered for the whole window and completes.</summary>
[MessageIdentity("workitem.settle-elapsed")]
public sealed record SettleWindowElapsed(ItemId Item, int Generation, string ArmId) : WorkItemInput(Item), IArmedInput;

/// <summary>What the saga decided, published once per decision. <paramref name="Sequence"/> counts the saga's decisions (a consumer that applied one skips it and the older ones).</summary>
[MessageIdentity("workitem.decided")]
public sealed record WorkItemDecided(ItemId Item, int Generation, long Sequence, string Action, string Plan, string? Step, string? ArmId, IReadOnlyList<string> Reasons, DateTimeOffset At) : ItemEvent(Item);

/// <summary>The actions of <see cref="WorkItemDecided"/>.</summary>
public static class WorkItemActions
{
    public const string Started = "started";
    public const string StepEntered = "step-entered";
    public const string PlanSwitched = "plan-switched";
    public const string LeaseExpired = "lease-expired";
    public const string Parked = "parked";
    public const string Resumed = "resumed";
    /// <summary>A person moved the card back to the column of an earlier step (W9.10h): the item returns to that step.</summary>
    public const string MovedBack = "moved-back";
    public const string Merged = "merged";
    public const string Settling = "settling";
    /// <summary>The settle window ended with the release not in prod (final environment before prod): the item stays Settling, in the Release Queue, until prod or the cap.</summary>
    public const string SettleHeld = "settle-held";
    /// <summary>The cap on the hold for prod ended with the release still not in prod; the reasons are the text and the release (in the final environment) that stayed behind.</summary>
    public const string SettleCapExpired = "settle-cap-expired";
    public const string Undelivered = "undelivered";
    public const string Reopened = "reopened";
    public const string Completed = "completed";
    /// <summary>The instance ended because the item moved to a plan this engine does not carry; v1 takes the item up again.</summary>
    public const string HandedBack = "handed-back";
    // the docs plan (W6.6)
    public const string Reviewed = "reviewed";
    public const string Verdict = "verdict";
    public const string Demoted = "demoted";
    public const string MergeRequested = "merge-requested";
    public const string MergeBlocked = "merge-blocked";
    // the feature plan (W7.3)
    public const string ChecksGreen = "checks-green";
    public const string ChecksRed = "checks-red";
    public const string FixForward = "fix-forward";
    public const string SlotGranted = "slot-granted";
    public const string Held = "held";
    public const string SessionLost = "session-lost";
    // the sweep's steps (W9.10f)
    public const string MasterGreen = "master-green";
    public const string MasterBroken = "master-broken";
    public const string MasterTimeout = "master-timeout";
    public const string ParkExpired = "park-expired";
    public const string HoldCleared = "hold-cleared";
    public const string OwnerReleased = "owner-released";
    public const string SettleRearmed = "settle-rearmed";
    // adoption (W9.10m)
    /// <summary>The saga took the item over from v1's state at a start (the state comment, the card, the pull request): the reasons say what was mapped or what changed.</summary>
    public const string Adopted = "adopted";
    /// <summary>W9.10n: a re-adoption found the item ended by v1 (settled, merged, finished) and closed the saga's open instance. Not one of the decisions the state comment's projection writes: the comment is where it came from.</summary>
    public const string AdoptedClosed = "adopted-closed";
    // the plant manager's actions (W9.10k)
    public const string OwnerReleaseAsked = "owner-release-asked";
    public const string Requeued = "requeued";
}

/// <summary>What the saga answered to one input.</summary>
public enum WorkItemOutcome
{
    /// <summary>The input changed the state or was a legal no-op of an open item (a renewal, a fact already known).</summary>
    Applied,
    /// <summary>There is no instance this message belongs to: the item never started, its instance completed, or the message names another generation. Ends quietly.</summary>
    NotFound,
    /// <summary>The message names an arm that is not the current one, or a step the item is no longer on: a late message of an earlier lease. Ignored.</summary>
    Stale,
    /// <summary>The business key was applied before.</summary>
    Duplicate,
    /// <summary>The input is valid but changes nothing (a start for an open item, a step message of a parked one).</summary>
    Refused,
}

/// <summary>The synchronous answer of the saga to an input (the decision verbs of the v2 engine answer a caller with it): the outcome, the item's place afterwards and why.</summary>
public sealed record WorkItemDecision(WorkItemOutcome Outcome, int Generation, string Plan, string? Step, string? ArmId, string Reason = "");

/// <summary>The timers an input made the saga ask for: the message and when it is due.</summary>
public sealed record WorkItemTimer(object Message, DateTimeOffset Due);

/// <summary>What one input made the saga do: the answer, the decisions to publish, the timers to schedule and the commands to send. Nothing here is sent until the state is saved.</summary>
public sealed record WorkItemResult(WorkItemDecision Decision, IReadOnlyList<WorkItemDecided> Decisions, IReadOnlyList<WorkItemTimer> Timers, IReadOnlyList<object>? Commands = null)
{
    public static WorkItemResult Quiet(WorkItemDecision d) => new(d, [], []);

    /// <summary>What the transition asks the outside to do (the merge call of the docs plan), published with the decisions after the state is saved.</summary>
    public IReadOnlyList<object> Sent => Commands ?? [];
}
