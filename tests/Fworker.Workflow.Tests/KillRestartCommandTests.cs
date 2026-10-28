using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W3.4: the kill and restart scenarios of the v2 commands. A command is published to the durable queue (the inbox and outbox in SQL Server), the process is killed
/// while the handler is in the middle of it (before the side effect, after it, between two writes, while the result is delivered), a second host starts on the same database
/// as a restarted gateway does (everything in memory is new), and the tests assert that the pending command is delivered again, the side effect happened once, and the result
/// reached its receiver. The outside world (the runtime, GitHub) lives across the restart and is idempotent as the real one is (a worker name is never reused; a journal
/// comment carries a marker; a Copilot launch is settled against the tasks that exist). The handlers are pre-built like the harness's own; the gateway's are the same calls.
/// They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class KillRestartCommandTests
{
    static bool NoServer => !TrackedSessionHost.Available;

    // ---- the outside world: survives the kill ----
    sealed class Hang
    {
        readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Called at the point of the kill: says the handler is there, and does not come back (the process is killed under it).</summary>
        public async Task Die() { reached.TrySetResult(); await never.Task; }
        public Task Reached => reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Cleanup() => never.TrySetCanceled();
    }

    sealed class Runtime
    {
        public readonly Dictionary<string, string> Started = new();   // worker -> execution
        public int Calls;
        public CommandResult Start(string worker)
        {
            lock (Started) { Calls++; if (!Started.TryGetValue(worker, out var e)) Started[worker] = e = "exec-" + (Started.Count + 1); return CommandResult.Success(e, "http://page/" + e); }
        }
        public int Distinct { get { lock (Started) return Started.Count; } }
    }

    sealed class RuntimePort(Runtime runtime, Func<Task>? before = null, Func<Task>? after = null) : ICommandPort
    {
        public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
        {
            if (before != null) await before();
            var r = runtime.Start(((StartRuntimeSession)command).WorkerName);
            if (after != null) await after();
            return r;
        }
    }

    sealed class Received { public readonly List<object> Messages = new(); public int Count<T>() { lock (Messages) return Messages.OfType<T>().Count(); } }

    // ---- handlers ----
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

    sealed class LineHandler(WriteJournalLineHandler handler, Func<Task>? afterWrite = null) : MessageHandler<WriteJournalLine>
    {
        protected override async Task HandleAsync(WriteJournalLine m, MessageContext context, CancellationToken cancellation)
        {
            var o = await handler.Process(m, cancellation);
            if (o.Retry) throw new CommandRetryException(o.Result.Error);
            if (afterWrite != null) await afterWrite();
            if (o.Failed != null) await context.PublishAsync(o.Failed);
            if (o.Message != null) await context.PublishAsync(o.Message);
        }
    }

    sealed class ResultHandler<T>(Received received, Func<Task>? onReceive = null) : MessageHandler<T> where T : class
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            if (onReceive != null) await onReceive();
            lock (received.Messages) received.Messages.Add(m);
        }
    }

    static void Route<T>(WolverineOptions w) => w.PublishMessage<T>().ToLocalQueue("harness");

    static Action<WolverineOptions> WireRuntime(ICommandPort port, Received received, Func<Task>? onResult = null) => w =>
    {
        // a new process: a new idempotency ledger (the gateway's is in memory)
        w.AddMessageHandler(new StartHandler(new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port)));
        w.AddMessageHandler(new ResultHandler<RuntimeSessionStarted>(received, onResult));
        w.AddMessageHandler(new ResultHandler<RuntimeStartFailed>(received));
        w.AddMessageHandler(new ResultHandler<IntegrationFailed>(received));
        Route<StartRuntimeSession>(w); Route<RuntimeSessionStarted>(w); Route<RuntimeStartFailed>(w); Route<IntegrationFailed>(w);
        w.OnException<CommandRetryException>().RetryTimes(5);
    };

    static StartRuntimeSession Start(string runtime, string worker, int item = 7) => new(RuntimeKeys.Start(runtime, worker), runtime, worker, item, "Development", "prof", "nonce");

    static async Task Publish(TrackedSessionHost h, object message)
    {
        await using var scope = h.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message);
    }

    static async Task Until(Func<bool> condition, string what, int seconds = 45)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(200); }
        Assert.Fail("timed out waiting for " + what);
    }

    /// <summary>Nothing is pending any more: the durable inbox and outbox are empty and nothing was dead-lettered.</summary>
    static async Task Settled(TrackedSessionHost h)
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until)
        {
            if (await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status <> 'Handled'") == 0
                && await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes") == 0) break;
            await Task.Delay(200);
        }
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status <> 'Handled'"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    // ---- runtime start ----
    [Fact]
    public async Task RuntimeStart_KilledBeforeTheCall_IsDeliveredAfterTheRestart_AndStartsOnce()
    {
        if (NoServer) return;
        var runtime = new Runtime(); var received = new Received(); var hang = new Hang();
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireRuntime(new RuntimePort(runtime, before: hang.Die), received));
            await Publish(h, Start("octopus", "kill-1"));
            await hang.Reached;
            Assert.Equal(0, runtime.Calls);   // killed before the side effect
            await h.RestartAsync(WireRuntime(new RuntimePort(runtime), received));
            await Until(() => received.Count<RuntimeSessionStarted>() == 1, "the result of the start");
            Assert.Equal(1, runtime.Distinct);
            Assert.Equal("exec-1", Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages)).Execution);
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    [Fact]
    public async Task RuntimeStart_KilledAfterTheCall_IsRunAgainAfterTheRestart_AndTheRuntimeStartsOnce()
    {
        if (NoServer) return;
        var runtime = new Runtime(); var received = new Received(); var hang = new Hang();
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireRuntime(new RuntimePort(runtime, after: hang.Die), received));
            await Publish(h, Start("octopus", "kill-2"));
            await hang.Reached;
            Assert.Equal(1, runtime.Calls);   // the execution exists, the result was never recorded
            await h.RestartAsync(WireRuntime(new RuntimePort(runtime), received));
            await Until(() => received.Count<RuntimeSessionStarted>() == 1, "the result of the start");
            // the new process has no ledger entry and calls the runtime again; the runtime answers with the execution it has (a worker name is never reused)
            Assert.Equal(1, runtime.Distinct);
            Assert.Equal("exec-1", Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages)).Execution);
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    [Fact]
    public async Task RuntimeStart_TheResultKilledOnDelivery_IsDeliveredAfterTheRestart_WithoutAnotherStart()
    {
        if (NoServer) return;
        var runtime = new Runtime(); var received = new Received(); var hang = new Hang();
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireRuntime(new RuntimePort(runtime), received, onResult: hang.Die));
            await Publish(h, Start("octopus", "kill-3"));
            await hang.Reached;   // the start is done and its result is being delivered to the manager
            Assert.Equal(1, runtime.Calls);
            await h.RestartAsync(WireRuntime(new RuntimePort(runtime), received));
            await Until(() => received.Count<RuntimeSessionStarted>() == 1, "the result to settle back");
            Assert.Equal(1, runtime.Calls);   // the command was completed before the kill: it does not run again
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    // ---- journal ----
    /// <summary>The item's comments and state comment of GitHub, as the ledger reads them. It outlives the process.</summary>
    sealed class FakeIssues : IJournalStore
    {
        readonly object gate = new();
        readonly List<IssueComment> comments = new();
        string? state; int rev;
        public Func<Task>? BeforeSave;
        public int PlantComments { get { lock (gate) return comments.Count(c => c.Body.Contains("Plant manager:")); } }

        public Task<ItemState> Read(int item, CancellationToken ct)
        {
            lock (gate)
                return Task.FromResult(new ItemState
                {
                    CommentId = state == null ? null : 1,
                    State = state == null ? new JsonObject() : (JsonObject)JsonNode.Parse(state)!,
                    Rev = rev,
                    Comments = comments.ToList(),
                });
        }

        public async Task Save(int item, ItemState holder, CancellationToken ct)
        {
            var before = BeforeSave;
            if (before != null) await before();
            lock (gate) { rev++; state = holder.State.ToJsonString(); }
        }

        public Task<long?> Post(int item, string body, CancellationToken ct)
        {
            lock (gate) { var id = 100 + comments.Count; comments.Add(new IssueComment(id, "bot", body)); return Task.FromResult<long?>(id); }
        }

        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => Task.FromResult(PatchOutcome.Gone);
    }

    static Action<WolverineOptions> WireJournal(FakeIssues issues, Received received, Func<Task>? afterWrite = null) => w =>
    {
        w.AddMessageHandler(new LineHandler(new WriteJournalLineHandler(new InMemoryIdempotencyLedger(), new JournalLedger(issues, new JournalSettings(), log: _ => { })), afterWrite));
        w.AddMessageHandler(new ResultHandler<JournalLineWritten>(received));
        w.AddMessageHandler(new ResultHandler<IntegrationFailed>(received));
        Route<WriteJournalLine>(w); Route<JournalLineWritten>(w); Route<IntegrationFailed>(w);
        w.OnException<CommandRetryException>().RetryTimes(5);
    };

    static WriteJournalLine Line(int item, string ev) => new(JournalKeys.For(item, ev, "k"), item, ev, new JsonObject(), "k");

    [Fact]
    public async Task JournalLine_KilledBetweenThePostAndTheStateSave_IsNotPostedTwice()
    {
        if (NoServer) return;
        var issues = new FakeIssues(); var received = new Received(); var hang = new Hang();
        try
        {
            issues.BeforeSave = hang.Die;   // the comment is on GitHub, the key is not yet in the item's state
            await using var h = await TrackedSessionHost.StartAsync(WireJournal(issues, received));
            await Publish(h, Line(7, "offered"));
            await hang.Reached;
            Assert.Equal(1, issues.PlantComments);
            issues.BeforeSave = null;
            await h.RestartAsync(WireJournal(issues, received));
            await Until(() => received.Count<JournalLineWritten>() == 1, "the journal line to be written");
            Assert.Equal(1, issues.PlantComments);   // found by its marker, not posted again
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    [Fact]
    public async Task JournalLine_KilledAfterTheWrite_IsReplayedAfterTheRestart_AsARepeat_AndOneCommentExists()
    {
        if (NoServer) return;
        var issues = new FakeIssues(); var received = new Received(); var hang = new Hang();
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireJournal(issues, received, afterWrite: hang.Die));
            await Publish(h, Line(8, "offered"));
            await hang.Reached;
            Assert.Equal(1, issues.PlantComments);
            await h.RestartAsync(WireJournal(issues, received));
            await Until(() => received.Count<JournalLineWritten>() == 1, "the result of the line");
            Assert.False(Assert.IsType<JournalLineWritten>(Assert.Single(received.Messages)).Posted);   // the key is in the state: a repeat
            Assert.Equal(1, issues.PlantComments);
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    // ---- Copilot launch ----
    sealed class GitHubTasks : ICopilotTaskLookup
    {
        public readonly List<(int Item, string Phase, string Id)> Tasks = new();
        public (string Id, string Url) Create(int item, string phase) { lock (Tasks) { var id = "task-" + (Tasks.Count + 1); Tasks.Add((item, phase, id)); return (id, "https://github.com/tasks/" + id); } }
        public int Count { get { lock (Tasks) return Tasks.Count; } }
        public Task<(string TaskId, string Url)?> FindAsync(int item, string phase, DateTime since, CancellationToken ct)
        {
            lock (Tasks)
            {
                var t = Tasks.FirstOrDefault(x => x.Item == item && x.Phase == phase);
                return Task.FromResult<(string TaskId, string Url)?>(t.Id == null ? null : (t.Id, "https://github.com/tasks/" + t.Id));
            }
        }
    }

    sealed class LaunchPort(GitHubTasks github, Func<Task>? before = null, Func<Task>? after = null) : ICommandPort
    {
        public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
        {
            var s = (StartRuntimeSession)command;
            if (before != null) await before();
            var t = github.Create(s.Item, s.Phase);
            if (after != null) await after();
            return CommandResult.Success(t.Id, t.Url);
        }
    }

    [Fact]
    public async Task CopilotLaunch_KilledAfterTheTaskWasCreated_IsSettledByTheReconciler_AndOneTaskExists()
    {
        if (NoServer) return;
        // the reservations stand for a store that survives the process (the gateway's InMemoryCopilotReservations does not: reported with this scenario)
        var github = new GitHubTasks(); var reservations = new InMemoryCopilotReservations(); var received = new Received(); var hang = new Hang();
        ICommandPort Port(ICommandPort inner) => new CopilotLaunchPort(inner, reservations, new CopilotReconciler(reservations, github), minAge: TimeSpan.Zero);
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireRuntime(Port(new LaunchPort(github, after: hang.Die)), received));
            await Publish(h, Start(CopilotLaunchPort.Runtime, "kill-cop", item: 11));
            await hang.Reached;
            Assert.Equal(1, github.Count);
            Assert.Equal(ReservationState.Reserved, reservations.Get(11, "Development")!.State);   // the outcome was never recorded
            await h.RestartAsync(WireRuntime(Port(new LaunchPort(github)), received));
            await Until(() => received.Count<RuntimeSessionStarted>() == 1, "the result of the launch");
            Assert.Equal(1, github.Count);   // the reservation was settled against GitHub, not launched again
            Assert.Equal("task-1", Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages)).Execution);
            Assert.Equal(ReservationState.Launched, reservations.Get(11, "Development")!.State);
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }

    [Fact]
    public async Task CopilotLaunch_KilledBeforeTheTaskWasCreated_IsLaunchedOnceAfterTheRestart()
    {
        if (NoServer) return;
        var github = new GitHubTasks(); var reservations = new InMemoryCopilotReservations(); var received = new Received(); var hang = new Hang();
        ICommandPort Port(ICommandPort inner) => new CopilotLaunchPort(inner, reservations, new CopilotReconciler(reservations, github), minAge: TimeSpan.Zero);
        try
        {
            await using var h = await TrackedSessionHost.StartAsync(WireRuntime(Port(new LaunchPort(github, before: hang.Die)), received));
            await Publish(h, Start(CopilotLaunchPort.Runtime, "kill-cop2", item: 12));
            await hang.Reached;
            Assert.Equal(0, github.Count);
            await h.RestartAsync(WireRuntime(Port(new LaunchPort(github)), received));
            await Until(() => received.Count<RuntimeSessionStarted>() == 1, "the result of the launch");
            Assert.Equal(1, github.Count);   // the reconciler found no task, released the reservation, the launch ran
            await Settled(h);
        }
        finally { hang.Cleanup(); }
    }
}
