using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// The item history written through Wolverine's durable local queue "shadow-history" (task W2.3, docs/design/wolverine-host.md): the ingress inbox, the merge and the delivery train
// return a ShadowHistoryRecord, and the handler below writes it to ItemHistory (Fworker.Persistence, W2.1), so a line survives a restart of the node. (The shadow run itself,
// which recorded v1's events and journal lines this way, is gone with V3.2.)

/// <summary>One row for ItemHistory, on its way through the durable queue. Public because Wolverine's generated code refers to it.</summary>
public sealed record ShadowHistoryRecord(string ItemKey, string Type, string? Detail, DateTimeOffset At);

static class ShadowHistory
{
    public const int MaxDetailChars = 16000;
}

/// <summary>Holds the connection string for the handler in the nested Wolverine host.</summary>
public sealed class ShadowHistoryStore(string connectionString)
{
    public async Task WriteAsync(ShadowHistoryRecord m, Guid messageId, CancellationToken ct)
    {
        await using var db = new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString).Options);
        // a redelivered message writes its row once (the unique index on MessageId and Kind is the last word)
        if (await db.ItemHistory.AnyAsync(x => x.MessageId == messageId && x.Kind == "event", ct)) return;
        db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = m.ItemKey, Generation = 0, At = m.At, Kind = "event", Type = m.Type, Detail = m.Detail, MessageId = messageId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }
}

/// <summary>The handler of the durable queue "shadow-history": one row in ItemHistory. Public because Wolverine's generated code refers to it.</summary>
public static class ShadowHistoryHandler
{
    public static Task Handle(ShadowHistoryRecord message, ShadowHistoryStore store, Envelope envelope, CancellationToken ct) => store.WriteAsync(message, envelope.Id, ct);
}
