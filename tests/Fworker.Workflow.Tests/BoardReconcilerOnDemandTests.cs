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

    /// <summary>A board that holds every read until released, to overlap passes.</summary>
    sealed class SlowBoard(List<BoardCard> cards) : IBoardReader
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) { Entered.TrySetResult(); await Release.Task; return cards.ToList(); }
    }

    [Fact]
    public async Task OverlappingPasses_AreSerialized_OnePublishPerCard_AndNoException()
    {
        var cards = Enumerable.Range(1, 50).Select(i => new BoardCard(Home, i, "Todo", true, "t" + i)).ToList();
        var board = new SlowBoard(cards);
        var sent = new List<string>(); var sync = new object();
        var r = new BoardReconciler(board, Snap, j => { lock (sync) sent.Add(j); return Task.FromResult(true); }, () => true) { OnDemandWait = TimeSpan.FromSeconds(30) };
        var job = r.Run(default);                       // the recurring job's pass, held inside the board read
        await board.Entered.Task;
        var verb1 = Task.Run(() => r.Run(default, null, false));   // two sync-board calls on top of it
        var verb2 = Task.Run(() => r.Run(default, new HashSet<int> { 1, 2, 3 }, false));
        await Task.Delay(100);
        board.Release.SetResult();
        var results = await Task.WhenAll(job, verb1, verb2);
        Assert.Equal(50, sent.Count);                   // each card once, however many passes overlapped
        Assert.Equal(50, sent.Select(s => JsonNode.Parse(s)!["item"]!.ToString()).Distinct().Count());
        Assert.Equal(50, results.Sum(x => x.Published));
        Assert.All(results, x => Assert.Equal("ok", x.Outcome));
    }

    [Fact]
    public async Task AnOnDemandPass_ThatFindsAPassRunning_AnswersBusyAfterItsWait_AndTheRunningPassIsUnharmed()
    {
        var board = new SlowBoard([new BoardCard(Home, 1, "Todo", true, "t")]);
        var sent = new List<string>();
        var r = new BoardReconciler(board, Snap, j => { sent.Add(j); return Task.FromResult(true); }, () => true) { OnDemandWait = TimeSpan.FromMilliseconds(50) };
        var job = r.Run(default);
        await board.Entered.Task;
        var busy = await r.Run(default, null, false);
        Assert.Equal(("busy", 0, 0), (busy.Outcome, busy.Cards, busy.Published));
        board.Release.SetResult();
        Assert.Equal(1, (await job).Published);
        Assert.Single(sent);
        Assert.Equal("ok", (await r.Run(default, null, true)).Outcome);   // the gate was released
    }
}
