using System.Text.Json;
using System.Text.Json.Serialization;
using Fworker.Contracts.Ids;

namespace Fworker.Workflows.WorkItem;

// W6.1: the state of one work item instance. It is the JSON document of a row of the WorkItems table (Fworker.Persistence), saved with a revision (the saga's IRevisioned): the plan and the step it is on,
// the arm of the step, the lease, the budgets, what the design decided, the keys already applied and what the last transition published. Plain mutable classes: System.Text.Json round-trips them, and a field
// a later wave adds is optional in a state saved before it (docs/design/wolverine-factory.md 4.9).

public static class WorkItemStatus
{
    public const string Open = "open";
    /// <summary>Delivered to the final environment; the item stays open for the settle window, in which an Undelivered returns it to its Deliver step.</summary>
    public const string Settling = "settling";
    public const string Completed = "completed";
}

public sealed class WorkItemState
{
    /// <summary>"owner/repo#N", lowercase.</summary>
    public string Item { get; set; } = "";
    /// <summary>1 for the first instance of the item; a completed item that is reopened takes the next.</summary>
    public int Generation { get; set; }
    public string Status { get; set; } = WorkItemStatus.Open;
    public string Plan { get; set; } = WorkPlans.Feature;
    public StepKind Step { get; set; }
    /// <summary>The arm of the current step: "generation.n". A message that answers an earlier arm is stale.</summary>
    public string ArmId { get; set; } = "";
    public int ArmSeq { get; set; }
    /// <summary>When the current step has no sign of life by, and null for a step without a lease, a parked item or an item that completed.</summary>
    public DateTimeOffset? LeaseDue { get; set; }
    public DateTimeOffset? SettleUntil { get; set; }
    public bool Parked { get; set; }
    public string ParkReason { get; set; } = "";
    /// <summary>The budgets (delivery-speed.md 18.1.3): failures and launches by phase (a step's name), idle follow-ups for the item.</summary>
    public Dictionary<string, int> Failures { get; set; } = [];
    public Dictionary<string, int> Launches { get; set; } = [];
    public int IdleFollowUps { get; set; }
    /// <summary>What the latest design section recorded: the marker present, or null before any design was recorded. The item is docs-only per the D4 port.</summary>
    public bool? DesignMarker { get; set; }
    public int PullRequest { get; set; }
    public string Head { get; set; } = "";
    public string MergeCommit { get; set; } = "";
    /// <summary>The release the item reached by environment (tdd, uat, prod).</summary>
    public Dictionary<string, string> Delivered { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>
    /// The sequence of the decision that put the item on its current step (W9.10h): the version the card projection carries for that step. A card event counts as a person's move only once the projection has applied
    /// this version (ProjectionVersions of the card); before that the card is not yet where the saga wants it and an event about it is the old column, not news. 0 in a state saved before it.
    /// </summary>
    public long CardSeq { get; set; }
    /// <summary>How the instance began: the source of the start, or "reopened: ...".</summary>
    public string Source { get; set; } = "";
    /// <summary>The business keys applied, the newest last; the oldest are dropped past <see cref="WorkItemPolicy.KeepKeys"/>.</summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>The number of decisions published so far.</summary>
    public long Sequence { get; set; }
    /// <summary>
    /// What the last applied input published, with the key of that input. A redelivery of the input (the handler died between the save and the publish) finds its key here and publishes the same again, so a
    /// decision or a timer is not lost to the window between the two; consumers skip a sequence number they applied.
    /// </summary>
    public string LastKey { get; set; } = "";
    public List<WorkItemDecided> Emitted { get; set; } = [];

    // ---- the docs plan (W6.6, Plans/DocsSteps.cs): optional fields, absent in a state saved before them ----------------------------------------------------------
    /// <summary>Review rounds recorded by step name (v1's reviews[column].round); entering the step starts them again.</summary>
    public Dictionary<string, int> ReviewRounds { get; set; } = [];
    /// <summary>The steps whose latest review ended REVIEW: COMPLETE (v1's reviews[column].ref); entering the step again clears it, because a new arm is new work.</summary>
    public List<string> ReviewsComplete { get; set; } = [];
    /// <summary>FAIL verdicts that sent the item back to Development (v1's demotions), against D5's FailVerdict row.</summary>
    public int Demotions { get; set; }
    /// <summary>Merges of master the item needed because its pull request fell out of date (D5's MechanicalFailure row).</summary>
    public int MechanicalFixes { get; set; }
    /// <summary>The head the latest PASS verdict covers ("" before one): the merge sends exactly this head.</summary>
    public string VerdictHead { get; set; } = "";
    public string VerdictSummary { get; set; } = "";
    /// <summary>Merge calls asked of the current Merge arm; the count that reaches <see cref="DocsPlanRules.MaxMergeAttempts"/> parks the item.</summary>
    public int MergeAttempt { get; set; }
    /// <summary>Answers of the merge calls of the current Merge arm that were about the pull request (W9.10o): not a failure of the gateway's own reading, no credential, the budget. These, not the calls, are counted against
    /// <see cref="DocsPlanRules.MaxMergeAttempts"/>; <see cref="MergeAttempt"/> still numbers the calls (the answer names its call) and bounds all of them by <see cref="DocsPlanRules.CallsPerAnswer"/> times the limit.</summary>
    public int MergeAnswers { get; set; }
    /// <summary>Updates of the branch made for this verdict (the script's verdict.updates), against timing.outOfDateMergeAttempts in the merge command.</summary>
    public int MergeUpdates { get; set; }
    /// <summary>An earlier merge call updated the branch from this head and its new head was not there yet.</summary>
    public string MergeUpdatedFrom { get; set; } = "";
    /// <summary>The merge call asked for and not yet answered; the answer (MergeAttempted) names its attempt. Null when none is waiting.</summary>
    public MergeRequestState? MergeRequest { get; set; }
    /// <summary>Why the last merge call was blocked: a decision is published when the reason changes, not at every call.</summary>
    public string MergeBlockedReason { get; set; } = "";
    /// <summary>When the next merge call is due after a blocked or transient answer (a scheduled MergeRetryDue).</summary>
    public DateTimeOffset? MergeRetryAt { get; set; }
    /// <summary>Whether the last applied input asked for the merge call: a redelivery of it asks again (the command is idempotent), a later renewal does not.</summary>
    public bool MergeRequestEmitted { get; set; }

    // ---- the feature plan (W7.3, Plans/FeatureSteps.cs): optional fields, absent in a state saved before them ------------------------------------------------------
    /// <summary>Fix-forward rounds used: CI was red on the head under test (D5's CiRed row, fixForwardMaxRounds). Green CI starts them again, as v1 does.</summary>
    public int FixForwardRounds { get; set; }
    /// <summary>The head the latest checks observation was about ("" before one) and what it said: pending, green or red. A new head makes both empty again.</summary>
    public string ChecksHead { get; set; } = "";
    public string ChecksState { get; set; } = "";
    public List<string> ChecksFailed { get; set; } = [];
    /// <summary>The Merge step waits for green checks on the head the PASS covers before it asks to merge ("the merge still needs both", delivery-speed.md 5.4).</summary>
    public bool MergeAwaitingChecks { get; set; }
    /// <summary>A merge request of this arm entered the repository's merge line: leaving the Merge step without a merge releases the place.</summary>
    public bool TrainEntered { get; set; }
    /// <summary>The footprint in force (D6): the Technical Design's <c>Files:</c> line, replaced by the pull request's changed files after the first push.</summary>
    public List<string> Footprint { get; set; } = [];
    public bool FootprintFromPullRequest { get; set; }
    /// <summary>The scheduler's slot the item holds ("" when it holds none), where it runs, and whether the item has ever held one (an owned item asks again at rank 2, delivery-speed.md 3.1).</summary>
    public string SlotId { get; set; } = "";
    public string SlotRuntime { get; set; } = "";
    public string SlotHost { get; set; } = "";
    public bool EverGranted { get; set; }
    /// <summary>The saga asked for a slot and has none: no session is offered, the step's lease is not running. <see cref="SlotWait"/> is why the scheduler says it waits ("" until it says).</summary>
    public bool SlotAsked { get; set; }
    /// <summary>The scheduler may know the item (a slot was asked for and not cleared since): ClearItem is sent once, not at every step the item leaves.</summary>
    public bool SlotKnown { get; set; }
    /// <summary>The time of the latest slot event applied (a grant, a wait, a lost session): an older one that arrives later is stale.</summary>
    public DateTimeOffset SlotStamp { get; set; }
    /// <summary>The slot requests made, the number in the key of the next: asking again in the same arm (after a lost session) is a new request.</summary>
    public int AskSeq { get; set; }
    public string SlotWait { get; set; } = "";
    /// <summary>The commands the last applied input asked for besides the merge call; kept so a redelivery of the input publishes them again, and emptied by the next input.</summary>
    public List<StateCommand> Outbox { get; set; } = [];

    // ---- the sweep's steps (W9.10f, Plans/SweepSteps.cs): optional fields, absent in a state saved before them ---------------------------------------------------------------
    /// <summary>When the current park ends (null: it does not, or the engine expires none). The park's timer is checked against it like a lease.</summary>
    public DateTimeOffset? ParkUntil { get; set; }
    /// <summary>
    /// The wait for master's CI after the merge (the settle's master-green, D5): "" before the merge or where nothing watches, "waiting" while the merge commit is not judged, "broken" (a gate failed, parked master-broken),
    /// "timeout" (parked master-green-timeout) and "green". A broken or timed-out wait goes on being watched: it ends when master is green for the merge, the park or not.
    /// </summary>
    public string MasterState { get; set; } = "";
    /// <summary>When the wait began, and when the failed jobs were last re-run (the timeout counts again from there; issue 79).</summary>
    public DateTimeOffset? MasterSince { get; set; }
    public DateTimeOffset? MasterRerunAt { get; set; }
    /// <summary>The runs whose failed jobs were re-run and the attempt that was: the verdict waits for a newer, completed attempt of each.</summary>
    public List<RerunRun> MasterRerunRuns { get; set; } = [];
    /// <summary>When the master-green wait times out (waiting only); the timer is checked against it.</summary>
    public DateTimeOffset? MasterDue { get; set; }
    public List<string> MasterFailed { get; set; } = [];
    /// <summary>The commit that made master green for the merge: the merge commit or a later one that contains it.</summary>
    public string MasterGreenVia { get; set; } = "";
    /// <summary>The lifts of parks by the saga itself, by park code (timing.autoUnparkMax per code).</summary>
    public Dictionary<string, int> AutoUnparks { get; set; } = [];
    /// <summary>When a held PASS's owner gives the item up, and whether it did (one release per park).</summary>
    public DateTimeOffset? OwnerIdleDue { get; set; }
    public bool OwnerReleased { get; set; }
    /// <summary>Who approved the pull request that cleared the inspector's hold.</summary>
    public string ApprovedBy { get; set; } = "";

    // ---- adoption (W9.10m, Adoption/*): optional field, absent in a state saved before it ------------------------------------------------------------------------------
    /// <summary>
    /// The revision of the item's state comment this state was last brought to by adoption (0: never adopted; the item was started by the saga itself). The rule of the reconciliation is that the comment wins at a start
    /// when its revision is newer than this one (Adoption/AdoptionReconciler.cs).
    /// </summary>
    public int CommentRev { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static WorkItemState FromJson(string json) => JsonSerializer.Deserialize<WorkItemState>(json, Json) ?? new WorkItemState();

    public WorkPlan CurrentPlan => WorkPlans.Get(Plan);
    public bool IsOpen => Status != WorkItemStatus.Completed;
    public ItemId ItemId => ItemId.Parse(Item);

    public int FailuresOf(StepKind step) => Failures.GetValueOrDefault(step.ToString());
    public int LaunchesOf(StepKind step) => Launches.GetValueOrDefault(step.ToString());
}

/// <summary>A merge call as the saga asked for it: the attempt number names the answer.</summary>
public sealed class MergeRequestState
{
    public int Attempt { get; set; }
    public int PullRequest { get; set; }
    public string Head { get; set; } = "";
    public int UpdatesSoFar { get; set; }
    public string UpdatedFrom { get; set; } = "";
}
