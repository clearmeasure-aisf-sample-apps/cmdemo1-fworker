using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Delivery;

// W5.2: where the settled comparisons of the shadow run are kept (the DeliveryShadowComparisons table of the Wolverine database) and read back for the report.

/// <summary>A stored comparison as the report lists it.</summary>
public sealed record ShadowComparisonRow(long Id, DateTimeOffset At, string Deployable, string Environment, string Release, string Outcome, string Reason, string V1Action, string V2Action, JsonNode? V1, JsonNode? V2, bool Replay = false)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["at"] = At.ToString("o"), ["deployable"] = Deployable, ["environment"] = Environment, ["release"] = Release, ["outcome"] = Outcome, ["reason"] = Reason,
        ["v1Action"] = V1Action, ["v2Action"] = V2Action, ["replay"] = Replay, ["v1"] = V1?.DeepClone(), ["v2"] = V2?.DeepClone(),
    };
}

/// <summary>
/// What the stored comparisons add up to: a count per outcome, the releases they cover and the span of time. The gate counts the live ones only: <paramref name="LiveReleases"/> are the releases with
/// a decision of the train on a live event (not the replay of history) and <paramref name="FirstLive"/> the time of the first such comparison; <paramref name="Replays"/> are the replayed ones.
/// </summary>
public sealed record ShadowSummary(IReadOnlyDictionary<string, int> Counts, int Releases, DateTimeOffset? First, DateTimeOffset? Last, int LiveReleases = 0, DateTimeOffset? FirstLive = null, int Replays = 0)
{
    public int Total => Counts.Values.Sum();
}

public interface IShadowComparisonStore
{
    Task Add(IReadOnlyList<ShadowComparisonResult> results, CancellationToken ct);

    /// <summary>The newest first; <paramref name="outcome"/> narrows to one outcome.</summary>
    Task<IReadOnlyList<ShadowComparisonRow>> Recent(int limit, string? outcome, CancellationToken ct);

    Task<ShadowSummary> Summary(CancellationToken ct);
}

public sealed class SqlShadowComparisonStore(Func<FactoryDbContext> open) : IShadowComparisonStore
{
    public async Task Add(IReadOnlyList<ShadowComparisonResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;
        await using var db = open();
        foreach (var r in results)
            db.DeliveryShadowComparisons.Add(new DeliveryShadowComparison
            {
                At = r.At, Deployable = Clip(r.Deployable, 100), Environment = Clip(r.Environment, 32), Release = Clip(r.Release, 200), Outcome = r.Outcome, Reason = Clip(r.Reason, 2000),
                V1Action = r.V1?.Action ?? "", V2Action = r.V2?.Action ?? "", V1Json = r.V1?.ToJson().ToJsonString(), V2Json = r.V2?.ToJson().ToJsonString(), V1At = r.V1?.At, V2At = r.V2?.At, Replay = r.Replay,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ShadowComparisonRow>> Recent(int limit, string? outcome, CancellationToken ct)
    {
        await using var db = open();
        IQueryable<DeliveryShadowComparison> q = db.DeliveryShadowComparisons.AsNoTracking();
        if (!string.IsNullOrEmpty(outcome)) q = q.Where(x => x.Outcome == outcome);
        var rows = await q.OrderByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return rows.Select(x => new ShadowComparisonRow(x.Id, x.At, x.Deployable, x.Environment, x.Release, x.Outcome, x.Reason, x.V1Action, x.V2Action, Parse(x.V1Json), Parse(x.V2Json), x.Replay)).ToList();
    }

    public async Task<ShadowSummary> Summary(CancellationToken ct)
    {
        await using var db = open();
        var counts = (await db.DeliveryShadowComparisons.AsNoTracking().GroupBy(x => x.Outcome).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var releases = await db.DeliveryShadowComparisons.AsNoTracking().Select(x => x.Release).Distinct().CountAsync(ct);
        var any = counts.Count > 0;
        DateTimeOffset? first = any ? await db.DeliveryShadowComparisons.AsNoTracking().MinAsync(x => (DateTimeOffset?)x.At, ct) : null;
        DateTimeOffset? last = any ? await db.DeliveryShadowComparisons.AsNoTracking().MaxAsync(x => (DateTimeOffset?)x.At, ct) : null;
        // live: the train decided on a live event (a V2 side that is no replay); the rows of the replay of history are stored and shown, and counted by nothing else
        var live = db.DeliveryShadowComparisons.AsNoTracking().Where(x => !x.Replay && x.V2Action != "");
        var liveReleases = await live.Select(x => x.Release).Distinct().CountAsync(ct);
        DateTimeOffset? firstLive = liveReleases > 0 ? await live.MinAsync(x => (DateTimeOffset?)x.At, ct) : null;
        var replays = await db.DeliveryShadowComparisons.AsNoTracking().CountAsync(x => x.Replay, ct);
        return new ShadowSummary(counts, releases, first, last, liveReleases, firstLive, replays);
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
    static JsonNode? Parse(string? json) { try { return json is null ? null : JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return null; } }
}

public sealed class InMemoryShadowComparisonStore : IShadowComparisonStore
{
    readonly List<ShadowComparisonRow> rows = [];
    public bool Fail { get; set; }
    public IReadOnlyList<ShadowComparisonRow> All { get { lock (rows) return rows.ToList(); } }

    public Task Add(IReadOnlyList<ShadowComparisonResult> results, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("the store is down");
        lock (rows)
            foreach (var r in results)
                rows.Add(new ShadowComparisonRow(rows.Count + 1, r.At, r.Deployable, r.Environment, r.Release, r.Outcome, r.Reason, r.V1?.Action ?? "", r.V2?.Action ?? "", r.V1?.ToJson(), r.V2?.ToJson(), r.Replay));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShadowComparisonRow>> Recent(int limit, string? outcome, CancellationToken ct)
    {
        lock (rows) return Task.FromResult<IReadOnlyList<ShadowComparisonRow>>(rows.Where(r => string.IsNullOrEmpty(outcome) || r.Outcome == outcome).OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 500)).ToList());
    }

    public Task<ShadowSummary> Summary(CancellationToken ct)
    {
        lock (rows)
        {
            var live = rows.Where(r => !r.Replay && r.V2Action != "").ToList();
            return Task.FromResult(new ShadowSummary(rows.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()), rows.Select(r => r.Release).Distinct().Count(),
                rows.Count > 0 ? rows.Min(r => r.At) : null, rows.Count > 0 ? rows.Max(r => r.At) : null,
                live.Select(r => r.Release).Distinct().Count(), live.Count > 0 ? live.Min(r => r.At) : null, rows.Count(r => r.Replay)));
        }
    }
}
