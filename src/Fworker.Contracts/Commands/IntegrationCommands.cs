namespace Fworker.Contracts.Commands;

// The integration-command contracts of seam S2 (docs/design/fworker-wolverine-refactor.md 3 and 4, task W1.5): one record per external side effect the
// gateway issues to GitHub, Octopus or a worker runtime. Plain data with no behaviour; a command port (ICommandPort) executes them or records them.
// IdempotencyKey is the key a v2 outbox deduplicates on: the same key is the same side effect, so a replay does nothing twice.

/// <summary>The systems a command reaches.</summary>
public enum CommandTarget { GitHub, Octopus, Runtime }

/// <summary>One external side effect. <see cref="Name"/> is the stable name of the command (the golden comparison and the outbox use it, so it never changes).</summary>
public abstract record IntegrationCommand(string IdempotencyKey)
{
    public abstract string Name { get; }
    public abstract CommandTarget Target { get; }
}

/// <summary>Starts a worker on a runtime (RuntimeManager.Provision): the answer is a <see cref="CommandResult"/>; v2 turns it into SessionStarted or StartFailed.</summary>
public sealed record StartRuntimeSession(string IdempotencyKey, string Runtime, string WorkerName, int Item, string Phase, string ModelProfile, string Nonce, string Role = "") : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "StartRuntimeSession";
    public override CommandTarget Target => CommandTarget.Runtime;
}

/// <summary>Stops a worker's execution on its runtime (idempotent).</summary>
public sealed record StopRuntimeSession(string IdempotencyKey, string Runtime, string WorkerName, string Reason) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "StopRuntimeSession";
    public override CommandTarget Target => CommandTarget.Runtime;
}

/// <summary>Runs an Octopus runbook (the octopus runtime's starts and overflow runs).</summary>
public sealed record RunRunbook(string IdempotencyKey, string Project, string Runbook, string Environment, IReadOnlyDictionary<string, string> Variables) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "RunRunbook";
    public override CommandTarget Target => CommandTarget.Octopus;
}

/// <summary>Adds one line to an item's journal (the ledger of Add-Journal), as a comment on the work item.</summary>
public sealed record AppendJournalLine(string IdempotencyKey, int Item, string Line) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "AppendJournalLine";
    public override CommandTarget Target => CommandTarget.GitHub;
}

/// <summary>Writes the item's state comment (the format Get-State reads).</summary>
public sealed record WriteStateComment(string IdempotencyKey, int Item, string Body) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "WriteStateComment";
    public override CommandTarget Target => CommandTarget.GitHub;
}

/// <summary>Moves an item's card to a board column.</summary>
public sealed record MoveBoardCard(string IdempotencyKey, int Item, string Column) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "MoveBoardCard";
    public override CommandTarget Target => CommandTarget.GitHub;
}

/// <summary>How the one that retries a failed command should treat it.</summary>
public enum FailureKind { None, Transient, RateLimited, Permanent, NotSupported }

/// <summary>The outcome of a command. <see cref="Execution"/> and <see cref="Url"/> are filled by a runtime start (the execution id and its page).</summary>
public sealed record CommandResult(bool Ok, FailureKind Failure = FailureKind.None, string Error = "", string Execution = "", string Url = "")
{
    public static CommandResult Success(string execution = "", string url = "") => new(true, FailureKind.None, "", execution, url);
    public static CommandResult Failed(FailureKind kind, string error) => new(false, kind, error);
}
