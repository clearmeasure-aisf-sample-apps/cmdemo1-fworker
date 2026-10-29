using System.Text.Json;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.ErrorHandling;

namespace Fworker.Workflows.Merge;

// W7.2: the Wolverine side of the merge train. Public and static because Wolverine's generated code refers to them; the gateway attaches them only where the engine acts (Engine.V2Acts, WorkItemWiring), in place of the
// direct handler of W6.6 (the train with the direct strategy lets every request through at once, which is that handler's behaviour). The router turns the saga's MergeRequested into the train's input; the train's own
// handlers take an input to MergeTrainService and return what it answered (they leave through the outbox after the state is saved); the merge itself is the gateway's port (MergeGranted, needs GitHub).

/// <summary>The saga's merge request enters the train of its repository.</summary>
public static class MergeTrainRouting
{
    public static ReadyToMerge Handle(MergeRequested m) => new(m.Item.Repository.ToString(), m);

    /// <summary>
    /// A merge that happened anywhere (the delivery train's ItemMerged: the webhook, the reconciler) releases the item's place in the line, so a pull request that a person merged does not hold the turn until it expires.
    /// A repository with no train row answers nothing and saves nothing.
    /// </summary>
    public static MergeTrainLeft Handle(ItemMerged m) => new(m.Item.Repository.ToString(), m.Item, "merged elsewhere");
}

public static class MergeTrainHandlers
{
    public static Task<OutgoingMessages> Handle(ReadyToMerge m, MergeTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeTrainAnswered m, MergeTrainService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(MergeTrainLeft m, MergeTrainService svc, CancellationToken ct) => Run(m, svc, ct);

    public static async Task<OutgoingMessages> Run(MergeTrainInput input, MergeTrainService svc, CancellationToken ct)
    {
        var o = new OutgoingMessages();
        foreach (var m in await svc.Handle(input, ct)) o.Add(m);
        return o;
    }
}

/// <summary>Where the train's messages travel in a host: its inputs one at a time (a durable queue: a request survives a restart), the turns beside each other (the merge calls of different repositories do not wait for one another).</summary>
public static class MergeTrainTopology
{
    public const string InputQueue = "merge-train";
    public const string GrantQueue = "merge-train-grants";

    public static IEnumerable<Type> Inputs => typeof(MergeTrainInput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(MergeTrainInput).IsAssignableFrom(t));

    public static void Route(WolverineOptions w)
    {
        foreach (var t in Inputs) w.PublishMessage(t).ToLocalQueue(InputQueue);
        w.LocalQueue(InputQueue).UseDurableInbox().Sequential();
        w.PublishMessage<MergeGranted>().ToLocalQueue(GrantQueue);
        w.LocalQueue(GrantQueue).UseDurableInbox().MaximumParallelMessages(4);
        // two writers on one train more often than the service's own retries allow: the input is run again after a pause
        w.Policies.OnException<MergeTrainConflictException>().RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)).Then.MoveToErrorQueue();
    }
}

/// <summary>The item history of the train's decisions: one row per envelope, so a message handled twice writes it once. Public because Wolverine's generated code refers to it.</summary>
public static class MergeTrainHistoryHandler
{
    public static async Task Handle(MergeTrainDecided m, MergeTrainHistory history, Envelope envelope, CancellationToken ct) => await history.Record(m, envelope.Id, ct);
}

public sealed class MergeTrainHistory(Func<FactoryDbContext> open)
{
    public const string HistoryType = "merge.train.decided";

    public async Task Record(MergeTrainDecided m, Guid messageId, CancellationToken ct = default)
    {
        await using var db = open();
        // written by an earlier attempt of this message (the unique (MessageId, Kind) index): nothing to add. Any other fault is not swallowed: the message is run again.
        if (await db.ItemHistory.AnyAsync(x => x.MessageId == messageId && x.Kind == "event", ct)) return;
        var detail = JsonSerializer.Serialize(new { m.Key, m.Sequence, m.Action, m.Reasons });
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = m.Item.ToString(), Generation = m.Generation, At = m.At, Kind = "event", Type = HistoryType, Detail = detail.Length <= 4000 ? detail : detail[..4000], MessageId = messageId });
        await db.SaveChangesAsync(ct);
    }
}
