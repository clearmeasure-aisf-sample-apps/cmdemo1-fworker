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
