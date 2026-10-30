using System.Text.Json.Nodes;
using Fworker.Persistence;
using Fworker.Workflows.Qc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: the table of QC environments (factory.QcEnvironments, script 012) as the saga's store, on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one) and in memory: the first save inserts, the next
/// replaces the record and raises the revision of the row, a record older than the stored one of the same attempt does not take the row back, the list leaves out what is torn down, and two writers at once end with one row.
/// The service on the SQL store leaves the copy of every record it saves.
/// </summary>
public class QcEnvironmentStoreTests : IAsyncLifetime
{
    TestDatabase? database;
    static bool NoServer => !TestDatabase.Available;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_w91_");
    }

    public async Task DisposeAsync()
    {
        if (database != null) await database.DisposeAsync();
    }

    static JsonObject Rec(string state, int rev, string sha = "aaaaaaa", int attempt = 1) => new() { ["state"] = state, ["rev"] = rev, ["sha"] = sha, ["attempt"] = attempt, ["pr"] = 9 };


    [Fact]
    public async Task TheFirstSaveInsertsTheRow_TheNextReplacesItAndRaisesTheRevision()
    {
        if (NoServer) return;
        var store = new SqlQcEnvironmentStore(Open);
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("requested", 1), default);
        var first = (await store.Load("o/r!9", default))!;
        Assert.Equal(("o/r!9", "o/r", "o/r#5", 1, "requested", 1), (first.Key, first.Repository, first.ItemKey, first.Revision, first.Status, QcJson.Int(first.State, "rev")));
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("assigned", 2), default);
        var second = (await store.Load("o/r!9", default))!;
        Assert.Equal((2, "assigned", 2), (second.Revision, second.Status, QcJson.Int(second.State, "rev")));
        Assert.Null(await store.Load("o/r!10", default));
    }

    [Fact]
    public async Task ARecordOlderThanTheStoredOneOfTheSameAttemptDoesNotTakeTheRowBack_ANewAttemptDoes()
    {
        if (NoServer) return;
        var store = new SqlQcEnvironmentStore(Open);
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("ready", 5), default);
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("building", 3), default);                  // a verb that lost the race to a later one
        Assert.Equal(("ready", 1), ((await store.Load("o/r!9", default))!.Status, (await store.Load("o/r!9", default))!.Revision));
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("requested", 1, attempt: 2), default);       // the next attempt starts again at revision 1
        Assert.Equal(("requested", 2), ((await store.Load("o/r!9", default))!.Status, (await store.Load("o/r!9", default))!.Revision));
    }

    [Fact]
    public async Task TheListOfARepositoryLeavesOutWhatIsTornDown_AndOtherRepositories()
    {
        if (NoServer) return;
        var store = new SqlQcEnvironmentStore(Open);
        await store.Save("o/r!1", "o/r", "o/r#1", Rec("ready", 3), default);
        await store.Save("o/r!2", "o/r", "o/r#2", Rec("failed", 3), default);
        await store.Save("o/r!3", "o/r", "o/r#3", Rec("torn-down", 4), default);
        await store.Save("x/y!1", "x/y", "x/y#1", Rec("ready", 3), default);
        Assert.Equal(["o/r!1", "o/r!2"], (await store.Live("o/r", default)).Select(e => e.Key).OrderBy(x => x));
    }

    [Fact]
    public async Task TwoWritersAtOnceLeaveOneRow_NeitherFails()
    {
        if (NoServer) return;
        var store = new SqlQcEnvironmentStore(Open);
        await Task.WhenAll(Enumerable.Range(1, 8).Select(i => store.Save("o/r!9", "o/r", "o/r#5", Rec(i % 2 == 0 ? "building" : "assigned", i, attempt: 1 + i / 100), default)));
        await using var db = Open();
        var row = await db.QcEnvironments.SingleAsync();
        Assert.Equal("o/r!9", row.PullRequestKey);
        Assert.Equal(row.Status, QcJson.Str(JsonNode.Parse(row.StateJson)!.AsObject(), "state"));
    }

    [Fact]
    public async Task TheServiceLeavesTheCopyOfEveryRecordItSaves_AndAdoptsItWhenTheCommentHasNone()
    {
        if (NoServer) return;
        var table = new SqlQcEnvironmentStore(Open);
        var h = new QcHarness(useTable: false);
        // the same service over the SQL store
        var service = new QcService(h.Config, h.World, h.World, h.World, h.Index, table, () => h.Now);
        h.World.AddValidatedItem();
        async Task<JsonObject> Call(string verb, object body) => (JsonObject)JsonNode.Parse((await service.Run(verb, System.Text.Json.JsonSerializer.Serialize(body))).Output)!;
        Assert.Equal("started", (await Call("qc-request", new { item = 5, pr = 9 }))["outcome"]!.ToString());
        Assert.Equal(0, (int)(await Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw1", assignment = "a-1" }))["exit"]!);
        var row = (await table.Load("o/r!9", default))!;
        Assert.Equal(("assigned", "o/r#5"), (row.Status, row.ItemKey));
        Assert.Equal(h.World.Rec(5, 9)!.ToJsonString(), row.State.ToJsonString());
        Assert.True(row.Revision >= 2);
        // the comment loses the record; the next verb finds it in the table and writes it back
        h.World.State(5).Remove("qc");
        var view = await Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal("assigned", QcJson.Str((JsonObject)view["record"]!, "state"));
        var reset = await Call("qc-reset", new { item = 5, pr = 9 });
        Assert.Equal("reset", reset["outcome"]!.ToString());
        Assert.Equal("reset", QcJson.Str(h.World.Rec(5, 9), "state"));
        Assert.Equal("reset", (await table.Load("o/r!9", default))!.Status);
    }
}

/// <summary>The same contract in memory: the store the tests of the service and a gateway without a database use.</summary>
public sealed class InMemoryQcEnvironmentStoreTests
{
    static JsonObject Rec(string state, int rev, int attempt = 1) => new() { ["state"] = state, ["rev"] = rev, ["sha"] = "aaaaaaa", ["attempt"] = attempt, ["pr"] = 9 };

    [Fact]
    public async Task SavesLoadsAndLists_AsTheTableDoes()
    {
        var store = new InMemoryQcEnvironmentStore();
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("ready", 5), default);
        await store.Save("o/r!9", "o/r", null, Rec("building", 3), default);                    // older: kept out
        var e = (await store.Load("o/r!9", default))!;
        Assert.Equal(("ready", "o/r#5", 1), (e.Status, e.ItemKey, e.Revision));
        await store.Save("o/r!9", "o/r", null, Rec("torn-down", 6), default);
        Assert.Equal((2, "o/r#5"), ((await store.Load("o/r!9", default))!.Revision, (await store.Load("o/r!9", default))!.ItemKey));   // (the item stays when a save names none)
        Assert.Empty(await store.Live("o/r", default));
        Assert.Null(await store.Load("nope", default));
    }

    [Fact]
    public async Task ALoadedStateIsACopy()
    {
        var store = new InMemoryQcEnvironmentStore();
        await store.Save("o/r!9", "o/r", "o/r#5", Rec("ready", 5), default);
        (await store.Load("o/r!9", default))!.State["state"] = "changed";
        Assert.Equal("ready", QcJson.Str((await store.Load("o/r!9", default))!.State, "state"));
    }

    [Fact]
    public void TheKeysAreOwnerRepoBangNumber_InLowerCase()
    {
        Assert.Equal("clearmeasure-aisf-sample-apps/cmdemo1-workorders!353", QcKeys.For("ClearMeasure-AISF-Sample-Apps", "cmdemo1-Workorders", 353));
        Assert.Equal("o/r#5", QcKeys.ItemKey("O", "R", 5));
    }

    [Fact]
    public void AStatusIsAtMost16Characters_TheColumn()
    {
        Assert.Equal("torn-down", SqlQcEnvironmentStore.Status(Rec("torn-down", 1)));
        Assert.Equal(16, SqlQcEnvironmentStore.Status(Rec(new string('x', 40), 1)).Length);
    }
}
