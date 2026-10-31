using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W3.2: the sink that the plant's journal delegate calls under the V2 engine (gateway/Runtimes/JournalCommandService.cs). The plant's chain
/// (Plant.Chain, ReserveLaunched) hands it the lines in the order it decided them, and the sink publishes them in that order; a line becomes one command keyed by
/// (item, event, key), the keyless ones by the ticks of the decision, as the verb keys them by the ticks of the write.
/// </summary>
public sealed class JournalCommandSinkTests
{
    static (JournalCommandSink Sink, List<WriteJournalLine> Sent, Func<DateTime> Clock, Action<DateTime> Set) Make(bool up = true, double waitingMinutes = 30)
    {
        var sent = new List<WriteJournalLine>(); var now = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
        var sink = new JournalCommandSink(c => { if (up) sent.Add(c); return Task.FromResult(up); }, waitingMinutes, () => now);
        return (sink, sent, () => now, t => now = t);
    }

    [Fact]
    public async Task ALineWithAKey_IsPublishedWithThatKey_AndACopyOfItsFields()
    {
        var (sink, sent, _, _) = Make();
        var fields = new JsonObject { ["worker"] = "w1", ["type"] = "persistent-local", ["ackSeconds"] = 120.0 };
        await sink.Send(7, "offered", fields, "off|a-1|w1");
        var c = Assert.Single(sent);
        Assert.Equal(("offered", 7, "off|a-1|w1"), (c.Event, c.Item, c.Key));
        Assert.Equal(JournalKeys.For(7, "offered", "off|a-1|w1"), c.IdempotencyKey);
        fields["worker"] = "changed after";
        Assert.Equal("w1", c.Fields["worker"]!.ToString());
    }

    [Fact]
    public async Task LinesAreSentInTheOrderOfTheCalls()
    {
        var (sink, sent, _, _) = Make();
        foreach (var ev in new[] { "matched", "offered", "launched", "assigned" }) await sink.Send(3, ev, new JsonObject(), ev + "|k");
        Assert.Equal(["matched", "offered", "launched", "assigned"], sent.Select(s => s.Event).ToArray());
    }

    [Fact]
    public async Task AKeylessLine_TakesTheTicksOfTheDecision()
    {
        var (sink, sent, clock, _) = Make();
        await sink.Send(3, "skipped", new JsonObject { ["reason"] = "x" }, "");
        Assert.Equal(clock().Ticks.ToString(), sent[0].Key);
        Assert.Contains(sent[0].Key, sent[0].IdempotencyKey);
    }

    [Fact]
    public async Task TwoKeylessLinesOfOneKind_AreTwoCommands_NeverOne_AsAddJournalTakesTheTicksOfAnEmptyKey()
    {
        var (sink, sent, clock, set) = Make();
        await sink.Send(3, "released", new JsonObject { ["worker"] = "w1" }, "");
        set(clock().AddSeconds(1));
        await sink.Send(3, "released", new JsonObject { ["worker"] = "w2" }, "");
        Assert.Equal(2, sent.Count);
        Assert.NotEqual(sent[0].Key, sent[1].Key);
        Assert.NotEqual(sent[0].IdempotencyKey, sent[1].IdempotencyKey);
        Assert.All(sent, c => Assert.Matches(@"^\d{10,}$", c.Key));   // never "" (the key "released|" of the script's old rule)
    }

    [Fact]
    public async Task Waiting_IsSentOncePerWindowPerItem_AndTheEarlierOneIsFreeAgainWhenThePublishFails()
    {
        var (sink, sent, _, set) = Make(waitingMinutes: 30);
        await sink.Send(3, "waiting", new JsonObject { ["reason"] = "a" }, "");
        await sink.Send(3, "waiting", new JsonObject { ["reason"] = "a" }, "");
        await sink.Send(4, "waiting", new JsonObject { ["reason"] = "a" }, "");
        Assert.Equal([3, 4], sent.Select(s => s.Item).ToArray());
        set(new DateTime(2026, 10, 27, 9, 31, 0, DateTimeKind.Utc));
        await sink.Send(3, "waiting", new JsonObject { ["reason"] = "b" }, "");
        Assert.Equal(3, sent.Count);
        Assert.NotEqual(sent[0].Key, sent[2].Key);

        var (down, _, _, _) = Make(up: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => down.Send(3, "waiting", new JsonObject(), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => down.Send(3, "waiting", new JsonObject(), ""));   // not held back by the failed one
    }

    [Fact]
    public async Task AHostThatIsNotUp_FailsTheLine_ForThePlantToLog()
    {
        var (sink, _, _, _) = Make(up: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.Send(3, "offered", new JsonObject(), "k"));
    }
}
