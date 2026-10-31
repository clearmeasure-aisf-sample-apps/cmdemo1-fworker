using Fworker.Persistence;
using Fworker.Workflows.Faults;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Adoption;

// W9.9b: the evidence the adoption reads to tell whether v1 may have acted on the items since v2 last did (W9.10n's afterInterval). Two markers in factory.EngineMarkers (script 290): "acted", the heartbeat of the acting v2
// process (written once a minute, and only after the adoption of its term is over, so that a start whose adoption broke off finds the same evidence again), and "v1-ran", written by a process on which the kill switch
// (FACTORY_V2_OFF) turned an environment of v2Environments back to v1. Evidence only: the truth stays the board and the state comments.

/// <summary>When the engine last acted and when a process that was not the engine last ran with this database; null for a marker never written.</summary>
public sealed record EngineMarks(DateTimeOffset? ActedAt, DateTimeOffset? V1RanAt);

public interface IEngineMarkers
{
    Task<EngineMarks> Read(CancellationToken ct);

    /// <summary>Writes the marker (the time, who wrote it and a short detail); the later write replaces the earlier.</summary>
    Task Mark(string name, DateTimeOffset at, string holder, string detail, CancellationToken ct);
}

public static class EngineMarkerNames
{
    public const string Acted = "acted", V1Ran = "v1-ran";
}

/// <summary>Whether the interval since the engine last acted may have had v1 acting on the items (W9.10n: then an item the state comment changed is the next generation of its instance).</summary>
public static class AdoptionBreak
{
    /// <summary>A gateway that was away longer than this left the items long enough for something else to have worked on them (a rollback to an image without the engine does not write a marker).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    public static (bool Break, string Why) Detect(EngineMarks marks, DateTimeOffset now, TimeSpan? gap = null)
    {
        if (marks.V1RanAt is { } v1 && (marks.ActedAt is not { } acted || v1 > acted)) return (true, "v1 ran with the kill switch on after the engine last acted");
        if (marks.ActedAt is { } last && now - last > (gap ?? Gap)) return (true, $"the engine last acted {(int)(now - last).TotalMinutes} minutes ago");
        return (false, marks.ActedAt is null ? "the engine has not acted here before" : "the engine acted shortly before");
    }
}

public sealed class SqlEngineMarkers(Func<FactoryDbContext> open) : IEngineMarkers
{
    public async Task<EngineMarks> Read(CancellationToken ct)
    {
        await using var db = open();
        var rows = await db.EngineMarkers.AsNoTracking().ToListAsync(ct);
        return new EngineMarks(rows.FirstOrDefault(r => r.Name == EngineMarkerNames.Acted)?.At, rows.FirstOrDefault(r => r.Name == EngineMarkerNames.V1Ran)?.At);
    }

    public async Task Mark(string name, DateTimeOffset at, string holder, string detail, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = open();
            var row = await db.EngineMarkers.FirstOrDefaultAsync(x => x.Name == name, ct);
            if (row is null) db.EngineMarkers.Add(new EngineMarkerRow { Name = name, At = at, Holder = Cut(holder, 100), Detail = Cut(detail, 200) });
            else { row.At = at; row.Holder = Cut(holder, 100); row.Detail = Cut(detail, 200); }
            try { await db.SaveChangesAsync(ct); return; }
            catch (Exception e) when (attempt < 2 && SqlFaults.IsConflict(e)) { }   // (another process wrote the row at once: look again)
        }
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class InMemoryEngineMarkers : IEngineMarkers
{
    readonly Dictionary<string, (DateTimeOffset At, string Holder, string Detail)> rows = [];
    public IReadOnlyDictionary<string, (DateTimeOffset At, string Holder, string Detail)> Rows { get { lock (rows) return new Dictionary<string, (DateTimeOffset, string, string)>(rows); } }
    /// <summary>A test control: called before each write; it may throw to stand for a database that fails.</summary>
    public Action<string>? BeforeMark { get; set; }

    public Task<EngineMarks> Read(CancellationToken ct)
    {
        lock (rows) return Task.FromResult(new EngineMarks(rows.TryGetValue(EngineMarkerNames.Acted, out var a) ? a.At : null, rows.TryGetValue(EngineMarkerNames.V1Ran, out var v) ? v.At : null));
    }

    public Task Mark(string name, DateTimeOffset at, string holder, string detail, CancellationToken ct)
    {
        BeforeMark?.Invoke(name);
        lock (rows) rows[name] = (at, holder, detail);
        return Task.CompletedTask;
    }
}
