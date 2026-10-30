using System.Net;
using System.Text;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>
/// W9.10c: the pending claim of the idempotency ledger (in memory; the SQL ledger has the same cases in Fworker.Workflow.Tests). Two concurrent handlings of a key make one side effect, a claim that ran out
/// is settled by looking for the effect, and a call that took no effect gives the key back.
/// </summary>
public class PendingClaimTests
{
    static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    // ---- the ledger -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AKeyIsClaimedOnce_UntilItsClaimRunsOut_AndThenTakenOver()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        Assert.Equal(ClaimState.Claimed, ledger.Claim("k").State);
        var held = ledger.Claim("k");
        Assert.Equal(ClaimState.Pending, held.State); Assert.Equal(T0.AddMinutes(2), held.ExpiresAt);
        now = T0.AddMinutes(2).AddSeconds(1);
        Assert.Equal(ClaimState.TakenOver, ledger.Claim("k").State);
        Assert.Equal(ClaimState.Pending, ledger.Claim("k").State);   // and the new claim holds
    }

    [Fact]
    public void ARecordedKey_AnswersDone_AndEndsItsClaim()
    {
        var ledger = new InMemoryIdempotencyLedger();
        ledger.Claim("k");
        ledger.Record("k", CommandResult.Success("x"));
        var c = ledger.Claim("k");
        Assert.Equal(ClaimState.Done, c.State); Assert.Equal("x", c.Result!.Execution);
    }

    [Fact]
    public void OnlyTheOwnerReleasesOrExpiresAClaim()
    {
        var node = "a"; long epoch = 1;
        var ledger = new InMemoryIdempotencyLedger(owner: () => new ClaimOwner(node, epoch));
        ledger.Claim("k");
        node = "b"; ledger.ReleaseClaim("k"); ledger.ExpireClaim("k");   // another node: nothing happens
        Assert.Equal(ClaimState.Pending, ledger.Claim("k").State);
        node = "a"; epoch = 2; ledger.ReleaseClaim("k");   // the same node, another epoch: a later lease, not this claim's
        Assert.Equal(ClaimState.Pending, ledger.Claim("k").State);
        epoch = 1; ledger.ReleaseClaim("k");
        Assert.Equal(ClaimState.Claimed, ledger.Claim("k").State);
    }

    [Fact]
    public async Task ManyClaimsAtOnce_OneWins()
    {
        var ledger = new InMemoryIdempotencyLedger();
        var states = new System.Collections.Concurrent.ConcurrentBag<ClaimState>();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => states.Add(ledger.Claim("k").State))));
        Assert.Equal(1, states.Count(s => s == ClaimState.Claimed)); Assert.Equal(39, states.Count(s => s == ClaimState.Pending));
    }

    // ---- the handler base -------------------------------------------------------------------------------------------------------------------

    sealed class H(IIdempotencyLedger l, Func<CancellationToken, Task<CommandResult>> run, Func<MoveBoardCard, CancellationToken, Task<EffectLookup>>? lookup = null) : IntegrationCommandHandler<MoveBoardCard>(l, lookup)
    {
        public int Calls;
        protected override Task<CommandResult> Execute(MoveBoardCard c, CancellationToken ct) { Interlocked.Increment(ref Calls); return run(ct); }
    }

    static readonly MoveBoardCard Cmd = new("k1", 5, "Dev");

    /// <summary>A side effect that stays inside until released, so a second handling can arrive while the first is making it.</summary>
    sealed class Gate
    {
        public readonly TaskCompletionSource Inside = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Enter() { Inside.TrySetResult(); await Release.Task; }
    }

    /// <summary>Starts the first handling, waits until it is inside its side effect, runs the second one, then lets the first finish. Returns (first, second, third after both).</summary>
    static async Task<(CommandResult First, CommandResult Second, CommandResult Third)> Concurrent(Gate gate, Func<Task<CommandResult>> handle)
    {
        var first = Task.Run(handle);
        await gate.Inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await handle();
        gate.Release.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        return (firstResult, second, await handle());
    }

    [Fact]
    public async Task TwoConcurrentHandlings_MakeOneSideEffect_TheSecondWaits_AndLaterOnesAreAnsweredFromTheLedger()
    {
        var gate = new Gate();
        var h = new H(new InMemoryIdempotencyLedger(), async _ => { await gate.Enter(); return CommandResult.Success("done"); });
        var (first, second, third) = await Concurrent(gate, () => h.Handle(Cmd));
        Assert.True(first.Ok);
        Assert.Equal((false, FailureKind.Transient), (second.Ok, second.Failure)); Assert.Contains(IntegrationCommandHandler<MoveBoardCard>.PendingError, second.Error);
        Assert.Equal("done", third.Execution);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task ARetryableAnswer_GivesTheKeyBack_SoTheNextHandlingCallsAgain_WithoutLookingForAnEffect()
    {
        var looked = 0;
        var answers = new Queue<CommandResult>([CommandResult.Failed(FailureKind.Transient, "busy"), CommandResult.Failed(FailureKind.RateLimited, "slow"), CommandResult.Success("ok")]);
        var h = new H(new InMemoryIdempotencyLedger(), _ => Task.FromResult(answers.Dequeue()), (_, _) => { looked++; return Task.FromResult(EffectLookup.Absent); });
        Assert.Equal(FailureKind.Transient, (await h.Handle(Cmd)).Failure);
        Assert.Equal(FailureKind.RateLimited, (await h.Handle(Cmd)).Failure);
        Assert.True((await h.Handle(Cmd)).Ok);
        Assert.Equal((3, 0), (h.Calls, looked));
    }

    [Fact]
    public async Task AnExpiredClaim_IsSettledByLookingForTheEffect_AFoundEffectIsRecordedAndNotMadeAgain()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        ledger.Claim("k1");   // an earlier handling claimed the key and died before it recorded anything
        var h = new H(ledger, _ => Task.FromResult(CommandResult.Success("second call")), (_, _) => Task.FromResult(EffectLookup.Found(CommandResult.Success("the earlier effect", "u"))));
        Assert.Equal(FailureKind.Transient, (await h.Handle(Cmd)).Failure);   // the claim is still alive
        now = T0.AddMinutes(3);
        var r = await h.Handle(Cmd);
        Assert.Equal(("the earlier effect", "u"), (r.Execution, r.Url)); Assert.Equal(0, h.Calls);
        Assert.Equal("the earlier effect", ledger.TryGet("k1")!.Execution);   // recorded: later handlings do not look again
        Assert.Equal("the earlier effect", (await h.Handle(Cmd)).Execution);
    }

    [Fact]
    public async Task AnExpiredClaim_WhoseEffectIsAbsent_IsCalledOnce()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        ledger.Claim("k1"); now = T0.AddMinutes(3);
        var h = new H(ledger, _ => Task.FromResult(CommandResult.Success("made")), (_, _) => Task.FromResult(EffectLookup.Absent));
        Assert.Equal("made", (await h.Handle(Cmd)).Execution); Assert.Equal("made", (await h.Handle(Cmd)).Execution);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task AnExpiredClaim_WhoseEffectCannotBeTold_IsNotCalledBlind_ItAsksAgainAtTheNextHandling()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        ledger.Claim("k1"); now = T0.AddMinutes(3);
        var lookups = new Queue<Func<EffectLookup>>([() => EffectLookup.Unknown("down"), () => throw new HttpRequestException("down"), () => EffectLookup.Found(CommandResult.Success("there"))]);
        var h = new H(ledger, _ => Task.FromResult(CommandResult.Success("blind")), (_, _) => Task.FromResult(lookups.Dequeue()()));
        var a = await h.Handle(Cmd); var b = await h.Handle(Cmd);
        Assert.All([a, b], r => { Assert.Equal(FailureKind.Transient, r.Failure); Assert.Contains(IntegrationCommandHandler<MoveBoardCard>.UnknownError, r.Error); });
        Assert.Equal("there", (await h.Handle(Cmd)).Execution);   // each asking took the claim again at once: the claim did not have to run out a second time
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task WithoutALookup_AnExpiredClaim_IsTakenOverAndTheCallMadeAgain()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        ledger.Claim("k1"); now = T0.AddMinutes(3);
        var h = new H(ledger, _ => Task.FromResult(CommandResult.Success("again")));
        Assert.Equal("again", (await h.Handle(Cmd)).Execution);
    }

    [Fact]
    public async Task ATransportFailure_MayHaveReachedTheOtherSide_SoTheNextHandlingLooksFirst()
    {
        var ledger = new InMemoryIdempotencyLedger();
        var throws = true; var lookups = 0;
        var h = new H(ledger, _ => throws ? throw new TimeoutException() : Task.FromResult(CommandResult.Success("late")), (_, _) => { lookups++; return Task.FromResult(EffectLookup.Absent); });
        Assert.Equal(FailureKind.Transient, (await h.Handle(Cmd)).Failure);
        throws = false;
        Assert.Equal("late", (await h.Handle(Cmd)).Execution);
        Assert.Equal((1, 2), (lookups, h.Calls));
    }

    [Fact]
    public async Task AStopOfTheHost_LeavesTheEffectUnknown_SoTheNextProcessLooks()
    {
        var ledger = new InMemoryIdempotencyLedger();
        using var cts = new CancellationTokenSource();
        var lookups = 0;
        var h = new H(ledger, ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(CommandResult.Success()); }, (_, _) => { lookups++; return Task.FromResult(EffectLookup.Absent); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Handle(Cmd, cts.Token));
        await h.Handle(Cmd);
        Assert.Equal(1, lookups);
    }

    // ---- every command kind: two concurrent handlings, one side effect ------------------------------------------------------------------------

    sealed class GatedPort(Gate gate) : ICommandPort
    {
        public int Calls;
        public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default) { Interlocked.Increment(ref Calls); await gate.Enter(); return CommandResult.Success("exec-1", "http://page"); }
    }

    sealed class GatedRunner(Gate gate) : IRunbookRunner
    {
        public int Calls;
        public async Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct) { Interlocked.Increment(ref Calls); await gate.Enter(); return CommandResult.Success("task-1", "http://task"); }
    }

    [Fact]
    public async Task StartRuntimeSession_TwoConcurrentHandlings_StartOneWorker()
    {
        var gate = new Gate(); var port = new GatedPort(gate);
        var h = new StartRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var c = new StartRuntimeSession(RuntimeKeys.Start("octopus", "w1"), "octopus", "w1", 7, "Development", "prof", "nonce");
        var (first, second, third) = await Concurrent(gate, async () => (await h.Process(c)).Result);
        Assert.True(first.Ok); Assert.Equal(FailureKind.Transient, second.Failure); Assert.Equal("exec-1", third.Execution);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task StopRuntimeSession_TwoConcurrentHandlings_StopOnce()
    {
        var gate = new Gate(); var port = new GatedPort(gate);
        var h = new StopRuntimeSessionHandler(new InMemoryIdempotencyLedger(), port);
        var c = new StopRuntimeSession(RuntimeKeys.Stop("octopus", "w1"), "octopus", "w1", "done");
        var (first, second, third) = await Concurrent(gate, async () => (await h.Process(c)).Result);
        Assert.True(first.Ok); Assert.Equal(FailureKind.Transient, second.Failure); Assert.True(third.Ok);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task RunRunbook_TwoConcurrentHandlings_QueueOneRun()
    {
        var gate = new Gate(); var runner = new GatedRunner(gate);
        var h = new RunRunbookHandler(new InMemoryIdempotencyLedger(), runner);
        var c = new RunRunbook(RuntimeKeys.Runbook("p", "Start worker", "tdd", "k"), "p", "Start worker", "tdd", new Dictionary<string, string> { ["Fworker.Name"] = "w1" });
        var (first, second, third) = await Concurrent(gate, async () => (await h.Process(c)).Result);
        Assert.True(first.Ok); Assert.Equal(FailureKind.Transient, second.Failure); Assert.Equal("task-1", third.Execution);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task RunRunbook_AnExpiredClaim_IsSettledByTheRunOfThatWorkerName()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        var runner = new GatedRunner(new Gate());
        var c = new RunRunbook(RuntimeKeys.Runbook("p", "Start worker", "tdd", "k"), "p", "Start worker", "tdd", new Dictionary<string, string> { ["Fworker.Name"] = "w1" });
        ledger.Claim(c.IdempotencyKey); now = T0.AddMinutes(3);
        string? asked = null;
        var h = new RunRunbookHandler(ledger, runner, (cmd, _) => { asked = cmd.Variables["Fworker.Name"]; return Task.FromResult(EffectLookup.Found(CommandResult.Success("task-9", "http://task/9"))); });
        var o = await h.Process(c);
        var m = Assert.IsType<RunbookRunFinished>(o.Message);
        Assert.Equal(("w1", true, "task-9"), (asked, m.Ok, m.Execution)); Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task StartRuntimeSession_AnExpiredClaim_IsSettledByTheRunOfThatWorkerName()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        var port = new GatedPort(new Gate());
        var c = new StartRuntimeSession(RuntimeKeys.Start("octopus", "w1"), "octopus", "w1", 7, "Development", "prof", "nonce");
        ledger.Claim(c.IdempotencyKey); now = T0.AddMinutes(3);
        var h = new StartRuntimeSessionHandler(ledger, port, (cmd, _) => Task.FromResult(cmd.WorkerName == "w1" ? EffectLookup.Found(CommandResult.Success("task-9", "http://task/9")) : EffectLookup.Absent));
        var m = Assert.IsType<RuntimeSessionStarted>((await h.Process(c)).Message);
        Assert.Equal(("task-9", "http://task/9"), (m.Execution, m.Url)); Assert.Equal(0, port.Calls);
    }

    // ---- merge: a stub of GitHub that holds the merge call --------------------------------------------------------------------------------

    sealed class MergeStub(Gate? gate, bool mergedAlready = false, bool readFails = false) : HttpMessageHandler
    {
        public int Puts, Gets;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/merge", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Puts);
                if (gate != null) await gate.Enter();
                return Json(HttpStatusCode.OK, $$"""{"sha":"{{Merged}}","merged":true}""");
            }
            Interlocked.Increment(ref Gets);
            if (readFails) return Json(HttpStatusCode.BadGateway, """{"message":"bad gateway"}""");
            var state = mergedAlready ? "closed" : "open"; var flag = mergedAlready ? "true" : "false"; var sha = mergedAlready ? "\"" + Merged + "\"" : "null";
            return Json(HttpStatusCode.OK, $$$"""{"state":"{{{state}}}","draft":false,"title":"x","merged":{{{flag}}},"merge_commit_sha":{{{sha}}},"mergeable":true,"mergeable_state":"clean","head":{"sha":"{{{Pass}}}","ref":"copilot/x"},"base":{"ref":"master"}}""");
        }
        static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    const string Pass = "1111111111111111111111111111111111111111", Merged = "4444444444444444444444444444444444444444";
    static readonly RepoId Repo = new("acme", "orders");
    static MergePullRequest MergeCmd() => new(MergePullRequest.KeyFor(Repo, 9, Pass), Repo, 9, Pass);

    static MergeDirect Direct(MergeStub stub) =>
        new(new GitHubClientFactory(new HttpClient(stub) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new GitHubBudget()), null, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task MergePullRequest_TwoConcurrentHandlings_MergeOnce()
    {
        var gate = new Gate(); var stub = new MergeStub(gate);
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(stub));
        var (first, second, third) = await Concurrent(gate, async () => (await h.Process(MergeCmd())).ToResult());
        Assert.True(first.Ok); Assert.Equal(FailureKind.Transient, second.Failure); Assert.True(third.Ok);
        Assert.Equal(1, stub.Puts);
    }

    [Fact]
    public async Task MergePullRequest_AnExpiredClaim_IsSettledByTheMergeStateOfThePullRequest()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        var stub = new MergeStub(null, mergedAlready: true);
        ledger.Claim(MergeCmd().IdempotencyKey); now = T0.AddMinutes(3);
        var o = await new MergePullRequestHandler(ledger, Direct(stub)).Process(MergeCmd());
        Assert.True(o.Merged); Assert.Equal(Merged, o.Sha);
        Assert.Equal(0, stub.Puts);
    }

    [Fact]
    public async Task MergePullRequest_AnExpiredClaim_OfAnOpenPullRequest_MergesOnce_AndOneThatCannotBeReadIsNotMergedBlind()
    {
        var now = T0;
        var ledger = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        var open = new MergeStub(null);
        ledger.Claim(MergeCmd().IdempotencyKey); now = T0.AddMinutes(3);
        Assert.True((await new MergePullRequestHandler(ledger, Direct(open)).Process(MergeCmd())).Merged);
        Assert.Equal(1, open.Puts);

        var blind = new MergeStub(null, readFails: true); var ledger2 = new InMemoryIdempotencyLedger(claimTtl: TimeSpan.FromMinutes(2), utcNow: () => now);
        var key2 = MergePullRequest.KeyFor(Repo, 10, Pass);
        ledger2.Claim(key2); now = now.AddMinutes(3);
        var r = await new MergePullRequestHandler(ledger2, Direct(blind)).Handle(MergeCmd() with { IdempotencyKey = key2, PullRequest = 10 });
        Assert.Equal(FailureKind.Transient, r.Failure); Assert.Equal(0, blind.Puts);
    }
}
