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

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static WorkItemState FromJson(string json) => JsonSerializer.Deserialize<WorkItemState>(json, Json) ?? new WorkItemState();

    public WorkPlan CurrentPlan => WorkPlans.Get(Plan);
    public bool IsOpen => Status != WorkItemStatus.Completed;
    public ItemId ItemId => ItemId.Parse(Item);

    public int FailuresOf(StepKind step) => Failures.GetValueOrDefault(step.ToString());
    public int LaunchesOf(StepKind step) => Launches.GetValueOrDefault(step.ToString());
}
