using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.1 on a Wolverine tracked session over SQL Server: the ledger in the database, the events in the item history, the scheduler's timer as a durable scheduled message, a handler that dies between the save
/// and the publish, a restart in the middle, and the sequential queue that serialises many requests at once. The handlers are the pre-built ones of the harness (no runtime compiler in this project), calling
/// the same service as the gateway's static handlers; the gateway's own host is tested in Gateway.Tests (CapacityWiringTests). They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class CapacityTrackedTests
{
    sealed class InputHandler<T>(CapacityService svc, ConcurrentDictionary<string, int>? crashOnce = null) : MessageHandler<T> where T : CapacityInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            // the first attempt of the message saves and dies before it publishes; Wolverine runs the message again
            if (crashOnce != null && crashOnce.AddOrUpdate(m.Key, 1, (_, n) => n + 1) == 1) throw new HarnessTransientException("died after the save, before the publish");
            foreach (var o in r.Messages) await context.PublishAsync(o);
            if (r.WakeAt is { } due) await context.PublishAsync(new CapacityWake(due), new DeliveryOptions { ScheduledTime = due });
        }
    }

    sealed class EventHandler<T>(CapacityHistory history) : MessageHandler<T>
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) => m switch
        {
            SlotGranted g => CapacityHistoryHandler.Handle(g, history, context.Envelope!, cancellation),
            Held h => CapacityHistoryHandler.Handle(h, history, context.Envelope!, cancellation),
            SlotExpired e => CapacityHistoryHandler.Handle(e, history, context.Envelope!, cancellation),
            _ => Task.CompletedTask,
        };
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static CapacitySettings Settings(Action<JsonObject>? edit = null)
    {
        var cfg = (JsonObject)JsonNode.Parse(CapacityFixture.DefaultConfig)!;
        edit?.Invoke(cfg);
        return CapacitySettings.From(cfg, "tdd");
    }

    /// <summary>The host's wiring: the scheduler on the database of the host, every input on its sequential queue, the events written to the history.</summary>
    static Func<string, Action<WolverineOptions>> Wire(CapacitySettings? settings = null, ConcurrentDictionary<string, int>? crashOnce = null) => cs => w =>
    {
        var svc = new CapacityService(new SqlCapacityStore(() => Open(cs)), new CapacityScheduler(settings ?? Settings()));
        var history = new CapacityHistory(() => Open(cs));
        foreach (var t in CapacityTopology.Inputs) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(InputHandler<>).MakeGenericType(t), svc, crashOnce)!);
        foreach (var t in new[] { typeof(SlotGranted), typeof(Held), typeof(SlotExpired) }) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(EventHandler<>).MakeGenericType(t), history)!);
        CapacityTopology.Route(w);
        foreach (var t in new[] { typeof(SlotGranted), typeof(Held), typeof(SlotExpired) }) w.PublishMessage(t).ToLocalQueue("capacity-side");
        w.LocalQueue("capacity-side").UseDurableInbox();
        w.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
    };

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    static RequestSlot Ask(int n, string runtime = "copilot-cloud", string key = "", string phase = "design", string[]? files = null)
        => new(new ItemId("acme", "orders", n), key.Length > 0 ? key : $"ask-{n}", 1, runtime, phase, default, false, false, 1, DateTimeOffset.UtcNow, [], files is null ? Footprint.Empty : new Footprint(files, FootprintSource.Design));

    static async Task<CapacityRecord?> Ledger(TrackedSessionHost h) => await new SqlCapacityStore(() => Open(h.ConnectionString)).Load(CapacityKeys.Factory, default);

    static Task<long> History(TrackedSessionHost h, string type, string? item = null) =>
        h.Count($"SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = '{type}'" + (item == null ? "" : $" AND ItemKey = '{item}'"));

    [SqlFact]
    public async Task ARequest_IsGranted_TheLedgerTheHistoryAndTheSlotsTimerAreInTheDatabase()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.TrackSend(Ask(1));
        var rec = (await Ledger(h))!;
        Assert.Equal(1, rec.Revision);
        Assert.Equal(["acme/orders#1"], rec.State.Slots.Keys);
        Assert.Equal(1, rec.State.ExecutionsToday["copilot-cloud"]);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.CapacityLedgers WHERE [Key] = 'factory'"));
        Assert.True(await Until(async () => await History(h, CapacityHistory.GrantedType, "acme/orders#1") == 1, 20));   // one row per envelope
        // the slot's time limit is a durable scheduled message
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [SqlFact]
    public async Task TheEndOfASession_HandsTheSlotToTheWaitingItem_AndTheHistoryTellsBoth()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.TrackSend(Ask(1, "cursor-cloud"));
        await h.TrackSend(Ask(2, "cursor-cloud"));
        Assert.Equal(["acme/orders#1"], (await Ledger(h))!.State.Slots.Keys);
        Assert.True(await Until(async () => await History(h, CapacityHistory.HeldType, "acme/orders#2") == 1, 20));
        await h.TrackSend(new SessionEnded(new ItemId("acme", "orders", 1), "end-1", ""));
        Assert.Equal(["acme/orders#2"], (await Ledger(h))!.State.Slots.Keys);
        Assert.True(await Until(async () => await History(h, CapacityHistory.GrantedType) == 2, 20));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [SqlFact]
    public async Task AHandlerThatDiesAfterTheSave_IsRunAgain_AndTheGrantIsPublishedOnce()
    {
        if (!TrackedSessionHost.Available) return;
        var attempts = new ConcurrentDictionary<string, int>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(crashOnce: attempts));
        await h.TrackSend(Ask(1, key: "crash-1"));
        Assert.True(attempts["crash-1"] >= 2);   // the first attempt saved and died; the second found its own transition as the last one
        var rec = (await Ledger(h))!;
        Assert.Equal(["acme/orders#1"], rec.State.Slots.Keys);
        Assert.Equal(1, rec.State.ExecutionsToday["copilot-cloud"]);   // the day's execution was counted once
        Assert.Equal(1, rec.Revision);
        Assert.True(await Until(async () => await History(h, CapacityHistory.GrantedType, "acme/orders#1") == 1, 20));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [SqlFact]
    public async Task TheLedger_SurvivesARestartOfTheHost_AndTheNextInputWorksOnIt()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.TrackSend(Ask(1, "cursor-cloud"));
        await h.TrackSend(Ask(2, "cursor-cloud"));
        await h.RestartAsync(Wire());
        Assert.Equal(["acme/orders#1"], (await Ledger(h))!.State.Slots.Keys);
        await h.TrackSend(new SessionEnded(new ItemId("acme", "orders", 1), "end-1", ""));
        var rec = (await Ledger(h))!;
        Assert.Equal(["acme/orders#2"], rec.State.Slots.Keys);
        Assert.Equal(2, rec.State.ExecutionsToday["cursor-cloud"]);
    }

    [SqlFact]
    public async Task AHostThatAnsweredHostFull_IsPlannedAgainByTheDurableTimer_WhenItsBlockEnds()
    {
        if (!TrackedSessionHost.Available) return;
        var settings = Settings(c => c["runtimes"]!["octopus"]!["hostFullSeconds"] = 2);
        await using var h = await TrackedSessionHost.StartAsync(Wire(settings));
        await h.TrackSend(new HostReported("report-1", "h1", "octopus", 0, CapacityFixture.Roomy, DateTimeOffset.UtcNow));
        await h.TrackSend(Ask(1, "octopus"));
        var slot = (await Ledger(h))!.State.Slots["acme/orders#1"];
        Assert.Equal("h1", slot.Host);

        await h.TrackSend(new StartRefused(new ItemId("acme", "orders", 1), "refused-1", slot.SlotId, AdmissionCodes.HostFull));
        var waiting = (await Ledger(h))!.State;
        Assert.Empty(waiting.Slots);
        Assert.Equal("host-full", waiting.Queue["acme/orders#1"].Announced);
        Assert.Equal(0, waiting.ExecutionsToday["octopus"]);   // a wait, not a failure: the execution is not used

        // nothing sends anything now: the timer the refusal asked for fires through the database and the item has a slot again
        Assert.True(await Until(async () => (await Ledger(h))!.State.Slots.ContainsKey("acme/orders#1"), 30), "the scheduler's timer did not plan again");
        Assert.Equal(1, (await Ledger(h))!.State.ExecutionsToday["octopus"]);
        Assert.True(await Until(async () => await History(h, CapacityHistory.GrantedType, "acme/orders#1") == 2, 20));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [SqlFact]
    public async Task ManyRequestsAtOnce_AreSerialisedByTheQueue_FiveSlotsFiveGrantsNoLostWrite()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire());
        await h.Host.TrackActivity().Timeout(TimeSpan.FromSeconds(90)).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async ctx => { await Task.WhenAll(Enumerable.Range(1, 30).Select(n => ctx.PublishAsync(Ask(n)).AsTask())); }));
        var rec = (await Ledger(h))!;
        Assert.Equal(5, rec.State.Slots.Count);                       // copilot-cloud allows 5 at once
        Assert.Equal(25, rec.State.Queue.Count);
        Assert.Equal(5, rec.State.ExecutionsToday["copilot-cloud"]);
        Assert.Equal(5, rec.State.SlotSeq);
        Assert.True(await Until(async () => await History(h, CapacityHistory.GrantedType) == 5, 30));
        Assert.True(await Until(async () => await History(h, CapacityHistory.HeldType) == 25, 30));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }
}
