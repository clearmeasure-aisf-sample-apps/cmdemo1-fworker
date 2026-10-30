using System.Collections.Concurrent;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.2 (#339) on a Wolverine tracked session over SQL Server: the merge train's state in its table, the order of its line across a durable sequential queue, a transient answer that is never kept as a lasting one, a restart
/// and a crash between the save and the publish. The handlers are the pre-built ones of the harness (no runtime compiler in this project), calling the same service and the same pure rules as the gateway's static
/// handlers; the merge port is a fake the test answers for. The gateway's own host is tested in Gateway.Tests (MergeTrainWiringTests). They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class MergeTrainTrackedTests
{
    const string Repo = "acme/orders";
    const string HeadSha = "aaa1111111111111111111111111111111111111";
    static readonly string Commit = new('b', 40);

    /// <summary>What the fake merge port saw and what it answers.</summary>
    sealed class Port
    {
        public readonly ConcurrentQueue<MergeGranted> Grants = new();
        public readonly ConcurrentQueue<MergeAttempted> Answers = new();
        public volatile Func<MergeGranted, MergeResult> Answer = _ => new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit);
        public int GrantsOf(int item) => Grants.Count(g => g.Request.Item.Number == item);
        public IEnumerable<MergeAttempted> AnswersOf(int item) => Answers.Where(a => a.Item.Number == item);
    }

    sealed class InputHandler<T>(MergeTrainService svc, Func<T, bool>? dieAfterSave = null) : MessageHandler<T> where T : MergeTrainInput
    {
        readonly ConcurrentDictionary<string, int> seen = new();
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var messages = await svc.Handle(m, cancellation);
            // the first attempt saves and dies before it publishes; Wolverine runs the message again
            if (dieAfterSave?.Invoke(m) == true && seen.AddOrUpdate(m.ToString()!, 1, (_, n) => n + 1) == 1) throw new HarnessTransientException("died after the save, before the publish");
            foreach (var o in messages) await context.PublishAsync(o);
        }
    }

    sealed class RouteRequest : MessageHandler<MergeRequested>
    {
        protected override async Task HandleAsync(MergeRequested m, MessageContext context, CancellationToken cancellation) => await context.PublishAsync(MergeTrainRouting.Handle(m));
    }

    sealed class RouteMerged : MessageHandler<ItemMerged>
    {
        protected override async Task HandleAsync(ItemMerged m, MessageContext context, CancellationToken cancellation) => await context.PublishAsync(MergeTrainRouting.Handle(m));
    }

    sealed class PortHandler(Port port) : MessageHandler<MergeGranted>
    {
        protected override async Task HandleAsync(MergeGranted m, MessageContext context, CancellationToken cancellation)
        {
            port.Grants.Enqueue(m);
            await context.PublishAsync(new MergeTrainAnswered(m.Key, m.Request, port.Answer(m)));
        }
    }

    sealed class SagaSink(Port port) : MessageHandler<MergeAttempted>
    {
        protected override Task HandleAsync(MergeAttempted m, MessageContext context, CancellationToken cancellation) { port.Answers.Enqueue(m); return Task.CompletedTask; }
    }

    sealed class DecidedHandler(MergeTrainHistory history) : MessageHandler<MergeTrainDecided>
    {
        protected override Task HandleAsync(MergeTrainDecided m, MessageContext context, CancellationToken cancellation) => history.Record(m, context.Envelope!.Id, cancellation);
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static MergeTrainSettings Train => new(MergeStrategy.Train, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    static MergeTrainSettings Direct => new(MergeStrategy.Direct);

    /// <summary>The train on the database of the host: its three inputs through the durable sequential queue, the turns to the fake port, the answers to the sink, the decisions to the history.</summary>
    static Func<string, Action<WolverineOptions>> Wire(Port port, MergeTrainSettings settings, Func<MergeTrainInput, bool>? dieAfterSave = null) => cs => w =>
    {
        var svc = new MergeTrainService(new SqlMergeTrainStore(() => Open(cs)), settings);
        w.AddMessageHandler(new InputHandler<ReadyToMerge>(svc, m => dieAfterSave?.Invoke(m) == true));
        w.AddMessageHandler(new InputHandler<MergeTrainAnswered>(svc));
        w.AddMessageHandler(new InputHandler<MergeTrainLeft>(svc));
        w.AddMessageHandler(new RouteRequest());
        w.AddMessageHandler(new RouteMerged());
        w.AddMessageHandler(new PortHandler(port));
        w.AddMessageHandler(new SagaSink(port));
        w.AddMessageHandler(new DecidedHandler(new MergeTrainHistory(() => Open(cs))));
        MergeTrainTopology.Route(w);
        foreach (var t in new[] { typeof(MergeRequested), typeof(ItemMerged), typeof(MergeAttempted), typeof(MergeTrainDecided) }) w.PublishMessage(t).ToLocalQueue("merge-side");
        w.LocalQueue("merge-side").UseDurableInbox();
    };

    static MergeRequested Req(int n, int attempt = 1, string repo = "orders") => new(new ItemId("acme", repo, n), 1, "1.4", attempt, 10 + n, HeadSha, 0, "");

    static async Task<bool> Until(Func<bool> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (cond()) return true; await Task.Delay(100); }
        return false;
    }

    static async Task<MergeTrainState?> Row(TrackedSessionHost h, string key = Repo) => (await new SqlMergeTrainStore(() => Open(h.ConnectionString)).Load(key, default))?.State;

    static Task<long> History(TrackedSessionHost h, string action) =>
        h.Count($"SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = '{MergeTrainHistory.HistoryType}' AND Detail LIKE '%\"Action\":\"{action}\"%'");

    static async Task NoDeadLetters(TrackedSessionHost h) => Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));

    // ---- the table -------------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task TheStore_SavesByRevision_AStaleWriterAndASecondInsertOfTheKeyLose_AndTheJsonRoundTrips()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync();
        var store = new SqlMergeTrainStore(() => Open(h.ConnectionString));
        var state = MergeTrainState.New(Repo);
        state.Line.Add(new MergeTrainEntry { Item = "acme/orders#1", Generation = 1, ArmId = "1.4", Attempt = 1, PullRequest = 11, Head = HeadSha, Order = 1, EnqueuedAt = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow, Turns = 1, HeadSince = DateTimeOffset.UtcNow });
        state.NextOrder = 2; state.Sequence = 3;
        Assert.True(await store.Save(Repo, state, 0, default));
        Assert.False(await store.Save(Repo, state, 0, default));   // (the key is taken)
        var read = (await store.Load(Repo, default))!;
        Assert.Equal(1, read.Revision);
        Assert.Equal(state.ToJson(), read.State.ToJson());
        Assert.True(await store.Save(Repo, read.State, 1, default));
        Assert.False(await store.Save(Repo, read.State, 1, default));   // (a writer that read revision 1 lost to the one that made revision 2)
        Assert.Equal(2, (await store.Load(Repo, default))!.Revision);
        Assert.Null(await store.Load("acme/other", default));
    }

    // ---- direct ----------------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task Direct_ARequestIsGranted_Merged_AndAnsweredToTheSaga_StateAndHistoryInTheDatabase()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port();
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Direct));
        await h.TrackSend(Req(1));
        var answer = Assert.Single(port.AnswersOf(1));
        Assert.Equal((MergeResultKind.Merged, Commit, "merge-attempt|1.4|1"), (answer.Result.Kind, answer.Result.Commit, answer.Key));
        Assert.Equal(1, port.GrantsOf(1));
        Assert.True(port.Grants.Single().FirstTurn);
        var state = (await Row(h))!;
        Assert.Empty(state.Line);
        Assert.Equal([("acme/orders#1", Commit)], state.Merged.Select(m => (m.Item, m.Commit)));
        Assert.Equal(2, (await new SqlMergeTrainStore(() => Open(h.ConnectionString)).Load(Repo, default))!.Revision);   // the request and the answer
        Assert.True(await Until(() => History(h, "merged").GetAwaiter().GetResult() == 1, 20));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task Direct_SeveralItems_AreAllLetThroughWithoutWaiting()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Direct));
        foreach (var n in new[] { 1, 2, 3 }) await h.TrackSend(Req(n));
        Assert.Equal([1, 2, 3], port.Grants.Select(g => g.Request.Item.Number).OrderBy(n => n));
        Assert.DoesNotContain(port.Answers, a => a.Result.Reason == MergeTrain.WaitReason);
        await NoDeadLetters(h);
    }

    // ---- train -----------------------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task Train_TheSecondItemWaitsForTheFirst_ThenMergesInItsTurn()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };   // (item 1 waits for its checks)
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        await h.TrackSend(Req(1));
        await h.TrackSend(Req(2));
        Assert.Equal((1, 0), (port.GrantsOf(1), port.GrantsOf(2)));
        var wait = Assert.Single(port.AnswersOf(2));
        Assert.Equal((MergeResultKind.Blocked, MergeTrain.WaitReason, true), (wait.Result.Kind, wait.Result.Reason, wait.Result.Transient));
        Assert.Equal(["acme/orders#1", "acme/orders#2"], (await Row(h))!.Line.OrderBy(e => e.Order).Select(e => e.Item));

        // item 1 is merged on its next attempt; item 2 asks again and gets the turn
        port.Answer = _ => new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit);
        await h.TrackSend(Req(1, attempt: 2));
        await h.TrackSend(Req(2, attempt: 2));
        Assert.Equal((2, 1), (port.GrantsOf(1), port.GrantsOf(2)));
        Assert.Equal([MergeResultKind.Blocked, MergeResultKind.Merged], port.AnswersOf(2).Select(a => a.Result.Kind));
        var state = (await Row(h))!;
        Assert.Empty(state.Line);
        Assert.Equal(["acme/orders#1", "acme/orders#2"], state.Merged.Select(m => m.Item));
        Assert.True(await Until(() => History(h, "merged").GetAwaiter().GetResult() == 2 && History(h, "enqueued").GetAwaiter().GetResult() == 2 && History(h, "granted").GetAwaiter().GetResult() >= 3, 20));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task Train_RequestsThatArriveTogether_GrantExactlyOneTurn_TheLineIsSerializedByTheQueue()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        var bus = h.Host.Services.GetRequiredService<IMessageBus>();
        await Task.WhenAll(Enumerable.Range(1, 8).Select(n => bus.PublishAsync(Req(n)).AsTask()));
        Assert.True(await Until(() => port.Answers.Count(a => a.Result.Reason == MergeTrain.WaitReason) == 7 && port.Answers.Count == 8, 40), $"answers: {port.Answers.Count}");
        Assert.Single(port.Grants);   // (one turn, whoever was first)
        var state = (await Row(h))!;
        Assert.Equal(8, state.Line.Count);
        Assert.Equal(8, state.Line.Select(e => e.Order).Distinct().Count());
        Assert.Equal(1, state.Line.Count(e => e.Turns > 0));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task Train_EachRepositoryHasItsOwnLine_InTheTable()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        await h.TrackSend(Req(1)); await h.TrackSend(Req(1, repo: "billing")); await h.TrackSend(Req(2, repo: "billing"));
        Assert.Equal(2, port.Grants.Count);   // (the first of each repository)
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.MergeTrains"));
        Assert.Equal(["acme/billing#1", "acme/billing#2"], (await Row(h, "acme/billing"))!.Line.OrderBy(e => e.Order).Select(e => e.Item));
        Assert.Equal(["acme/orders#1"], (await Row(h))!.Line.Select(e => e.Item));
    }

    [SqlFact]
    public async Task Train_ATransientFaultOfThePort_IsNotKeptAsALastingAnswer_TheItemMergesOnTheNextAttempt()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "merge-error", "HttpRequestException", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        await h.TrackSend(Req(1));
        var first = Assert.Single(port.AnswersOf(1));
        Assert.Equal(("merge-error", true), (first.Result.Reason, first.Result.Transient));   // (the saga hears the fault as it was: a wait)
        Assert.Equal(["acme/orders#1"], (await Row(h))!.Line.Select(e => e.Item));           // (and the item is still in the line)
        Assert.Empty((await Row(h))!.Merged);

        port.Answer = _ => new MergeResult(MergeResultKind.Merged, "merged", Commit: Commit);
        await h.TrackSend(Req(1, attempt: 2));
        Assert.Equal([MergeResultKind.Blocked, MergeResultKind.Merged], port.AnswersOf(1).Select(a => a.Result.Kind));
        Assert.Empty((await Row(h))!.Line);
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task Train_AMergeByAPerson_ReleasesThePlace_ThroughTheDeliveryTrainsItemMerged()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        await h.TrackSend(Req(1)); await h.TrackSend(Req(2));
        Assert.Equal(0, port.GrantsOf(2));
        var gates = new ItemMergedGates(false, "", false, false, false, []);
        await h.TrackSend(new ItemMerged(new ItemId("acme", "orders", 1), Commit, 11, gates, [], "webhook"));
        Assert.Equal(["acme/orders#2"], (await Row(h))!.Line.Select(e => e.Item));
        await h.TrackSend(Req(2, attempt: 2));
        Assert.Equal(1, port.GrantsOf(2));
        // an item of a repository that never had a train row creates none
        await h.TrackSend(new ItemMerged(new ItemId("acme", "other", 4), Commit, 12, gates, [], "webhook"));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.MergeTrains"));
        await NoDeadLetters(h);
    }

    // ---- restart and crash -----------------------------------------------------------------------------------------------------------------

    [SqlFact]
    public async Task ARestart_KeepsTheLine_TheNewProcessAnswersTheSecondItemAWait()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port { Answer = _ => new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true) };
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train));
        await h.TrackSend(Req(1));
        var port2 = new Port { Answer = port.Answer };
        await h.RestartAsync(Wire(port2, Train)(h.ConnectionString));
        await h.TrackSend(Req(2));
        Assert.Empty(port2.Grants);
        Assert.Equal(MergeTrain.WaitReason, Assert.Single(port2.AnswersOf(2)).Result.Reason);
        Assert.Equal(["acme/orders#1", "acme/orders#2"], (await Row(h))!.Line.OrderBy(e => e.Order).Select(e => e.Item));
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task ACrashBetweenTheSaveAndThePublish_IsHealedByTheRedelivery_OneTurnOneEntry_AndTheItemMerges()
    {
        if (!TrackedSessionHost.Available) return;
        var port = new Port();
        await using var h = await TrackedSessionHost.StartAsync(Wire(port, Train, dieAfterSave: m => m is ReadyToMerge));
        await h.TrackSend(Req(1));
        Assert.Equal(1, port.GrantsOf(1));   // (the first attempt died before publishing the turn; the second published it, once)
        var answer = Assert.Single(port.AnswersOf(1));
        Assert.Equal(MergeResultKind.Merged, answer.Result.Kind);
        var state = (await Row(h))!;
        Assert.Empty(state.Line);   // (one entry: merged and gone)
        Assert.Single(state.Merged);
        await NoDeadLetters(h);
    }
}
