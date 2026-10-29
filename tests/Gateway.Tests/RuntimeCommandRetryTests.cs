using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// #475: a v2 runtime start (or stop) whose retries are exhausted settles as a failed result instead of going to the dead letters. The service decides per run
/// (Envelope.Attempts): before the last run a failure that is tried again throws, so Wolverine's rule runs the command again; the last run publishes the failed
/// result, which reaches the RuntimeManager as CompleteStart (the item is taken back as in v1) or CompleteStop.
/// </summary>
public sealed class RuntimeCommandRetryTests
{
    sealed class Port : ICommandPort
    {
        public int Calls; public Func<IntegrationCommand, int, CommandResult> Answer = (_, _) => CommandResult.Failed(FailureKind.Transient, "start-error: HttpRequestException");
        public Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default) => Task.FromResult(Answer(command, ++Calls));
    }

    sealed class Rt : IWorkerRuntime
    {
        public string Kind => "cursor-cloud"; public int MaxConcurrent => 5;
        public List<(string, string)> Stops = new();
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "exec-v1", "", "http://v1"));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) { Stops.Add((workerName, reason)); return Task.CompletedTask; }
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>([]);
    }

    sealed class ThrowingLedger : IIdempotencyLedger
    {
        public int Reads;
        public CommandResult? TryGet(string key) { Reads++; throw new InvalidOperationException("connection detail that must not travel"); }
        public void Record(string key, CommandResult result) { }
    }

    static RuntimeManager Manager(Rt rt)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.IsOn = () => true; limits.MaxConcurrent = 5; limits.MaxExecutionsPerDay = 100;
        return new RuntimeManager(cfg, rt, new RuntimeNonces(), limits) { MayAct = () => true };
    }

    static RuntimeCommandService Service(Port port, IIdempotencyLedger? ledger = null, RuntimeManager? manager = null)
    {
        ledger ??= new InMemoryIdempotencyLedger();
        return new RuntimeCommandService(new StartRuntimeSessionHandler(ledger, port), new StopRuntimeSessionHandler(ledger, port), new RunRunbookHandler(ledger, new NoRunner()),
            _ => manager);
    }

    sealed class NoRunner : IRunbookRunner
    {
        public Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct) => Task.FromResult(CommandResult.Failed(FailureKind.Transient, "runbook"));
    }

    static StartRuntimeSession Start(string worker = "w1") => new(RuntimeKeys.Start("cursor-cloud", worker), "cursor-cloud", worker, 7, "Development", "prof", "nonce");
    static StopRuntimeSession Stop(string worker = "w1") => new(RuntimeKeys.Stop("cursor-cloud", worker), "cursor-cloud", worker, "done");

    static Task<IReadOnlyList<object>> OutcomeOfStart(RuntimeCommandService s, StartRuntimeSession c, int attempts) => s.StartMessages(c, attempts, CancellationToken.None);

    [Fact]
    public void TheRetryRule_AndTheLastRun_AreOneNumber()
    {
        Assert.Equal(RuntimeCommandService.RetryPauses.Length + 1, RuntimeCommandService.MaxAttempts);
        Assert.Equal(5, RuntimeCommandService.MaxAttempts);
    }

    [Fact]
    public async Task ATransientStart_BeforeTheLastRun_ThrowsSoThatWolverineRunsItAgain()
    {
        var port = new Port(); var s = Service(port); var c = Start();
        for (var attempt = 1; attempt < RuntimeCommandService.MaxAttempts; attempt++)
            await Assert.ThrowsAsync<CommandRetryException>(() => OutcomeOfStart(s, c, attempt));
        Assert.Equal(RuntimeCommandService.MaxAttempts - 1, port.Calls);
    }

    [Fact]
    public async Task AStart_ThatStillFailsOnTheLastRun_PublishesAFailedStart_AndTheSagasMessage_NoException()
    {
        var port = new Port(); var s = Service(port); var c = Start("w2");
        var messages = await OutcomeOfStart(s, c, RuntimeCommandService.MaxAttempts);
        var failed = Assert.IsType<RuntimeStartFailed>(Assert.Single(messages, m => m is RuntimeStartFailed));
        Assert.Equal(("cursor-cloud", "w2", 7, "Development"), (failed.Runtime, failed.WorkerName, failed.Item, failed.Phase));
        Assert.StartsWith(RuntimeOutcome.ExhaustedPrefix, failed.Error); Assert.Contains("HttpRequestException", failed.Error);
        Assert.Contains(messages, m => m is IntegrationFailed { CommandName: "StartRuntimeSession" });
    }

    [Fact]
    public async Task ATransientFault_ThatClears_OnALaterRun_StartsTheSession_NothingFailedIsCached()
    {
        var port = new Port { Answer = (_, n) => n < 3 ? CommandResult.Failed(FailureKind.Transient, "start-error: x") : CommandResult.Success("exec-3", "http://page") };
        var s = Service(port); var c = Start();
        await Assert.ThrowsAsync<CommandRetryException>(() => OutcomeOfStart(s, c, 1));
        await Assert.ThrowsAsync<CommandRetryException>(() => OutcomeOfStart(s, c, 2));
        var messages = await OutcomeOfStart(s, c, 3);
        var started = Assert.IsType<RuntimeSessionStarted>(Assert.Single(messages));
        Assert.Equal("exec-3", started.Execution); Assert.Equal(3, port.Calls);
    }

    [Fact]
    public async Task AStart_ThatSucceedsOnTheLastRun_IsStillAStart()
    {
        var port = new Port { Answer = (_, n) => n < RuntimeCommandService.MaxAttempts ? CommandResult.Failed(FailureKind.Transient, "x") : CommandResult.Success("exec-5", "") };
        var s = Service(port); var c = Start();
        for (var attempt = 1; attempt < RuntimeCommandService.MaxAttempts; attempt++) await Assert.ThrowsAsync<CommandRetryException>(() => OutcomeOfStart(s, c, attempt));
        Assert.IsType<RuntimeSessionStarted>(Assert.Single(await OutcomeOfStart(s, c, RuntimeCommandService.MaxAttempts)));
    }

    [Fact]
    public async Task APermanentFailure_IsStillSettledOnTheFirstRun_NotRetried()
    {
        var port = new Port { Answer = (_, _) => CommandResult.Failed(FailureKind.Permanent, "runtime off: cursor-cloud") };
        var messages = await OutcomeOfStart(Service(port), Start(), 1);
        Assert.Equal(1, port.Calls);
        Assert.Equal("runtime off: cursor-cloud", Assert.IsType<RuntimeStartFailed>(messages.Single(m => m is RuntimeStartFailed)).Error);   // not marked exhausted: it was never retried
    }

    [Fact]
    public async Task AFaultOfTheIdempotencyLedger_IsRetriedToo_ThenSettled_AndItsMessageIsNotCarried()
    {
        var ledger = new ThrowingLedger(); var port = new Port(); var s = Service(port, ledger); var c = Start();
        var e = await Assert.ThrowsAsync<CommandRetryException>(() => OutcomeOfStart(s, c, 1));
        Assert.DoesNotContain("must not travel", e.Message);
        var messages = await OutcomeOfStart(s, c, RuntimeCommandService.MaxAttempts);
        var failed = Assert.IsType<RuntimeStartFailed>(messages.Single(m => m is RuntimeStartFailed));
        Assert.DoesNotContain("must not travel", failed.Error); Assert.Contains("InvalidOperationException", failed.Error);
        Assert.Equal(0, port.Calls);   // the runtime was never reached
    }

    [Fact]
    public async Task ACancellation_IsNotASettlement_ItPropagates()
    {
        var c = Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RuntimeCommandService.Outcome(c, () => throw new OperationCanceledException(), e => StartRuntimeSessionHandler.Exhausted(c, e), RuntimeCommandService.MaxAttempts));
    }

    [Fact]
    public async Task AStop_ThatStillFailsOnTheLastRun_PublishesAFailedStop()
    {
        var port = new Port { Answer = (_, _) => CommandResult.Failed(FailureKind.Transient, "stop-error") };
        var s = Service(port); var c = Stop();
        Task<IReadOnlyList<object>> Run(int attempts) => s.StopMessages(c, attempts, CancellationToken.None);
        await Assert.ThrowsAsync<CommandRetryException>(() => Run(1));
        var messages = await Run(RuntimeCommandService.MaxAttempts);
        var stopped = Assert.IsType<RuntimeSessionStopped>(messages.Single(m => m is RuntimeSessionStopped));
        Assert.False(stopped.Ok); Assert.Equal("w1", stopped.WorkerName); Assert.StartsWith(RuntimeOutcome.ExhaustedPrefix, stopped.Error);
        Assert.Contains(messages, m => m is IntegrationFailed { CommandName: "StopRuntimeSession" });
    }

    [Fact]
    public async Task TheFailedStart_ReachesTheManager_AndTheExecutionIsTakenBack_LikeV1()
    {
        var rt = new Rt(); var m = Manager(rt);
        m.DispatchStart = _ => Task.CompletedTask;   // v2: the start is a command; the execution stays starting until a result comes
        var t = m.Begin(7, "Development", "prof");
        await m.Provision(t);
        Assert.True(m.Alive(t.Name));   // starting, no result yet: what #475 left until the execution lapsed
        var s = Service(new Port(), manager: m);
        var c = Start(t.Name);
        var messages = await OutcomeOfStart(s, c, RuntimeCommandService.MaxAttempts);
        foreach (var msg in messages.OfType<RuntimeStartFailed>()) await s.StartFailed(msg);   // the handler of the result message
        Assert.False(m.Alive(t.Name)); Assert.Equal("", m.ExecutionOf(t.Name));
        Assert.Empty(rt.Stops);   // nothing was started, so nothing is stopped
    }

    [Fact]
    public async Task TheFailedStop_ReachesTheManager_WithoutAnException()
    {
        var rt = new Rt(); var m = Manager(rt);
        var s = Service(new Port { Answer = (_, _) => CommandResult.Failed(FailureKind.Transient, "stop-error") }, manager: m);
        var c = Stop();
        var messages = await s.StopMessages(c, RuntimeCommandService.MaxAttempts, CancellationToken.None);
        foreach (var msg in messages.OfType<RuntimeSessionStopped>()) s.Stopped(msg);
    }
}
