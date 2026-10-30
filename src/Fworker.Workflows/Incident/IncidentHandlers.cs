using System.Text.Json;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Persistence;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.ErrorHandling;

namespace Fworker.Workflows.Incident;

// W8.1: the Wolverine side of the IncidentSaga. The handlers are static and public because Wolverine's generated code refers to them; the gateway attaches them only where the engine acts (Engine.V2Acts, IncidentWiring).
// Every input of the saga goes to one handler that applies it (IncidentService) and returns what the saga decided, the messages it sends and its timer as the messages to publish: they leave through the outbox after
// the handler, and a handler that dies between the save and the publish is run again by Wolverine, which finds its own transition as the last one and publishes it again (IncidentSaga).

public static class IncidentHandlers
{
    public static Task<OutgoingMessages> Handle(AlertReceived m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(HealthObserved m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(DeploymentObserved m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(TriageReported m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(RollbackReported m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(IssueOpened m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(HotfixCreated m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(HotfixProgress m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(OwnerDecided m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(IncidentTimerFired m, IncidentService svc, CancellationToken ct) => Run(m, svc, ct);

    public static async Task<OutgoingMessages> Run(IncidentInput input, IncidentService svc, CancellationToken ct) => Outgoing(await svc.Handle(input, ct));

    /// <summary>The messages of a result: the decisions, the commands (the issue, the hotfix, the train), then the timer scheduled for its due time.</summary>
    public static OutgoingMessages Outgoing(IncidentResult result)
    {
        var o = new OutgoingMessages();
        foreach (var d in result.Decisions) o.Add(d);
        foreach (var m in result.Messages) o.Add(m);
        foreach (var t in result.Timers) o.Add(t.Message.ScheduledAt(t.Due));
        return o;
    }
}

/// <summary>
/// Which incident a fact concerns. The Octopus watcher's deployment events reach the incident of every watched environment of the deployables that have an Octopus project (they name no deployable, as for the
/// trains); the hotfix item's events find their incident through the table's index; the train's answer to a rollback names its incident.
/// </summary>
public sealed class IncidentRouter(IncidentSettings settings, IDeliveryPolicies policies, IIncidentStore store, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<IncidentInput> Route(OctopusDeploymentChanged e)
    {
        var now = clock();
        var at = e.At is { } t && t < now ? t : now;   // (never ahead of the saga's clock: clock skew between Octopus and the gateway)
        return policies.ForOctopusEvents().Where(p => settings.Watches(p.Environment) && string.Equals(p.KeyOf(e.Environment), p.Environment, StringComparison.Ordinal))
            .Select(p => (IncidentInput)new DeploymentObserved(TrainKey.Of(p.Deployable, p.Environment), e.DeploymentId, e.Version, e.Phase.ToString(), at)).ToList();
    }

    /// <summary>The item reached an environment: the hotfix of the open incident of that environment is delivered.</summary>
    public async Task<IReadOnlyList<IncidentInput>> Route(DeliveredTo e, CancellationToken ct)
    {
        var inputs = new List<IncidentInput>();
        foreach (var key in await store.OpenOfHotfix(e.Item.ToString(), ct))
            if (string.Equals(TrainKey.Split(key).Environment, e.Environment, StringComparison.OrdinalIgnoreCase))
                inputs.Add(new HotfixProgress(key, e.Item.ToString(), "delivered", e.Release, clock()));
        return inputs;
    }

    /// <summary>What the item's saga decided about a hotfix item: merged, parked or handed back to v1.</summary>
    public async Task<IReadOnlyList<IncidentInput>> Route(WorkItemDecided d, CancellationToken ct)
    {
        if (!string.Equals(d.Plan, WorkPlans.Hotfix, StringComparison.OrdinalIgnoreCase)) return [];
        var stage = d.Action switch { WorkItemActions.Merged => "merged", WorkItemActions.Parked => "parked", WorkItemActions.HandedBack => "handed-back", _ => "" };
        if (stage.Length == 0) return [];
        var inputs = new List<IncidentInput>();
        foreach (var key in await store.OpenOfHotfix(d.Item.ToString(), ct)) inputs.Add(new HotfixProgress(key, d.Item.ToString(), stage, "", d.At));
        return inputs;
    }

    public static IncidentInput Route(RollbackAnswered a) => new RollbackReported(a.Key, a.Generation, a.Outcome, a.Code, a.ToRelease, a.Reason, a.At);
}

/// <summary>The router's handlers: each fact becomes the inputs of its incidents, published through the outbox. Public because Wolverine's generated code refers to them.</summary>
public static class IncidentRouting
{
    public static OutgoingMessages Handle(OctopusDeploymentChanged m, IncidentRouter r) => All(r.Route(m));
    public static async Task<OutgoingMessages> Handle(DeliveredTo m, IncidentRouter r, CancellationToken ct) => All(await r.Route(m, ct));
    public static async Task<OutgoingMessages> Handle(WorkItemDecided m, IncidentRouter r, CancellationToken ct) => All(await r.Route(m, ct));
    public static OutgoingMessages Handle(RollbackAnswered m) => All([IncidentRouter.Route(m)]);

    static OutgoingMessages All(IReadOnlyList<IncidentInput> inputs)
    {
        var o = new OutgoingMessages();
        foreach (var i in inputs) o.Add(i);
        return o;
    }
}

/// <summary>Where the incident messages travel in a host: the inputs one at a time (a durable queue: a signal survives a restart), the issue and hotfix commands one at a time and in order (the comment after the open).</summary>
public static class IncidentTopology
{
    public const string InputQueue = "incidents";
    public const string IssueQueue = "incident-issues";

    public static IEnumerable<Type> Inputs => typeof(IncidentInput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IncidentInput).IsAssignableFrom(t));

    public static readonly Type[] IssueCommands = [typeof(OpenIncidentIssue), typeof(CommentIncidentIssue), typeof(CloseIncidentIssue), typeof(CreateHotfixItem)];

    public static void Route(WolverineOptions w)
    {
        foreach (var t in Inputs) w.PublishMessage(t).ToLocalQueue(InputQueue);
        w.LocalQueue(InputQueue).UseDurableInbox().Sequential();
        foreach (var t in IssueCommands) w.PublishMessage(t).ToLocalQueue(IssueQueue);
        w.LocalQueue(IssueQueue).UseDurableInbox().Sequential();
        // two writers on one incident more often than the service's own retries allow: the input is run again after a pause; GitHub not answering is tried again with growing pauses and then every few minutes
        w.Policies.OnException<IncidentConflictException>().RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)).Then.MoveToErrorQueue();
        w.Policies.OnException<IncidentIssueFault>().RetryWithCooldown(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)).Then.ScheduleRetry(TimeSpan.FromMinutes(5));
    }
}

/// <summary>The timeline of an incident in the item history ("incident:deployable/environment"): one row per decision, written once per envelope. Public because Wolverine's generated code refers to it.</summary>
public static class IncidentHistoryHandler
{
    public static async Task Handle(IncidentDecided m, IncidentHistory history, Envelope envelope, CancellationToken ct) => await history.Record(m, envelope.Id, ct);
}

public sealed class IncidentHistory(Func<FactoryDbContext> open)
{
    public const string HistoryType = "incident.decided";

    public async Task Record(IncidentDecided m, Guid messageId, CancellationToken ct = default)
    {
        await using var db = open();
        // written by an earlier attempt of this message (the unique (MessageId, Kind) index): nothing to add. Any other fault is not swallowed: the message is run again.
        if (await db.ItemHistory.AnyAsync(x => x.MessageId == messageId && x.Kind == "event", ct)) return;
        var detail = JsonSerializer.Serialize(new { m.Sequence, m.Action, m.Status, m.Reasons });
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = IncidentState.HistoryKey(m.Key), Generation = m.Generation, At = m.At, Kind = "event", Type = HistoryType, Detail = detail.Length <= 4000 ? detail : detail[..4000], MessageId = messageId });
        await db.SaveChangesAsync(ct);
    }
}

// ---- the issue and the hotfix item ---------------------------------------------------------------------------------------------------------------------------

/// <summary>A fault of GitHub that a later try gets past: the command is run again with growing pauses.</summary>
public sealed class IncidentIssueFault(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the incident needs of GitHub, each call idempotent (the repeated command finds what the first made by the marker it put in the issue or the comment). A fault that a later try gets past is an
/// <see cref="IncidentIssueFault"/>; a refusal for good is a plain exception the handler reports and does not repeat. The gateway implements it (Integrations has the HTTP).
/// </summary>
public interface IIncidentIssues
{
    /// <summary>Opens the incident issue (or finds the one opened for this incident) and answers its number.</summary>
    Task<int> Open(string key, int generation, string title, string body, string label, CancellationToken ct);

    /// <summary>Comments on the incident issue; <paramref name="number"/> 0 finds the issue by its marker. A comment with this key exists once.</summary>
    Task Comment(string key, int generation, int number, string commentKey, string body, CancellationToken ct);

    /// <summary>Closes the incident issue with the note.</summary>
    Task Close(string key, int generation, int number, string note, CancellationToken ct);

    /// <summary>Creates the hotfix item (or finds the one created for this incident and this request), puts it on the board and answers its id.</summary>
    Task<ItemId> CreateHotfix(string key, int generation, string title, string body, CancellationToken ct);
}

/// <summary>The incident's commands to GitHub. Without a credential (<c>issues</c> null) they have no effect and say so in the log: the incident goes on, its timeouts escalate it.</summary>
public sealed class IncidentIssuePort(IIncidentIssues? issues, Action<string>? log = null)
{
    readonly Action<string> log = log ?? (m => Console.Error.WriteLine(m));

    public async Task<OutgoingMessages> Run(OpenIncidentIssue m, CancellationToken ct)
    {
        var o = new OutgoingMessages();
        if (issues is null) { log($"incident {m.Key}#{m.Generation}: the gateway has no GitHub credential; the incident issue is not opened"); return o; }
        o.Add(new IssueOpened(m.Key, m.Generation, await issues.Open(m.Key, m.Generation, m.Title, m.Body, m.Label, ct)));
        return o;
    }

    public async Task Run(CommentIncidentIssue m, CancellationToken ct)
    {
        if (issues is null) { log($"incident {m.Key}#{m.Generation}: the gateway has no GitHub credential; the comment is not written"); return; }
        await issues.Comment(m.Key, m.Generation, m.Number, m.CommentKey, m.Body, ct);
    }

    public async Task Run(CloseIncidentIssue m, CancellationToken ct)
    {
        if (issues is null) { log($"incident {m.Key}#{m.Generation}: the gateway has no GitHub credential; the issue is not closed"); return; }
        await issues.Close(m.Key, m.Generation, m.Number, m.Note, ct);
    }

    /// <summary>Creates the hotfix item and starts its saga on the hotfix plan (rank top); the incident learns the item from <see cref="HotfixCreated"/>.</summary>
    public async Task<OutgoingMessages> Run(CreateHotfixItem m, CancellationToken ct)
    {
        var o = new OutgoingMessages();
        if (issues is null) { log($"incident {m.Key}#{m.Generation}: the gateway has no GitHub credential; the hotfix item is not created"); return o; }
        var item = await issues.CreateHotfix(m.Key, m.Generation, m.Title, m.Body, ct);
        o.Add(new HotfixCreated(m.Key, m.Generation, item.ToString()));
        o.Add(new StartWorkItem(item, $"incident {m.Key}#{m.Generation}", Hotfix: true));
        return o;
    }
}

/// <summary>The handlers of the incident's commands to GitHub. Public because Wolverine's generated code refers to them.</summary>
public static class IncidentIssueHandlers
{
    public static Task<OutgoingMessages> Handle(OpenIncidentIssue m, IncidentIssuePort port, CancellationToken ct) => port.Run(m, ct);
    public static Task Handle(CommentIncidentIssue m, IncidentIssuePort port, CancellationToken ct) => port.Run(m, ct);
    public static Task Handle(CloseIncidentIssue m, IncidentIssuePort port, CancellationToken ct) => port.Run(m, ct);
    public static Task<OutgoingMessages> Handle(CreateHotfixItem m, IncidentIssuePort port, CancellationToken ct) => port.Run(m, ct);
}
