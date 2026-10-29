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

    // #475: the gateway's handler (RuntimeCommandService.Outcome): the run after the last cooldown of the rule settles as a failed start instead of throwing again
    sealed class ExhaustingStartHandler(StartRuntimeSessionHandler handler, int maxAttempts, List<int> attemptsSeen) : MessageHandler<StartRuntimeSession>
    {
        protected override async Task HandleAsync(StartRuntimeSession m, MessageContext context, CancellationToken cancellation)
        {
            var attempts = context.Envelope!.Attempts; lock (attemptsSeen) attemptsSeen.Add(attempts);
            var o = await handler.Process(m, cancellation);
            if (o.Retry && attempts >= maxAttempts) o = StartRuntimeSessionHandler.Exhausted(m, o.Result.Error);
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

    static Action<WolverineOptions> WireExhausting(FakeAdapters adapters, Received received, List<int> attemptsSeen, int pauses) => w =>
    {
        w.AddMessageHandler(new ExhaustingStartHandler(new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), adapters), pauses + 1, attemptsSeen));
        w.AddMessageHandler(new ResultHandler<RuntimeSessionStarted>(received));
        w.AddMessageHandler(new ResultHandler<RuntimeStartFailed>(received));
        w.AddMessageHandler(new ResultHandler<IntegrationFailed>(received));
        w.PublishMessage<StartRuntimeSession>().ToLocalQueue("harness");
        w.PublishMessage<RuntimeSessionStarted>().ToLocalQueue("harness");
        w.PublishMessage<RuntimeStartFailed>().ToLocalQueue("harness");
        w.PublishMessage<IntegrationFailed>().ToLocalQueue("harness");
        // the gateway's rule with short pauses: the retries, then the dead letters as the fallback
        w.OnException<CommandRetryException>().RetryWithCooldown(Enumerable.Repeat(TimeSpan.FromMilliseconds(20), pauses).ToArray()).Then.MoveToErrorQueue();
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
    public async Task Start_ThatExhaustsItsRetries_SettlesAsAFailedStart_NotADeadLetter()
    {
        if (NoServer) return;
        var adapters = new FakeAdapters { Answer = _ => CommandResult.Failed(FailureKind.Transient, "start-error: HttpRequestException") };
        var received = new Received(); var attempts = new List<int>();
        await using var h = await TrackedSessionHost.StartAsync(WireExhausting(adapters, received, attempts, pauses: 4));
        await h.TrackSend(Start("tracked-exhausted"));
        Assert.Equal(5, adapters.Calls);                       // the first run and one after each of the 4 pauses
        Assert.Equal([1, 2, 3, 4, 5], attempts);               // Envelope.Attempts counts from 1: the 5th is the last the rule gives
        var failed = Assert.IsType<RuntimeStartFailed>(Assert.Single(received.Messages, x => x is RuntimeStartFailed));
        Assert.Equal("tracked-exhausted", failed.WorkerName); Assert.StartsWith(RuntimeOutcome.ExhaustedPrefix, failed.Error);
        Assert.Contains(received.Messages, x => x is IntegrationFailed { CommandName: "StartRuntimeSession" });
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task Start_ThatClearsOnTheLastRun_IsAStart_NotAFailure()
    {
        if (NoServer) return;
        var adapters = new FakeAdapters { Answer = n => n < 5 ? CommandResult.Failed(FailureKind.Transient, "start-error: x") : CommandResult.Success("exec-5", "") };
        var received = new Received(); var attempts = new List<int>();
        await using var h = await TrackedSessionHost.StartAsync(WireExhausting(adapters, received, attempts, pauses: 4));
        await h.TrackSend(Start("tracked-last"));
        Assert.Equal("exec-5", Assert.IsType<RuntimeSessionStarted>(Assert.Single(received.Messages)).Execution);
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
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
