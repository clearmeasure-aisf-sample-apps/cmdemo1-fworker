using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10a: the hand-over of the Solo node between two gateway processes of a release, on one database. In Wolverine's Solo mode every node is node 1, and a node that starts clears the
/// node table and releases the ownership of every row of node 1 (StartSoloModeAsync); so a successor that started while the old node was still handling rows took those rows over and
/// handled them a second time. The gateway now stops the old node first (WolverineHostService, GatewayLeader.BeforeHandOver: handlers in flight finish, ownership is released) and starts the
/// new one when it holds the lease and has the old one's snapshot. These tests run the two nodes in that order with a start of a runtime session and a merge IN FLIGHT in the old node and show
/// that each side effect happened once. (The overlap itself, a successor started beside a node that is handling a merge, did double the merge in 3 of 4 runs while this was written;
/// it is not a test, because Wolverine's recovery of the released rows is a race.) They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class NodeHandOverTests
{
    static bool NoServer => !TrackedSessionHost.Available;

    /// <summary>A pull request merge, a message of the test (the merge train's command is in a later wave's port): the side effect is counted at the "GitHub" below.</summary>
    public sealed record HandOverMerge(string Pr);
    public sealed record HandOverMerged(string Pr);

    /// <summary>The outside world, which outlives both nodes. A merge is NOT guarded here: a second call would be a second merge, which is what the counts would show.</summary>
    sealed class World
    {
        readonly object gate = new();
        readonly Dictionary<string, int> merges = new();
        readonly Dictionary<string, string> executions = new();
        public int Calls;
        public int Merges(string pr) { lock (gate) return merges.GetValueOrDefault(pr); }
        public int Executions { get { lock (gate) return executions.Count; } }
        public void Merge(string pr) { lock (gate) merges[pr] = merges.GetValueOrDefault(pr) + 1; }
        public CommandResult Start(string worker)
        {   // a worker name is never reused: the runtime answers a second call with the execution it has, but the call is counted
            lock (gate) { Calls++; if (!executions.TryGetValue(worker, out var e)) executions[worker] = e = "exec-" + (executions.Count + 1); return CommandResult.Success(e, "http://page/" + e); }
        }
    }

    /// <summary>A handler that is in the middle of its work: it says so, and waits to be let go.</summary>
    sealed class Gate
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Pass() { entered.TrySetResult(); await open.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        public Task Entered => entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Open() => open.TrySetResult();
    }

    sealed class Received
    {
        readonly object gate = new();
        readonly List<object> messages = new();
        public void Add(object m) { lock (gate) messages.Add(m); }
        public int Count<T>() { lock (gate) return messages.OfType<T>().Count(); }
    }

    sealed class GatedRuntimePort(World world, Gate? gate) : ICommandPort
    {
        public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
        {
            if (gate != null) await gate.Pass();   // the call is in flight when the hand-over begins
            return world.Start(((StartRuntimeSession)command).WorkerName);
        }
    }

    sealed class StartHandler(StartRuntimeSessionHandler handler) : MessageHandler<StartRuntimeSession>
    {
        protected override async Task HandleAsync(StartRuntimeSession m, MessageContext context, CancellationToken cancellation)
        {
            var o = await handler.Process(m, cancellation);
            if (o.Retry) throw new CommandRetryException(o.Result.Error);
            if (o.Failed != null) await context.PublishAsync(o.Failed);
            if (o.Message != null) await context.PublishAsync(o.Message);
        }
    }

    sealed class MergeHandler(World world, Gate? gate) : MessageHandler<HandOverMerge>
    {
        protected override async Task HandleAsync(HandOverMerge m, MessageContext context, CancellationToken cancellation)
        {
            if (gate != null) await gate.Pass();
            world.Merge(m.Pr);
            await context.PublishAsync(new HandOverMerged(m.Pr));
        }
    }

    sealed class ResultHandler<T>(Received received) : MessageHandler<T> where T : class
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) { received.Add(m); return Task.CompletedTask; }
    }

    static void Route<T>(WolverineOptions w) => w.PublishMessage<T>().ToLocalQueue("harness");

    /// <summary>The handlers of one node (a gateway process): its own in-memory idempotency ledger, as the gateway's runtime commands have.</summary>
    static Action<WolverineOptions> Node(World world, Received received, Gate? startGate = null, Gate? mergeGate = null) => w =>
    {
        w.AddMessageHandler(new StartHandler(new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), new GatedRuntimePort(world, startGate))));
        w.AddMessageHandler(new MergeHandler(world, mergeGate));
        w.AddMessageHandler(new ResultHandler<RuntimeSessionStarted>(received));
        w.AddMessageHandler(new ResultHandler<RuntimeStartFailed>(received));
        w.AddMessageHandler(new ResultHandler<IntegrationFailed>(received));
        w.AddMessageHandler(new ResultHandler<HandOverMerged>(received));
        Route<StartRuntimeSession>(w); Route<RuntimeSessionStarted>(w); Route<RuntimeStartFailed>(w); Route<IntegrationFailed>(w); Route<HandOverMerge>(w); Route<HandOverMerged>(w);
        w.OnException<CommandRetryException>().RetryTimes(5);
    };

    static StartRuntimeSession Start(string worker) => new(RuntimeKeys.Start("octopus", worker), "octopus", worker, 7, "Development", "prof", "nonce");

    static async Task Publish(IHost host, params object[] messages)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var m in messages) await bus.PublishAsync(m);
    }

    static async Task Until(Func<bool> condition, string what, int seconds = 45)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(100); }
        Assert.Fail("timed out waiting for " + what);
    }

    static async Task Settled(TrackedSessionHost h)
    {
        await Until(async () => await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status <> 'Handled'") == 0
            && await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes") == 0, "the durable inbox and outbox to be empty");
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    static async Task Until(Func<Task<bool>> condition, string what, int seconds = 45)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (await condition()) return; await Task.Delay(100); }
        Assert.Fail("timed out waiting for " + what);
    }

    [Fact]
    public async Task TheOldNodeLeavesWithAStartAndAMergeInFlight_ThenTheNewNodeStarts_AndEachSideEffectHappenedOnce()
    {
        if (NoServer) return;
        var world = new World(); var received = new Received(); var startGate = new Gate(); var mergeGate = new Gate();
        try
        {
            await using var old = await TrackedSessionHost.StartAsync(Node(world, received, startGate, mergeGate));
            await Publish(old.Host, Start("handover-1"), new HandOverMerge("pr-9"));
            await startGate.Entered; await mergeGate.Entered;   // both handlers of the old node are in the middle of their work
            Assert.Equal(0, world.Calls);
            Assert.Equal(0, world.Merges("pr-9"));

            // the hand-over, in the order the gateway now keeps: the old node leaves (the handlers finish, the ownership of its rows is released) ...
            var leaving = old.Host.StopAsync(TimeSpan.FromSeconds(30));
            startGate.Open(); mergeGate.Open();
            await leaving.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(1, world.Calls);
            Assert.Equal(1, world.Merges("pr-9"));

            // ... and only then does the new node start (the lease taken, the snapshot seen); it finds nothing of the old one to take over
            var next = await old.StartSecondHostAsync(Node(world, received));
            try
            {
                await Until(() => received.Count<RuntimeSessionStarted>() == 1 && received.Count<HandOverMerged>() == 1, "the results of the start and of the merge");
                await Settled(old);
                await Task.Delay(1500);   // a row handled a second time would show by now
                Assert.Equal(1, world.Calls);
                Assert.Equal(1, world.Executions);
                Assert.Equal(1, world.Merges("pr-9"));
                Assert.Equal(1, received.Count<RuntimeSessionStarted>());
                Assert.Equal(1, received.Count<HandOverMerged>());
            }
            finally { try { await next.StopAsync(TimeSpan.FromSeconds(10)); } catch { } next.Dispose(); }
        }
        finally { startGate.Open(); mergeGate.Open(); }
    }
}
