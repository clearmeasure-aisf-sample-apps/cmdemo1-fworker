using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Persistence;
using Fworker.Workflows.Reconcilers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10g: the board reconciler's memory in the database (BoardSeen, script 200) and the measure of its GitHub cost. The first group runs the reconciler against an in-memory store; the second runs the real store on the SQL
/// Server of FACTORY_TEST_SQL (reported as skipped without one).
/// </summary>
public class BoardSeenTests
{
    static readonly RepoId Home = new("acme", "orders");

    sealed class Board : IMeteredBoardReader
    {
        public List<BoardCard> Cards = [];
        public long Requests { get; private set; }
        public int PagesPerRead = 2;
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef b, CancellationToken ct = default) { Requests += PagesPerRead; return Task.FromResult<IReadOnlyList<BoardCard>>(Cards.ToList()); }
    }

    sealed class MemoryStore : IBoardSeenStore
    {
        public Dictionary<string, SeenCard> Rows = new(StringComparer.OrdinalIgnoreCase);
        public int Saves, Loads; public bool FailLoad, FailSave;
        public Task<IReadOnlyDictionary<string, SeenCard>> Load(CancellationToken ct = default)
        {
            Loads++; if (FailLoad) throw new InvalidOperationException("database down");
            return Task.FromResult<IReadOnlyDictionary<string, SeenCard>>(new Dictionary<string, SeenCard>(Rows));
        }
        public Task Save(IReadOnlyDictionary<string, SeenCard> changed, IReadOnlyCollection<string> removed, CancellationToken ct = default)
        {
            Saves++; if (FailSave) throw new InvalidOperationException("database down");
            foreach (var (k, v) in changed) Rows[k] = v;
            foreach (var k in removed) Rows.Remove(k);
            return Task.CompletedTask;
        }
    }

    static CatalogSnapshot Snap() => new(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, new BoardRef("acme", 1), [new RepositoryEntry(Home, [])], []);

    static BoardReconciler Make(Board b, MemoryStore s, List<string> sent) => new(b, Snap, j => { sent.Add(j); return Task.FromResult(true); }, () => true, store: s);

    [Fact]
    public async Task ACardThatClosedWhileNoGatewayWasUp_IsNewsAfterTheRestart_Because_TheStoreRemembersItWasOpen()
    {
        var store = new MemoryStore(); var board = new Board { Cards = [new(Home, 5, "Functional Testing", true, "a"), new(Home, 6, "Done", false, "old")] };
        var first = new List<string>();
        var r1 = Make(board, store, first);
        Assert.Equal(1, (await r1.Run(default)).Published);   // 5 is news; the closed 6 is history, and remembered as such
        Assert.Equal(new SeenCard("Functional Testing", true), store.Rows["acme/orders#5"]);
        Assert.Equal(new SeenCard("Done", false), store.Rows["acme/orders#6"]);

        // the gateway stops; card 5 is merged and closed meanwhile; a new process starts with the same database
        board.Cards = [new(Home, 5, "Done", false, "a"), new(Home, 6, "Done", false, "old")];
        var second = new List<string>();
        var r2 = Make(board, store, second);
        var result = await r2.Run(default);
        Assert.Equal(1, result.Published);
        Assert.Contains("\"open\":false", second.Single());   // the closed card is what ItemMergedDeriver derives a merge from
        Assert.Contains("\"item\":5", second.Single());
        Assert.Equal(0, (await r2.Run(default)).Published);   // and only once
    }

    [Fact]
    public async Task WithoutTheStore_AClosedCardOfAnotherProcessIsHistory_AsBefore()
    {
        var board = new Board { Cards = [new(Home, 5, "Done", false, "a")] }; var sent = new List<string>();
        var r = new BoardReconciler(board, Snap, j => { sent.Add(j); return Task.FromResult(true); }, () => true);
        Assert.Equal(0, (await r.Run(default)).Published);
    }

    [Fact]
    public async Task OnlyWhatChangedIsWritten_AndACardOffTheBoardIsForgotten()
    {
        var store = new MemoryStore(); var board = new Board { Cards = [new(Home, 5, "Doing", true, "a"), new(Home, 6, "Todo", true, "b")] };
        var r = Make(board, store, []);
        await r.Run(default); Assert.Equal(1, store.Saves);
        await r.Run(default); Assert.Equal(1, store.Saves);   // nothing changed: nothing written
        board.Cards = [new(Home, 5, "Review", true, "a")];     // 5 moved, 6 left the board
        await r.Run(default);
        Assert.Equal(2, store.Saves);
        Assert.Equal(new SeenCard("Review", true), store.Rows["acme/orders#5"]);
        Assert.DoesNotContain("acme/orders#6", store.Rows.Keys);
    }

    [Fact]
    public async Task AStoreThatCannotBeRead_PublishesNothing_AndIsAskedAgain()
    {
        var store = new MemoryStore { FailLoad = true }; var board = new Board { Cards = [new(Home, 5, "Doing", true, "a")] }; var sent = new List<string>();
        var r = Make(board, store, sent);
        var first = await r.Run(default);
        Assert.Equal("seen-unavailable", first.Outcome); Assert.Empty(sent); Assert.Equal(0, board.Requests);   // an empty memory would republish the whole board: the board is not even read
        store.FailLoad = false;
        Assert.Equal("ok", (await r.Run(default)).Outcome); Assert.Equal(2, store.Loads);
        Assert.Single(sent);
    }

    [Fact]
    public async Task AStoreThatCannotBeWritten_DoesNotFailThePass()
    {
        var store = new MemoryStore { FailSave = true }; var board = new Board { Cards = [new(Home, 5, "Doing", true, "a")] };
        var r = Make(board, store, []);
        Assert.Equal(1, (await r.Run(default)).Published);
        Assert.Equal(0, (await r.Run(default)).Published);   // the process still remembers
    }

    [Fact]
    public async Task ADryPassWritesNothing_AndAPassOfNamedItemsForgetsNothing()
    {
        var store = new MemoryStore(); var board = new Board { Cards = [new(Home, 5, "Doing", true, "a"), new(Home, 6, "Todo", true, "b")] };
        var r = Make(board, store, []);
        await r.Run(default, null, dry: true); Assert.Equal(0, store.Saves);
        await r.Run(default); Assert.Equal(2, store.Rows.Count);
        board.Cards = [new(Home, 5, "Review", true, "a")];
        await r.Run(default, new HashSet<int> { 5 }, dry: false);   // an incremental pass: 6 is not named, so it is not forgotten
        Assert.Contains("acme/orders#6", store.Rows.Keys);
        Assert.Equal("Review", store.Rows["acme/orders#5"].Column);
    }

    [Fact]
    public async Task TheMeterCountsTheRequestsOfEachPass_AndEstimatesTheCostOfAnHour()
    {
        var at = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var board = new Board { Cards = [new(Home, 5, "Doing", true, "a")], PagesPerRead = 3 };
        var r = new BoardReconciler(board, Snap, _ => Task.FromResult(true), () => true);
        r.Meter.IntervalSeconds = 120;
        Assert.Equal(3, (await r.Run(default)).Requests);
        await r.Run(default);
        var block = r.Meter.Block();
        Assert.Equal(2, (int)block["passesLastHour"]!); Assert.Equal(6, (int)block["requestsLastHour"]!); Assert.Equal(3, (double)block["requestsPerPass"]!);
        Assert.Equal(90, (long)block["estimatedRequestsPerHour"]!);   // 3 requests every 2 minutes: 30 passes of 3
        Assert.Equal("ok", (string)block["lastOutcome"]!);
        _ = at;
    }

    [Fact]
    public async Task APassThatReadsNothing_IsNoSampleOfTheCost()
    {
        var board = new Board { Cards = [new(Home, 5, "Doing", true, "a")] };
        var r = new BoardReconciler(board, Snap, _ => Task.FromResult(true), () => false);
        Assert.Equal("not-leader", (await r.Run(default)).Outcome);
        Assert.Equal(0, (int)r.Meter.Block()["passesLastHour"]!);
        Assert.Equal("not-leader", (string)r.Meter.Block()["lastOutcome"]!);
    }

    [Fact]
    public void TheMeterForgetsPassesOlderThanAnHour()
    {
        var now = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var meter = new BoardReconcileMeter(() => now) { IntervalSeconds = 120 };
        meter.Record(new("ok", 10, 0, 2)); now = now.AddMinutes(30); meter.Record(new("ok", 10, 0, 2));
        Assert.Equal(2, (int)meter.Block()["passesLastHour"]!);
        now = now.AddMinutes(45);
        Assert.Equal(1, (int)meter.Block()["passesLastHour"]!);
        Assert.Equal(2, (long)meter.Block()["totalPasses"]!);
    }

    // ---- the real store, on a SQL Server --------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task TheSqlStore_RoundTrips_UpdatesInPlace_AndForgets()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_seen_");
        FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(db.ConnectionString).Options);
        var store = new SqlBoardSeenStore(Open);
        Assert.Empty(await store.Load());
        await store.Save(new Dictionary<string, SeenCard> { ["acme/orders#5"] = new("Doing", true), ["acme/orders#6"] = new(null, false), ["acme/system#5"] = new("Todo", true) }, []);
        var rows = await store.Load();
        Assert.Equal(3, rows.Count);
        Assert.Equal(new SeenCard("Doing", true), rows["acme/orders#5"]);
        Assert.Equal(new SeenCard(null, false), rows["acme/orders#6"]);
        Assert.Equal(new SeenCard("Todo", true), rows["acme/system#5"]);   // the same number in another repository is another card
        await store.Save(new Dictionary<string, SeenCard> { ["acme/orders#5"] = new("Done", false) }, ["acme/orders#6"]);
        rows = await store.Load();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new SeenCard("Done", false), rows["acme/orders#5"]);
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.BoardSeen WHERE Repository = N'acme/orders'"));
    }

    [SqlFact]
    public async Task TheReconcilerOnTheSqlStore_SurvivesAProcessRestart()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_seen2_");
        FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(db.ConnectionString).Options);
        var board = new Board { Cards = [new(Home, 5, "Functional Testing", true, "a")] };
        var first = new List<string>();
        await new BoardReconciler(board, Snap, j => { first.Add(j); return Task.FromResult(true); }, () => true, store: new SqlBoardSeenStore(Open)).Run(default);
        Assert.Single(first);
        board.Cards = [new(Home, 5, "Done", false, "a")];
        var second = new List<string>();
        var result = await new BoardReconciler(board, Snap, j => { second.Add(j); return Task.FromResult(true); }, () => true, store: new SqlBoardSeenStore(Open)).Run(default);
        Assert.Equal(1, result.Published);
        Assert.Contains("\"open\":false", second.Single());
    }
}
