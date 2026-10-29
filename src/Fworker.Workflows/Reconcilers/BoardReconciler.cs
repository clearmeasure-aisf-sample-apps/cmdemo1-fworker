using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.Reconcilers;

/// <summary>What one pass did.</summary>
public sealed record BoardReconcileResult(string Outcome, int Cards, int Published);

/// <summary>
/// W4.3: the board reconciler. One <see cref="Run"/> reads the board (cards of every repository of the catalog, keyed by repository) and publishes an event for each card
/// that is new to this process or stands in another column or state than at the last pass; the gateway hands the events to the ingress inbox with source "poll", the same
/// message the webhook path sends. It backstops a lost webhook and the gap of a restart. It reads only; the leader lease and the GitHub budget decide whether it reads
/// at all (as v1's sync-board: a low budget skips non-essential reads). Not thread safe: one job runs it at a time. What was published is remembered only once the
/// publish was accepted, so a refused one is offered again by the next pass.
/// </summary>
public sealed class BoardReconciler(IBoardReader reader, Func<CatalogSnapshot> catalog, Func<string, Task<bool>> publish, Func<bool> mayAct, Func<bool>? budgetLow = null, Func<DateTimeOffset>? clock = null)
{
    public const string EventName = "board.card";
    readonly Dictionary<string, (string? Column, bool Open)> seen = [];
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<BoardReconcileResult> Run(CancellationToken ct) => Run(ct, null, false);

    /// <summary>
    /// W9.8, the verb <c>sync-board</c> on v2: the same pass on demand. <paramref name="only"/> limits it to those item numbers of the home repository (v1's <c>items</c>: an incremental pass; the cards that are not named are
    /// left as they were, and nothing is forgotten); <paramref name="dry"/> publishes nothing and remembers nothing, and counts what a pass would have published (v1's <c>dry</c>).
    /// </summary>
    public async Task<BoardReconcileResult> Run(CancellationToken ct, IReadOnlySet<int>? only, bool dry)
    {
        if (!mayAct()) return new("not-leader", 0, 0);
        if (budgetLow?.Invoke() == true) return new("budget-low", 0, 0);
        var snap = catalog();
        if (snap.Board is not { } board) return new("no-board", 0, 0);
        var cards = await reader.Read(board, ct);
        if (!mayAct()) return new("not-leader", cards.Count, 0);   // the lease was lost while the board was read: nothing of it is published
        var repos = snap.Repositories.Select(r => r.Id).ToHashSet();
        var current = new HashSet<string>(); var published = 0;
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (!repos.Contains(card.Repository) || card.Number <= 0) continue;
            if (only is not null && (card.Repository != snap.Home || !only.Contains(card.Number))) continue;
            var key = new ItemId(card.Repository, card.Number).ToString();
            current.Add(key);
            var known = seen.TryGetValue(key, out var prev);
            if (known && prev == (card.Column, card.Open)) continue;
            if (!known && !card.Open) { if (!dry) seen[key] = (card.Column, card.Open); continue; }   // a closed card seen for the first time is history, not news
            if (dry) { published++; continue; }
            if (await publish(Event(card))) { seen[key] = (card.Column, card.Open); published++; }
        }
        if (only is null && !dry) foreach (var gone in seen.Keys.Where(k => !current.Contains(k)).ToList()) seen.Remove(gone);   // off the board: forgotten, so a card that returns is news
        return new("ok", cards.Count, published);
    }

    string Event(BoardCard c) => new JsonObject
    {
        ["provider"] = "github", ["event"] = EventName, ["item"] = c.Number, ["repository"] = c.Repository.ToString(), ["column"] = c.Column, ["open"] = c.Open,
        ["title"] = c.Title.Length <= 200 ? c.Title : c.Title[..200],
        // "poll:" marks an event without a delivery id (IngressKeys.Logical): the inbox does not deduplicate a card by delivery, the pass compares columns itself
        ["ref"] = $"poll:board:{c.Repository}#{c.Number}:{c.Column}:{(c.Open ? "open" : "closed")}", ["at"] = now().ToString("o"),
    }.ToJsonString();
}
