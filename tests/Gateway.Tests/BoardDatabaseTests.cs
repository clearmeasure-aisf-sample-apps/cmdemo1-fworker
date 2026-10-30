using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Persistence;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.Transcripts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.8 on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), the database built by the scripts: a runtime session's transcript goes through the store to factory.Transcripts and comes back after a
/// restart, the transcript verbs speak the sealed form and store rows, and a journal line through the verb lands in the item's history.
/// </summary>
public sealed class BoardDatabaseTests : IAsyncLifetime
{
    static bool NoServer => !TestDatabase.Available;
    static readonly RepoId Home = new("acme", "orders");
    static readonly TimeSpan Time = TimeSpan.FromSeconds(30);
    TestDatabase? database;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync() { if (!NoServer) database = await TestDatabase.CreateAsync("fw_w98g_"); }
    public async Task DisposeAsync() { if (database != null) await database.DisposeAsync(); }

    [Fact]
    public async Task ASessionSavedByTheStore_IsARowWithItsItem_AndASecondStoreReadsItBack()
    {
        if (NoServer) return;
        var repo = new SqlTranscriptRepository(Open);
        var first = new TranscriptStore(null, ""); first.UseBackend(new DatabaseTranscriptBackend(repo, Home, () => true));
        first.Started("cae-7.dev", 12, "develop", "octopus");
        await first.Append("cae-7.dev", ["one", "two", "three"]);
        first.Ended("cae-7.dev");
        await first.Flush(false, Time);   // an ended session is saved at once
        Assert.Equal(0, (int)first.Status()["dirty"]!);
        Assert.Equal(["cae-7.dev"], await repo.SessionsOf("acme/orders#12"));
        var second = new TranscriptStore(null, ""); second.UseBackend(new DatabaseTranscriptBackend(repo, Home, () => true));   // a restarted gateway
        var r = (await second.Read("cae-7.dev", 0))!;
        Assert.Equal((12, "develop", "octopus", 3L, false), ((int)r["item"]!, r["phase"]!.ToString(), r["runtime"]!.ToString(), (long)r["total"]!, (bool)r["live"]!));
        Assert.Equal(["one", "two", "three"], r["lines"]!.AsArray().Select(x => x!.ToString()).ToArray());
        await second.Append("cae-7.dev", ["four"]);
        await second.Flush(true, Time);
        Assert.Equal(4L, (await repo.Load("cae-7.dev"))!.Total);
    }

    [Fact]
    public async Task TheTranscriptVerbs_SpeakTheSealedForm_AndStoreRows()
    {
        if (NoServer) return;
        var seal = new TranscriptSeal("board-db-tests-secret");
        var verbs = new BoardVerbs(new BoardVerbParts { Transcripts = new SqlTranscriptRepository(Open), Seal = seal, Home = Home });
        var doc = new JsonObject
        {
            ["v"] = 1, ["name"] = "cae-8", ["item"] = 3, ["phase"] = "review", ["runtime"] = "cae-job", ["day"] = DateTime.UtcNow.ToString("yyyyMMdd"), ["started"] = DateTime.UtcNow.ToString("o"),
            ["ended"] = null, ["lastLine"] = DateTime.UtcNow.ToString("o"), ["dropped"] = 0L, ["total"] = 2L, ["lines"] = new JsonArray("a", "ü"),
        };
        var saved = await verbs.TranscriptSave(new JsonObject { ["name"] = "cae-8", ["day"] = DateTime.UtcNow.ToString("yyyyMMdd"), ["data"] = seal.Seal(doc) }.ToJsonString(), Time);
        Assert.Equal(0, saved.Code);
        Assert.Equal(1, await database!.Count("SELECT COUNT(*) FROM factory.Transcripts WHERE Name = 'cae-8' AND ItemKey = 'acme/orders#3' AND LinesJson LIKE '%a%'"));
        var loaded = (JsonObject)JsonNode.Parse((await verbs.TranscriptLoad("""{"name":"cae-8"}""", Time)).Output)!;
        var back = seal.Open(loaded["data"]!.ToString())!;
        Assert.Equal(("cae-8", 3, "review"), (back["name"]!.ToString(), (int)back["item"]!, back["phase"]!.ToString()));
        Assert.Equal(["a", "ü"], back["lines"]!.AsArray().Select(x => x!.ToString()).ToArray());
        var pruned = (JsonObject)JsonNode.Parse((await verbs.TranscriptPrune("""{"days":1}""", Time)).Output)!;
        Assert.Equal(0, (int)pruned["removed"]!);   // it started a moment ago: the last day stays
    }

    [Fact]
    public async Task AJournalLineThroughTheVerb_IsRecordedInTheItemsHistory_Once()
    {
        if (NoServer) return;
        var store = new MemoryJournal();
        var now = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var verbs = new BoardVerbs(new BoardVerbParts
        {
            Home = Home, Ledger = () => new JournalLedger(store, new JournalSettings(30), () => now, _ => { }), History = new JournalHistory(Open), Now = () => new DateTimeOffset(now),
        });
        var body = """{"item":7,"event":"tier","key":"t1","fields":{"name":"opus","reasons":"big"}}""";
        Assert.True((bool)JsonNode.Parse((await verbs.Journal(body, Time)).Output)!["posted"]!);
        Assert.False((bool)JsonNode.Parse((await verbs.Journal(body, Time)).Output)!["posted"]!);   // the same line again: the ledger refuses it, and the history has it once
        var lines = await new JournalHistory(Open).LinesOf("acme/orders#7");
        var line = Assert.Single(lines);
        Assert.Equal(("tier", "t1", true), (line.Event, line.Key, line.Posted));
        Assert.Equal(1, await database!.Count("SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = 'acme/orders#7' AND Type = 'journal.tier'"));
    }

    sealed class MemoryJournal : IJournalStore
    {
        readonly Dictionary<int, JsonObject> states = new(); long id = 100;
        public Task<ItemState> Read(int item, CancellationToken ct) => Task.FromResult(new ItemState { State = (JsonObject)(states.GetValueOrDefault(item)?.DeepClone() ?? new JsonObject()), Rev = 0 });
        public Task Save(int item, ItemState holder, CancellationToken ct) { states[item] = (JsonObject)holder.State.DeepClone(); return Task.CompletedTask; }
        public Task<long?> Post(int item, string body, CancellationToken ct) => Task.FromResult<long?>(++id);
        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => Task.FromResult(PatchOutcome.Ok);
    }
}
