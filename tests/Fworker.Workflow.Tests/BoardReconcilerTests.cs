using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Correlation;
using Fworker.Workflows.Reconcilers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W4.3: the board reconciler on a fake board, and its events through the inbox processor.</summary>
public class BoardReconcilerTests
{
    static readonly RepoId Home = new("acme", "orders"), System = new("acme", "system"), Other = new("else", "x");

    sealed class Board : IBoardReader
    {
        public List<BoardCard> Cards = [];
        public int Reads;
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) { Reads++; return Task.FromResult<IReadOnlyList<BoardCard>>(Cards.ToList()); }
    }

    static CatalogSnapshot Snap(bool board = true) => new(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, board ? new BoardRef("acme", 1) : null,
        [new RepositoryEntry(Home, []), new RepositoryEntry(System, [])], []);

    static (BoardReconciler R, Board B, List<string> Sent) Make(Func<bool>? may = null, Func<bool>? low = null, bool board = true)
    {
        var b = new Board(); var sent = new List<string>();
        return (new BoardReconciler(b, () => Snap(board), j => { sent.Add(j); return Task.FromResult(true); }, may ?? (() => true), low), b, sent);
    }

    [Fact]
    public async Task CardsOfEveryCatalogRepositoryArePublished_OnceUntilTheyMove()
    {
        var (r, b, sent) = Make();
        b.Cards = [new(Home, 5, "Doing", true, "a"), new(System, 9, "Todo", true, "b"), new(Other, 1, "Todo", true, "foreign"), new(Home, 6, "Done", false, "old")];
        Assert.Equal(2, (await r.Run(default)).Published);
        Assert.Equal(0, (await r.Run(default)).Published);
        var ev = JsonNode.Parse(sent[1])!;
        Assert.Equal("acme/system", ev["repository"]!.ToString()); Assert.Equal(9, (int)ev["item"]!);
        b.Cards[0] = new(Home, 5, "Review", true, "a");
        Assert.Equal(1, (await r.Run(default)).Published);
    }

    [Fact]
    public async Task ACardSeenClosedAndThenOpen_IsAReopening_UntilItIsSeenClosedAgain_AndACardNeverSeenClosedIsNot()
    {
        var (r, b, _) = Make();
        b.Cards = [new(Home, 5, "Done", false, "finished"), new(Home, 6, "Development", true, "never closed")];
        await r.Run(default);
        Assert.Null(r.ReopenedAt("acme/orders#5")); Assert.Null(r.ReopenedAt("acme/orders#6"));
        b.Cards[0] = new(Home, 5, "Development", true, "finished");
        await r.Run(default);
        Assert.NotNull(r.ReopenedAt("acme/orders#5")); Assert.Null(r.ReopenedAt("acme/orders#6"));
        await r.Run(default);
        Assert.NotNull(r.ReopenedAt("acme/orders#5"));
        r.ReopenUsed("acme/orders#5");
        Assert.Null(r.ReopenedAt("acme/orders#5"));
        b.Cards[0] = new(Home, 5, "Development", true, "finished");
        b.Cards[0] = new(Home, 5, "Done", false, "finished");
        await r.Run(default);
        Assert.Null(r.ReopenedAt("acme/orders#5"));
    }

    [Fact]
    public async Task NotTheLeader_OrBudgetLow_OrNoBoard_ReadsNothing()
    {
        var (r1, b1, _) = Make(may: () => false); Assert.Equal("not-leader", (await r1.Run(default)).Outcome); Assert.Equal(0, b1.Reads);
        var (r2, b2, _) = Make(low: () => true); Assert.Equal("budget-low", (await r2.Run(default)).Outcome); Assert.Equal(0, b2.Reads);
        var (r3, b3, _) = Make(board: false); Assert.Equal("no-board", (await r3.Run(default)).Outcome); Assert.Equal(0, b3.Reads);
    }

    [Fact]
    public async Task ARefusedPublishIsOfferedAgain()
    {
        var accept = false; var b = new Board { Cards = [new(Home, 5, "Doing", true, "a")] };
        var r = new BoardReconciler(b, () => Snap(), j => Task.FromResult(accept), () => true);
        Assert.Equal(0, (await r.Run(default)).Published);
        accept = true;
        Assert.Equal(1, (await r.Run(default)).Published);
    }

    [Fact]
    public async Task ACardThatLeavesTheBoardAndReturnsIsNewsAgain()
    {
        var (r, b, _) = Make(); b.Cards = [new(Home, 5, "Doing", true, "a")];
        await r.Run(default); b.Cards = []; await r.Run(default); b.Cards = [new(Home, 5, "Doing", true, "a")];
        Assert.Equal(1, (await r.Run(default)).Published);
    }

    [Fact]
    public async Task ThePollEventIsAcceptedByTheInbox_EachTimeTheCardMoves()
    {
        var (r, b, sent) = Make(); var p = new IngressInboxProcessor(new InMemoryIngressDedup(), new InMemoryCorrelationMap(), Home);
        b.Cards = [new(Home, 5, "Doing", true, "a")]; await r.Run(default);
        b.Cards = [new(Home, 5, "Review", true, "a")]; await r.Run(default);
        b.Cards = [new(Home, 5, "Doing", true, "a")]; await r.Run(default);
        Assert.Equal(3, sent.Count);
        Assert.All(sent, j => Assert.True(p.Process(new("poll", j), Guid.NewGuid()).Accepted));
    }
}
