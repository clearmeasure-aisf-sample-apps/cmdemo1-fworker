using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W3.2 on a Wolverine tracked session (the pattern of RuntimeCommandTrackedTests): the journal lines of an item, published in the order of the plant's chain, reach the
/// handler in that order even when the lines of different items run side by side, because the queue is partitioned by item (gateway/Runtimes/JournalCommandService.cs
/// configures the same). They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class JournalCommandTrackedTests
{
    static bool NoServer => !TrackedSessionHost.Available;

    sealed class Seen { public readonly List<(int Item, string Event)> Lines = new(); }

    sealed class LineHandler(Seen seen) : MessageHandler<WriteJournalLine>
    {
        protected override async Task HandleAsync(WriteJournalLine m, MessageContext context, CancellationToken cancellation)
        {
            // the first line of an item is the slow one: without the partitioning the later lines of that item would overtake it
            if (m.Event == "matched") await Task.Delay(150, cancellation);
            lock (seen.Lines) seen.Lines.Add((m.Item, m.Event));
        }
    }

    static Action<WolverineOptions> Wire(Seen seen) => w =>
    {
        w.AddMessageHandler(new LineHandler(seen));
        w.MessagePartitioning.ByMessage<WriteJournalLine>(m => m.Item.ToString());
        w.MessagePartitioning.PublishToPartitionedLocalMessaging("journal-lines", 4, t => { t.Message<WriteJournalLine>(); t.ConfigureQueues(q => q.UseDurableInbox()); });
    };

    static WriteJournalLine Line(int item, string ev) => new(JournalKeys.For(item, ev, "k"), item, ev, new JsonObject(), "k");

    [Fact]
    public async Task TheLinesOfAnItem_RunInTheOrderTheyWerePublished_WhileOtherItemsRunBesideThem()
    {
        if (NoServer) return;
        var seen = new Seen();
        await using var h = await TrackedSessionHost.StartAsync(Wire(seen));
        string[] order = ["matched", "offered", "launched", "assigned"];
        // all lines are published at once, item by item in the plant's order, and the session waits until every one has been handled
        await h.Host.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async bus =>
        {
            foreach (var ev in order) foreach (var item in new[] { 1, 2, 3, 4, 5, 6 }) await bus.PublishAsync(Line(item, ev));
        }));
        foreach (var item in new[] { 1, 2, 3, 4, 5, 6 })
            Assert.Equal(order, seen.Lines.Where(l => l.Item == item).Select(l => l.Event).ToArray());
    }
}
