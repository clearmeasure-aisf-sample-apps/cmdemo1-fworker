using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Integrations.Correlation;
using Fworker.Persistence;
using Fworker.Workflows.Delivery;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W5.0 on a Wolverine tracked session: IngressInboxMessage goes through the durable local queue to a handler that runs the deriver on the SQL store and publishes each ItemMerged
/// through the outbox (the gateway's handler, gateway/Ports/ItemMergedPort.cs, is the same call). They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class ItemMergedTrackedTests
{
    sealed class DeriveHandler(ItemMergedDeriver deriver) : MessageHandler<IngressInboxMessage>
    {
        protected override async Task HandleAsync(IngressInboxMessage m, MessageContext context, CancellationToken cancellation)
        {
            var d = await deriver.Derive(m.Source, m.EventJson, context.Envelope!.Id, cancellation);
            foreach (var merged in d.Merged) await context.PublishAsync(merged);
        }
    }

    sealed class CaptureHandler(List<ItemMerged> seen) : MessageHandler<ItemMerged>
    {
        protected override Task HandleAsync(ItemMerged m, MessageContext context, CancellationToken cancellation) { lock (seen) seen.Add(m); return Task.CompletedTask; }
    }

    sealed class DeliveredHandler(DeliveredToTable table) : MessageHandler<DeliveredTo>
    {
        protected override Task HandleAsync(DeliveredTo m, MessageContext context, CancellationToken cancellation) => table.Record(m, DateTimeOffset.UtcNow, cancellation);
    }

    sealed class UndeliveredHandler(DeliveredToTable table) : MessageHandler<Undelivered>
    {
        protected override Task HandleAsync(Undelivered m, MessageContext context, CancellationToken cancellation) => table.Remove(m, cancellation);
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static Func<string, Action<WolverineOptions>> Wire(ItemMergedDeriverTests.FakeReader reader, List<ItemMerged> seen) => cs => w =>
    {
        w.AddMessageHandler(new DeriveHandler(new ItemMergedDeriver(reader, new SqlMergedItemStore(() => Open(cs)), ItemMergedDeriverTests.Home, () => "master", new("Auto Merge", "video", ["hold:owner"]))));
        w.AddMessageHandler(new CaptureHandler(seen));
        w.AddMessageHandler(new DeliveredHandler(new DeliveredToTable(() => Open(cs))));
        w.AddMessageHandler(new UndeliveredHandler(new DeliveredToTable(() => Open(cs))));
        w.PublishMessage<IngressInboxMessage>().ToLocalQueue("harness");
        w.PublishMessage<ItemMerged>().ToLocalQueue("harness-merged");
        w.PublishMessage<DeliveredTo>().ToLocalQueue("harness-delivered");
        w.PublishMessage<Undelivered>().ToLocalQueue("harness-delivered");
    };

    [Fact]
    public async Task WebhookReconcilerAndSecondWebhook_GiveOneItemMerged_WithGatesAndDependencies()
    {
        if (!TrackedSessionHost.Available) return;
        var seen = new List<ItemMerged>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(ItemMergedDeriverTests.Reader(), seen));
        await h.TrackSend(new IngressInboxMessage("webhook", ItemMergedDeriverTests.BaseMerged()));
        await h.TrackSend(new IngressInboxMessage("poll", ItemMergedDeriverTests.ClosedCard()));
        await h.TrackSend(new IngressInboxMessage("webhook", ItemMergedDeriverTests.PrMerged()));
        await h.TrackSend(new IngressInboxMessage("webhook", ItemMergedDeriverTests.BaseMerged("d-redelivered")));
        var m = Assert.Single(seen);
        Assert.Equal(("acme/orders#7", ItemMergedDeriverTests.Sha, "webhook"), (m.Item.ToString(), m.Commit, m.Source));
        Assert.Equal(["acme/orders#5", "other/repo#12"], m.Dependencies.Select(d => d.ToString()));
        Assert.True(m.Gates.Qualifies);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.MergedItems WHERE ItemKey = 'acme/orders#7'"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task TheReconcilerFirst_GivesTheMessage_AndTheWebhookAfterItGivesNone()
    {
        if (!TrackedSessionHost.Available) return;
        var seen = new List<ItemMerged>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(ItemMergedDeriverTests.Reader(), seen));
        await h.TrackSend(new IngressInboxMessage("poll", ItemMergedDeriverTests.ClosedCard()));
        await h.TrackSend(new IngressInboxMessage("webhook", ItemMergedDeriverTests.BaseMerged()));
        Assert.Equal("poll", Assert.Single(seen).Source);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.MergedItems WHERE Source = 'poll'"));
    }

    [Fact]
    public async Task TwoItemsOfOneCommit_AreTwoRows_AndTheRowKeepsGatesAndDependenciesAsJson()
    {
        if (!TrackedSessionHost.Available) return;
        var reader = ItemMergedDeriverTests.Reader();
        reader.Pulls[9] = reader.Pulls[9] with { Body = "Closes #7\nCloses #8" };
        reader.Items[8] = ItemMergedDeriverTests.Facts(8, body: "Depends on #7");
        var seen = new List<ItemMerged>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(reader, seen));
        await h.TrackSend(new IngressInboxMessage("webhook", ItemMergedDeriverTests.BaseMerged()));
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, await h.Count($"SELECT COUNT(*) FROM factory.MergedItems WHERE [Commit] = '{ItemMergedDeriverTests.Sha}'"));
        await using var db = Open(h.ConnectionString);
        var row = await db.MergedItems.SingleAsync(x => x.ItemKey == "acme/orders#8");
        Assert.Contains("acme/orders#7", row.DependenciesJson);
        Assert.Contains("\"AutoMerge\":false", row.GatesJson);
    }

    [Fact]
    public async Task TheDeliveredToTable_FollowsDeliveredToAndUndelivered_AndAnswersTheDependenciesSlot()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire(ItemMergedDeriverTests.Reader(), []));
        var table = new DeliveredToTable(() => Open(h.ConnectionString));
        ItemId A = new(ItemMergedDeriverTests.Home, 5), B = new(ItemMergedDeriverTests.Home, 6);
        await h.TrackSend(new DeliveredTo(A, "tdd", "1.0.10"));
        await h.TrackSend(new DeliveredTo(A, "tdd", "1.0.11"));   // a later release replaces the earlier
        await h.TrackSend(new DeliveredTo(B, "tdd", "1.0.11"));
        await h.TrackSend(new DeliveredTo(A, "uat", "1.0.11"));
        Assert.Equal("1.0.11", await table.ReleaseOf(A, "TDD"));
        Assert.Empty(await table.NotYetDelivered([A, B], "tdd"));
        Assert.Equal([B], await table.NotYetDelivered([A, B], "uat"));
        await h.TrackSend(new Undelivered(B, "tdd"));
        Assert.Null(await table.ReleaseOf(B, "tdd"));
        Assert.Equal([B], await table.NotYetDelivered([A, B], "tdd"));
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo"));   // A in tdd and uat; B gone
    }
}
