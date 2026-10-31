using System.Text.Json.Nodes;
using Fworker.Persistence;
using Fworker.Workflows.PlantState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10k: the plant's hand-over in the database (script 240). The first group is the pure split of a snapshot into rows and the document and its join back; the second runs the real store on the SQL Server of
/// FACTORY_TEST_SQL (the SQL tests return at once where it is unset).
/// </summary>
public class PlantStateStoreTests
{
    static JsonObject Snapshot(string at = "2026-10-30T10:00:00.0000000Z") => (JsonObject)JsonNode.Parse($$"""
        {
          "v": 1, "at": "{{at}}",
          "assignments": [
            { "id": "a-1f2b", "item": 12, "kind": "own-item", "role": "owner", "column": "Development", "mode": "resume", "reason": "relaunch", "worker": "alice", "pr": 41, "pin": "alice",
              "accepted": true, "acceptedAt": "2026-10-30T09:58:00.0000000Z", "createdAt": "2026-10-30T09:50:00.0000000Z", "slot": "slot-7", "runtime": "cursor-cloud", "arm": "1.3" },
            { "id": "a-0c01", "item": 9, "kind": "own-item", "role": "owner", "column": "Technical Design", "mode": "first", "reason": "new", "worker": "", "pr": 0, "pin": "",
              "accepted": false, "acceptedAt": "", "createdAt": "2026-10-30T09:59:00.0000000Z", "slot": "", "runtime": "", "arm": "" },
            { "id": "a-77aa", "item": 12, "kind": "bot-job", "role": "inspector", "column": "Functional Testing", "mode": "first", "reason": "new", "worker": "bot-1", "pr": 41, "pin": "",
              "accepted": false, "acceptedAt": "", "createdAt": "2026-10-30T09:59:30.0000000Z", "slot": "", "runtime": "", "arm": "" }
          ],
          "owners": { "12": "alice", "30": "bob" },
          "bots": { "12": "bot-1" },
          "drained": ["carol"],
          "managerDrained": ["dave", "carol"],
          "managerMode": "advise", "managerSummaryDay": "2026-10-30",
          "ownedSince": { "12": "2026-10-30T09:00:00.0000000Z" },
          "owned": { "12": { "holder": "alice", "column": "Development", "leaseUntil": "2026-10-30T11:00:00.0000000Z", "heartbeat": null, "parked": false } },
          "copilotOwners": null, "cursorOwners": [ { "item": 30, "task": "t-1" } ],
          "digest": { "reported": [] },
          "epoch": 4, "holder": "host:1:abc", "final": true
        }
        """)!;

    [Fact]
    public void ASnapshotSplitsIntoRowsAndADocument_AndJoinsBackToTheSameSnapshot()
    {
        var snap = Snapshot();
        var rows = PlantStateSplit.Split(snap);
        Assert.Equal(["a-0c01", "a-1f2b", "a-77aa"], rows.Assignments.Select(a => a.Id).Order());
        var first = rows.Assignments.Single(a => a.Id == "a-1f2b");
        Assert.Equal((12, "own-item", "owner", "Development", "alice", 41, "alice", true, "slot-7", "cursor-cloud", "1.3"), (first.Item, first.Kind, first.Role, first.BoardColumn, first.Worker, first.PullRequest, first.Pin, first.Accepted, first.SlotId, first.SlotRuntime, first.ArmId));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 58, 0, TimeSpan.Zero), first.AcceptedAt);
        Assert.Equal([(12, "bot", "bot-1"), (12, "owner", "alice"), (30, "owner", "bob")], rows.Holders.Select(h => (h.Item, h.Kind, h.Holder)).Order());
        Assert.Equal([("carol", "manager"), ("carol", "operator"), ("dave", "manager")], rows.Drains.Select(d => (d.Name, d.Kind)).Order());
        // the rest is the document: not a row key among them
        Assert.Equal(["at", "copilotOwners", "cursorOwners", "digest", "epoch", "final", "holder", "managerMode", "managerSummaryDay", "owned", "ownedSince", "v"], rows.Document.Select(kv => kv.Key).Order());

        var back = PlantStateSplit.Join(rows.Assignments, rows.Holders, rows.Drains, rows.Document);
        Assert.Equal(["a-0c01", "a-1f2b", "a-77aa"], back["assignments"]!.AsArray().Select(a => a!["id"]!.ToString()).Order());
        Assert.Equal("alice", back["owners"]!["12"]!.ToString()); Assert.Equal("bob", back["owners"]!["30"]!.ToString()); Assert.Equal("bot-1", back["bots"]!["12"]!.ToString());
        Assert.Equal(["carol"], back["drained"]!.AsArray().Select(x => x!.ToString())); Assert.Equal(["carol", "dave"], back["managerDrained"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal("advise", back["managerMode"]!.ToString()); Assert.Equal(4, back["epoch"]!.GetValue<int>());
        // an assignment keeps every field the plant restores from (the slot of a saga item among them)
        var a1 = back["assignments"]!.AsArray().Single(a => a!["id"]!.ToString() == "a-1f2b")!;
        Assert.Equal("slot-7", a1["slot"]!.ToString()); Assert.Equal("cursor-cloud", a1["runtime"]!.ToString()); Assert.Equal("1.3", a1["arm"]!.ToString()); Assert.True(a1["accepted"]!.GetValue<bool>());
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T09:58:00Z"), DateTimeOffset.Parse(a1["acceptedAt"]!.ToString()));
        // a join of the rows in another order is the same text (the order of the rows is the database's, the join orders them)
        Assert.Equal(back.ToJsonString(), PlantStateSplit.Join(rows.Assignments.AsEnumerable().Reverse(), rows.Holders.AsEnumerable().Reverse(), rows.Drains.AsEnumerable().Reverse(), rows.Document).ToJsonString());
    }

    [Fact]
    public void WhatIsNotAnAssignmentOrAHolderIsLeftOut_AndTheColumnsAreCut()
    {
        var snap = (JsonObject)JsonNode.Parse("""
            { "v": 1, "assignments": [ { "id": "", "item": 3 }, { "id": "a-1", "item": 0 }, { "id": "a-2", "item": 4, "worker": "w" }, { "id": "a-2", "item": 5 }, "x", { "id": "a-3", "item": 6, "column": "%COL%" } ],
              "owners": { "x": "w", "0": "w", "7": "" }, "bots": {}, "drained": ["", "n", "n"] }
            """.Replace("%COL%", new string('c', 300)))!;
        var rows = PlantStateSplit.Split(snap);
        Assert.Equal(["a-2", "a-3"], rows.Assignments.Select(a => a.Id).Order());   // (a repeated id is the first)
        Assert.Equal(4, rows.Assignments.Single(a => a.Id == "a-2").Item);
        Assert.Equal(100, rows.Assignments.Single(a => a.Id == "a-3").BoardColumn.Length);
        Assert.Empty(rows.Holders);
        Assert.Equal([("n", "operator")], rows.Drains.Select(d => (d.Name, d.Kind)));
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    [SqlFact]
    public async Task TheStoreReplacesThePlantInOneTransaction_AndReadsItBack()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_plant_");
        var store = new SqlPlantStateStore(() => Open(db.ConnectionString));
        Assert.Null(await store.Load());   // nothing stored yet

        Assert.True(await store.Save(Snapshot(), 4, "host:1:abc"));
        var read = (await store.Load())!;
        Assert.Equal((4L, "host:1:abc"), (read.Epoch, read.Holder));
        Assert.Equal(["a-0c01", "a-1f2b", "a-77aa"], read.Snapshot["assignments"]!.AsArray().Select(a => a!["id"]!.ToString()).Order());
        Assert.Equal("alice", read.Snapshot["owners"]!["12"]!.ToString());
        Assert.Equal("cursor-cloud", read.Snapshot["assignments"]!.AsArray().Single(a => a!["id"]!.ToString() == "a-1f2b")!["runtime"]!.ToString());
        Assert.Equal("advise", read.Snapshot["managerMode"]!.ToString());
        Assert.Equal(3, await db.Count("SELECT COUNT(*) FROM factory.PlantAssignments")); Assert.Equal(3, await db.Count("SELECT COUNT(*) FROM factory.PlantHolders")); Assert.Equal(3, await db.Count("SELECT COUNT(*) FROM factory.PlantDrains"));

        // the next write replaces every row: what left the plant leaves the database
        var next = Snapshot("2026-10-30T10:05:00.0000000Z");
        next["assignments"] = new JsonArray(next["assignments"]!.AsArray().Where(a => a!["id"]!.ToString() == "a-0c01").Select(a => a!.DeepClone()).ToArray());
        next["owners"] = new JsonObject(); next["bots"] = new JsonObject(); next["drained"] = new JsonArray(); next["managerDrained"] = new JsonArray();
        Assert.True(await store.Save(next, 5, "host:2:def"));
        read = (await store.Load())!;
        Assert.Equal((5L, "host:2:def"), (read.Epoch, read.Holder));
        Assert.Equal(["a-0c01"], read.Snapshot["assignments"]!.AsArray().Select(a => a!["id"]!.ToString()));
        Assert.Empty(read.Snapshot["owners"]!.AsObject()); Assert.Empty(read.Snapshot["drained"]!.AsArray());
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.PlantAssignments")); Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM factory.PlantHolders")); Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.PlantState"));
    }

    [SqlFact]
    public async Task AWriterOfAnOlderEpoch_IsRefused_AndChangesNothing_ASameEpochWriterIsNot()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_plant_");
        var store = new SqlPlantStateStore(() => Open(db.ConnectionString));
        Assert.True(await store.Save(Snapshot(), 7, "newer"));
        var stale = Snapshot(); stale["owners"] = new JsonObject();
        Assert.False(await store.Save(stale, 6, "older"));   // a process that lost the lock
        var read = (await store.Load())!;
        Assert.Equal((7L, "newer"), (read.Epoch, read.Holder));
        Assert.Equal("alice", read.Snapshot["owners"]!["12"]!.ToString());
        Assert.True(await store.Save(stale, 7, "newer"));
        Assert.Empty((await store.Load())!.Snapshot["owners"]!.AsObject());
        // a process with no lease and no lock (epoch 0) has nothing to compare: it writes
        Assert.True(await store.Save(Snapshot(), 0, "no-lease"));
        Assert.Equal("alice", (await store.Load())!.Snapshot["owners"]!["12"]!.ToString());
    }

    [SqlFact]
    public async Task ALargeDocument_AndTwoStoresOnTheSameDatabase_AgreeOnThePlant()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_plant_");
        var a = new SqlPlantStateStore(() => Open(db.ConnectionString)); var b = new SqlPlantStateStore(() => Open(db.ConnectionString));
        var snap = Snapshot(); snap["digest"] = new JsonObject { ["reported"] = new JsonArray(Enumerable.Range(0, 2000).Select(i => (JsonNode)new JsonObject { ["key"] = $"parked|{i}|x", ["bucket"] = 1 }).ToArray()) };
        Assert.True(await a.Save(snap, 1, "a"));
        Assert.Equal(2000, (await b.Load())!.Snapshot["digest"]!["reported"]!.AsArray().Count);
    }
}
