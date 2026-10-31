using System.Text.Json.Nodes;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Reconcilers;

// W9.10g: the board reconciler's memory made durable (factory.BoardSeen, script 200), and the measure of what it costs GitHub.
// Until now the reconciler remembered what it had published only in memory, so a restart forgot it and a card that closed while no gateway was up was dropped as "a closed card seen for the first time is history":
// its merge was never derived. With the rows, such a card is still news.

/// <summary>What the reconciler last published of a card: its Status column and whether the issue was open.</summary>
public readonly record struct SeenCard(string? Column, bool Open);

public interface IBoardSeenStore
{
    /// <summary>Every card remembered, by the key "owner/repo#N". Throws when the database does not answer.</summary>
    Task<IReadOnlyDictionary<string, SeenCard>> Load(CancellationToken ct = default);

    /// <summary>Writes the cards that changed and forgets the cards that left the board, in one transaction. Throws when the database does not take it.</summary>
    Task Save(IReadOnlyDictionary<string, SeenCard> changed, IReadOnlyCollection<string> removed, CancellationToken ct = default);
}

public sealed class SqlBoardSeenStore(Func<FactoryDbContext> open, Func<DateTimeOffset>? clock = null) : IBoardSeenStore
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    static bool Split(string key, out string repository, out int number)
    {
        repository = ""; number = 0;
        var i = key.LastIndexOf('#');
        if (i <= 0 || !int.TryParse(key[(i + 1)..], out number) || number <= 0) return false;
        repository = key[..i].ToLowerInvariant();
        return true;
    }

    public async Task<IReadOnlyDictionary<string, SeenCard>> Load(CancellationToken ct = default)
    {
        await using var db = open();
        var rows = await db.BoardSeen.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => $"{r.Repository}#{r.Number}", r => new SeenCard(r.StatusColumn, r.IsOpen), StringComparer.OrdinalIgnoreCase);
    }

    public async Task Save(IReadOnlyDictionary<string, SeenCard> changed, IReadOnlyCollection<string> removed, CancellationToken ct = default)
    {
        if (changed.Count == 0 && removed.Count == 0) return;
        await using var db = open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var at = now();
        // one read of the rows the pass touches, in chunks (a key list of a whole board would be a long IN list)
        var keys = changed.Keys.Select(k => Split(k, out var r, out var n) ? (Repository: r, Number: n, Key: k) : default).Where(x => x.Key is not null).ToList();
        foreach (var chunk in keys.Chunk(500))
        {
            var repos = chunk.Select(c => c.Repository).Distinct().ToList(); var numbers = chunk.Select(c => c.Number).Distinct().ToList();
            var existing = (await db.BoardSeen.Where(r => repos.Contains(r.Repository) && numbers.Contains(r.Number)).ToListAsync(ct)).ToDictionary(r => (r.Repository, r.Number));
            foreach (var c in chunk)
            {
                var card = changed[c.Key];
                if (existing.TryGetValue((c.Repository, c.Number), out var row)) { row.StatusColumn = Clip(card.Column); row.IsOpen = card.Open; row.SeenAt = at; }
                else db.BoardSeen.Add(new BoardSeenRow { Repository = c.Repository, Number = c.Number, StatusColumn = Clip(card.Column), IsOpen = card.Open, SeenAt = at });
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        foreach (var key in removed)
            if (Split(key, out var repository, out var number))
                await db.BoardSeen.Where(r => r.Repository == repository && r.Number == number).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    static string? Clip(string? s) => s is { Length: > 200 } ? s[..200] : s;
}

/// <summary>
/// What the board reconciler costs GitHub (W9.10g): per pass the GraphQL requests it made (one per page of 100 cards, one point of the GraphQL budget each), kept for an hour. The block of /api/v1/status shows the passes
/// of the last hour, the requests they made, the average per pass and the hourly cost at the configured interval, so that the interval can be chosen against a measured number.
/// </summary>
public sealed class BoardReconcileMeter(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Queue<(DateTimeOffset At, int Requests, int Cards, int Published)> passes = new();
    long totalPasses, totalRequests;
    string lastOutcome = "";
    DateTimeOffset? lastAt;

    /// <summary>The interval the recurring job runs at, for the hourly estimate (0 when unknown).</summary>
    public int IntervalSeconds { get; set; }

    public void Record(BoardReconcileResult r)
    {
        if (r.Outcome is "busy") return;
        lock (passes)
        {
            var at = now(); lastAt = at; lastOutcome = r.Outcome;
            // a pass that did not read (not the leader, budget low, no board) costs nothing and is not a sample of the cost
            if (r.Requests <= 0) return;
            totalPasses++; totalRequests += r.Requests;
            passes.Enqueue((at, r.Requests, r.Cards, r.Published));
            while (passes.Count > 0 && at - passes.Peek().At > TimeSpan.FromHours(1)) passes.Dequeue();
        }
    }

    public JsonObject Block()
    {
        lock (passes)
        {
            var at = now();
            while (passes.Count > 0 && at - passes.Peek().At > TimeSpan.FromHours(1)) passes.Dequeue();
            var n = passes.Count; var requests = passes.Sum(p => p.Requests);
            var perPass = n == 0 ? 0 : Math.Round((double)requests / n, 2);
            return new JsonObject
            {
                ["intervalSeconds"] = IntervalSeconds, ["passesLastHour"] = n, ["requestsLastHour"] = requests, ["requestsPerPass"] = perPass,
                ["cardsLastPass"] = n == 0 ? 0 : passes.Last().Cards,
                ["estimatedRequestsPerHour"] = IntervalSeconds > 0 ? (long)Math.Ceiling(perPass * 3600.0 / IntervalSeconds) : null,
                ["totalPasses"] = totalPasses, ["totalRequests"] = totalRequests, ["lastOutcome"] = lastOutcome, ["lastAt"] = lastAt?.ToString("o"),
            };
        }
    }
}
