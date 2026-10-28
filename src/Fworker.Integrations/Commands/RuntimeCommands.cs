using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.Commands;

// W3.1: the runtime start, the runtime stop and the Octopus runbook run as v2 integration commands (docs/design/fworker-wolverine-implementation.md W3).
// A command is dispatched through the Wolverine outbox, run by a handler built on IntegrationCommandHandler (classification, idempotency key), and its outcome
// goes back to the RuntimeManager as a result message. v1 does none of this: RuntimeManager calls the adapters itself unless the environment's engine is V2.

/// <summary>The runbook run itself (Octopus). Return a result for an answer; throw for a transport failure (it is classified).</summary>
public interface IRunbookRunner
{
    Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct);
}

/// <summary>A worker's execution was started on its runtime: the manager records the execution and its page.</summary>
[MessageIdentity("runtime.session-started")]
public sealed record RuntimeSessionStarted(string IdempotencyKey, string Runtime, string WorkerName, int Item, string Phase, string Execution, string Url);

/// <summary>A start was refused or kept failing. <see cref="Kind"/> tells the manager whether it is no failure of the item (RateLimited: no capacity, quota) or one (Permanent, Transient).</summary>
[MessageIdentity("runtime.start-failed")]
public sealed record RuntimeStartFailed(string IdempotencyKey, string Runtime, string WorkerName, int Item, string Phase, FailureKind Kind, string Error);

/// <summary>A stop was done, or failed for good. A stop is idempotent; a stop that failed is the manager's log line only.</summary>
[MessageIdentity("runtime.session-stopped")]
public sealed record RuntimeSessionStopped(string IdempotencyKey, string Runtime, string WorkerName, bool Ok, string Error);

/// <summary>A runbook run was queued (<see cref="Ok"/>, with the execution and its page) or refused.</summary>
[MessageIdentity("runtime.runbook-run-finished")]
public sealed record RunbookRunFinished(string IdempotencyKey, string Project, string Runbook, bool Ok, string Execution, string Url, FailureKind Kind, string Error);

/// <summary>
/// What a handler produced: the <see cref="Result"/> of the call, the <see cref="Message"/> to send back (null while the command will be retried) and the
/// <see cref="Failed"/> message for the saga when the failure is final. <see cref="Retry"/> says the caller (Wolverine) must run the command again.
/// </summary>
public sealed record RuntimeOutcome(CommandResult Result, object? Message, IntegrationFailed? Failed)
{
    public bool Retry => Message == null && !Result.Ok;
}

public sealed class StartRuntimeSessionHandler : IntegrationCommandHandler<StartRuntimeSession>
{
    readonly ICommandPort adapters;
    public StartRuntimeSessionHandler(IIdempotencyLedger ledger, ICommandPort adapters) : base(ledger) { this.adapters = adapters; }

    protected override Task<CommandResult> Execute(StartRuntimeSession c, CancellationToken ct) => adapters.SendAsync(c, ct);

    public async Task<RuntimeOutcome> Process(StartRuntimeSession c, CancellationToken ct = default)
    {
        var r = await Handle(c, ct);
        if (r.Ok) return new(r, new RuntimeSessionStarted(c.IdempotencyKey, c.Runtime, c.WorkerName, c.Item, c.Phase, r.Execution, r.Url), null);
        // a start that waits for capacity or a quota is reported to the manager at once (it queues the item, no failure); a transient one is retried by the caller
        if (r.Failure == FailureKind.RateLimited) return new(r, new RuntimeStartFailed(c.IdempotencyKey, c.Runtime, c.WorkerName, c.Item, c.Phase, r.Failure, r.Error), null);
        var failed = FailureMessage(c, r);
        return new(r, failed == null ? null : new RuntimeStartFailed(c.IdempotencyKey, c.Runtime, c.WorkerName, c.Item, c.Phase, r.Failure, r.Error), failed);
    }
}

public sealed class StopRuntimeSessionHandler : IntegrationCommandHandler<StopRuntimeSession>
{
    readonly ICommandPort adapters;
    public StopRuntimeSessionHandler(IIdempotencyLedger ledger, ICommandPort adapters) : base(ledger) { this.adapters = adapters; }

    protected override Task<CommandResult> Execute(StopRuntimeSession c, CancellationToken ct) => adapters.SendAsync(c, ct);

    public async Task<RuntimeOutcome> Process(StopRuntimeSession c, CancellationToken ct = default)
    {
        var r = await Handle(c, ct);
        var failed = FailureMessage(c, r);
        return new(r, r.Ok || failed != null ? new RuntimeSessionStopped(c.IdempotencyKey, c.Runtime, c.WorkerName, r.Ok, r.Error) : null, failed);
    }
}

public sealed class RunRunbookHandler : IntegrationCommandHandler<RunRunbook>
{
    readonly IRunbookRunner runner;
    public RunRunbookHandler(IIdempotencyLedger ledger, IRunbookRunner runner) : base(ledger) { this.runner = runner; }

    protected override Task<CommandResult> Execute(RunRunbook c, CancellationToken ct) => runner.RunAsync(c, ct);

    public async Task<RuntimeOutcome> Process(RunRunbook c, CancellationToken ct = default)
    {
        var r = await Handle(c, ct);
        var failed = FailureMessage(c, r);
        return new(r, r.Ok || failed != null ? new RunbookRunFinished(c.IdempotencyKey, c.Project, c.Runbook, r.Ok, r.Execution, r.Url, r.Failure, r.Error) : null, failed);
    }
}

/// <summary>The idempotency keys of the runtime commands: one start per worker name (a name is never reused), one stop per name, one runbook run per caller key.</summary>
public static class RuntimeKeys
{
    public static string Start(string runtime, string workerName) => IdempotencyKeys.For("StartRuntimeSession", runtime, workerName);
    public static string Stop(string runtime, string workerName) => IdempotencyKeys.For("StopRuntimeSession", runtime, workerName);
    public static string Runbook(string project, string runbook, string environment, string callerKey) => IdempotencyKeys.For("RunRunbook", project, runbook, environment, callerKey);
}

/// <summary>Thrown by the host's handler when a command failed in a way that is tried again (transient, or rate limited without a message to send): Wolverine's failure rule retries it.</summary>
public sealed class CommandRetryException(string message) : Exception(message);
