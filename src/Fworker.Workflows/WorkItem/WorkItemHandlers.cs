using System.Text.Json;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Fworker.Workflows.WorkItem;

// W6.1: the Wolverine side of the WorkItemSaga. The handlers are static and public because Wolverine's generated code refers to them; the gateway attaches them only where the engine acts (Engine.V2Acts,
// WorkItemWiring), and a host of the tests names pre-built handlers that call the same service. Every input of the saga goes to one handler that applies it (WorkItemService) and returns what the saga decided
// and the timers it asked for as the messages to publish: they leave through the outbox after the handler, and a handler that dies between the save and the publish is run again by Wolverine, which finds its
// own transition as the last one and publishes it again (WorkItemSaga). A message for an item with no saga ends quietly (NotFound is an answer, not an error).

public static class WorkItemHandlers
{
    public static Task<OutgoingMessages> Handle(StartWorkItem m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ReopenWorkItem m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(DesignRecorded m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(StepCompleted m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(BudgetSpent m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(LeaseRenewed m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(PullRequestChanged m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeFactsRead m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeSeen m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(DeliverySeen m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(DeliveryLost m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ParkWorkItem m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ResumeWorkItem m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(PhaseLeaseExpired m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(SettleWindowElapsed m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ReviewRecorded m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(VerdictRecorded m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeAttempted m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeRetryDue m, WorkItemService svc, CancellationToken ct) => Run(m, svc, ct);

    public static async Task<OutgoingMessages> Run(WorkItemInput input, WorkItemService svc, CancellationToken ct)
    {
        var result = await svc.Handle(input, ct);
        return Outgoing(result);
    }

    /// <summary>The messages of a result: the decisions, the commands (the merge call), then each timer scheduled for its due time.</summary>
    public static OutgoingMessages Outgoing(WorkItemResult result)
    {
        var o = new OutgoingMessages();
        foreach (var d in result.Decisions) o.Add(d);
        foreach (var command in result.Sent) o.Add(command);
        foreach (var t in result.Timers) o.Add(t.Message.ScheduledAt(t.Due));
        return o;
    }
}

/// <summary>
/// What the delivery train and the merge say about an item, as inputs of its saga: ItemMerged is the merge step's fact, DeliveredTo and Undelivered the deliver step's. Items that have no saga answer NotFound and
/// nothing more, so the routing costs nothing for the items v1 owns.
/// </summary>
public static class WorkItemRouting
{
    public static OutgoingMessages Handle(ItemMerged m) => new() { new MergeSeen(m.Item, $"merged|{m.Commit}", m.Commit, m.PullRequest) };
    public static OutgoingMessages Handle(DeliveredTo m) => new() { new DeliverySeen(m.Item, $"delivered|{m.Environment}|{m.Release}", m.Environment, m.Release) };

    // an Undelivered names no release: the key is the message's own id, so a redelivery of one message repeats a key and a rollback later does not
    public static OutgoingMessages Handle(Undelivered m, Envelope envelope) => new() { new DeliveryLost(m.Item, $"undelivered|{m.Environment}|{envelope.Id}", m.Environment) };
}

/// <summary>The item history of the saga's decisions (4.7): one row per envelope, so a message handled twice writes it once. Public because Wolverine's generated code refers to it.</summary>
public static class WorkItemHistoryHandler
{
    public static async Task Handle(WorkItemDecided m, WorkItemHistory history, Envelope envelope, CancellationToken ct) => await history.Record(m, envelope.Id, ct);
}

public sealed class WorkItemHistory(Func<FactoryDbContext> open)
{
    public const string HistoryType = "workitem.decided";

    public async Task Record(WorkItemDecided m, Guid messageId, CancellationToken ct = default)
    {
        await using var db = open();
        var detail = JsonSerializer.Serialize(new { m.Sequence, m.Action, m.Plan, m.Step, m.ArmId, m.Reasons });
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = m.Item.ToString(), Generation = m.Generation, At = m.At, Kind = "event", Type = HistoryType, Detail = detail.Length <= 4000 ? detail : detail[..4000], MessageId = messageId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* the unique (MessageId, Kind) index: written by an earlier attempt of this message */ }
    }
}

/// <summary>Where the saga's messages travel in a host: one partitioned local topology keyed by the item, so one item's messages run one at a time on one node while different items run beside each other.</summary>
public static class WorkItemTopology
{
    public const string QueuePrefix = "workitems";
    public const int Shards = 4;

    public static IEnumerable<Type> Inputs => typeof(WorkItemInput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(WorkItemInput).IsAssignableFrom(t));

    /// <summary>Partitions by item and shards the inputs over local queues (durable: the inbox keeps a message across a restart); the history handler's input is not an item input.</summary>
    public static void Route(WolverineOptions w)
    {
        w.MessagePartitioning.ByMessage<IItemMessage>(m => m.Item.ToString());
        w.MessagePartitioning.PublishToPartitionedLocalMessaging(QueuePrefix, Shards, t =>
        {
            foreach (var type in Inputs) t.Message(type);
        });
        for (var i = 1; i <= Shards; i++) w.LocalQueue($"{QueuePrefix}{i}").UseDurableInbox();
    }
}
