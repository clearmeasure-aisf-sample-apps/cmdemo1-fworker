using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W0.9 (docs/design/delivery-speed.md 3.1, 3.3, 4.1): the ranking comparer and the plan on a freed slot.
/// The ranking tests pin the order Plant.Plan sorted inline before the comparer existed (pinned, priority label, past-intake): the comparer
/// must give exactly that order, which a differential test against the old expression checks over many generated sweeps.
/// </summary>
public sealed class PlanRankingTests
{
    const string Intake = "Technical Design";

    static JsonArray Offers(params (int Item, string Column)[] rows)
        => new(rows.Select(r => (JsonNode)new JsonObject { ["item"] = r.Item, ["column"] = r.Column, ["kind"] = "own-item" }).ToArray());

    static int[] OrderOf(JsonArray offers, JsonObject? pins = null, JsonObject? prios = null)
        => PlanRankComparer.Order(offers, pins, prios, Intake).Select(o => o!["item"]!.GetValue<int>()).ToArray();

    // the expression Plant.Plan used before W0.9, verbatim
    static int[] LegacyOrder(JsonArray offers, JsonObject? pins, JsonObject? priorities)
    {
        static int Rank(string? p) => p == "top" ? 0 : p == "high" ? 1 : 2;
        return offers.OrderBy(x => string.IsNullOrEmpty(pins?[x!["item"]!.ToString()]?.ToString()) ? 1 : 0).ThenBy(x => Rank(priorities?[x!["item"]!.ToString()]?.ToString()))
            .ThenBy(x => string.Equals(x!["column"]?.ToString() ?? "", Intake, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(o => o!["item"]!.GetValue<int>()).ToArray();
    }

    [Fact]
    public void NoPinsNoPriorities_KeepsTheSweepsOrder_WithinTheColumnRank()
    {
        var o = Offers((1, Intake), (2, "Development"), (3, Intake), (4, "Functional Testing"), (5, "Development"));
        Assert.Equal(new[] { 2, 4, 5, 1, 3 }, OrderOf(o));
    }

    [Fact]
    public void PinnedFirst_ThenPriority_ThenStartedBeforeNew()
    {
        var o = Offers((1, Intake), (2, "Development"), (3, Intake), (4, Intake), (5, "Development"), (6, Intake));
        var pins = new JsonObject { ["6"] = "worker-a" };
        var prios = new JsonObject { ["3"] = "top", ["4"] = "high", ["5"] = "high" };
        Assert.Equal(new[] { 6, 3, 5, 4, 2, 1 }, OrderOf(o, pins, prios));
    }

    [Fact]
    public void PinnedBeatsTopPriority_AndAnEmptyPinIsNoPin()
    {
        var o = Offers((1, "Development"), (2, "Development"), (3, "Development"));
        var pins = new JsonObject { ["2"] = "w", ["3"] = "" };
        var prios = new JsonObject { ["1"] = "top", ["3"] = "top" };
        Assert.Equal(new[] { 2, 1, 3 }, OrderOf(o, pins, prios));
    }

    [Fact]
    public void IntakeColumn_IsComparedWithoutCase_AndAnUnknownPriorityIsNone()
    {
        Assert.Equal(new[] { 2, 1 }, OrderOf(Offers((1, "technical design"), (2, "Development"))));
        Assert.Equal(new[] { 1, 2 }, OrderOf(Offers((1, Intake), (2, Intake)), null, new JsonObject { ["2"] = "urgent" }));
    }

    [Fact]
    public void ColumnMissing_CountsAsPastIntake()
    {
        var o = new JsonArray(new JsonObject { ["item"] = 1 }, new JsonObject { ["item"] = 2, ["column"] = Intake });
        Assert.Equal(new[] { 1, 2 }, OrderOf(o));
    }

    [Fact]
    public void SameOrderAsTheOldInlineSort_ForManyGeneratedSweeps()
    {
        var rnd = new Random(297);
        string[] cols = { Intake, "Development", "Functional Testing", "Todo" };
        string?[] prios = { null, "top", "high", "low", "" };
        for (var n = 0; n < 300; n++)
        {
            var count = rnd.Next(0, 25);
            var offers = new JsonArray(Enumerable.Range(1, count).Select(i => (JsonNode)new JsonObject { ["item"] = i, ["column"] = cols[rnd.Next(cols.Length)], ["kind"] = "own-item" }).ToArray());
            var pins = new JsonObject(); var pr = new JsonObject();
            for (var i = 1; i <= count; i++)
            {
                if (rnd.Next(4) == 0) pins[i.ToString()] = rnd.Next(2) == 0 ? "w" + i : "";
                if (prios[rnd.Next(prios.Length)] is { } p) pr[i.ToString()] = p;
            }
            Assert.Equal(LegacyOrder(offers, pins, pr), OrderOf(offers, pins, pr));
            Assert.Equal(LegacyOrder(offers, null, null), OrderOf(offers));
        }
    }

    static void Own(Plant p, int item, string owner) => p.TestSetRoles(item, owner, null, null);

    static Plant NewPlant() => new("secret", (_, _, _, _) => Task.CompletedTask, 30);

    static void Wait(Plant p, params int[] items)
        => p.Plan(Offers(items.Select(i => (i, "Development")).ToArray()), null, null);

    [Fact]
    public void SlotFreed_AsksForTheQueuedItems_NotTheFreedOne()
    {
        var p = NewPlant(); var asked = new List<int>(); p.SweepRequest = asked.Add;
        Wait(p, 11, 12, 13);
        Own(p, 5, "w1");
        Assert.Empty(asked);
        Own(p, 5, "");
        Assert.Equal(new[] { 11, 12, 13 }, asked);
    }

    [Fact]
    public void SlotFreed_QueuedItemsComeInPlanOrder_AndAreCapped()
    {
        var p = NewPlant(); var asked = new List<int>(); p.SweepRequest = asked.Add;
        p.Plan(Offers((1, Intake), (2, "Development"), (3, Intake), (4, "Development"), (5, "Development"), (6, Intake), (7, "Functional Testing")), null, null);
        Own(p, 99, "w1"); Own(p, 99, "");
        Assert.Equal(5, asked.Count);
        Assert.Equal(new[] { 2, 4, 5, 7 }, asked.Take(4).OrderBy(i => i));
        Assert.DoesNotContain(1, asked.Take(4));
    }

    [Fact]
    public void SlotFreed_NothingQueued_OrNotLeader_AsksForNothing()
    {
        var p = NewPlant(); var asked = new List<int>(); p.SweepRequest = asked.Add;
        Own(p, 5, "w1"); Own(p, 5, "");
        Assert.Empty(asked);
        Wait(p, 11);
        p.MayAct = () => false;
        Own(p, 5, "w1"); Own(p, 5, "");
        Assert.Empty(asked);
        p.MayAct = () => true;
        Own(p, 5, "w1"); Own(p, 5, "");
        Assert.Equal(new[] { 11 }, asked);
    }

    [Fact]
    public void SlotFreed_ARuntimeExecutionEnding_AsksForTheQueuedItems()
    {
        var p = NewPlant(); var asked = new List<int>(); p.SweepRequest = asked.Add;
        Wait(p, 21, 22);
        p.RuntimeEnded("fw-1", 20, "exited");
        Assert.Equal(new[] { 21, 22 }, asked);
    }

    [Fact]
    public void FreedSlot_GivesTheNextQueuedItem_WithoutAFullSweep()
    {
        var p = NewPlant();
        var (_, ch, _) = p.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        Own(p, 5, "w1");                                   // w1's only slot is taken by item 5
        p.Plan(Offers((7, "Development")), null, null);    // item 7 waits: every session is at capacity
        while (ch.Reader.TryRead(out _)) { }
        // the hub's sweep request for an item becomes the incremental pass of that item, as the sweeper does it (no full sweep runs)
        p.SweepRequest = item => p.Plan(Offers((item, "Development")), null, null, scope: new[] { item });
        Own(p, 5, "");                                     // item 5 ends: the slot is free
        Assert.True(ch.Reader.TryRead(out var msg), "the freed slot offered nothing");
        Assert.Contains("item.assigned", msg.Item2);
        Assert.Contains("\"item\":7", msg.Item2);
        Assert.Equal(1, p.SlotFreedRequests);
    }

    [Fact]
    public void WithoutTheDelegate_NothingHappens()
    {
        var p = NewPlant(); Wait(p, 1);
        Own(p, 5, "w1"); Own(p, 5, "");
        Assert.Equal(0, p.SlotFreedRequests);
    }
}
