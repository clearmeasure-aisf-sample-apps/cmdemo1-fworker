using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.Reconcilers;

/// <summary>What one pass did.</summary>
/// <summary>What one pass did: how it ended, the cards it read, the events it published and the GitHub requests it made (0 where the reader does not count them or the pass read nothing).</summary>
public sealed record BoardReconcileResult(string Outcome, int Cards, int Published, int Requests = 0);

/// <summary>
/// W4.3: the board reconciler. One <see cref="Run"/> reads the board (cards of every repository of the catalog, keyed by repository) and publishes an event for each card
/// that is new to this process or stands in another column or state than at the last pass; the gateway hands the events to the ingress inbox with source "poll", the same
/// message the webhook path sends. It backstops a lost webhook and the gap of a restart. It reads only; the leader lease and the GitHub budget decide whether it reads
/// at all (as v1's sync-board: a low budget skips non-essential reads). W9.10g: what it has published is remembered in <see cref="IBoardSeenStore"/> when there is one (read before the first pass, written after each
/// pass), so a restart does not forget it; a pass that cannot read the store ends with outcome "seen-unavailable" and publishes nothing, since an empty memory would republish the board. Not thread safe by itself, so a pass takes a gate: passes run one after the other (the recurring job and the verb sync-board share one instance; the job waits its turn, an on-demand pass waits <see cref="OnDemandWait"/> and then answers outcome "busy"). What was published is remembered only once the
/// publish was accepted, so a refused one is offered again by the next pass.
/// </summary>
public sealed class BoardReconciler(IBoardReader reader, Func<CatalogSnapshot> catalog, Func<string, Task<bool>> publish, Func<bool> mayAct, Func<bool>? budgetLow = null, Func<DateTimeOffset>? clock = null, IBoardSeenStore? store = null)
{
    public const string EventName = "board.card";
    readonly Dictionary<string, (string? Column, bool Open)> seen = [];
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim gate = new(1, 1);   // one pass at a time: "seen" is a plain Dictionary, and two passes would publish the same card twice
    bool seenLoaded;

    /// <summary>The cards of the last pass that read the board (W9.10k: the plant reads the labels of the items the engine owns from them). Empty before the first pass; a dry pass leaves it as it was.</summary>
    public IReadOnlyList<BoardCard> LastCards { get; private set; } = [];

    readonly Dictionary<string, DateTimeOffset> reopened = [];

    /// <summary>
    /// V1.1: when a pass saw the card of this item (key "owner/repo#N") open after an earlier pass had seen it closed (null: it did not): the proof of a reopening that the admission asks for before it reopens the saga of a completed item. The memory is the
    /// reconciler's own (and BoardSeen's across a restart: a card last seen closed and now open is the same transition); it is forgotten when the card is seen closed again, so a second completion does not inherit it.
    /// </summary>
    public DateTimeOffset? ReopenedAt(string key) { lock (reopened) return reopened.TryGetValue(key, out var at) ? at : null; }

    /// <summary>The proof is used up: the admission reopened the saga on it, and it must not reopen the next generation too.</summary>
    public void ReopenUsed(string key) { lock (reopened) reopened.Remove(key); }

    /// <summary>What the reconciler costs GitHub, per pass (W9.10g).</summary>
    public BoardReconcileMeter Meter { get; } = new();

    /// <summary>How long an on-demand pass waits for a pass that is running before it answers outcome "busy".</summary>
    public TimeSpan OnDemandWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The recurring job's pass: waits for the pass that is running (an on-demand one is bounded by its own timeout).</summary>
    public Task<BoardReconcileResult> Run(CancellationToken ct) => Pass(ct, null, false, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// W9.8, the verb <c>sync-board</c> on v2: the same pass on demand. <paramref name="only"/> limits it to those item numbers of the home repository (v1's <c>items</c>: an incremental pass; the cards that are not named are
    /// left as they were, and nothing is forgotten); <paramref name="dry"/> publishes nothing and remembers nothing, and counts what a pass would have published (v1's <c>dry</c>).
    /// </summary>
    public Task<BoardReconcileResult> Run(CancellationToken ct, IReadOnlySet<int>? only, bool dry) => Pass(ct, only, dry, OnDemandWait);

    async Task<BoardReconcileResult> Pass(CancellationToken ct, IReadOnlySet<int>? only, bool dry, TimeSpan wait)
    {
        if (!await gate.WaitAsync(wait, ct)) return new("busy", 0, 0);
        try { var r = await Locked(ct, only, dry); Meter.Record(r); return r; }
        finally { gate.Release(); }
    }

    async Task<BoardReconcileResult> Locked(CancellationToken ct, IReadOnlySet<int>? only, bool dry)
    {
        if (!mayAct()) return new("not-leader", 0, 0);
        if (budgetLow?.Invoke() == true) return new("budget-low", 0, 0);
        var snap = catalog();
        if (snap.Board is not { } board) return new("no-board", 0, 0);
        if (!seenLoaded && store is not null)
        {
            try { foreach (var (k, v) in await store.Load(ct)) seen[k] = (v.Column, v.Open); seenLoaded = true; }
            catch (Exception e) when (e is not OperationCanceledException) { seen.Clear(); Console.Error.WriteLine($"board reconciler: the seen cards could not be read ({e.GetType().Name})"); return new("seen-unavailable", 0, 0); }
        }
        var metered = reader as IMeteredBoardReader; var before = metered?.Requests ?? 0;
        var cards = await reader.Read(board, ct);
        var requests = (int)((metered?.Requests ?? 0) - before);
        if (!dry) LastCards = cards;
        if (!mayAct()) return new("not-leader", cards.Count, 0, requests);   // the lease was lost while the board was read: nothing of it is published
        var repos = snap.Repositories.Select(r => r.Id).ToHashSet();
        var current = new HashSet<string>(); var published = 0;
        var changed = new Dictionary<string, SeenCard>(StringComparer.OrdinalIgnoreCase); var removed = new List<string>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (!repos.Contains(card.Repository) || card.Number <= 0) continue;
            if (only is not null && (card.Repository != snap.Home || !only.Contains(card.Number))) continue;
            var key = new ItemId(card.Repository, card.Number).ToString();
            current.Add(key);
            var known = seen.TryGetValue(key, out var prev);
            if (!dry && known) { lock (reopened) { if (!prev.Open && card.Open) reopened[key] = now(); else if (prev.Open && !card.Open) reopened.Remove(key); } }
            if (known && prev == (card.Column, card.Open)) continue;
            if (!known && !card.Open) { if (!dry) { seen[key] = (card.Column, card.Open); changed[key] = new(card.Column, card.Open); } continue; }   // a closed card seen for the first time is history, not news
            if (dry) { published++; continue; }
            if (await publish(Event(card))) { seen[key] = (card.Column, card.Open); changed[key] = new(card.Column, card.Open); published++; }
        }
        if (only is null && !dry) foreach (var gone in seen.Keys.Where(k => !current.Contains(k)).ToList()) { seen.Remove(gone); removed.Add(gone); }   // off the board: forgotten, so a card that returns is news
        // the memory is written after the events were accepted; a write that fails is logged and the pass stands (the next restart then knows a little less, and a card it republishes is the same event as before)
        if (store is not null && (changed.Count > 0 || removed.Count > 0))
        {
            try { await store.Save(changed, removed, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"board reconciler: the seen cards could not be saved ({e.GetType().Name})"); }
        }
        return new("ok", cards.Count, published, requests);
    }

    string Event(BoardCard c) => new JsonObject
    {
        ["provider"] = "github", ["event"] = EventName, ["item"] = c.Number, ["repository"] = c.Repository.ToString(), ["column"] = c.Column, ["open"] = c.Open,
        ["title"] = c.Title.Length <= 200 ? c.Title : c.Title[..200],
        // "poll:" marks an event without a delivery id (IngressKeys.Logical): the inbox does not deduplicate a card by delivery, the pass compares columns itself
        ["ref"] = $"poll:board:{c.Repository}#{c.Number}:{c.Column}:{(c.Open ? "open" : "closed")}", ["at"] = now().ToString("o"),
    }.ToJsonString();
}
