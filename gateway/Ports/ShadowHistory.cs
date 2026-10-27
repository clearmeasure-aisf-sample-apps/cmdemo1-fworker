using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// Shadow history (task W2.3, docs/design/wolverine-host.md "Shadow history"). Only where the environment is in engine.shadowEnvironments (and the kill switch
// FACTORY_V2_OFF is off) and the Wolverine host is registered: every event that enters through the ingress (seam S3) and every journal line the plant posts is
// written to ItemHistory (Fworker.Persistence, W2.1) by way of Wolverine's durable local queue, so a line survives a restart of the node. v1 acts exactly as before:
//  - recording runs after v1 has done its part, on the thread pool, and never waits for the database or for the host;
//  - it is best-effort: a host that is not up, a database that is paused or down, a full backlog or any exception drops the record (counted, never thrown);
//  - nothing in v2 acts: no command is sent, nothing is written to GitHub.
// Writes happen on activity only (no polling of ours), so they wake a paused serverless database; that is expected and accepted for the window.

/// <summary>One row for ItemHistory, on its way through the durable queue. Public because Wolverine's generated code refers to it.</summary>
public sealed record ShadowHistoryRecord(string ItemKey, string Type, string? Detail, DateTimeOffset At);

/// <summary>What the v1 code paths call to record; every call returns at once and never throws.</summary>
interface IShadowHistory
{
    void Event(IngressEvent ev);
    void Journal(int item, string ev, JsonObject fields, string key, bool posted);
    JsonObject Status();
}

sealed class ShadowHistory(Func<ShadowHistoryRecord, Task<bool>> publish, RepoId? home, int maxInFlight = 500) : IShadowHistory
{
    public const int MaxDetailChars = 16000;
    const string BoardKey = "board";

    int inFlight;
    long recorded, dropped, failed;

    public void Event(IngressEvent ev)
    {
        try
        {
            switch (ev)
            {
                case WebhookEventIngested w:
                {
                    JsonNode? o = null;
                    try { o = JsonNode.Parse(w.Json); } catch { }
                    var name = o?["event"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : "unknown";
                    var item = o?["item"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0;
                    Enqueue(new ShadowHistoryRecord(KeyOf(item), "ingress.webhook." + Clip(name, 120), Cap(w.Json), DateTimeOffset.UtcNow));
                    break;
                }
                case SweepRequested s:
                    Enqueue(new ShadowHistoryRecord(KeyOf(s.Item), "ingress.sweep-requested", new JsonObject { ["item"] = s.Item }.ToJsonString(), DateTimeOffset.UtcNow));
                    break;
                default:
                    Enqueue(new ShadowHistoryRecord(BoardKey, "ingress." + Clip(ev.GetType().Name, 120), null, DateTimeOffset.UtcNow));
                    break;
            }
        }
        catch { Interlocked.Increment(ref failed); }
    }

    public void Journal(int item, string ev, JsonObject fields, string key, bool posted)
    {
        try
        {
            var detail = new JsonObject { ["event"] = ev, ["key"] = key, ["posted"] = posted, ["fields"] = fields.DeepClone() }.ToJsonString();
            Enqueue(new ShadowHistoryRecord(KeyOf(item), "v1.journal." + Clip(ev, 120), Cap(detail), DateTimeOffset.UtcNow));
        }
        catch { Interlocked.Increment(ref failed); }
    }

    string KeyOf(int item) => item > 0 ? (home is { } h ? $"{h}#{item}" : "#" + item) : BoardKey;

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
    static string Cap(string s) => s.Length <= MaxDetailChars ? s : s[..MaxDetailChars];

    // the publish runs on the pool after v1 has gone on; a backlog beyond maxInFlight (a database that does not answer) drops the newest
    void Enqueue(ShadowHistoryRecord r)
    {
        if (Interlocked.Increment(ref inFlight) > maxInFlight) { Interlocked.Decrement(ref inFlight); Interlocked.Increment(ref dropped); return; }
        _ = Task.Run(async () =>
        {
            try { if (await publish(r)) Interlocked.Increment(ref recorded); else Interlocked.Increment(ref dropped); }
            catch { Interlocked.Increment(ref failed); }
            finally { Interlocked.Decrement(ref inFlight); }
        });
    }

    public JsonObject Status() => new() { ["recorded"] = Interlocked.Read(ref recorded), ["dropped"] = Interlocked.Read(ref dropped), ["failed"] = Interlocked.Read(ref failed), ["inFlight"] = Volatile.Read(ref inFlight) };
}

/// <summary>S3 with shadow history: v1's ingress does its part first, then the event is recorded. A recording problem never reaches the caller.</summary>
sealed class ShadowEventIngress(IEventIngress v1, IShadowHistory shadow) : IEventIngress
{
    public void Ingest(IngressEvent ev)
    {
        v1.Ingest(ev);
        try { shadow.Event(ev); } catch { }   // the recorder does not throw; this is the second guard, v1 has already done its part
    }
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
