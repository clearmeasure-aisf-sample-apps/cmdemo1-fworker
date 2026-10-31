using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.9b on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), with the database built by the scripts (290 is the engine's markers): the markers, the two small additions to the delivery stores, and the
/// whole adoption over the SQL stores, started twice (the second is the restart: nothing is written again).
/// </summary>
public class AdoptionSqlTests : IAsyncLifetime
{
    static readonly RepoId Home = Item.Repository;
    TestDatabase? database;

    static bool NoServer => !TestDatabase.Available;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (NoServer) return;
        database = await TestDatabase.CreateAsync("fw_w99b_");
    }

    public async Task DisposeAsync()
    {
        if (database != null) await database.DisposeAsync();
    }

    [Fact]
    public async Task TheEngineMarkers_AreWrittenAndReplaced_AndRead()
    {
        if (NoServer) return;
        var markers = new SqlEngineMarkers(Open);
        Assert.Equal(new EngineMarks(null, null), await markers.Read(default));
        await markers.Mark(EngineMarkerNames.Acted, Now.AddMinutes(-2), "node-a", "epoch 4", default);
        await markers.Mark(EngineMarkerNames.Acted, Now, "node-b", "epoch 5", default);
        await markers.Mark(EngineMarkerNames.V1Ran, Now.AddHours(-1), "node-c", "FACTORY_V2_OFF", default);
        Assert.Equal(new EngineMarks(Now, Now.AddHours(-1)), await markers.Read(default));
        await using var db = Open();
        var acted = await db.EngineMarkers.SingleAsync(m => m.Name == EngineMarkerNames.Acted);
        Assert.Equal(("node-b", "epoch 5"), (acted.Holder, acted.Detail));
    }

    [Fact]
    public async Task DeliveredToIsRecordedOnlyWhereTheTableHasNoRow_AndTheMergedItemsAreHeldByTheirPairs()
    {
        if (NoServer) return;
        var table = new DeliveredToTable(Open);
        var item = new ItemId(Home, 7);
        Assert.True(await table.RecordIfAbsent(new DeliveredTo(item, "tdd", "1.0.7"), Now, default));
        Assert.False(await table.RecordIfAbsent(new DeliveredTo(item, "tdd", "1.0.8"), Now, default));
        Assert.Equal("1.0.7", await table.ReleaseOf(item, "tdd"));
        var merged = new SqlMergedItemStore(Open);
        await merged.TryRecordAll([new MergedItem { ItemKey = "acme/orders#7", Commit = Sha, PullRequest = 11, GatesJson = "{}", DependenciesJson = "[]", Source = "adopted", MergedAt = Now, MessageId = Guid.NewGuid() }], default);
        var held = await merged.Held([("acme/orders#7", Sha), ("acme/orders#7", new string('e', 40)), ("acme/orders#8", Sha)], default);
        Assert.Equal([$"acme/orders#7|{Sha}"], held);
    }

    [Fact]
    public async Task TheWholeAdoption_OverTheSqlStores_IsIdempotentAcrossARestart()
    {
        if (NoServer) return;
        var reads = new AdoptionRunnerTests.FakeReads();
        void Put(int n, string column, System.Text.Json.Nodes.JsonObject? state)
        {
            reads.Board.Add(new BoardCard(Home, n, column, true, $"item {n}"));
            reads.Records[n] = new V1ItemRecord(true, ["factory worker", "Auto Merge"], "The body.", Now.AddDays(-2), state, false);
        }
        Put(7, Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))));
        Put(8, Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")));
        Put(9, "Todo", null);
        var trains = new SqlTrainStore(Open);
        var stored = new SqlWorkItemStore(Open);
        var published = new List<object>(); var scheduled = new List<object>();
        AdoptionRunner Runner() => new(new AdoptionOptions(Home, Settings(), () => ["cmdemo1-ui/tdd"]),
            new AdoptionPorts(reads, stored, new AdoptionRunnerTests.Projected(), new SqlCapacityStore(Open), new SqlMergeTrainStore(Open), trains, new SqlQcEnvironmentStore(Open), new SqlMergedItemStore(Open),
                new DeliveredBackfillTable(new DeliveredToTable(Open)), null, m => { published.Add(m); return Task.FromResult(true); }, (m, _) => { scheduled.Add(m); return Task.FromResult(true); }), () => Now);

        var first = await Runner().Run(false, "", () => true, default);
        Assert.Equal(("done", 2, 1), (first.Outcome, first.Adopted, first.Skipped));
        Assert.Equal(["acme/orders#7", "acme/orders#8"], (await stored.OwnedItems(default)).Order());
        Assert.Equal(["acme/orders#7"], (await new SqlCapacityStore(Open).Load(CapacityKeys.Factory, default))!.State.Slots.Keys);
        await using (var db = Open()) Assert.Equal(1, await db.MergedItems.CountAsync());
        var revisions = new[] { (await stored.Load("acme/orders#7", default))!.Revision, (await stored.Load("acme/orders#8", default))!.Revision };
        var ledgerRevision = (await new SqlCapacityStore(Open).Load(CapacityKeys.Factory, default))!.Revision;

        // the restart: a new process, the same database; the second adoption finds both rows in place
        var second = await Runner().Run(false, "", () => true, default);
        Assert.Equal(("done", 0, 2), (second.Outcome, second.Adopted, second.Kept));
        Assert.Equal(revisions, new[] { (await stored.Load("acme/orders#7", default))!.Revision, (await stored.Load("acme/orders#8", default))!.Revision });
        Assert.Equal(ledgerRevision, (await new SqlCapacityStore(Open).Load(CapacityKeys.Factory, default))!.Revision);
        await using var after = Open();
        Assert.Equal(1, await after.MergedItems.CountAsync());
        Assert.Equal(2, await after.WorkItems.CountAsync());
    }
}
