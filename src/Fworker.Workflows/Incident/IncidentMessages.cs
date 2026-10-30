using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Incident;

// W8.1 (#349): the messages of the IncidentSaga (docs/design/wolverine-factory.md 3.3). One saga per application and environment, keyed "deployable/environment" in lower case, which is also the key of the delivery
// train of that pair (TrainKey): the incident holds that train and asks it for the rollback. Everything that reaches the saga is an IncidentInput addressed by that key; the inputs that answer one incident name its
// generation (0 is "the current one"), and a message of an earlier incident finds another generation and is ignored. Delivery is at least once, so every input that changes the state has a business key and a repeated
// key does nothing. What the saga publishes: IncidentDecided (once per decision, for the incident timeline in the item history) and the commands of the incident (the issue, the hotfix item, the train's hold and
// rollback), all of them sent after the state is saved.

/// <summary>Any input of an incident saga. <paramref name="Key"/> is "deployable/environment", lower case.</summary>
public abstract record IncidentInput(string Key) : IMessage;

/// <summary>An input with a business key: the same key is applied once.</summary>
public interface IIncidentKeyed { string BusinessKey { get; } }

/// <summary>An input that answers one incident; 0 means the current one.</summary>
public interface IIncidentGeneration { int Generation { get; } }

/// <summary>An alert of the monitoring (the Azure Monitor webhook, W8.2) about the application in the environment. Suppressed while a deployment runs there; joins the open incident, or opens one.</summary>
[MessageIdentity("incident.alert")]
public sealed record AlertReceived(string Key, string AlertId, string Rule, string Severity, string Summary, DateTimeOffset At) : IncidentInput(Key), IIncidentKeyed
{
    public string BusinessKey => $"alert|{AlertId}";
}

/// <summary>One read of the environment's health: <paramref name="Status"/> is the health endpoint's overall status (Healthy, Degraded, Unhealthy) or "unreachable". The confirmation rule turns bad reads into an incident.</summary>
[MessageIdentity("incident.health")]
public sealed record HealthObserved(string Key, string Status, string Detail, DateTimeOffset At) : IncidentInput(Key), IIncidentKeyed
{
    public string BusinessKey => $"health|{At.UtcTicks}";
}

/// <summary>A deployment to the environment started, succeeded or failed (<paramref name="Phase"/> is "Started", "Succeeded" or "Failed"): the Octopus watcher's event, routed by <see cref="IncidentRouting"/>.</summary>
[MessageIdentity("incident.deployment")]
public sealed record DeploymentObserved(string Key, string DeploymentId, string Release, string Phase, DateTimeOffset At) : IncidentInput(Key), IIncidentKeyed
{
    public string BusinessKey => $"deployment|{DeploymentId}|{Phase}";
}

/// <summary>The read-only analyst's verdict on the incident (or a person's): "rollback", "fix-forward", "escalate" or "close" (no incident). Taken in the triage only.</summary>
[MessageIdentity("incident.triage-reported")]
public sealed record TriageReported(string Key, int Generation, string ReportId, string Verdict, string Summary) : IncidentInput(Key), IIncidentKeyed, IIncidentGeneration
{
    public string BusinessKey => $"triage|{Generation}|{ReportId}";
}

/// <summary>
/// The delivery train's answer to the rollback request (<see cref="Delivery.RollbackAnswered"/>, routed by <see cref="IncidentRouting"/>): <paramref name="Outcome"/> is "started" (the deployment of
/// <paramref name="ToRelease"/> is asked for), "done" (it succeeded), "refused" or "failed"; <paramref name="Code"/> says why a refusal or failure ("migration": a migration was added since the previous release, so a
/// rollback would be unsafe; "no-previous-release"; "not-permitted"; "unavailable").
/// </summary>
[MessageIdentity("incident.rollback-reported")]
public sealed record RollbackReported(string Key, int Generation, string Outcome, string Code, string? ToRelease, string Reason, DateTimeOffset At) : IncidentInput(Key), IIncidentKeyed, IIncidentGeneration
{
    public string BusinessKey => $"rollback|{Generation}|{Outcome}|{Code}|{ToRelease}";
}

/// <summary>The incident issue was opened (or found): its number.</summary>
[MessageIdentity("incident.issue-opened")]
public sealed record IssueOpened(string Key, int Generation, int Number) : IncidentInput(Key), IIncidentKeyed, IIncidentGeneration
{
    public string BusinessKey => $"issue|{Generation}";
}

/// <summary>The hotfix item exists (its issue is created and its saga is started on the hotfix plan): the train may now let its release through the hold.</summary>
[MessageIdentity("incident.hotfix-created")]
public sealed record HotfixCreated(string Key, int Generation, string Item) : IncidentInput(Key), IIncidentKeyed, IIncidentGeneration
{
    public string BusinessKey => $"hotfix|{Generation}|{Item}";
}

/// <summary>
/// What became of the hotfix item (routed by <see cref="IncidentRouting"/> from the item's saga and from the delivery train): <paramref name="Stage"/> is "merged", "delivered" (to this environment, in
/// <paramref name="Release"/>), "parked" or "handed-back".
/// </summary>
[MessageIdentity("incident.hotfix-progress")]
public sealed record HotfixProgress(string Key, string Item, string Stage, string Release, DateTimeOffset At) : IncidentInput(Key), IIncidentKeyed
{
    public string BusinessKey => $"hotfix-progress|{Item}|{Stage}|{Release}";
}

/// <summary>The owner's choice from Escalated: "rollback", "fix-forward", "fixed" (by hand: verify it) or "close". <paramref name="DecisionId"/> makes a repeated call one decision.</summary>
[MessageIdentity("incident.owner-decided")]
public sealed record OwnerDecided(string Key, int Generation, string DecisionId, string Decision, string By, string Note) : IncidentInput(Key), IIncidentKeyed, IIncidentGeneration
{
    public string BusinessKey => $"owner|{Generation}|{DecisionId}";
}

/// <summary>The timer of a stage (a Wolverine scheduled message): <paramref name="Due"/> is the time it was scheduled for. It counts only when it names the pending timer; a stage that has moved on made another.</summary>
[MessageIdentity("incident.timer")]
public sealed record IncidentTimerFired(string Key, int Generation, string Stage, DateTimeOffset Due) : IncidentInput(Key), IIncidentGeneration;

/// <summary>What the saga decided, published once per decision. <paramref name="Sequence"/> counts the decisions of the pair; the history writes one row per decision.</summary>
[MessageIdentity("incident.decided")]
public sealed record IncidentDecided(string Key, int Generation, long Sequence, string Action, string Status, IReadOnlyList<string> Reasons, DateTimeOffset At) : IEvent;

/// <summary>The actions of <see cref="IncidentDecided"/>.</summary>
public static class IncidentActions
{
    public const string Detected = "detected";
    public const string Joined = "joined";
    public const string Triaging = "triaging";
    public const string RollingBack = "rolling-back";
    public const string RollbackStarted = "rollback-started";
    public const string Fixing = "fixing";
    public const string HotfixCreated = "hotfix-created";
    public const string HotfixMerged = "hotfix-merged";
    public const string Verifying = "verifying";
    public const string Relapsed = "relapsed";
    public const string Escalated = "escalated";
    public const string Resolved = "resolved";
    public const string Suspect = "suspect";
}

/// <summary>Opens the incident issue (the owner is notified by it). The port finds an issue it opened before by the marker in its body, so a repeated command opens one.</summary>
[MessageIdentity("incident.open-issue")]
public sealed record OpenIncidentIssue(string Key, int Generation, string Title, string Body, string Label) : ICommand;

/// <summary>A comment on the incident issue; <paramref name="CommentKey"/> makes a repeated command one comment.</summary>
[MessageIdentity("incident.comment-issue")]
public sealed record CommentIncidentIssue(string Key, int Generation, int Number, string CommentKey, string Body) : ICommand;

/// <summary>Closes the incident issue with the note built from the history.</summary>
[MessageIdentity("incident.close-issue")]
public sealed record CloseIncidentIssue(string Key, int Generation, int Number, string Note) : ICommand;

/// <summary>Creates the hotfix item on the board and starts its saga on the hotfix plan (rank top). The port answers with <see cref="HotfixCreated"/>.</summary>
[MessageIdentity("incident.create-hotfix")]
public sealed record CreateHotfixItem(string Key, int Generation, string Title, string Body) : ICommand;

/// <summary>The incident asks for an analyst's verdict (a read-only session over /_healthcheck, /_telemetry, Application Insights and the recent releases). The answer is <see cref="TriageReported"/>.</summary>
[MessageIdentity("incident.triage-requested")]
public sealed record TriageRequested(string Key, int Generation, string Trigger, string Summary) : IEvent;
