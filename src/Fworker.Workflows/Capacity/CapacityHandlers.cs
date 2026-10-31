using System.Text.Json;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Fworker.Workflows.Capacity;

// W7.1: the Wolverine side of the CapacityScheduler. The handlers are static and public because Wolverine's generated code refers to them; the gateway attaches them only where the engine acts (Engine.V2Acts,
// CapacityWiring), and a host of the tests names pre-built handlers that call the same service. Every input goes to one handler that applies it (CapacityService) and returns what the scheduler published and its
// timer as the messages to send: they leave through the outbox after the handler, and a handler that dies between the save and the publish is run again by Wolverine, which finds its own transition as the last one
// and publishes it again (CapacityScheduler.Apply). The scheduler's queue is sequential: one writer at a time, on the node that holds the listener.

public static class CapacityHandlers
{
    public static Task<OutgoingMessages> Handle(RequestSlot m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ClearItem m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(SessionEnded m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(StartRefused m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(HostReported m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(FootprintChanged m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(ReapStale m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);
    public static Task<OutgoingMessages> Handle(CapacityWake m, CapacityService svc, CancellationToken ct) => Run(m, svc, ct);

    public static async Task<OutgoingMessages> Run(CapacityInput input, CapacityService svc, CancellationToken ct)
    {
        var result = await svc.Handle(input, ct);
        return Outgoing(result);
    }

    /// <summary>The messages of a result, then the timer scheduled for its due time.</summary>
    public static OutgoingMessages Outgoing(CapacityResult result)
    {
        var o = new OutgoingMessages();
        foreach (var m in result.Messages) o.Add(m);
        if (result.WakeAt is { } due) o.Add(new CapacityWake(due).ScheduledAt(due));
        return o;
    }
}

/// <summary>The item history of what the scheduler said (4.7): one row per envelope, so a message handled twice writes it once. Public because Wolverine's generated code refers to it.</summary>
public static class CapacityHistoryHandler
{
    public static Task Handle(SlotGranted m, CapacityHistory history, Envelope envelope, CancellationToken ct)
        => history.Record(m.Item.ToString(), m.Generation, m.At, CapacityHistory.GrantedType, new { m.SlotId, m.Runtime, m.Host, m.Kind, m.Phase, m.ExpiresAt }, envelope.Id, ct);

    public static Task Handle(Held m, CapacityHistory history, Envelope envelope, CancellationToken ct)
        => history.Record(m.Item.ToString(), m.Generation, m.At, CapacityHistory.HeldType, new { m.Code, m.Detail, By = m.By?.ToString() }, envelope.Id, ct);

    public static Task Handle(SlotExpired m, CapacityHistory history, Envelope envelope, CancellationToken ct)
        => history.Record(m.Item.ToString(), m.Generation, m.At, CapacityHistory.ExpiredType, new { m.SlotId, m.Runtime, m.Host }, envelope.Id, ct);
}

public sealed class CapacityHistory(Func<FactoryDbContext> open)
{
    public const string GrantedType = "capacity.slot-granted";
    public const string HeldType = "capacity.held";
    public const string ExpiredType = "capacity.slot-expired";

    public async Task Record(string item, int generation, DateTimeOffset at, string type, object detail, Guid messageId, CancellationToken ct = default)
    {
        await using var db = open();
        var json = JsonSerializer.Serialize(detail);
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = item, Generation = generation, At = at, Kind = "event", Type = type, Detail = json.Length <= 4000 ? json : json[..4000], MessageId = messageId });
        try { await db.SaveChangesAsync(ct); }
        catch (Exception e) when (Faults.SqlFaults.IsDuplicateKey(e)) { /* the unique (MessageId, Kind) index: written by an earlier attempt of this message */ }
    }
}

/// <summary>
/// Where the scheduler's messages travel in a host: its inputs go to one durable local queue that runs one message at a time (the ledger has one writer; a queue per item would let two inputs move it at once and
/// exhaust the revision retries). The queue is durable, so an input or a timer survives a restart.
/// </summary>
public static class CapacityTopology
{
    public const string Queue = "capacity";

    public static IEnumerable<Type> Inputs => typeof(CapacityInput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(CapacityInput).IsAssignableFrom(t));

    public static void Route(WolverineOptions w)
    {
        foreach (var t in Inputs) w.PublishMessage(t).ToLocalQueue(Queue);
        w.LocalQueue(Queue).UseDurableInbox().Sequential();
    }
}
