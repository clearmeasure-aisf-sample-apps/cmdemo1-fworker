using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.3 (#340) on a Wolverine tracked session over SQL Server: the feature plan's saga, the CapacityScheduler and the merge train in one host, with the hand-offs between them as the gateway wires them (the saga's slot
/// request becomes the scheduler's, the scheduler's grant, hold and expiry become inputs of the saga, the saga's releases and its merge request go to the scheduler and the train, the train's answer comes back as the merge's
/// answer), and every state, the history and the timers in the database. The handlers are the pre-built ones of the harness (no runtime compiler in this project), calling the same services and routers as the gateway's
/// static handlers; the slot request of the gateway (labels) and the merge port (GitHub) are fakes the test answers for. The gateway's own host is tested in Gateway.Tests (FeaturePlanWiringTests). They return at once where
/// FACTORY_TEST_SQL is unset.
/// </summary>
public class FeaturePlanTrackedTests
{
    static readonly ItemId A = new("acme", "orders", 7);
    static readonly ItemId B = new("acme", "orders", 8);
    static readonly string Head = new('a', 40);
    static readonly string Head2 = new('c', 40);
    static readonly string Commit = new('b', 40);

    sealed class WorldProbe
    {
        public readonly ConcurrentQueue<MergeGranted> Grants = new();
        public readonly ConcurrentQueue<SlotWanted> Asked = new();
        public readonly ConcurrentQueue<RequestSlot> Requests = new();
        public volatile Func<MergeGranted, MergeResult> Merge = _ => new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit);
    }

    sealed class SagaInput<T>(WorkItemService svc, Func<T, bool>? dieAfterSave = null) : MessageHandler<T> where T : WorkItemInput
    {
        readonly ConcurrentDictionary<string, int> seen = new();
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            // the first attempt saves and dies before it publishes; Wolverine runs the message again
            if (dieAfterSave?.Invoke(m) == true && seen.AddOrUpdate(m.Item + "|" + m.GetType().Name, 1, (_, n) => n + 1) == 1) throw new HarnessTransientException("died after the save, before the publish");
            foreach (var d in r.Decisions) await context.PublishAsync(d);
            foreach (var c in r.Sent) await context.PublishAsync(c);
            foreach (var t in r.Timers) await context.PublishAsync(t.Message, new DeliveryOptions { ScheduledTime = t.Due });
        }
    }

    sealed class CapacityInputHandler<T>(CapacityService svc) : MessageHandler<T> where T : CapacityInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            foreach (var o in r.Messages) await context.PublishAsync(o);
            if (r.WakeAt is { } due) await context.PublishAsync(new CapacityWake(due), new DeliveryOptions { ScheduledTime = due });
        }
    }

    sealed class TrainInput<T>(MergeTrainService svc) : MessageHandler<T> where T : MergeTrainInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var o in await svc.Handle(m, cancellation)) await context.PublishAsync(o);
        }
    }

    /// <summary>The gateway's slot request, without the labels: the scheduler's request for the saga's.</summary>
    sealed class SlotRequests(WorldProbe world, string runtime) : MessageHandler<SlotWanted>
    {
        protected override async Task HandleAsync(SlotWanted m, MessageContext context, CancellationToken cancellation)
        {
            world.Asked.Enqueue(m);
            var footprint = m.Footprint.Count == 0 ? Footprint.Empty : new Footprint(m.Footprint, m.FootprintFromPullRequest ? FootprintSource.PullRequest : FootprintSource.Design);
            var request = new RequestSlot(m.Item, m.Key, m.Generation, runtime, m.Phase, default, false, m.Resume, m.ColumnDepth, m.RequestedAt, [], footprint);
            world.Requests.Enqueue(request);
            await context.PublishAsync(request);
        }
    }

    /// <summary>What the scheduler publishes: the history row, and the input of the saga it stands for (the router of the gateway).</summary>
    sealed class SchedulerEvent<T>(CapacityHistory history) : MessageHandler<T>
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            switch (m)
            {
                case SlotGranted g: await CapacityHistoryHandler.Handle(g, history, context.Envelope!, cancellation); foreach (var o in WorkItemRouting.Handle(g)) await context.PublishAsync(o); break;
                case Held h: await CapacityHistoryHandler.Handle(h, history, context.Envelope!, cancellation); foreach (var o in WorkItemRouting.Handle(h)) await context.PublishAsync(o); break;
                case SlotExpired e: await CapacityHistoryHandler.Handle(e, history, context.Envelope!, cancellation); foreach (var o in WorkItemRouting.Handle(e)) await context.PublishAsync(o); break;
            }
        }
    }

    sealed class Merged : MessageHandler<ItemMerged>
    {
        protected override async Task HandleAsync(ItemMerged m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var o in WorkItemRouting.Handle(m)) await context.PublishAsync(o);
            await context.PublishAsync(MergeTrainRouting.Handle(m));
        }
    }

    sealed class RouteRequest : MessageHandler<MergeRequested>
    {
        protected override async Task HandleAsync(MergeRequested m, MessageContext context, CancellationToken cancellation) => await context.PublishAsync(MergeTrainRouting.Handle(m));
    }

    sealed class PortHandler(WorldProbe world) : MessageHandler<MergeGranted>
    {
        protected override async Task HandleAsync(MergeGranted m, MessageContext context, CancellationToken cancellation)
        {
            world.Grants.Enqueue(m);
            await context.PublishAsync(new MergeTrainAnswered(m.Key, m.Request, world.Merge(m)));
        }
    }

    sealed class Decided(WorkItemHistory history) : MessageHandler<WorkItemDecided>
    {
        protected override Task HandleAsync(WorkItemDecided m, MessageContext context, CancellationToken cancellation) => history.Record(m, context.Envelope!.Id, cancellation);
    }

    sealed class TrainDecided(MergeTrainHistory history) : MessageHandler<MergeTrainDecided>
    {
        protected override Task HandleAsync(MergeTrainDecided m, MessageContext context, CancellationToken cancellation) => history.Record(m, context.Envelope!.Id, cancellation);
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static WorkItemPolicy Policy(bool slots = true) => WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(slots) };

    static CapacitySettings Capacity(Action<JsonObject>? edit = null)
    {
        var cfg = (JsonObject)JsonNode.Parse(CapacityFixture.DefaultConfig)!;
        edit?.Invoke(cfg);
        return CapacitySettings.From(cfg, "tdd");
    }

    /// <summary>The whole world in one host: the saga, the scheduler, the merge train and the fakes of the gateway's two ports.</summary>
    static Func<string, Action<WolverineOptions>> Wire(WorldProbe world, WorkItemPolicy? policy = null, CapacitySettings? capacity = null, MergeTrainSettings? train = null, Func<WorkItemInput, bool>? dieAfterSave = null, string runtime = "copilot-cloud") => cs => w =>
    {
        var items = new WorkItemService(new SqlWorkItemStore(() => Open(cs)), new WorkItemSaga(new FakeDocsPort(), policy ?? Policy()));
        var scheduler = new CapacityService(new SqlCapacityStore(() => Open(cs)), new CapacityScheduler(capacity ?? Capacity()));
        var trains = new MergeTrainService(new SqlMergeTrainStore(() => Open(cs)), train ?? new MergeTrainSettings(MergeStrategy.Direct));
        var capacityHistory = new CapacityHistory(() => Open(cs));
        foreach (var t in WorkItemTopology.Inputs) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(SagaInput<>).MakeGenericType(t), items, dieAfterSave is null ? null : (object)MakeCheck(t, dieAfterSave))!);
        foreach (var t in CapacityTopology.Inputs) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(CapacityInputHandler<>).MakeGenericType(t), scheduler)!);
        foreach (var t in new[] { typeof(ReadyToMerge), typeof(MergeTrainAnswered), typeof(MergeTrainLeft) }) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(TrainInput<>).MakeGenericType(t), trains)!);
        foreach (var t in new[] { typeof(SlotGranted), typeof(Held), typeof(SlotExpired) }) w.AddMessageHandler(t, (IMessageHandler)Activator.CreateInstance(typeof(SchedulerEvent<>).MakeGenericType(t), capacityHistory)!);
        w.AddMessageHandler(new SlotRequests(world, runtime));
        w.AddMessageHandler(new Merged());
        w.AddMessageHandler(new RouteRequest());
        w.AddMessageHandler(new PortHandler(world));
        w.AddMessageHandler(new Decided(new WorkItemHistory(() => Open(cs))));
        w.AddMessageHandler(new TrainDecided(new MergeTrainHistory(() => Open(cs))));
        WorkItemTopology.Route(w);
        CapacityTopology.Route(w);
        MergeTrainTopology.Route(w);
        foreach (var t in new[] { typeof(WorkItemDecided), typeof(ItemMerged), typeof(MergeRequested), typeof(SlotWanted), typeof(SlotGranted), typeof(Held), typeof(SlotExpired), typeof(MergeTrainDecided) }) w.PublishMessage(t).ToLocalQueue("feature-side");
        w.LocalQueue("feature-side").UseDurableInbox();
        w.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
    };

    // the handler of an input type dies once after the save when the predicate says so for that input
    static object MakeCheck(Type input, Func<WorkItemInput, bool> pred)
    {
        var method = typeof(FeaturePlanTrackedTests).GetMethod(nameof(Typed), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(input);
        return method.Invoke(null, [pred])!;
    }
    static Func<T, bool> Typed<T>(Func<WorkItemInput, bool> pred) where T : WorkItemInput => m => pred(m);

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    static async Task<WorkItemState> Row(TrackedSessionHost h, ItemId item) => (await new SqlWorkItemStore(() => Open(h.ConnectionString)).Load(item.ToString(), default))!.State;
    static async Task<CapacityState> Ledger(TrackedSessionHost h) => (await new SqlCapacityStore(() => Open(h.ConnectionString)).Load(CapacityKeys.Factory, default))!.State;
    static async Task<MergeTrainState?> Train(TrackedSessionHost h) => (await new SqlMergeTrainStore(() => Open(h.ConnectionString)).Load("acme/orders", default))?.State;

    static Task<long> History(TrackedSessionHost h, string item, string action) =>
        h.Count($"SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '{item}' AND Type = '{WorkItemHistory.HistoryType}' AND Detail LIKE '%\"Action\":\"{action}\"%'");

    static async Task NoDeadLetters(TrackedSessionHost h) => Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));

    /// <summary>Starts the item and waits until the scheduler's grant has reached the saga.</summary>
    static async Task<WorkItemState> Started(TrackedSessionHost h, ItemId item)
    {
        await h.TrackSend(new StartWorkItem(item, "intake"));
        Assert.True(await Until(async () => (await Row(h, item)).SlotId.Length > 0), "the grant did not reach the saga");
        return await Row(h, item);
    }

    static async Task ReviewAndComplete(TrackedSessionHost h, ItemId item, string tag)
    {
        var s = await Row(h, item);
        await h.TrackSend(new ReviewRecorded(item, s.Generation, s.ArmId, $"review-{tag}", true));
        await h.TrackSend(new StepCompleted(item, s.Generation, s.ArmId, $"done-{tag}"));
    }

    static async Task AtVerify(TrackedSessionHost h, ItemId item, string[]? files = null)
    {
        await ReviewAndComplete(h, item, "design");
        await h.TrackSend(new PullRequestChanged(item, 0, $"pr-{item.Number}", 100 + item.Number, Head, files ?? ["src/a.cs"]));
        await ReviewAndComplete(h, item, "develop");
        Assert.Equal(StepKind.Verify, (await Row(h, item)).Step);
    }

    // ---- the whole way ----------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task AFeatureItem_GoesFromItsStartToItsMerge_AcrossTheSchedulerAndTheMergeTrain_WithEveryStateInTheDatabase()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world));
        var started = await Started(h, A);
        // the saga asked, the scheduler granted, the saga holds the slot and its lease runs (a durable timer)
        Assert.Equal(("s1", false, "feature", StepKind.Design), (started.SlotId, started.SlotAsked, started.Plan, started.Step));
        Assert.NotNull(started.LeaseDue);
        var ledger = await Ledger(h);
        Assert.Equal(["acme/orders#7"], ledger.Slots.Keys);
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'"));   // the step's lease, and the scheduler's look at the slot's time limit
        Assert.True(await Until(async () => await History(h, "acme/orders#7", "slot-granted") == 1, 20));

        await h.TrackSend(new DesignRecorded(A, 1, started.ArmId, "design-1", false, ["src/a.cs", "src/b.cs"]));
        await ReviewAndComplete(h, A, "design");
        var develop = await Row(h, A);
        Assert.Equal((StepKind.Develop, "s1", false), (develop.Step, develop.SlotId, develop.SlotAsked));   // (the same session goes on: its slot is kept)
        Assert.Equal(["src/a.cs", "src/b.cs"], develop.Footprint);
        Assert.Contains("acme/orders#7", (await Ledger(h)).InFlight.Keys);   // and it is a holder of the file-overlap rule from Development on
        var asks = world.Asked.ToArray();
        Assert.Equal(["design", "develop"], asks.Select(a => a.Phase));
        Assert.Equal([false, true], asks.Select(a => a.Resume));

        await h.TrackSend(new PullRequestChanged(A, 0, "pr-1", 107, Head, ["src/a.cs", "src/c.cs"]));
        Assert.Equal(["src/a.cs", "src/c.cs"], (await Ledger(h)).InFlight["acme/orders#7"].Footprint.Entries);   // the pull request's files replace the design's
        await ReviewAndComplete(h, A, "develop");
        Assert.Equal(StepKind.Verify, (await Row(h, A)).Step);   // Functional Testing starts with CI still running
        await h.TrackSend(new ChecksObserved(A, 1, "checks-1", Head, ChecksVerdict.Pending, []));
        var verify = await Row(h, A);
        Assert.Equal(("pending", Head), (verify.ChecksState, verify.ChecksHead));

        // the PASS arrives before CI ends: the merge waits for the checks, with no call to the port
        await h.TrackSend(new VerdictRecorded(A, 1, verify.ArmId, "verdict-1", true, 107, Head, "ok"));
        var merge = await Row(h, A);
        Assert.Equal((StepKind.Merge, true, "", 0), (merge.Step, merge.MergeAwaitingChecks, merge.SlotId, merge.MergeAttempt));
        Assert.Empty(world.Grants);
        Assert.Empty((await Ledger(h)).Slots);                                  // the merge needs no session: the slot went back
        Assert.Contains("acme/orders#7", (await Ledger(h)).InFlight.Keys);      // but the item is in flight until it is merged

        // green checks on the head the PASS covers ask for the merge: through the train, the port, and back
        await h.TrackSend(new ChecksObserved(A, 1, "checks-2", Head, ChecksVerdict.Green, []));
        Assert.True(await Until(async () => (await Row(h, A)).Step == StepKind.Deliver, 30), "the merge did not complete the step");
        var grant = Assert.Single(world.Grants);
        Assert.Equal(("feature", Head, 107), (grant.Request.Plan, grant.Request.Head, grant.Request.PullRequest));
        var delivered = await Row(h, A);
        Assert.Equal((Commit, false), (delivered.MergeCommit, delivered.TrainEntered));   // (the merge settled the line: nothing to give back)
        Assert.True(await Until(async () => (await Ledger(h)).InFlight.Count == 0, 20), "the merged item stayed in flight");   // ClearItem("merged")
        Assert.Empty((await Train(h))!.Line);
        Assert.True(await Until(async () => await History(h, "acme/orders#7", "merged") == 1, 20));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task RedChecksOnTheTestedHead_AreAFixForwardRound_TheSlotIsAskedAgainAsAnOwnedItem_AndTheNextHeadIsMerged()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world));
        await Started(h, A);
        await AtVerify(h, A);
        var verify = await Row(h, A);
        await h.TrackSend(new ChecksObserved(A, 1, "red-1", Head, ChecksVerdict.Red, ["Build result"]));
        var fixing = await Row(h, A);
        Assert.Equal((StepKind.Develop, 1, 0), (fixing.Step, fixing.FixForwardRounds, fixing.Demotions));
        Assert.NotEqual(verify.ArmId, fixing.ArmId);
        Assert.True(await Until(async () => await History(h, "acme/orders#7", "fix-forward") == 1, 20));
        Assert.Equal("s1", fixing.SlotId);   // (the session was never over: its slot is kept)
        // the owner pushes the fix; Functional Testing runs on the new head and the merge follows its checks
        await h.TrackSend(new PullRequestChanged(A, 0, "pr-2", 107, Head2, ["src/a.cs"]));
        await ReviewAndComplete(h, A, "fix");
        var again = await Row(h, A);
        Assert.Equal((StepKind.Verify, Head2), (again.Step, again.Head));
        await h.TrackSend(new ChecksObserved(A, 1, "green-2", Head2, ChecksVerdict.Green, []));
        await h.TrackSend(new VerdictRecorded(A, 1, again.ArmId, "verdict-2", true, 107, Head2, "ok"));
        Assert.True(await Until(async () => (await Row(h, A)).Step == StepKind.Deliver, 30));
        Assert.Equal(Head2, Assert.Single(world.Grants).Request.Head);
        await NoDeadLetters(h);
    }

    // ---- the hold in the invoke -----------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task AnItemWhoseDesignOverlapsAnItemInFlight_IsHeldAtTheEndOfItsDesign_AndGoesWhenTheHolderIsMerged()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world));
        await Started(h, A);
        await h.TrackSend(new DesignRecorded(A, 1, (await Row(h, A)).ArmId, "design-a", false, ["src/a.cs"]));
        await ReviewAndComplete(h, A, "design-a");   // A is in Development with a slot: a holder
        Assert.Contains("acme/orders#7", (await Ledger(h)).InFlight.Keys);

        var b = await Started(h, B);
        await h.TrackSend(new DesignRecorded(B, 1, b.ArmId, "design-b", false, ["src/a.cs", "src/z.cs"]));
        await ReviewAndComplete(h, B, "design-b");   // the end of B's design: the overlap rule decides in the invoke
        Assert.True(await Until(async () => (await Row(h, B)).SlotWait.Length > 0, 20), "the hold did not reach the saga");
        var held = await Row(h, B);
        Assert.Equal((StepKind.Develop, "", true, null), (held.Step, held.SlotId, held.SlotAsked, held.LeaseDue));   // no session, no lease: nothing is offered
        Assert.Equal("file-overlap acme/orders#7", held.SlotWait);
        var ledger = await Ledger(h);
        Assert.DoesNotContain("acme/orders#8", ledger.Slots.Keys);
        Assert.Contains("acme/orders#8", ledger.Queue.Keys);
        Assert.Equal(7, ledger.Holds["acme/orders#8"].By);
        Assert.True(await Until(async () => await History(h, "acme/orders#8", "held") == 1, 20));

        // the holder is merged: the hold ends, the scheduler grants a new slot, the saga's lease runs again
        await h.TrackSend(new ItemMerged(A, Commit, 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        Assert.True(await Until(async () => (await Row(h, B)).SlotId.Length > 0, 30), "the grant did not reach the held item");
        var released = await Row(h, B);
        Assert.Equal((false, ""), (released.SlotAsked, released.SlotWait));
        Assert.NotNull(released.LeaseDue);
        Assert.NotEqual("s2", released.SlotId);   // (a new session: the first one ended with the hold)
        Assert.Contains("acme/orders#8", (await Ledger(h)).InFlight.Keys);
        await NoDeadLetters(h);
    }

    // ---- the session -------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task ASessionThatIsGone_FreesItsSlot_AndTheItemIsGrantedANewOne()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world));
        var started = await Started(h, A);
        // what the gateway's journal does when the plant says the session is gone: the scheduler's end signal, and the saga's loss
        await h.TrackSend(new SessionEnded(A, "ended|gone|1", ""));
        await h.TrackSend(new SessionLost(A, 0, "gone|gone|1", "", "the session that held the item is gone", DateTimeOffset.UtcNow));
        Assert.True(await Until(async () => { var s = await Row(h, A); return s.SlotId.Length > 0 && s.SlotId != started.SlotId; }, 30), "the item did not get a new slot");
        var after = await Row(h, A);
        Assert.Equal(["acme/orders#7"], (await Ledger(h)).Slots.Keys);
        Assert.Equal(0, after.FailuresOf(StepKind.Design));
        Assert.True(world.Asked.Last().Resume);   // (asked again as an owned item: rank 2)
        Assert.True(await Until(async () => await History(h, "acme/orders#7", "session-lost") == 1, 20));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task AParkedItem_GivesItsSlotBackAndLeavesTheScheduler_AndAnUnparkedOneAsksAgain()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world));
        await Started(h, A);
        await h.TrackSend(new ParkWorkItem(A, 0, "park-1", "a person looks"));
        Assert.True(await Until(async () => (await Ledger(h)).Slots.Count == 0 && (await Ledger(h)).Queue.Count == 0, 20));
        Assert.Equal("", (await Row(h, A)).SlotId);
        await h.TrackSend(new ResumeWorkItem(A, 0, "resume-1"));
        Assert.True(await Until(async () => (await Row(h, A)).SlotId.Length > 0, 30), "the resumed item got no slot");
        Assert.Equal(["acme/orders#7"], (await Ledger(h)).Slots.Keys);
        await NoDeadLetters(h);
    }

    // ---- faults ------------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task ACrashBetweenTheSaveAndThePublish_AsksForOneSlot_NotNone_AndNotTwo()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        await using var h = await TrackedSessionHost.StartAsync(Wire(world, dieAfterSave: m => m is StartWorkItem));
        await h.TrackSend(new StartWorkItem(A, "intake"));
        Assert.True(await Until(async () => (await Row(h, A)).SlotId.Length > 0, 30), "the redelivery did not ask for the slot");
        Assert.Equal(1, world.Asked.Count);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = 'acme/orders#7' AND Type = 'capacity.slot-granted'"));
        Assert.Equal(["acme/orders#7"], (await Ledger(h)).Slots.Keys);
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task ARestartWhileAnItemWaitsForCapacity_KeepsItsRequest_AndTheEndOfTheHoldersSessionGrantsIt()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        var one = Capacity(cfg => cfg["api"]!["copilot"]!["maxConcurrent"] = 1);
        await using var h = await TrackedSessionHost.StartAsync(Wire(world, capacity: one));
        await Started(h, A);
        await h.TrackSend(new StartWorkItem(B, "intake"));
        Assert.True(await Until(async () => (await Row(h, B)).SlotWait.Length > 0, 20), "the wait did not reach the saga");
        Assert.Equal("runtime-at-capacity", (await Row(h, B)).SlotWait.Split(' ')[0]);
        Assert.Null((await Row(h, B)).LeaseDue);
        await h.RestartAsync(Wire(world, capacity: one)(h.ConnectionString));
        var waiting = await Row(h, B);
        Assert.True(waiting.SlotAsked);
        // the first item's session ends: the waiting item is granted, and its lease starts
        await h.TrackSend(new SessionEnded(A, "end-a", ""));
        Assert.True(await Until(async () => (await Row(h, B)).SlotId.Length > 0, 30), "the waiting item was not granted after the restart");
        Assert.NotNull((await Row(h, B)).LeaseDue);
        Assert.Equal(["acme/orders#8"], (await Ledger(h)).Slots.Keys);
        await NoDeadLetters(h);
    }

    // ---- the merge line --------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task AHeadThatMovesAfterThePass_SendsTheItemBackToVerify_AndGivesItsPlaceInTheLineBack()
    {
        if (!TrackedSessionHost.Available) return;
        var world = new WorldProbe();
        // the train strategy: the first request in line is let through to the port, which answers a wait, so the item holds its place
        world.Merge = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", "required checks have not passed", Transient: true);
        await using var h = await TrackedSessionHost.StartAsync(Wire(world, train: new MergeTrainSettings(MergeStrategy.Train, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30))));
        await Started(h, A);
        await AtVerify(h, A);
        await h.TrackSend(new ChecksObserved(A, 1, "green-1", Head, ChecksVerdict.Green, []));
        var verify = await Row(h, A);
        await h.TrackSend(new VerdictRecorded(A, 1, verify.ArmId, "verdict-1", true, 107, Head, "ok"));
        Assert.True(await Until(async () => (await Train(h))?.Line.Count == 1, 30), "the item did not take its place in the line");
        Assert.True((await Row(h, A)).TrainEntered);
        // someone pushes: the head the PASS covers is not the branch's any more
        await h.TrackSend(new PullRequestChanged(A, 0, "pr-moved", 107, Head2, []));
        var back = await Row(h, A);
        Assert.Equal((StepKind.Verify, "", false), (back.Step, back.VerdictHead, back.TrainEntered));
        Assert.True(await Until(async () => (await Train(h))!.Line.Count == 0, 30), "the place in the line was not given back");
        await NoDeadLetters(h);
    }
}
