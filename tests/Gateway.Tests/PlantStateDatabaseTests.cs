using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Persistence;
using Fworker.Workflows.PlantState;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10k on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one), the database built by the scripts: a gateway process stops, a new one starts on the same database, and the plant's assignments (the ids the
/// workers' item tokens name), owners, bot holders and drains are back, corrected from the saga's table (WorkItems) before the first sweep.
/// </summary>
public sealed class PlantStateDatabaseTests : IAsyncLifetime
{
    static bool NoServer => !TestDatabase.Available;
    static readonly RepoId Home = new("acme", "orders");
    TestDatabase? database;

    FactoryDbContext Open() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(database!.ConnectionString).Options);

    public async Task InitializeAsync() { if (!NoServer) database = await TestDatabase.CreateAsync("fw_w910k_"); }
    public async Task DisposeAsync() { if (database != null) await database.DisposeAsync(); }

    static Plant NewPlant() => new("secret", (_, _, _, _) => Task.CompletedTask, 30) { Msg = new MessagingSettings { Configured = true, Enabled = true } };

    static JsonObject Seed() => (JsonObject)JsonNode.Parse($$"""
        {
          "v": 1, "at": "{{DateTime.UtcNow:o}}",
          "assignments": [
            { "id": "a-1f2b", "item": 12, "kind": "own-item", "role": "owner", "column": "Development", "mode": "resume", "reason": "relaunch", "worker": "alice", "pr": 41, "pin": "alice",
              "accepted": true, "acceptedAt": "{{DateTime.UtcNow.AddMinutes(-2):o}}", "createdAt": "{{DateTime.UtcNow.AddMinutes(-9):o}}", "slot": "slot-7", "runtime": "cursor-cloud", "arm": "1.3" },
            { "id": "a-0c01", "item": 9, "kind": "own-item", "role": "owner", "column": "Technical Design", "mode": "first", "reason": "new", "worker": "", "pr": 0, "pin": "",
              "accepted": false, "acceptedAt": "", "createdAt": "{{DateTime.UtcNow.AddMinutes(-1):o}}" }
          ],
          "owners": { "12": "alice" }, "bots": { "12": "bot-1" }, "drained": ["carol"], "managerDrained": ["dave"]
        }
        """)!;

    static SnapshotCoordinator Coordinator(Plant plant, IPlantStateStore store) =>
        new(true, new GatewayLeader(new LeaderSettings { On = false }), store, plant, new SweepGap());

    [Fact]
    public async Task AGatewayThatRestarts_GetsItsPlantBackFromTheDatabase_CorrectedFromTheSagasTable()
    {
        if (NoServer) return;
        var sagas = new SqlWorkItemStore(Open);
        var store = new SqlPlantStateStore(Open);

        // the first process: its plant is written at its stop
        var before = NewPlant(); Assert.Equal((2, 1, ""), before.Restore(Seed(), 30));
        var first = Coordinator(before, store);
        await first.LoadOnceAsync();   // (nothing stored yet)
        Assert.True(await first.SaveAsync(true, TimeSpan.FromSeconds(30)));
        Assert.Equal(2, await database!.Count("SELECT COUNT(*) FROM factory.PlantAssignments"));

        // item 9's saga completes while no gateway is up
        var done = new WorkItemState { Item = new ItemId(Home, 9).ToString(), Generation = 1, Status = WorkItemStatus.Completed, Plan = "feature", Step = StepKind.Deliver, StartedAt = DateTimeOffset.UtcNow.AddHours(-2), CompletedAt = DateTimeOffset.UtcNow };
        Assert.True(await sagas.Save(done.Item, done, 0, default));
        var open = new WorkItemState { Item = new ItemId(Home, 12).ToString(), Generation = 1, Status = WorkItemStatus.Open, Plan = "feature", Step = StepKind.Develop, ArmId = "1.1", StartedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        Assert.True(await sagas.Save(open.Item, open, 0, default));

        // the second process, on the same database
        var after = NewPlant();
        var second = Coordinator(after, new SqlPlantStateStore(Open));
        var correction = new PlantRestoreCorrection(after, sagas, Home, (item, _) => Task.FromResult<string?>("alice"));   // (the claim of 12 still says alice)
        second.Correct = () => correction.Run();
        await second.RestoreAsync("start");

        var snap = after.Snapshot();
        Assert.Equal(["a-1f2b"], snap["assignments"]!.AsArray().Select(a => a!["id"]!.ToString()));   // the completed item's offer is gone, the accepted assignment is back with its id
        var kept = snap["assignments"]!.AsArray().Single()!;
        Assert.True(kept["accepted"]!.GetValue<bool>()); Assert.Equal("alice", kept["worker"]!.ToString()); Assert.Equal("slot-7", kept["slot"]!.ToString());
        Assert.Equal("alice", snap["owners"]!["12"]!.ToString()); Assert.Equal("bot-1", snap["bots"]!["12"]!.ToString());
        Assert.Equal(["carol"], snap["drained"]!.AsArray().Select(x => x!.ToString())); Assert.Equal(["dave"], snap["managerDrained"]!.AsArray().Select(x => x!.ToString()));
        var restore = second.Status()["restore"]!;
        Assert.Equal("sql", restore["how"]!.ToString());
        Assert.Equal([9], restore["corrected"]!["forgotten"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.True(second.Restored.IsCompleted);
    }
}
