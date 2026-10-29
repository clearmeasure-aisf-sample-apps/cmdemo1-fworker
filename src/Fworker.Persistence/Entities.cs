namespace Fworker.Persistence;

// The rows of the factory database (W2.1, docs/design/wolverine-factory.md 4). Item keys are the qualified text of an ItemId ("owner/repo#N", lowercase).
// Every table is written only when something happens (an event, a command, a push) and read when someone asks. No table has a status column that a
// loop would scan, so a serverless database stays paused while the factory is idle. Times are UTC (datetimeoffset).

/// <summary>One event or command of one item instance: the audit trail, the flow metrics, the incident timeline and "where is item X" (4.7).</summary>
public class ItemHistoryEntry
{
    public long Id { get; set; }
    public string ItemKey { get; set; } = "";
    public int Generation { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>"event" or "command".</summary>
    public string Kind { get; set; } = "";
    /// <summary>The message type's stable identity.</summary>
    public string Type { get; set; } = "";
    /// <summary>The message as JSON, or null when nothing beyond the type matters.</summary>
    public string? Detail { get; set; }
    /// <summary>The session that caused or received it, when there is one.</summary>
    public string? SessionId { get; set; }
    /// <summary>Wolverine's message id: a redelivered message writes its row once.</summary>
    public Guid? MessageId { get; set; }
}

/// <summary>Maps a thing outside the factory (pull request, branch, head commit, agent id, release, deployment, task) to the item and generation (4.2).</summary>
public class CorrelationEntry
{
    /// <summary>"pr", "branch", "commit", "agent", "release", "deployment", "task" or another kind a source adds.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The qualified key: "owner/repo!N", "owner/repo:branch", "owner/repo@sha", "deployable@version", an agent or task id.</summary>
    public string Key { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public int Generation { get; set; }
    /// <summary>Which source wrote it: a handler, a verb, a naming convention, the Octopus watcher.</summary>
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The current generation of an item, so an event that names only the item reaches the right saga instance (3.1).</summary>
public class OpenInstance
{
    public string ItemKey { get; set; } = "";
    public int Generation { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    /// <summary>Null while the instance is open; set when the saga completes. The row stays so a reopen takes the next generation.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>The last version a projector applied to one target of one item; an older command is skipped (4.6).</summary>
public class ProjectionVersion
{
    public string ItemKey { get; set; } = "";
    /// <summary>"card", "labels", "comment" or another projected target.</summary>
    public string Target { get; set; } = "";
    public long AppliedVersion { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>A push to a live session that any node can serve to its long poll or stream (2, 4.5).</summary>
public class SessionMailboxEntry
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public string? ItemKey { get; set; }
    /// <summary>"checks.failed", "base.updated", "session.end" and the like.</summary>
    public string Kind { get; set; } = "";
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>After this a reader ignores the row and the cleanup (a Wolverine recurring job, W2.2) deletes it.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Set when a node handed the row to the session; null until then.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>The last good copy of a document the gateway reads from outside, kept so an unreachable source never stops it (system-gateway.md H2).</summary>
public class CatalogCopy
{
    /// <summary>The document's name, "system-catalog" for cmdemo1-system's system.json.</summary>
    public string Name { get; set; } = "";
    public string Json { get; set; } = "";
    /// <summary>SHA-256 of Json, hex: a refresh that changed nothing writes nothing.</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>The commit of the source the copy was read at, when known.</summary>
    public string? SourceSha { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>An item whose pull request merged into the default branch, as the delivery train takes it in (W5.0). One row per item and merge commit: the webhook and the board reconciler both derive it, the first claims the row.</summary>
public class MergedItem
{
    public string ItemKey { get; set; } = "";
    /// <summary>The merge commit, 40 hex characters, lower case.</summary>
    public string Commit { get; set; } = "";
    public int PullRequest { get; set; }
    /// <summary>The ItemMergedGates as JSON.</summary>
    public string GatesJson { get; set; } = "";
    /// <summary>The qualified dependencies as a JSON array of "owner/repo#N".</summary>
    public string DependenciesJson { get; set; } = "";
    /// <summary>"webhook" or "poll": which source derived it first.</summary>
    public string Source { get; set; } = "";
    public DateTimeOffset MergedAt { get; set; }
    /// <summary>The Wolverine message that claimed the row: a redelivery of it passes, another message is a duplicate.</summary>
    public Guid MessageId { get; set; }
}

/// <summary>Where an item has been delivered: the release it reached in an environment (system-gateway.md H5). Kept from DeliveredTo and Undelivered; answers the train's dependencies-delivered(env) slot.</summary>
public class DeliveredToEntry
{
    public string ItemKey { get; set; } = "";
    /// <summary>"tdd", "uat" or "prod".</summary>
    public string Environment { get; set; } = "";
    public string Release { get; set; } = "";
    public DateTimeOffset DeliveredAt { get; set; }
}

/// <summary>The state of one delivery train (W5.1): one row per deployable and environment, the key "deployable/environment". The state is a JSON document owned by Fworker.Workflows.Delivery.</summary>
public class DeliveryTrainRow
{
    public string Key { get; set; } = "";
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    public string StateJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One comparison of the shadow run (W5.2): what the delivery train decided (v2) next to what v1's promotion pass did for the same deployable, environment and release. Written when the
/// comparison is settled; read by the report. <see cref="Outcome"/> is match, v2-only, v1-only or differs.
/// </summary>
public class DeliveryShadowComparison
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Deployable { get; set; } = "";
    /// <summary>"uat" or "prod": the environment the decision is about.</summary>
    public string Environment { get; set; } = "";
    public string Release { get; set; } = "";
    public string Outcome { get; set; } = "";
    /// <summary>Why the outcome: the difference for "differs", what was missing for "v1-only" and "v2-only", empty for a match.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The normalized actions (promote, wait, follow, signoff-proceed, signoff-abort, signoff-wait), empty where that side decided nothing.</summary>
    public string V1Action { get; set; } = "";
    public string V2Action { get; set; } = "";
    /// <summary>Each side's decision as JSON (its own action name, reasons, items, task) and the time it was made.</summary>
    public string? V1Json { get; set; }
    public string? V2Json { get; set; }
    public DateTimeOffset? V1At { get; set; }
    public DateTimeOffset? V2At { get; set; }
    /// <summary>The train's side is a judgment of the watcher's replay of history, not of a live event: shown, and not counted by the exit gate.</summary>
    public bool Replay { get; set; }
}

/// <summary>
/// The state of one work item instance (W6.0 and W6.1, wolverine-factory.md 3.1): one row per item, holding its current generation. The state is a JSON document owned by Fworker.Workflows.WorkItem, saved
/// with a revision so that two writers cannot both win (the saga's <c>IRevisioned</c>). A completed item keeps its row (a reopened item takes the next generation and replaces the state); the columns beside
/// the document are the ones read without opening it: the count in flight per repository (the WIP intake rule) and "where is item X".
/// </summary>
public class WorkItemRow
{
    /// <summary>The qualified item, "owner/repo#N", lowercase.</summary>
    public string ItemKey { get; set; } = "";
    /// <summary>"owner/repo", lowercase: the per-repository WIP count reads by it.</summary>
    public string Repository { get; set; } = "";
    /// <summary>The generation of the instance in <see cref="StateJson"/>.</summary>
    public int Generation { get; set; }
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    /// <summary>"open", "settling" (delivered, inside the settle window) or "completed".</summary>
    public string Status { get; set; } = "";
    /// <summary>The plan the item follows now ("feature", "docs", "hotfix") and its current step.</summary>
    public string Plan { get; set; } = "";
    public string Step { get; set; } = "";
    public string StateJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The state of one merge train (W7.2, wolverine-factory.md 3.2): one row per repository, the key "owner/repo" in lower case. The state is a JSON document owned by Fworker.Workflows.Merge (the line of
/// pull requests that asked to be merged, the turn that was granted, the last merges), saved with a revision so that two writers cannot both win.
/// </summary>
public class MergeTrainRow
{
    public string Key { get; set; } = "";
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    public string StateJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The capacity scheduler's ledger (W7.0 and W7.1, wolverine-factory.md 3.2): one row per factory, the key "factory". The state is a JSON document owned by Fworker.Workflows.Capacity (the slots granted per runtime
/// and host, the waiting requests, the items in flight with their footprints, the file-overlap holds, the hosts as last reported), saved with a revision so that two writers cannot both win. One writer at a time is
/// the design (the scheduler's queue is sequential); the revision is the backstop while a node changes.
/// </summary>
public class CapacityLedgerRow
{
    public string Key { get; set; } = "";
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    public string StateJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The state of one QC gate (W9.0 for W9.1, refactor R9): one row per repository and pull request, the key "owner/repo!N" in lower case. The state is a JSON document owned by Fworker.Workflows.Qc (the
/// environment record, its attempt and assignment, the verdict), saved with a revision so that two writers cannot both win. The columns beside the document are the ones read without opening it: the pull
/// requests of one repository that are not over ("Waiting for PR QC") and the item the pull request belongs to.
/// </summary>
public class QcEnvironmentRow
{
    /// <summary>"owner/repo!N", lowercase (the correlation key of a pull request).</summary>
    public string PullRequestKey { get; set; } = "";
    /// <summary>"owner/repo", lowercase.</summary>
    public string Repository { get; set; } = "";
    /// <summary>The qualified item, "owner/repo#N", when the pull request is an item's.</summary>
    public string? ItemKey { get; set; }
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    /// <summary>The record's state (requested, assigned, ready, passed, failed, torn-down and the like), so that a list of the ones not over opens no document.</summary>
    public string Status { get; set; } = "";
    public string StateJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The transcript of one runtime worker session (W9.0 for W9.8, refactor R9): one row per session name, with the item qualified. The redacted lines the container printed are kept as a JSON array, the last
/// bytes only (the store's own limit), saved at most once a minute while lines arrive and at once when the session ends. It replaces the ref store of the plant snapshot (a ciphertext commit in a public repository).
/// </summary>
public class TranscriptRow
{
    /// <summary>The session's name (letters, digits, '.', '_' and '-').</summary>
    public string Name { get; set; } = "";
    /// <summary>The qualified item, "owner/repo#N", when the session works one.</summary>
    public string? ItemKey { get; set; }
    public string Runtime { get; set; } = "";
    public string Phase { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastLineAt { get; set; }
    /// <summary>Null while the session runs.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Counts the saves; the concurrency token: a writer that read an older revision fails and runs again on the new state.</summary>
    public int Revision { get; set; }
    public long Bytes { get; set; }
    /// <summary>The lines ever received, and the ones dropped from the front to keep the limit.</summary>
    public long LinesTotal { get; set; }
    public long LinesDropped { get; set; }
    /// <summary>The kept lines as a JSON array of strings.</summary>
    public string LinesJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}
