using System.Collections.Concurrent;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Fworker.Workflows.Intake;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W6.1 on a Wolverine tracked session over SQL Server: the saga's state, its open-instance index and its history in the database, its lease timers as durable scheduled messages, the partitioning of its
/// messages by item, NotFound that ends quietly, a restart in the middle, a crash between the save and the publish, and the hybrid hand-off (the items a v1 sweep must leave alone). The handlers are the
/// pre-built ones of the harness (no runtime compiler in this project), calling the same service as the gateway's static handlers; the gateway's own host is tested in Gateway.Tests (WorkItemWiringTests).
/// They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class WorkItemTrackedTests
{
    static readonly ItemId A = new("acme", "orders", 7);

    sealed class InputHandler<T>(WorkItemService svc, Func<T, Task>? around = null) : MessageHandler<T> where T : WorkItemInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            if (around != null) { await around(m); return; }
            var r = await svc.Handle(m, cancellation);
            foreach (var d in r.Decisions) await context.PublishAsync(d);
            foreach (var t in r.Timers) await context.PublishAsync(t.Message, new DeliveryOptions { ScheduledTime = t.Due });
        }
    }

    sealed class CrashOnceHandler<T>(WorkItemService svc, ConcurrentDictionary<string, int> attempts) : MessageHandler<T> where T : WorkItemInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            // the first attempt saves and dies before it publishes; Wolverine runs the message again
            if (attempts.AddOrUpdate(m.Item.ToString(), 1, (_, n) => n + 1) == 1) throw new HarnessTransientException("died after the save, before the publish");
            foreach (var d in r.Decisions) await context.PublishAsync(d);
            foreach (var t in r.Timers) await context.PublishAsync(t.Message, new DeliveryOptions { ScheduledTime = t.Due });
        }
    }

    sealed class DecidedHandler(WorkItemHistory history) : MessageHandler<WorkItemDecided>
    {
        protected override Task HandleAsync(WorkItemDecided m, MessageContext context, CancellationToken cancellation) => history.Record(m, context.Envelope!.Id, cancellation);
    }

    sealed class FactHandler<T>(Func<T, Envelope, OutgoingMessages> route) : MessageHandler<T>
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var o in route(m, context.Envelope!)) await context.PublishAsync(o);
        }
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static WorkItemPolicy Quick(TimeSpan? design = null) => new(new Dictionary<StepKind, TimeSpan?> { [StepKind.Design] = design ?? TimeSpan.FromHours(1), [StepKind.Develop] = TimeSpan.FromHours(1) });

    /// <summary>The host's wiring: the saga on the database of the host, all inputs partitioned by item, the facts routed, the decisions written to the history. <paramref name="handlerFor"/> replaces an input's handler.</summary>
    static Func<string, Action<WolverineOptions>> Wire(WorkItemPolicy? policy = null, IDocsOnlyPort? docs = null, Func<WorkItemService, Type, object?>? handlerFor = null) => cs => w =>
    {
        var store = new SqlWorkItemStore(() => Open(cs));
        var svc = new WorkItemService(store, new WorkItemSaga(docs ?? new RecordedDesignDocsOnly(), policy));
        foreach (var t in WorkItemTopology.Inputs)
        {
            var handler = handlerFor?.Invoke(svc, t) ?? Activator.CreateInstance(typeof(InputHandler<>).MakeGenericType(t), svc, null)!;
            w.AddMessageHandler(t, (IMessageHandler)handler);
        }
        w.AddMessageHandler(new DecidedHandler(new WorkItemHistory(() => Open(cs))));
        w.AddMessageHandler(new FactHandler<ItemMerged>((m, _) => WorkItemRouting.Handle(m)));
        w.AddMessageHandler(new FactHandler<DeliveredTo>((m, _) => WorkItemRouting.Handle(m)));
        w.AddMessageHandler(new FactHandler<Undelivered>((m, e) => WorkItemRouting.Handle(m, e)));
        WorkItemTopology.Route(w);
        foreach (var t in new[] { typeof(WorkItemDecided), typeof(ItemMerged), typeof(DeliveredTo), typeof(Undelivered) }) w.PublishMessage(t).ToLocalQueue("workitem-side");
        w.LocalQueue("workitem-side").UseDurableInbox();
        w.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
    };

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    static async Task<WorkItemRecord?> Row(TrackedSessionHost h, ItemId item) => await new SqlWorkItemStore(() => Open(h.ConnectionString)).Load(item.ToString(), default);

    static Task<long> History(TrackedSessionHost h, string item, string? action = null) =>
        h.Count($"SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '{item}' AND Type = 'workitem.decided'" + (action == null ? "" : $" AND Detail LIKE '%\"Action\":\"{action}\"%'"));

    // ---- state, index and history in the database ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AStart_SavesTheStateTheOpenInstanceAndTheHistory_AndTheLeaseIsADurableScheduledMessage()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.TrackSend(new StartWorkItem(A, "intake"));
        var rec = (await Row(h, A))!;
        Assert.Equal(1, rec.Revision);
        Assert.Equal(("feature", StepKind.Design, "1.1", WorkItemStatus.Open), (rec.State.Plan, rec.State.Step, rec.State.ArmId, rec.State.Status));
        Assert.Equal("acme/orders|feature|Design|open|1", await Single(h, "SELECT Repository + '|' + [Plan] + '|' + Step + '|' + Status + '|' + CAST(Generation AS varchar) FROM factory.WorkItems WHERE ItemKey = 'acme/orders#7'"));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = 'acme/orders#7' AND Generation = 1 AND ClosedAt IS NULL"));
        Assert.True(await Until(async () => await History(h, "acme/orders#7") == 2, 20));   // started and step-entered, one row per envelope
        Assert.Equal(1, await History(h, "acme/orders#7", "started"));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'"));   // the lease of the design step
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    static async Task<string> Single(TrackedSessionHost h, string sql)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(h.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "";
    }

    [Fact]
    public async Task TheCompletionClosesTheOpenInstance_AndTheNextGenerationOpensItAgain()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.TrackSend(new StartWorkItem(A, "intake", "docs"));
        await h.TrackSend(new MergeSeen(A, "m1", new string('a', 40), 5));
        Assert.Equal(WorkItemStatus.Completed, (await Row(h, A))!.State.Status);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = 'acme/orders#7' AND ClosedAt IS NOT NULL"));
        Assert.Equal(0, (await new SqlWorkItemStore(() => Open(h.ConnectionString)).OwnedItems(default)).Count);
        await h.TrackSend(new ReopenWorkItem(A, "moved back", "r1"));
        Assert.Equal((2, WorkItemStatus.Open), ((await Row(h, A))!.State.Generation, (await Row(h, A))!.State.Status));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = 'acme/orders#7' AND Generation = 2 AND ClosedAt IS NULL"));
        Assert.Equal(["acme/orders#7"], await new SqlWorkItemStore(() => Open(h.ConnectionString)).OwnedItems(default));
    }

    // ---- timers --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaseThatRunsOut_FiresByItself_AndArmsTheStepAgain()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire(Quick(TimeSpan.FromSeconds(1))));
        await h.TrackSend(new StartWorkItem(A, "intake"));
        Assert.True(await Until(async () => (await Row(h, A))!.State.ArmSeq >= 2, 30), "the lease timer did not fire");
        var state = (await Row(h, A))!.State;
        Assert.Equal(1, state.FailuresOf(StepKind.Design));
        Assert.True(await Until(async () => await History(h, "acme/orders#7", "lease-expired") >= 1, 20));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task ARestartInTheMiddle_KeepsTheStateAndTheTimer_AndTheItemIdSurvivesTheDurableJson()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire(Quick(TimeSpan.FromSeconds(6))));
        await h.TrackSend(new StartWorkItem(A, "intake"));
        var before = (await Row(h, A))!;
        await h.RestartAsync(Wire(Quick(TimeSpan.FromSeconds(6)))(h.ConnectionString));
        var after = (await Row(h, A))!;
        Assert.Equal((before.Revision, before.State.ArmId), (after.Revision, after.State.ArmId));
        // the lease was scheduled before the restart (its message is in the database as JSON, with the item in it): the new process fires it
        Assert.True(await Until(async () => (await Row(h, A))!.State.ArmSeq >= 2, 40), "the lease timer did not survive the restart");
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task ACrashBetweenTheSaveAndThePublish_IsHealedByTheRedelivery_NothingIsLostAndNothingIsSavedTwice()
    {
        if (!TrackedSessionHost.Available) return;
        var attempts = new ConcurrentDictionary<string, int>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(handlerFor: (svc, t) => t == typeof(StartWorkItem) ? new CrashOnceHandler<StartWorkItem>(svc, attempts) : null));
        await h.TrackSend(new StartWorkItem(A, "intake"));
        Assert.True(attempts["acme/orders#7"] >= 2);
        Assert.Equal(1, (await Row(h, A))!.Revision);   // saved once
        Assert.True(await Until(async () => await History(h, "acme/orders#7") == 2, 20));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'"));   // the lease timer was published by the second attempt
    }

    // ---- partitioning ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMessagesOfOneItem_RunOneAtATime_WhileDifferentItemsRunBesideEachOther()
    {
        if (!TrackedSessionHost.Available) return;
        var running = new ConcurrentDictionary<string, int>(); var peak = new ConcurrentDictionary<string, int>(); var all = 0; var allPeak = 0; var handled = 0;
        Func<WorkItemService, Type, object?> probe = (svc, t) => t == typeof(LeaseRenewed)
            ? new InputHandler<LeaseRenewed>(svc, async m =>
            {
                var key = m.Item.ToString();
                var n = running.AddOrUpdate(key, 1, (_, c) => c + 1); peak.AddOrUpdate(key, n, (_, p) => Math.Max(p, n));
                var a = Interlocked.Increment(ref all); lock (peak) allPeak = Math.Max(allPeak, a);
                await Task.Delay(60);
                Interlocked.Decrement(ref all); running.AddOrUpdate(key, 0, (_, c) => c - 1); Interlocked.Increment(ref handled);
            }) : null;
        await using var h = await TrackedSessionHost.StartAsync(Wire(handlerFor: probe));
        var items = Enumerable.Range(1, 12).Select(n => new ItemId("acme", "orders", n)).ToList();
        var bus = h.Host.Services.GetRequiredService<IMessageBus>();
        foreach (var item in items) for (var i = 0; i < 4; i++) await bus.PublishAsync(new LeaseRenewed(item, 1, "1.1", $"beat-{item.Number}-{i}"));
        Assert.True(await Until(() => Task.FromResult(Volatile.Read(ref handled) == 48), 40), $"handled {handled} of 48");
        Assert.All(peak, kv => Assert.Equal(1, kv.Value));   // never two at once for one item
        Assert.True(allPeak >= 2, "different items never ran at the same time");
    }

    // ---- NotFound -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALateMessageForAnItemWithNoSaga_EndsQuietly_NoRowNoErrorNoDeadLetter()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        WorkItemInput[] late =
        [
            new PhaseLeaseExpired(A, 1, "1.1", DateTimeOffset.UtcNow), new SettleWindowElapsed(A, 1, "1.5"), new StepCompleted(A, 1, "1.1", "k1"), new MergeSeen(A, "k2", new string('a', 40), 3),
            new DeliverySeen(A, "k3", "prod", "1.0.1"), new DeliveryLost(A, "k4", "prod"), new BudgetSpent(A, 1, "1.1", "k5", BudgetKind.Failure, ""),
        ];
        foreach (var m in late) await h.TrackSend(m);
        await h.TrackSend(new ItemMerged(A, new string('b', 40), 3, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new DeliveredTo(A, "prod", "1.0.1"));
        await h.TrackSend(new Undelivered(A, "prod"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.WorkItems"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.OpenInstances"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'workitem.decided'"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    // ---- the merge and the delivery, as the train's events ------------------------------------------------------------------------------

    [Fact]
    public async Task TheDeliveryTrainsEvents_MoveTheItemsSaga_AndAreIgnoredForTheItemsOfV1()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        var other = new ItemId("acme", "orders", 99);   // an item v1 owns: no saga
        await h.TrackSend(new StartWorkItem(A, "intake"));
        var gates = new ItemMergedGates(true, "", true, false, false, []);
        await h.TrackSend(new ItemMerged(A, new string('c', 40), 11, gates, [], "webhook"));
        await h.TrackSend(new ItemMerged(other, new string('d', 40), 12, gates, [], "webhook"));
        var merged = (await Row(h, A))!.State;
        Assert.Equal((StepKind.Deliver, new string('c', 40), 11), (merged.Step, merged.MergeCommit, merged.PullRequest));
        await h.TrackSend(new DeliveredTo(A, "prod", "1.0.7"));
        Assert.Equal(WorkItemStatus.Settling, (await Row(h, A))!.State.Status);
        await h.TrackSend(new Undelivered(A, "prod"));
        var back = (await Row(h, A))!.State;
        Assert.Equal((WorkItemStatus.Open, StepKind.Deliver), (back.Status, back.Step));
        Assert.Null(await Row(h, other));
        Assert.Equal(["acme/orders#7"], await new SqlWorkItemStore(() => Open(h.ConnectionString)).OwnedItems(default));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    // ---- IRevisioned ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRevisionIsTheConcurrencyToken_OnlyOneOfTwoWritersOfTheSameRevisionWins()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        var store = new SqlWorkItemStore(() => Open(h.ConnectionString));
        var saga = new WorkItemSaga(new RecordedDesignDocsOnly());
        var t = saga.Apply(null, new StartWorkItem(A, "intake"), DateTimeOffset.UtcNow);
        // two nodes both read "no row" and start the item: one insert wins
        var firsts = await Task.WhenAll(store.Save(A.ToString(), t.State!, 0, default), store.Save(A.ToString(), t.State!, 0, default));
        Assert.Equal(1, firsts.Count(x => x));
        var rec = (await store.Load(A.ToString(), default))!;
        Assert.Equal(1, rec.Revision);
        // two nodes both read revision 1 and write: one update wins, the loser applies its input again to the state that won
        var a = saga.Apply(rec.State, new StepCompleted(A, 1, rec.State.ArmId, "k-a"), DateTimeOffset.UtcNow).State!;
        var b = saga.Apply(rec.State, new BudgetSpent(A, 1, rec.State.ArmId, "k-b", BudgetKind.IdleFollowUp, ""), DateTimeOffset.UtcNow).State!;
        var seconds = await Task.WhenAll(store.Save(A.ToString(), a, 1, default), store.Save(A.ToString(), b, 1, default));
        Assert.Equal(1, seconds.Count(x => x));
        Assert.Equal(2, (await store.Load(A.ToString(), default))!.Revision);
        Assert.False(await store.Save(A.ToString(), a, 1, default));   // (a stale revision never writes)
    }

    // ---- the hybrid hand-off -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemTheIntakeGaveToV2_IsOwnedUntilItCompletes_AndItsRepositoryCountsItAsInFlight()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        var store = new SqlWorkItemStore(() => Open(h.ConnectionString));
        var settings = new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int> { ["acme/orders"] = 1 }, ["docs"], "engine:v2", "factory worker", "AI Factory", "Demo");
        var intake = new WorkItemIntake(store, settings, async (m, ct) => await h.TrackSend(m));
        var docs = new IntakeCandidate(A, true, true, ["factory worker"], true, "docs");
        var d = await intake.Run([docs], 1, false);
        Assert.Equal(IntakeEngine.V2, d.Pull!.Engine);
        Assert.Equal(["acme/orders#7"], await store.OwnedItems(default));
        // the repository holds its one item in flight: a second docs item of it waits, another repository's does not
        var next = await intake.Run([new IntakeCandidate(new ItemId("acme", "orders", 8), true, true, ["factory worker"], true, "docs"), new IntakeCandidate(new ItemId("acme", "billing", 2), true, true, ["factory worker"], true, "docs")], 1, false);
        Assert.Equal(new ItemId("acme", "billing", 2), next.Pull!.Item);
        Assert.Equal(["acme/billing#2", "acme/orders#7"], (await store.OwnedItems(default)).Order());
        await h.TrackSend(new MergeSeen(A, "m", new string('a', 40), 5));
        Assert.Equal(["acme/billing#2"], await store.OwnedItems(default));
    }
}
