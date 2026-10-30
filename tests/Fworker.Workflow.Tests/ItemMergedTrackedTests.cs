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

    [SqlFact]
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

    [SqlFact]
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

    [SqlFact]
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

    [SqlFact]
    public async Task TheSqlStore_ClaimsPerItemAndCommit_InOneTransaction_AndAnItemTheOtherMessageHoldsDoesNotHideTheOthers()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync((Action<WolverineOptions>?)null);
        var store = new SqlMergedItemStore(() => Open(h.ConnectionString));
        MergedItem Row(int item, Guid by, string commit = ItemMergedDeriverTests.Sha) => new() { ItemKey = $"acme/orders#{item}", Commit = commit, PullRequest = 9, Source = "webhook", MessageId = by, GatesJson = "{}", DependenciesJson = "[]", MergedAt = DateTimeOffset.UtcNow };
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.Single(await store.TryRecordAll([Row(7, first)], default));
        Assert.True(await store.TakenByOther("acme/orders#7", ItemMergedDeriverTests.Sha, second, default));
        Assert.False(await store.TakenByOther("acme/orders#7", ItemMergedDeriverTests.Sha, first, default));
        Assert.False(await store.TakenByOther("acme/orders#8", ItemMergedDeriverTests.Sha, second, default));   // the commit is not the claim, the item is
        Assert.True(await store.PullRequestTakenByOther("acme/orders#7", 9, second, default));
        Assert.False(await store.PullRequestTakenByOther("acme/orders#7", 10, second, default));
        Assert.False(await store.PullRequestTakenByOther("acme/orders#8", 9, second, default));
        // the second message records 7 (held by the first: left out) and 8 (new) together; a redelivery of the first gets 7 back
        Assert.Equal(["acme/orders#8"], (await store.TryRecordAll([Row(7, second), Row(8, second)], default)).Select(r => r.ItemKey));
        Assert.Equal(["acme/orders#7"], (await store.TryRecordAll([Row(7, first)], default)).Select(r => r.ItemKey));
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.MergedItems"));
        // another message claims item 12 between this one's look and its save: the key violation fails the whole transaction, then the rows go one by one and the holder of each decides
        var third = Guid.NewGuid(); var racer = Guid.NewGuid(); var raced = false;
        var racing = new SqlMergedItemStore(() => new RacingDb(h.ConnectionString, () =>
        {
            if (raced) return; raced = true;
            using var other = Open(h.ConnectionString); other.MergedItems.Add(Row(12, racer)); other.SaveChanges();
        }));
        var kept = await racing.TryRecordAll([Row(11, third), Row(12, third)], default);
        Assert.Equal(["acme/orders#11"], kept.Select(r => r.ItemKey));
        Assert.Equal(1, await h.Count($"SELECT COUNT(*) FROM factory.MergedItems WHERE ItemKey = 'acme/orders#12' AND MessageId = '{racer}'"));
        Assert.Equal(4, await h.Count("SELECT COUNT(*) FROM factory.MergedItems"));
    }

    /// <summary>A context whose first save is preceded by the call of <paramref name="before"/>: another writer getting in between.</summary>
    sealed class RacingDb(string cs, Action before) : FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) { before(); return base.SaveChangesAsync(cancellationToken); }
    }

    [SqlFact]
    public async Task OnTheSqlStore_AMessageThatFailedBeforeItsClaim_LeavesEveryItemToTheOtherSource()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync((Action<WolverineOptions>?)null);
        var reader = ItemMergedDeriverTests.Reader();
        reader.Pulls[9] = reader.Pulls[9] with { Body = "Closes #7\nCloses #8" };
        reader.Items[8] = ItemMergedDeriverTests.Facts(8);
        reader.FailingItems.Add(8);
        var d = new ItemMergedDeriver(reader, new SqlMergedItemStore(() => Open(h.ConnectionString)), ItemMergedDeriverTests.Home, () => "master", new("Auto Merge", "video", ["hold:owner"]));
        await Assert.ThrowsAsync<HttpRequestException>(() => d.Derive("webhook", ItemMergedDeriverTests.BaseMerged(), Guid.NewGuid(), default));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.MergedItems"));
        reader.FailingItems.Clear();
        var r = await d.Derive("poll", ItemMergedDeriverTests.ClosedCard(), Guid.NewGuid(), default);
        Assert.Equal(["acme/orders#7", "acme/orders#8"], r.Merged.Select(m => m.Item.ToString()).Order());
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.MergedItems WHERE Source = 'poll'"));
    }

    [SqlFact]
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
