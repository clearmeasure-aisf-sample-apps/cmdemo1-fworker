using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10c on a real SQL Server (FACTORY_TEST_SQL; the tests return at once without one): the pending claim of the idempotency ledger is one statement, so processes that handle the same key at once make one side
/// effect; a claim that ran out is taken over by exactly one and settled by looking up the effect; a result recorded in the same transaction that ends the claim leaves no window for a second call; and the
/// Copilot reservation changes only conditionally. The "processes" are separate stores with their own owner on the same database, as the old and the new gateway of a release are.
/// </summary>
public class PendingClaimSqlTests
{
    static SqlCommandStores Process(string cs, string node, long epoch = 1, TimeSpan? ttl = null) => new(cs, () => new ClaimOwner(node, epoch), ttl);

    sealed class Gate
    {
        public readonly TaskCompletionSource Inside = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Enter() { Inside.TrySetResult(); await Release.Task; }
    }

    sealed class CountingPort(Gate? gate = null) : ICommandPort
    {
        public int Calls;
        public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (gate != null) await gate.Enter();
            return CommandResult.Success("exec-1", "http://page");
        }
    }

    sealed class CountingRunner(Gate? gate = null) : IRunbookRunner
    {
        public int Calls;
        public async Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (gate != null) await gate.Enter();
            return CommandResult.Success("task-1", "http://task");
        }
    }

    static StartRuntimeSession Start(string worker = "w1") => new(RuntimeKeys.Start("octopus", worker), "octopus", worker, 7, "Development", "prof", "nonce");

    [Fact]
    public async Task ManyProcessesClaimingAtOnce_OneWins_TheOthersWait()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claim_");
        var ledgers = Enumerable.Range(0, 12).Select(i => Process(db.ConnectionString, "node-" + i).Ledger).ToList();
        var outcomes = await Task.WhenAll(ledgers.Select(l => Task.Run(() => l.Claim("k1"))));
        Assert.Equal(1, outcomes.Count(o => o.State == ClaimState.Claimed));
        Assert.Equal(11, outcomes.Count(o => o.State == ClaimState.Pending));
        var holder = outcomes.First(o => o.State == ClaimState.Pending).Holder!;
        Assert.StartsWith("node-", holder.Node); Assert.Equal(1, holder.Epoch);
        Assert.Equal(1, await db.Count("SELECT COUNT(*) FROM factory.IdempotencyClaims"));
    }

    // V3.2 (item 5 of #578): the SQL application lock is the only fence, and the epoch of its term is on factory.Leadership. A process of an older term (it lost the lock and has not noticed) takes no claim.
    [Fact]
    public async Task AProcessOfAnOlderTerm_TakesNoClaim_WhileALiveClaimOfAnOlderTermIsStillWaitedFor()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimterm_");
        await db.Execute("INSERT INTO factory.Leadership (Id, Epoch, Holder, AcquiredAt) VALUES (1, 4, N'holder-4', SYSDATETIMEOFFSET())");
        var term4 = Process(db.ConnectionString, "p4", epoch: 4).Ledger;
        Assert.Equal(ClaimState.Claimed, term4.Claim("k").State);
        Assert.Equal(ClaimState.Pending, Process(db.ConnectionString, "p4b", epoch: 4).Ledger.Claim("k").State);   // the same term: a live claim is waited for

        // a newer process takes the lock (the term is 5): the old holder's node has left, its claim is no longer anyone's
        await db.Execute("UPDATE factory.Leadership SET Epoch = 5, Holder = N'holder-5' WHERE Id = 1");
        var term5 = Process(db.ConnectionString, "p5", epoch: 5).Ledger;
        Assert.Equal(ClaimState.Pending, term5.Claim("k").State);                           // the old handling may still be inside its call: its claim is waited for until it runs out

        // the old process has not noticed yet: it takes no claim, and holds no key the newer term could hold
        var late = term4.Claim("other");
        Assert.Equal(ClaimState.Pending, late.State); Assert.Equal(5, late.Holder!.Epoch);
        Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM factory.IdempotencyClaims WHERE [Key] = N'other'"));
        Assert.Equal(ClaimState.Pending, term4.Claim("k").State);                           // nor does it take the key back

        // a process without a lease (epoch 0) is not fenced by the row
        Assert.Equal(ClaimState.Claimed, Process(db.ConnectionString, "plain", epoch: 0).Ledger.Claim("free").State);
    }

    [Fact]
    public async Task ARecordedKey_IsDone_ItsClaimIsGone_AndTheFirstResultStays()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimdone_");
        var a = Process(db.ConnectionString, "a").Ledger; var b = Process(db.ConnectionString, "b").Ledger;
        Assert.Equal(ClaimState.Claimed, a.Claim("k").State);
        a.Record("k", CommandResult.Success("first", "u"));
        b.Record("k", CommandResult.Success("second"));
        Assert.Equal(0, await db.Count("SELECT COUNT(*) FROM factory.IdempotencyClaims"));
        var c = b.Claim("k");
        Assert.Equal((ClaimState.Done, "first", "u"), (c.State, c.Result!.Execution, c.Result.Url));
        Assert.Equal("first", b.TryGet("k")!.Execution);
    }

    [Fact]
    public async Task ARecordRacingTheClaims_NeverLetsASecondHandlingThrough()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimrace_");
        for (var round = 0; round < 15; round++)
        {
            var key = "race-" + round;
            var holder = Process(db.ConnectionString, "holder").Ledger;
            Assert.Equal(ClaimState.Claimed, holder.Claim(key).State);
            var claimers = Enumerable.Range(0, 6).Select(i => Process(db.ConnectionString, "c" + i).Ledger).ToList();
            var go = new ManualResetEventSlim();
            var tasks = claimers.Select(l => Task.Run(() => { go.Wait(); return l.Claim(key); })).ToList();
            var record = Task.Run(() => { go.Wait(); holder.Record(key, CommandResult.Success("r")); });
            go.Set();
            await record;
            var states = (await Task.WhenAll(tasks)).Select(o => o.State).ToList();
            // while the holder's claim is alive the others are Pending; once it recorded, they find the result; none ever gets to make the call
            Assert.DoesNotContain(ClaimState.Claimed, states); Assert.DoesNotContain(ClaimState.TakenOver, states);
        }
    }

    [Fact]
    public async Task ALateRecordOfASupersededHandling_RacingTheTakeOver_NeitherDeadlocksNorLosesTheResult()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimlate_");
        for (var round = 0; round < 20; round++)
        {
            var key = "late-" + round;
            var slow = Process(db.ConnectionString, "slow", 1, TimeSpan.FromMilliseconds(50)).Ledger; var live = Process(db.ConnectionString, "live", 2).Ledger;
            Assert.Equal(ClaimState.Claimed, slow.Claim(key).State);
            await Task.Delay(120);   // its claim ran out while its call was still going
            var go = new ManualResetEventSlim();
            var record = Task.Run(() => { go.Wait(); slow.Record(key, CommandResult.Success("late")); });
            var claim = Task.Run(() => { go.Wait(); return live.Claim(key); });
            go.Set();
            await record; var outcome = await claim;
            Assert.Contains(outcome.State, new[] { ClaimState.TakenOver, ClaimState.Done });
            Assert.Equal("late", live.TryGet(key)!.Execution);   // the result is there either way
        }
    }

    [Fact]
    public async Task AnExpiredClaim_IsTakenOverByExactlyOne()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimexp_");
        var dead = Process(db.ConnectionString, "dead", 1, TimeSpan.FromMilliseconds(300)).Ledger;
        Assert.Equal(ClaimState.Claimed, dead.Claim("k").State);
        await Task.Delay(700);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Process(db.ConnectionString, "live-" + i, 2).Ledger.Claim("k"))));
        Assert.Equal(1, outcomes.Count(o => o.State == ClaimState.TakenOver)); Assert.Equal(7, outcomes.Count(o => o.State == ClaimState.Pending));
        Assert.Equal(2, outcomes.First(o => o.State == ClaimState.Pending).Holder!.Epoch);   // the new owner and the epoch of its lease hold the claim now
    }

    [Fact]
    public async Task OnlyTheOwnerReleasesOrExpiresItsClaim_AndAnExpiredOneIsTakenOverAtOnce()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimown_");
        var a = Process(db.ConnectionString, "a", 1).Ledger; var other = Process(db.ConnectionString, "b", 1).Ledger; var nextEpoch = Process(db.ConnectionString, "a", 2).Ledger;
        a.Claim("k");
        other.ReleaseClaim("k"); other.ExpireClaim("k"); nextEpoch.ReleaseClaim("k"); nextEpoch.ExpireClaim("k");   // not theirs
        Assert.Equal(ClaimState.Pending, other.Claim("k").State);
        a.ExpireClaim("k");
        Assert.Equal(ClaimState.TakenOver, other.Claim("k").State);   // the effect may be there: the next handling looks
        other.ReleaseClaim("k");
        Assert.Equal(ClaimState.Claimed, a.Claim("k").State);         // released: nobody took anything
    }

    // ---- every command kind: two processes handling the same command at once make one side effect -------------------------------------------

    [Fact]
    public async Task StartRuntimeSession_TwoProcessesAtOnce_StartOneWorker()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimstart_");
        var gate = new Gate(); var port = new CountingPort(gate);
        var oldNode = new StartRuntimeSessionHandler(Process(db.ConnectionString, "old", 1).Ledger, port);
        var newNode = new StartRuntimeSessionHandler(Process(db.ConnectionString, "new", 2).Ledger, port);
        var first = Task.Run(() => oldNode.Process(Start()));
        await gate.Inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await newNode.Process(Start());   // the release's overlap: the new process handles the redelivered command while the old one is in the call
        Assert.True(second.Retry); Assert.Equal(FailureKind.Transient, second.Result.Failure);
        gate.Release.SetResult();
        Assert.IsType<RuntimeSessionStarted>((await first.WaitAsync(TimeSpan.FromSeconds(10))).Message);
        Assert.IsType<RuntimeSessionStarted>((await newNode.Process(Start())).Message);   // and then it is answered from the ledger
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task StopRuntimeSession_TwoProcessesAtOnce_StopOnce()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimstop_");
        var gate = new Gate(); var port = new CountingPort(gate);
        var stop = new StopRuntimeSession(RuntimeKeys.Stop("octopus", "w1"), "octopus", "w1", "done");
        var h1 = new StopRuntimeSessionHandler(Process(db.ConnectionString, "old").Ledger, port); var h2 = new StopRuntimeSessionHandler(Process(db.ConnectionString, "new", 2).Ledger, port);
        var first = Task.Run(() => h1.Process(stop));
        await gate.Inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await h2.Process(stop)).Retry);
        gate.Release.SetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Result.Ok); Assert.True((await h2.Process(stop)).Result.Ok);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task RunRunbook_TwoProcessesAtOnce_QueueOneRun()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimrun_");
        var gate = new Gate(); var runner = new CountingRunner(gate);
        var c = new RunRunbook(RuntimeKeys.Runbook("p", "Start worker", "tdd", "k"), "p", "Start worker", "tdd", new Dictionary<string, string> { ["Fworker.Name"] = "w1" });
        var h1 = new RunRunbookHandler(Process(db.ConnectionString, "old").Ledger, runner); var h2 = new RunRunbookHandler(Process(db.ConnectionString, "new", 2).Ledger, runner);
        var first = Task.Run(() => h1.Process(c));
        await gate.Inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await h2.Process(c)).Retry);
        gate.Release.SetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Result.Ok); Assert.Equal("task-1", (await h2.Process(c)).Result.Execution);
        Assert.Equal(1, runner.Calls);
    }

    // ---- a claim that ran out is settled by looking up the effect ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("found")]
    [InlineData("absent")]
    [InlineData("unknown")]
    public async Task AnExpiredClaim_IsSettledByLookup(string lookup)
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_claimlook_" + lookup[..2]);
        var key = Start().IdempotencyKey;
        Assert.Equal(ClaimState.Claimed, Process(db.ConnectionString, "dead", 1, TimeSpan.FromMilliseconds(200)).Ledger.Claim(key).State);   // the earlier handling died inside its call
        await Task.Delay(500);
        var port = new CountingPort();
        var looked = 0;
        var h = new StartRuntimeSessionHandler(Process(db.ConnectionString, "live", 2).Ledger, port, (_, _) =>
        {
            looked++;
            return Task.FromResult(lookup switch { "found" => EffectLookup.Found(CommandResult.Success("the-run", "http://run")), "absent" => EffectLookup.Absent, _ => EffectLookup.Unknown("octopus down") });
        });
        var o = await h.Process(Start());
        switch (lookup)
        {
            case "found": Assert.Equal("the-run", Assert.IsType<RuntimeSessionStarted>(o.Message).Execution); Assert.Equal(0, port.Calls); break;
            case "absent": Assert.Equal("exec-1", Assert.IsType<RuntimeSessionStarted>(o.Message).Execution); Assert.Equal(1, port.Calls); break;
            default:
                Assert.True(o.Retry); Assert.Equal(0, port.Calls);
                Assert.Equal(ClaimState.TakenOver, Process(db.ConnectionString, "next", 2).Ledger.Claim(key).State);   // the claim was let go: the next handling looks again
                break;
        }
        Assert.Equal(1, looked);
        if (lookup != "unknown") Assert.NotNull(Process(db.ConnectionString, "x").Ledger.TryGet(key));   // recorded, so the next handling neither looks nor calls
    }

    // ---- the Copilot reservation ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheReservationKeepsItsMode_AndChangesOnlyConditionally()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_resmode_");
        var store = Process(db.ConnectionString, "a").Reservations;
        var at = new DateTime(2026, 10, 30, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        var r = new CopilotReservation(7, "Development", "w1", ReservationState.Reserved, at);
        Assert.True(store.TryReserve(r));
        Assert.Equal("", store.Get(7, "Development")!.Mode);   // a reservation of before W9.10c has the default
        var resume = r with { Mode = CopilotReservation.ResumeMode };
        store.Put(resume);
        Assert.Equal(resume, store.Get(7, "Development")); Assert.Equal(resume, Assert.Single(store.Reserved()));
        var launched = resume with { State = ReservationState.Launched, TaskId = "t#2", Url = "u" };
        Assert.False(store.Transition(r, launched));   // the stale copy (mode "") no longer matches
        Assert.True(store.Transition(resume, launched));
        Assert.False(store.Transition(resume, resume with { State = ReservationState.Released }));   // already settled by someone else
        Assert.Equal(launched, store.Get(7, "Development"));
    }

    [Fact]
    public async Task TheReconciler_ThroughSql_RecordsTheOwner_ThenSettlesOnce_EvenWhenTwoProcessesRunIt()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_resrec_");
        var store = Process(db.ConnectionString, "a").Reservations;
        var r = new CopilotReservation(7, "Development", "w1", ReservationState.Reserved, DateTime.UtcNow.AddMinutes(-5), Mode: CopilotReservation.ResumeMode);
        store.Put(r);
        var recorded = 0;
        var lookup = new Found("task-1#3", "http://t");
        var owners = new Recorder(() => Interlocked.Increment(ref recorded));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new CopilotReconciler(Process(db.ConnectionString, "p").Reservations, lookup, null, owners).Settle(r)));
        Assert.Equal(1, outcomes.Count(o => o == ReconcileOutcome.Launched));   // the others find the reservation changed (or lose the conditional write)
        Assert.True(recorded >= 1);
        Assert.Equal(("task-1#3", ReservationState.Launched), (store.Get(7, "Development")!.TaskId, store.Get(7, "Development")!.State));
    }

    sealed class Found(string id, string url) : ICopilotTaskLookup
    {
        public Task<(string TaskId, string Url)?> FindAsync(int item, string phase, DateTime since, CancellationToken ct) => Task.FromResult<(string, string)?>(null);
        public Task<(string TaskId, string Url)?> FindResumeAsync(int item, string phase, DateTime since, CancellationToken ct) => Task.FromResult<(string, string)?>((id, url));
    }

    sealed class Recorder(Action count) : ICopilotOwnerRecorder
    {
        public Task RecordAsync(CopilotReservation settled, CancellationToken ct) { count(); return Task.CompletedTask; }
    }
}
