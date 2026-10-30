using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Reconcilers;

// W9.8: the journal in the item's history. The journal ledger (Fworker.Integrations JournalLedger) writes the comment and the ledger in the item's state comment; this records that the line was handled as a row of
// ItemHistory (Kind "event", Type "journal.<event>"), so "where is item X" and the timeline of an item show the plant manager's lines beside the saga's decisions and the ingress events. Best-effort by design: a database that
// does not answer never fails a line (the comment is the journal; the history is its record). A line is recorded once: the row's message id is derived from (item, event, key), and the unique index on
// (MessageId, Kind) is the last word.

/// <summary>What the history keeps of one journal line: the event, its key, whether a comment was written or a fold edited, and why not when it was not.</summary>
public sealed record JournalHistoryLine(string ItemKey, string Event, string Key, bool Posted, string Reason, bool Recorded, DateTimeOffset At);

public sealed class JournalHistory(Func<FactoryDbContext> open)
{
    public const string TypePrefix = "journal.";

    /// <summary>The message id of a line: the same (item, event, key) always gives the same one.</summary>
    public static Guid MessageIdOf(string itemKey, string ev, string key)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"journal|{itemKey}|{ev}|{key}")));

    /// <summary>Writes the row; false when the database did not take it (the failure is the caller's to log, never to throw).</summary>
    public async Task<bool> Record(JournalHistoryLine line, CancellationToken ct = default)
    {
        try
        {
            await using var db = open();
            var id = MessageIdOf(line.ItemKey, line.Event, line.Key);
            if (await db.ItemHistory.AnyAsync(x => x.MessageId == id && x.Kind == "event", ct)) return true;
            var detail = JsonSerializer.Serialize(new { @event = line.Event, key = line.Key, posted = line.Posted, recorded = line.Recorded, reason = line.Reason });
            db.ItemHistory.Add(new ItemHistoryEntry
            {
                ItemKey = line.ItemKey, Generation = 0, At = line.At, Kind = "event", Type = TypePrefix + (line.Event.Length <= 150 ? line.Event : line.Event[..150]),
                Detail = detail.Length <= 4000 ? detail : detail[..4000], MessageId = id,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 }) { /* an earlier attempt wrote it */ }
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"journal history {line.ItemKey} {line.Event}: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>The lines of one item, oldest first.</summary>
    public async Task<IReadOnlyList<JournalHistoryLine>> LinesOf(string itemKey, CancellationToken ct = default)
    {
        await using var db = open();
        var rows = await db.ItemHistory.AsNoTracking().Where(x => x.ItemKey == itemKey && x.Type.StartsWith(TypePrefix)).OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Select(r =>
        {
            using var doc = JsonDocument.Parse(r.Detail ?? "{}");
            var o = doc.RootElement;
            string S(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            return new JournalHistoryLine(r.ItemKey, S("event"), S("key"), B("posted"), S("reason"), B("recorded"), r.At);
        }).ToList();
    }
}
