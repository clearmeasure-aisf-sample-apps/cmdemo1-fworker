using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W3.1: the handlers of the runtime start, stop and runbook run: idempotency on replay, transient against permanent, and the result message for the manager.</summary>
public class RuntimeCommandTests
{
    sealed class Port : ICommandPort
    {
        public int Calls; public Func<IntegrationCommand, CommandResult> Answer = _ => CommandResult.Success("exec-1", "http://page");
        public Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default) { Calls++; return Task.FromResult(Answer(command)); }
    }

    sealed class Runner : IRunbookRunner
    {
        public int Calls; public Func<CommandResult> Answer = () => CommandResult.Success("task-1", "http://task"); public Exception? Throw;
        public Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct) { Calls++; if (Throw != null) throw Throw; return Task.FromResult(Answer()); }
    }

    static StartRuntimeSession Start(string worker = "w1") => new(RuntimeKeys.Start("octopus", worker), "octopus", worker, 7, "Development", "prof", "nonce");
    static RunRunbook Book(string key = "k1") => new(RuntimeKeys.Runbook("p", "Start worker", "tdd", key), "p", "Start worker", "tdd", new Dictionary<string, string> { ["a"] = "b" });

    [Fact]
    public async Task Start_Success_SendsSessionStarted_WithTheExecutionAndPage()
    {
        var o = await new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), new Port()).Process(Start());
        var m = Assert.IsType<RuntimeSessionStarted>(o.Message);
        Assert.Equal(("octopus", "w1", 7, "Development", "exec-1", "http://page"), (m.Runtime, m.WorkerName, m.Item, m.Phase, m.Execution, m.Url));
        Assert.Null(o.Failed); Assert.False(o.Retry);
    }

    [Fact]
    public async Task Start_Replay_OfASettledKey_DoesNotCallTheRuntimeAgain_AndAnswersTheSame()
    {
        var port = new Port(); var h = new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var first = await h.Process(Start()); var again = await h.Process(Start());
        Assert.Equal(1, port.Calls);
        Assert.Equal(first.Message, again.Message);
    }

    [Fact]
    public async Task Start_DifferentWorkers_HaveDifferentKeys_AndBothRun()
    {
        var port = new Port(); var h = new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        await h.Process(Start("a")); await h.Process(Start("b"));
        Assert.Equal(2, port.Calls);
        Assert.NotEqual(RuntimeKeys.Start("octopus", "a"), RuntimeKeys.Start("octopus", "b"));
    }

    [Fact]
    public async Task Start_Transient_IsRetried_NotRecorded_AndSendsNothing()
    {
        var port = new Port { Answer = _ => CommandResult.Failed(FailureKind.Transient, "start-error: x") };
        var h = new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var o = await h.Process(Start());
        Assert.True(o.Retry); Assert.Null(o.Message); Assert.Null(o.Failed);
        port.Answer = _ => CommandResult.Success("exec-2", "");
        var again = await h.Process(Start());   // a retryable failure is not remembered: the retry runs
        Assert.IsType<RuntimeSessionStarted>(again.Message); Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task Start_Permanent_SendsStartFailed_AndIntegrationFailed_AndIsRecorded()
    {
        var port = new Port { Answer = _ => CommandResult.Failed(FailureKind.Permanent, "runtime off: octopus") };
        var h = new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var o = await h.Process(Start());
        var f = Assert.IsType<RuntimeStartFailed>(o.Message);
        Assert.Equal(FailureKind.Permanent, f.Kind); Assert.Equal("runtime off: octopus", f.Error);
        Assert.Equal("StartRuntimeSession", o.Failed!.CommandName); Assert.Equal(Start().IdempotencyKey, o.Failed.IdempotencyKey);
        await h.Process(Start());
        Assert.Equal(1, port.Calls);
    }

    [Theory]
    [InlineData("no capacity")]
    [InlineData("quota")]
    public async Task Start_NoCapacityOrQuota_GoesBackToTheManagerAtOnce_AsRateLimited_NotAsFailure(string error)
    {
        var port = new Port { Answer = _ => CommandResult.Failed(FailureKind.RateLimited, error) };
        var o = await new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port).Process(Start());
        var f = Assert.IsType<RuntimeStartFailed>(o.Message);
        Assert.Equal(FailureKind.RateLimited, f.Kind); Assert.Equal(error, f.Error);
        Assert.Null(o.Failed); Assert.False(o.Retry);
    }

    [Fact]
    public async Task Start_AnExceptionOfTheRuntime_IsTransient()
    {
        var port = new Port { Answer = _ => throw new HttpRequestException("down") };
        var o = await new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port).Process(Start());
        Assert.True(o.Retry); Assert.Equal(FailureKind.Transient, o.Result.Failure);
    }

    [Fact]
    public async Task Start_WithoutAKey_IsPermanent()
    {
        var o = await new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), new Port()).Process(Start() with { IdempotencyKey = "" });
        Assert.Equal(FailureKind.Permanent, o.Result.Failure); Assert.NotNull(o.Failed);
    }

    [Fact]
    public async Task Stop_Success_SendsStopped_AndReplayStopsOnce()
    {
        var port = new Port { Answer = _ => CommandResult.Success() }; var h = new StopRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var c = new StopRuntimeSession(RuntimeKeys.Stop("octopus", "w1"), "octopus", "w1", "done");
        var o = await h.Process(c); await h.Process(c);
        Assert.True(Assert.IsType<RuntimeSessionStopped>(o.Message).Ok);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Stop_Transient_IsRetried_Permanent_ReportsFailure()
    {
        var port = new Port { Answer = _ => CommandResult.Failed(FailureKind.Transient, "stop-error") }; var h = new StopRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var c = new StopRuntimeSession(RuntimeKeys.Stop("octopus", "w1"), "octopus", "w1", "done");
        Assert.True((await h.Process(c)).Retry);
        port.Answer = _ => CommandResult.Failed(FailureKind.Permanent, "runtime off: octopus");
        var o = await h.Process(c);
        Assert.False(Assert.IsType<RuntimeSessionStopped>(o.Message).Ok); Assert.NotNull(o.Failed);
    }

    [Fact]
    public async Task Runbook_Success_SendsFinished_AndReplayRunsOnce()
    {
        var runner = new Runner(); var h = new RunRunbookHandler(new InMemoryIdempotencyLedger(), runner);
        var o = await h.Process(Book()); await h.Process(Book());
        var m = Assert.IsType<RunbookRunFinished>(o.Message);
        Assert.True(m.Ok); Assert.Equal("task-1", m.Execution);
        Assert.Equal(1, runner.Calls);
        await h.Process(Book("k2"));   // another caller key is another run
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task Runbook_TransientException_Retries_RateLimited_Retries_Permanent_Fails()
    {
        var runner = new Runner { Throw = new TimeoutException() }; var h = new RunRunbookHandler(new InMemoryIdempotencyLedger(), runner);
        Assert.True((await h.Process(Book())).Retry);
        runner.Throw = null; runner.Answer = () => CommandResult.Failed(FailureKind.RateLimited, "429");
        Assert.True((await h.Process(Book())).Retry);
        runner.Answer = () => CommandResult.Failed(FailureKind.Permanent, "runbook-not-published");
        var o = await h.Process(Book());
        Assert.False(Assert.IsType<RunbookRunFinished>(o.Message).Ok); Assert.Equal("RunRunbook", o.Failed!.CommandName);
    }
}
