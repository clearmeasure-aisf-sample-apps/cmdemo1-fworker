using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Reconcilers;

// W9.10g: the retention of the tables that only grow. The recurring job "retention-prune" (once a day, the leader) removes
//  - ItemHistory rows older than ItemHistoryDays (180) of an item that has no open instance (a WorkItems row open or settling keeps its whole history);
//  - the shadow run's records older than ShadowDays (30): DeliveryShadowComparisons, and the ItemHistory rows the shadow run and the ingress write ("ingress.inbox.*": every webhook event is one; "delivery.train.shadow-command" and "delivery.train.replay", the train's recorded commands). The
//    acting train's own decisions ("delivery.train.*", "delivery.item-merged") are history of items and live as long as it does;
//  - transcripts older than TranscriptDays (14), and then the oldest finished sessions until the stored transcripts are within TranscriptMaxBytes (the cap: a session is capped at 2 MB in memory, so a busy week of
//    sessions can still be more than the database should hold).
// Deletes go in chunks of ChunkRows (the transaction log of a serverless database is small), at most MaxChunks per table per run; a table that still has more is finished by the next run. Rows are removed by their age and
// their kind only, never by reading what a person wrote. Nothing is removed that a running thing reads: the open items' history, the sessions that are still writing.

/// <summary>The settings of the retention (instance.json "retention").</summary>
public sealed record RetentionSettings(int ItemHistoryDays = 180, int ShadowDays = 30, int TranscriptDays = 14, long TranscriptMaxBytes = 256L * 1024 * 1024, int ChunkRows = 5000, int MaxChunks = 40)
{
    public static RetentionSettings From(JsonNode? root)
    {
        var block = root?["retention"];
        int Int(string key, int fallback, int min, int max) => block?[key] is { } n && int.TryParse(n.ToString(), out var v) ? Math.Clamp(v, min, max) : fallback;
        return new(Int("itemHistoryDays", 180, 7, 3650), Int("shadowDays", 30, 1, 3650), Int("transcriptDays", 14, 1, 365), Int("transcriptMaxMb", 256, 16, 100_000) * 1024L * 1024);
    }
}

/// <summary>What one run removed.</summary>
public sealed record RetentionReport(int ItemHistory, int ShadowHistory, int ShadowComparisons, int Transcripts, int TranscriptsOverCap, bool More, int DeadLettersArchived = 0)
{
    public int Total => ItemHistory + ShadowHistory + ShadowComparisons + Transcripts + TranscriptsOverCap + DeadLettersArchived;
}

public sealed class RetentionPrune(Func<FactoryDbContext> open, RetentionSettings settings, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<RetentionReport> Run(CancellationToken ct = default)
    {
        var at = now();
        var shadowCut = at.AddDays(-settings.ShadowDays);
        var historyCut = at.AddDays(-settings.ItemHistoryDays);
        var more = false;

        // the shadow run's history first (the most rows), then the rest of the history of items that are over
        var (shadowHistory, m1) = await Chunks(db => db.Database.ExecuteSqlRawAsync(
            "DELETE TOP (@n) FROM [factory].[ItemHistory] WHERE [At] < @cut AND ([Type] LIKE N'ingress.inbox.%' OR [Type] IN (N'delivery.train.shadow-command', N'delivery.train.replay'))", Params(shadowCut), ct), ct);
        var (history, m2) = await Chunks(db => db.Database.ExecuteSqlRawAsync(
            "DELETE TOP (@n) FROM [factory].[ItemHistory] WHERE [At] < @cut AND NOT EXISTS (SELECT 1 FROM [factory].[WorkItems] w WHERE w.[ItemKey] = [ItemHistory].[ItemKey] AND w.[Status] <> N'completed')", Params(historyCut), ct), ct);
        var (comparisons, m3) = await Chunks(db => db.Database.ExecuteSqlRawAsync(
            "DELETE TOP (@n) FROM [factory].[DeliveryShadowComparisons] WHERE [At] < @cut", Params(shadowCut), ct), ct);
        more = m1 || m2 || m3;

        int transcripts, overCap;
        await using (var db = open())
        {
            var cut = new DateTimeOffset(at.UtcDateTime.AddDays(-settings.TranscriptDays).Date, TimeSpan.Zero);   // the day of the start, as the transcript store's own prune compares it
            transcripts = await db.Transcripts.Where(x => x.StartedAt < cut).ExecuteDeleteAsync(ct);
        }
        overCap = await TranscriptCap(at, ct);
        return new(history, shadowHistory, comparisons, transcripts, overCap, more, await ArchiveTrainWakeDeadLetters(ct));
    }

    /// <summary>
    /// V2.1: the 121 TrainWake dead letters with MergeFactsException that tdd collected from 2026-10-30 (a restart's replay left a timer per input and the burst of full evaluations met a refusal of GitHub that the train did not know as a
    /// fault) cannot happen again since #607: the wake is one timer, the facts reads obey the shadow's limits, and MergeFactsException is a failed read and a retry rule of every train's host. They are archived (Wolverine's own
    /// <c>archived_at</c>, which every count of the status block already leaves out; nothing is deleted), and only those that failed before <see cref="TrainWakeFixedAt"/>: one that fails later is a new fault and stays visible.
    /// Idempotent (an archived row is not touched again), harmless where there is none, and a table that is not there (a database of a test that builds only the factory tables) is no error.
    /// </summary>
    public static readonly DateTimeOffset TrainWakeFixedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    async Task<int> ArchiveTrainWakeDeadLetters(CancellationToken ct)
    {
        await using var db = open();
        try
        {
            return await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID(N'wolverine.wolverine_dead_letters') IS NOT NULL UPDATE wolverine.wolverine_dead_letters SET archived_at = SYSDATETIMEOFFSET() " +
                "WHERE archived_at IS NULL AND (message_type = N'delivery.train.wake' OR message_type LIKE N'%TrainWake') AND exception_type LIKE N'%MergeFactsException%' AND failed_at < @fixed", [new SqlParameter("@fixed", TrainWakeFixedAt)], ct);
        }
        catch (SqlException) { return 0; }   // (a housekeeping step never stops the others)
    }

    SqlParameter[] Params(DateTimeOffset cut) => [new("@n", settings.ChunkRows), new("@cut", cut)];

    async Task<(int Removed, bool More)> Chunks(Func<FactoryDbContext, Task<int>> delete, CancellationToken ct)
    {
        var total = 0;
        for (var i = 0; i < settings.MaxChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = open();
            var n = await delete(db);
            total += n;
            if (n < settings.ChunkRows) return (total, false);
        }
        return (total, true);
    }

    /// <summary>The oldest finished sessions go until the stored lines are within the cap. A session that is still writing (not ended, a line in the last hour) is never removed.</summary>
    async Task<int> TranscriptCap(DateTimeOffset at, CancellationToken ct)
    {
        await using var db = open();
        var all = await db.Transcripts.AsNoTracking().Select(x => new { x.Name, x.Bytes, x.StartedAt, x.EndedAt, x.LastLineAt }).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var total = all.Sum(x => x.Bytes);
        if (total <= settings.TranscriptMaxBytes) return 0;
        var live = at.AddHours(-1);
        var doomed = new List<string>();
        foreach (var t in all)
        {
            if (total <= settings.TranscriptMaxBytes) break;
            if (t.EndedAt is null && t.LastLineAt > live) continue;
            doomed.Add(t.Name); total -= t.Bytes;
        }
        var removed = 0;
        foreach (var chunk in doomed.Chunk(200))
            removed += await db.Transcripts.Where(x => chunk.Contains(x.Name)).ExecuteDeleteAsync(ct);
        return removed;
    }
}
