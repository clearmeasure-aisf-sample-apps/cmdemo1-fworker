using Fworker.Contracts.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

// The close of a finished item's issue. v1's settle closes the issue with the move to Done (RuleKit.CloseIssue, as factory-worker-author[bot], state_reason completed); the docs plan's pull request carries no closing
// keyword, so on v2 nothing closed it (2026-11-01, item #556 of the work-orders repository: the card was in Done and the issue stayed open). The saga's Completed decision is the one place every way of finishing meets
// (the saga's own merge, a merge by a person, the settle window), so the close follows it: the decision becomes a CloseWorkItemIssue on a queue of its own, run through the integration-command handler (ledger key per
// item and generation, an issue that is closed already is left as it is), and a fault that a later try gets past is scheduled again, never given up.

/// <summary>The close could not be made now (GitHub down or limited, the gateway without a credential): the command is tried again later by the handler's policy.</summary>
public sealed class IssueCloseRetryException(CloseWorkItemIssue command, string why)
    : Exception($"the issue of {command.Item} was not closed: {why}");

/// <summary>What closes the issue. Without a credential (<c>handler</c> null) nothing can be closed: the command is retried until the gateway has one, as every other write of the saga's projections is not lost.</summary>
public sealed class IssueCloseService(CloseIssueHandler? handler, Action<string>? log = null)
{
    readonly Action<string> log = log ?? (m => Console.Error.WriteLine(m));

    public async Task Run(CloseWorkItemIssue c, CancellationToken ct)
    {
        if (handler is null) throw new IssueCloseRetryException(c, "the gateway has no GitHub credential");
        CommandResult r;
        try { r = await handler.Process(c, ct); }
        catch (NoInstallationException e) { log($"issue of {c.Item}: {e.Message}; not closed"); return; }   // for good: the owner has no installation
        if (r.Ok) return;
        if (r.Failure is FailureKind.Transient or FailureKind.RateLimited) throw new IssueCloseRetryException(c, r.Error);
        log($"issue of {c.Item} not closed: {r.Error}");   // refused for good (the ledger keeps the answer): a retry says the same
    }
}

/// <summary>The handlers of the close. Public because Wolverine's generated code refers to them.</summary>
public static class IssueCloseHandler
{
    public const string Queue = "issue-close";

    public static readonly TimeSpan[] RetryPauses = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60)];

    /// <summary>The completion of an item goes on to the queue of the close; every other decision ends here.</summary>
    public static OutgoingMessages Handle(WorkItemDecided m, DocsProjection projection)
    {
        var o = new OutgoingMessages();
        if (projection.CloseFor(m) is { } close) o.Add(close);
        return o;
    }

    /// <summary>A close that threw is scheduled again, not held on the queue (which runs one message at a time): after 30 s, 2, 10 and 60 minutes, then every hour for ever. Never the dead letters: nothing replays them and the issue would stay open.</summary>
    public static void Configure(HandlerChain chain)
    {
        if (chain.MessageType != typeof(CloseWorkItemIssue)) return;
        chain.OnException<IssueCloseRetryException>().ScheduleRetry(RetryPauses).Then.ScheduleRetryIndefinitely(TimeSpan.FromHours(1));
    }

    public static Task Handle(CloseWorkItemIssue m, IssueCloseService service, CancellationToken ct) => service.Run(m, ct);
}
