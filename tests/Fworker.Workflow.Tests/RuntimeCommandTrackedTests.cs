using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W3.1 on a Wolverine tracked session (the pattern of TrackedSessionTests): a StartRuntimeSession goes through the durable outbox to its handler, which runs the
/// command base and cascades the result message that the RuntimeManager receives. The handlers here are pre-built like the harness's own; the gateway's
/// (gateway/Runtimes/RuntimeCommandService.cs) are the same calls. They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class RuntimeCommandTrackedTests
{
    static bool NoServer => !TrackedSessionHost.Available;

    sealed class FakeAdapters : ICommandPort
    {
        public int Calls; public Func<int, CommandResult> Answer = _ => CommandResult.Success("exec-1", "http://page");
        public Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default) => Task.FromResult(Answer(++Calls));
    }

    sealed class Received { public readonly List<object> Messages = new(); }

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

    sealed class ResultHandler<T>(Received received) : MessageHandler<T> where T : class
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) { lock (received.Messages) received.Messages.Add(m); return Task.CompletedTask; }
    }

    static Action<WolverineOptions> Wire(FakeAdapters adapters, Received received) => w =>
    {
        w.AddMessageHandler(new StartHandler(new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), adapters)));
        w.AddMessageHandler(new ResultHandler<RuntimeSessionStarted>(received));
        w.AddMessageHandler(new ResultHandler<RuntimeStartFailed>(received));
        w.AddMessageHandler(new ResultHandler<IntegrationFailed>(received));
        w.PublishMessage<StartRuntimeSession>().ToLocalQueue("harness");
        w.PublishMessage<RuntimeSessionStarted>().ToLocalQueue("harness");
        w.PublishMessage<RuntimeStartFailed>().ToLocalQueue("harness");
        w.PublishMessage<IntegrationFailed>().ToLocalQueue("harness");
        w.OnException<CommandRetryException>().RetryTimes(5);
    };

    static StartRuntimeSession Start(string worker) => new(RuntimeKeys.Start("octopus", worker), "octopus", worker, 7, "Development", "prof", "nonce");

    [Fact]
    public async Task Start_IsHandled_AndTheResultMessageReachesTheManager()
    {
        if (NoServer) return;
        var adapters = new FakeAdapters(); var received = new Received();
        await using var h = await TrackedSessionHost.StartAsync(Wire(adapters, received));
        var session = await h.TrackSend(Start("tracked-1"));
        Assert.Single(session.Executed.MessagesOf<RuntimeSessionStarted>());
        var m = Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages));
        Assert.Equal(("tracked-1", "exec-1"), (m.WorkerName, m.Execution));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task Start_ATransientFailure_IsRetriedByWolverine_AndTheRuntimeIsCalledAgain()
    {
        if (NoServer) return;
        var adapters = new FakeAdapters { Answer = n => n < 3 ? CommandResult.Failed(FailureKind.Transient, "start-error: x") : CommandResult.Success("exec-3", "") };
        var received = new Received();
        await using var h = await TrackedSessionHost.StartAsync(Wire(adapters, received));
        await h.TrackSend(Start("tracked-2"));
        Assert.Equal(3, adapters.Calls);
        Assert.Equal("exec-3", Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages)).Execution);
    }

    [Fact]
    public async Task Start_APermanentFailure_SendsStartFailed_AndIntegrationFailed_WithoutDeadLettering()
    {
        if (NoServer) return;
        var adapters = new FakeAdapters { Answer = _ => CommandResult.Failed(FailureKind.Permanent, "runtime off: octopus") };
        var received = new Received();
        await using var h = await TrackedSessionHost.StartAsync(Wire(adapters, received));
        await h.TrackSend(Start("tracked-3"));
        Assert.Equal(1, adapters.Calls);
        Assert.Contains(received.Messages, x => x is RuntimeStartFailed { Kind: FailureKind.Permanent });
        Assert.Contains(received.Messages, x => x is IntegrationFailed { CommandName: "StartRuntimeSession" });
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }
}
