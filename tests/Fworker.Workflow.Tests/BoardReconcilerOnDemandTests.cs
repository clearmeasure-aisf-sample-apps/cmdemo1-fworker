using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Reconcilers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.8: the reconciler's pass on demand, which the verb sync-board adds to the sweep: limited to named items (v1's items) and dry (v1's dry).</summary>
public class BoardReconcilerOnDemandTests
{
    static readonly RepoId Home = new("acme", "orders"), System = new("acme", "system");

    sealed class Board : IBoardReader
    {
        public List<BoardCard> Cards = [];
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BoardCard>>(Cards.ToList());
    }

    static CatalogSnapshot Snap() => new(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, new BoardRef("acme", 1), [new RepositoryEntry(Home, []), new RepositoryEntry(System, [])], []);

    static (BoardReconciler R, Board B, List<string> Sent) Make()
    {
        var b = new Board { Cards = [new(Home, 5, "Doing", true, "a"), new(Home, 6, "Todo", true, "b"), new(System, 5, "Todo", true, "other repository, same number"), new(Home, 7, "Done", false, "old")] };
        var sent = new List<string>();
        return (new BoardReconciler(b, Snap, j => { sent.Add(j); return Task.FromResult(true); }, () => true), b, sent);
    }

    [Fact]
    public async Task ADryPass_CountsWhatWouldBePublished_AndPublishesAndRemembersNothing()
    {
        var (r, _, sent) = Make();
        Assert.Equal(3, (await r.Run(default, null, dry: true)).Published);   // the open cards of both repositories; the closed one is history
        Assert.Empty(sent);
        Assert.Equal(3, (await r.Run(default)).Published);                    // nothing was remembered: the real pass still has all of it
        Assert.Equal(3, sent.Count);
        Assert.Equal(0, (await r.Run(default, null, dry: true)).Published);
    }

    [Fact]
    public async Task APassForNamedItems_ReadsOnlyThoseOfTheHomeRepository()
    {
        var (r, _, sent) = Make();
        var one = await r.Run(default, new HashSet<int> { 5 }, dry: false);
        Assert.Equal(1, one.Published);
        var ev = JsonNode.Parse(sent.Single())!;
        Assert.Equal(("acme/orders", 5), (ev["repository"]!.ToString(), (int)ev["item"]!));
        Assert.Equal(0, (await r.Run(default, new HashSet<int> { 99 }, dry: false)).Published);   // not on the board
        Assert.Equal(2, (await r.Run(default)).Published);                                       // the rest is still news: card 6 and the other repository's 5
    }

    [Fact]
    public async Task APassForNamedItems_ForgetsNothing()
    {
        var (r, b, _) = Make();
        await r.Run(default);
        var all = b.Cards.ToList();
        b.Cards = [];
        await r.Run(default, new HashSet<int> { 5 }, dry: false);   // the board is empty, but a limited pass does not forget the cards it was not asked about
        b.Cards = all;
        Assert.Equal(0, (await r.Run(default)).Published);          // still known: a full pass that had run on the empty board would have made them news
    }
}
