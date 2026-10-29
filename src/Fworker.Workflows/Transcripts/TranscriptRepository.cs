using System.Text.Json;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Transcripts;

// W9.8: the session transcripts of runtime workers in the database (factory.Transcripts, script 013), where the plant snapshot's ref store held them until now (ciphertext commits in the
// work-orders repository). One row per session name; the kept lines are a JSON array; the session's qualified item ("owner/repo#N") is a column, so the sessions of an item are one indexed read.
// The gateway's TranscriptStore keeps the live session in memory and saves it here at most once a minute while lines arrive, and at once when the session ends.

/// <summary>One session's transcript as stored: what the store holds for the page and the retention. <see cref="Lines"/> are the kept lines; <see cref="Dropped"/> the ones dropped from the front to keep the limit.</summary>
public sealed record TranscriptDoc(string Name, string? ItemKey, string Runtime, string Phase, DateTimeOffset Started, DateTimeOffset LastLine, DateTimeOffset? Ended, long Dropped, long Total, IReadOnlyList<string> Lines);

public interface ITranscriptRepository
{
    /// <summary>Stores the session whole (a later save of the same name replaces the earlier one). Throws when the database does not take it.</summary>
    Task Save(TranscriptDoc doc, CancellationToken ct = default);

    /// <summary>The session of that name, or null when there is none.</summary>
    Task<TranscriptDoc?> Load(string name, CancellationToken ct = default);

    /// <summary>Removes the sessions that started on a day before the last <paramref name="days"/> (1 to 365), as the ref store's prune did. Answers how many.</summary>
    Task<int> Prune(int days, CancellationToken ct = default);

    /// <summary>The names of the sessions of one qualified item, newest first (the sessions of an item).</summary>
    Task<IReadOnlyList<string>> SessionsOf(string itemKey, CancellationToken ct = default);
}

public sealed class SqlTranscriptRepository(Func<FactoryDbContext> open, Func<DateTimeOffset>? clock = null) : ITranscriptRepository
{
    public const int MaxAttempts = 8;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task Save(TranscriptDoc doc, CancellationToken ct = default)
    {
        var lines = JsonSerializer.Serialize(doc.Lines);
        long bytes = 0; foreach (var l in doc.Lines) bytes += l.Length + 1;
        for (var attempt = 1; ; attempt++)
        {
            await using var db = open();
            var row = await db.Transcripts.SingleOrDefaultAsync(x => x.Name == doc.Name, ct);
            var isNew = row is null;
            if (row is null) { row = new TranscriptRow { Name = doc.Name, Revision = 0 }; db.Transcripts.Add(row); }
            row.ItemKey = doc.ItemKey; row.Runtime = Clip(doc.Runtime, 32); row.Phase = Clip(doc.Phase, 32);
            row.StartedAt = doc.Started; row.LastLineAt = doc.LastLine; row.EndedAt = doc.Ended;
            row.Bytes = bytes; row.LinesTotal = doc.Total; row.LinesDropped = doc.Dropped; row.LinesJson = lines;
            row.Revision += 1; row.UpdatedAt = now();
            try { await db.SaveChangesAsync(ct); return; }
            // a writer that read an older revision loses and reads again; a second insert of the same name (two nodes during a hand-over) is the same collision
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts) { }
            catch (DbUpdateException e) when (isNew && attempt < MaxAttempts && e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 }) { }
        }
    }

    public async Task<TranscriptDoc?> Load(string name, CancellationToken ct = default)
    {
        await using var db = open();
        var row = await db.Transcripts.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
        if (row is null) return null;
        IReadOnlyList<string> lines;
        try { lines = JsonSerializer.Deserialize<List<string>>(row.LinesJson) ?? []; }
        catch (JsonException) { lines = []; }
        return new TranscriptDoc(row.Name, row.ItemKey, row.Runtime, row.Phase, row.StartedAt, row.LastLineAt, row.EndedAt, row.LinesDropped, row.LinesTotal, lines);
    }

    public async Task<int> Prune(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        // the ref store compared the day of the start (yyyymmdd) with the day `days` ago: a session of a day before that day goes
        var cut = new DateTimeOffset(now().UtcDateTime.AddDays(-days).Date, TimeSpan.Zero);
        await using var db = open();
        return await db.Transcripts.Where(x => x.StartedAt < cut).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> SessionsOf(string itemKey, CancellationToken ct = default)
    {
        await using var db = open();
        return await db.Transcripts.AsNoTracking().Where(x => x.ItemKey == itemKey).OrderByDescending(x => x.StartedAt).Select(x => x.Name).ToListAsync(ct);
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
}
