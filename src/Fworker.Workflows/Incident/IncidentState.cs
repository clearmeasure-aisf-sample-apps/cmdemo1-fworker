using System.Text.Json;
using System.Text.Json.Serialization;
using Fworker.Contracts.Ids;

namespace Fworker.Workflows.Incident;

// W8.1: the state of one incident saga, the (deployable, environment) pair. It is the JSON document of a row of the Incidents table (Fworker.Persistence), saved with a revision so that two writers cannot both
// win. Plain mutable classes: System.Text.Json round-trips them, and a field a later wave adds is optional in a state saved before it.

public static class IncidentStatus
{
    /// <summary>No incident is open (the pair has had none, or the last one is resolved).</summary>
    public const string Quiet = "quiet";
    public const string Triaging = "triaging";
    public const string RollingBack = "rolling-back";
    public const string Fixing = "fixing";
    public const string Verifying = "verifying";
    public const string Escalated = "escalated";
}

/// <summary>What the saga asks the outside to do after a transition. Kept in the state until the transition is published (a handler that died between the save and the publish publishes it again).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OpenIssueEffect), "open-issue")]
[JsonDerivedType(typeof(CommentEffect), "comment")]
[JsonDerivedType(typeof(CloseIssueEffect), "close-issue")]
[JsonDerivedType(typeof(HoldEffect), "hold")]
[JsonDerivedType(typeof(RollbackEffect), "rollback")]
[JsonDerivedType(typeof(HotfixEffect), "hotfix")]
[JsonDerivedType(typeof(TriageEffect), "triage")]
[JsonDerivedType(typeof(TimerEffect), "timer")]
public abstract record IncidentEffect;

public sealed record OpenIssueEffect(string Title, string Body) : IncidentEffect;
public sealed record CommentEffect(string CommentKey, string Body) : IncidentEffect;
public sealed record CloseIssueEffect(string Note) : IncidentEffect;
/// <summary>The train is held (or released); <paramref name="Allow"/> are the items whose release passes the hold.</summary>
public sealed record HoldEffect(bool Held, string Reason, List<string> Allow) : IncidentEffect;
public sealed record RollbackEffect(string Reason) : IncidentEffect;
public sealed record HotfixEffect(string Title, string Body) : IncidentEffect;
public sealed record TriageEffect(string Trigger, string Summary) : IncidentEffect;
public sealed record TimerEffect(string Stage, DateTimeOffset Due) : IncidentEffect;

public sealed class IncidentState
{
    /// <summary>"deployable/environment", lower case.</summary>
    public string Key { get; set; } = "";
    public string Deployable { get; set; } = "";
    public string Environment { get; set; } = "";
    /// <summary>0 before the first incident of the pair, then the number of the incident (the current one, or the last).</summary>
    public int Generation { get; set; }
    public string Status { get; set; } = IncidentStatus.Quiet;
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? StatusSince { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>What opened the incident: "alert", "health" or "deployment-failed".</summary>
    public string Trigger { get; set; } = "";
    /// <summary>The signals that joined the open incident.</summary>
    public int Joined { get; set; }
    /// <summary>The incident's story so far, one line per decision (the last <see cref="KeepTimeline"/>): the note that closes the issue is built from it.</summary>
    public List<string> Timeline { get; set; } = [];
    public int IssueNumber { get; set; }

    // ---- what the saga knows of the environment ------------------------------------------------------------------------------------------------------
    /// <summary>A deployment to the environment is running (its start was seen and its end not): health reads and alerts are suppressed. <see cref="DeployingId"/> is that deployment.</summary>
    public bool Deploying { get; set; }
    public string DeployingId { get; set; } = "";
    public DateTimeOffset? DeployingSince { get; set; }
    /// <summary>The release that runs in the environment (the newest deployment that succeeded) and when it was deployed; the one before it.</summary>
    public string Release { get; set; } = "";
    public DateTimeOffset? ReleaseAt { get; set; }
    public string PreviousRelease { get; set; } = "";
    /// <summary>The confirmation rule: bad reads in a row (a good read ends the streak) and when the streak began.</summary>
    public int SuspectReads { get; set; }
    public DateTimeOffset? SuspectSince { get; set; }

    // ---- the open incident ---------------------------------------------------------------------------------------------------------------------------
    public bool RollbackTried { get; set; }
    /// <summary>The release the rollback deploys (known when the train says it started), and what the train answered last.</summary>
    public string RollbackTo { get; set; } = "";
    public string HotfixItem { get; set; } = "";
    public bool HotfixMerged { get; set; }
    public bool HotfixDelivered { get; set; }
    public string HotfixRelease { get; set; } = "";
    /// <summary>The verification: since when the environment has been Healthy without a bad read, and the bad reads in a row. A bad streak at the confirmation count is a relapse.</summary>
    public DateTimeOffset? HealthySince { get; set; }
    public int BadReads { get; set; }
    public int Relapses { get; set; }
    public string EscalationReason { get; set; } = "";
    /// <summary>The one pending timer: its stage and when it is due (null: none). A timer that fires with another due time is stale.</summary>
    public string TimerStage { get; set; } = "";
    public DateTimeOffset? TimerDue { get; set; }

    // ---- bookkeeping ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>The business keys applied, the newest last.</summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>The number of decisions published so far (across incidents of the pair).</summary>
    public long Sequence { get; set; }
    /// <summary>What the last applied input published (its key, decisions and effects): a redelivery of that input publishes the same again.</summary>
    public string LastKey { get; set; } = "";
    public List<IncidentDecided> Emitted { get; set; } = [];
    public List<IncidentEffect> Outbox { get; set; } = [];

    public const int KeepKeys = 100, KeepTimeline = 40;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static IncidentState FromJson(string json) => JsonSerializer.Deserialize<IncidentState>(json, Json) ?? new IncidentState();

    public static IncidentState New(string key)
    {
        var (d, e) = Delivery.TrainKey.Split(key);
        return new IncidentState { Key = key, Deployable = d, Environment = e };
    }

    public bool IsOpen => Status != IncidentStatus.Quiet;

    /// <summary>The key of the incident's timeline in the item history ("incident:deployable/environment").</summary>
    public static string HistoryKey(string key) => "incident:" + key;

    /// <summary>The hotfix item as the table's column keeps it: null when there is none.</summary>
    public string? HotfixColumn => HotfixItem.Length == 0 ? null : HotfixItem;

    public ItemId? HotfixItemId => ItemId.TryParse(HotfixItem, out var id) ? id : null;
}
