using System.Reflection;
using System.Runtime.CompilerServices;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Faults;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>A SqlException with the error number given: the type has no public constructor, so the object is made without one and given the error collection that carries the number.</summary>
static class SqlFaultFactory
{
    public static SqlException Make(int number)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var error = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        var numberField = typeof(SqlError).GetFields(all).First(f => f.FieldType == typeof(int) && f.Name.Contains("number", StringComparison.OrdinalIgnoreCase));
        numberField.SetValue(error, number);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", all)!.Invoke(collection, [error]);
        var ex = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        foreach (var f in typeof(SqlException).GetFields(all).Where(f => f.FieldType == typeof(SqlErrorCollection))) f.SetValue(ex, collection);
        return ex;
    }
}

/// <summary>W9.10o: which exceptions a store turns into "another writer was first" and which are faults that a retry gets past.</summary>
public sealed class SqlFaultsTests
{
    [Theory]
    [InlineData(-2)] [InlineData(1205)] [InlineData(4060)] [InlineData(40197)] [InlineData(40501)] [InlineData(40613)] [InlineData(49918)] [InlineData(10928)] [InlineData(10929)]
    public void TheNineSqlErrorNumbers_AreTransient_AtAnyDepthOfTheChain(int number)
    {
        var sql = SqlFaultFactory.Make(number);
        Assert.Equal(number, sql.Number);
        Assert.True(SqlFaults.IsTransient(sql));
        Assert.True(SqlFaults.IsTransient(new DbUpdateException("save", sql)));
        Assert.True(SqlFaults.IsTransient(new InvalidOperationException("retries", new DbUpdateException("save", sql))));
        Assert.True(SqlFaults.IsTransient(new AggregateException(new TimeoutException(), sql)));
        // a transient fault is never a conflict: the handler must not apply the input again to a state that did not change
        Assert.False(SqlFaults.IsConflict(new DbUpdateException("save", sql)));
    }

    [Theory]
    [InlineData(2627)] [InlineData(2601)] [InlineData(547)] [InlineData(18456)] [InlineData(207)]
    public void AnotherSqlError_IsNotTransient(int number) => Assert.False(SqlFaults.IsTransient(new DbUpdateException("save", SqlFaultFactory.Make(number))));

    [Fact]
    public void OnlyAConcurrencyException_AndADuplicateKey_AreConflicts()
    {
        Assert.True(SqlFaults.IsConflict(new DbUpdateConcurrencyException("the revision moved")));
        Assert.True(SqlFaults.IsConflict(new DbUpdateException("insert", SqlFaultFactory.Make(2627))));
        Assert.True(SqlFaults.IsConflict(new DbUpdateException("insert", SqlFaultFactory.Make(2601))));
        Assert.False(SqlFaults.IsConflict(new DbUpdateException("some other violation", SqlFaultFactory.Make(547))));
        Assert.False(SqlFaults.IsConflict(new DbUpdateException("no inner exception")));
        Assert.False(SqlFaults.IsConflict(new InvalidOperationException("not a save")));
        Assert.True(SqlFaults.IsDuplicateKey(new DbUpdateException("insert", SqlFaultFactory.Make(2627))));
        Assert.False(SqlFaults.IsDuplicateKey(new DbUpdateException("save", SqlFaultFactory.Make(1205))));
    }

    [Fact]
    public void TheHandlersConflictExceptions_AreRetryable()
    {
        Assert.True(SqlFaults.IsHandlerConflict(new WorkItemConflictException("acme/orders#7")));
        Assert.True(SqlFaults.IsHandlerConflict(new Fworker.Workflows.Capacity.CapacityConflictException()));
        Assert.True(SqlFaults.IsHandlerConflict(new Fworker.Workflows.Merge.MergeTrainConflictException("acme/orders")));
        Assert.True(SqlFaults.IsHandlerConflict(new Fworker.Workflows.Delivery.TrainConflictException("orders|uat")));
        Assert.False(SqlFaults.IsHandlerConflict(new InvalidOperationException()));
    }

    [Fact]
    public void TheSchedule_IsFinite_AndLongerThanAnAfternoon()
    {
        Assert.Equal(4, FaultPolicy.Long.Length);
        Assert.Equal(TimeSpan.FromSeconds(72) + TimeSpan.FromMinutes(5 + 15 + 60 + 180), FaultPolicy.Total);
    }
}

public sealed record FaultInjectingMessage(string Fault);

/// <summary>A pre-built handler (the test project has no runtime compiler) that fails once with the fault named and then works: the attempts are counted by the test.</summary>
public sealed class FaultInjectingHandler : Wolverine.Runtime.Handlers.MessageHandler<FaultInjectingMessage>
{
    public static int Attempts;
    protected override Task HandleAsync(FaultInjectingMessage m, Wolverine.Runtime.MessageContext context, CancellationToken cancellation)
    {
        if (++Attempts > 1) return Task.CompletedTask;
        throw m.Fault switch
        {
            "conflict" => new WorkItemConflictException("acme/orders#7"),
            "transient" => new DbUpdateException("save", SqlFaultFactory.Make(40613)),
            _ => new InvalidOperationException("not a database fault"),
        };
    }
}

/// <summary>The retry rule on a real Wolverine host (in memory: the rule is the host's, not the database's).</summary>
public sealed class FaultPolicyHostTests
{
    static async Task<IHost> Host() => await new HostBuilder()
        .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical))
        .UseWolverine(w =>
        {
            w.Discovery.DisableConventionalDiscovery();
            w.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;
            w.AddMessageHandler(new FaultInjectingHandler());
            w.PublishMessage<FaultInjectingMessage>().ToLocalQueue("faults");
            FaultPolicy.Apply(w);
        }).StartAsync();

    [Theory]
    [InlineData("conflict")]
    [InlineData("transient")]
    public async Task AConflictOfTheHandler_AndATransientDatabaseFault_AreRunAgainAfterTheFirstPause(string fault)
    {
        FaultInjectingHandler.Attempts = 0;
        using var host = await Host();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30)).DoNotAssertOnExceptionsDetected().SendMessageAndWaitAsync(new FaultInjectingMessage(fault));
        Assert.Equal(2, FaultInjectingHandler.Attempts);   // the second try, after the 2 second pause, worked: nothing went to the dead letters
    }

    [Fact]
    public async Task AFaultThatIsNeither_IsNotTakenByThisRule()
    {
        FaultInjectingHandler.Attempts = 0;
        using var host = await Host();
        var session = await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30)).DoNotAssertOnExceptionsDetected().SendMessageAndWaitAsync(new FaultInjectingMessage("other"));
        Assert.True(FaultInjectingHandler.Attempts >= 1);
        Assert.NotEmpty(session.AllExceptions());
    }
}

/// <summary>W9.10o: the idempotency ledger keeps a success for a retention period, and a settled failure for ever.</summary>
public sealed class LedgerRetentionTests
{
    [Fact]
    public void TheInMemoryLedger_ForgetsOldSuccesses_AndKeepsFailuresAndNewerOnes()
    {
        var ledger = new InMemoryIdempotencyLedger();
        ledger.Record("old-success", CommandResult.Success());
        ledger.Record("old-failure", CommandResult.Failed(FailureKind.Permanent, "no"));
        Assert.Equal(0, ledger.PurgeSuccesses(TimeSpan.FromDays(30), DateTimeOffset.UtcNow));
        var later = DateTimeOffset.UtcNow.AddDays(31);
        ledger.Record("new-success", CommandResult.Success());
        Assert.Equal(2, ledger.PurgeSuccesses(TimeSpan.FromDays(30), later));   // (both successes are older than 30 days at that time)
        Assert.Null(ledger.TryGet("old-success"));
        Assert.NotNull(ledger.TryGet("old-failure"));
    }

    [Fact]
    public async Task TheSqlLedger_PurgeAuthFaults_DropsOnlyTheRowsOfAnOutageOfTheCredential()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_ledger_");
        var ledger = new SqlIdempotencyLedger(new SqlCommandSchema(db.ConnectionString));
        ledger.Record("merge-403", new Fworker.Integrations.GitHub.MergeOutcome(Fworker.Integrations.GitHub.MergeStatus.Failed, "github-forbidden", "403", Kind: FailureKind.Permanent).ToResult());
        ledger.Record("journal-403", CommandResult.Failed(FailureKind.Permanent, "WriteJournalLine: GitHub 403 on POST repos/a/b/issues/1/comments"));
        ledger.Record("merge-final", new Fworker.Integrations.GitHub.MergeOutcome(Fworker.Integrations.GitHub.MergeStatus.Failed, "not-mergeable", "false", Kind: FailureKind.Permanent).ToResult());
        ledger.Record("journal-404", CommandResult.Failed(FailureKind.Permanent, "WriteJournalLine: GitHub 404 on GET repos/a/b"));
        ledger.Record("ok", CommandResult.Success("posted"));
        Assert.Equal(2, ledger.PurgeAuthFaults());
        Assert.Null(ledger.TryGet("merge-403")); Assert.Null(ledger.TryGet("journal-403"));
        Assert.NotNull(ledger.TryGet("merge-final")); Assert.NotNull(ledger.TryGet("journal-404")); Assert.NotNull(ledger.TryGet("ok"));
        Assert.Equal(0, ledger.PurgeAuthFaults());
        Assert.Equal(ClaimState.Claimed, ledger.Claim("merge-403").State);   // the key runs again
    }

    [Fact]
    public async Task TheSqlLedger_PurgesOnlySuccessesPastTheRetention()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_ledger_");
        var ledger = new SqlIdempotencyLedger(new SqlCommandSchema(db.ConnectionString));
        ledger.Record("PromoteRelease|orders|1.0.2|uat|1", CommandResult.Success("task-1"));
        ledger.Record("PromoteRelease|orders|1.0.3|uat|1", CommandResult.Success("task-2"));
        ledger.Record("PromoteRelease|orders|1.0.4|uat|1", CommandResult.Failed(FailureKind.Permanent, "refused"));
        await db.Execute("UPDATE factory.IdempotencyLedger SET RecordedAt = DATEADD(day, -40, SYSUTCDATETIME()) WHERE [Key] IN (N'PromoteRelease|orders|1.0.2|uat|1', N'PromoteRelease|orders|1.0.4|uat|1')");
        Assert.Equal(1, ledger.PurgeSuccesses(TimeSpan.FromDays(30), DateTimeOffset.UtcNow));
        Assert.Null(ledger.TryGet("PromoteRelease|orders|1.0.2|uat|1"));   // the old success: a request for the same version runs again
        Assert.NotNull(ledger.TryGet("PromoteRelease|orders|1.0.3|uat|1"));
        Assert.NotNull(ledger.TryGet("PromoteRelease|orders|1.0.4|uat|1")); // the old failure stays
    }
}

/// <summary>W9.10o: the re-arm of the open sagas whose timer or command was lost between the save and the outbox.</summary>
public sealed class WorkItemRearmTests
{
    static readonly WorkItemPolicy Policy = WorkItemPolicy.Default;

    static (SagaFixture Fx, List<object> Sent, WorkItemRearm Rearm) Make()
    {
        var fx = new SagaFixture();
        fx.Store.Clock = () => fx.Now;
        var sent = new List<object>();
        return (fx, sent, new WorkItemRearm(fx.Store, Policy, m => { sent.Add(m); return Task.FromResult(true); }, () => fx.Now));
    }

    [Fact]
    public async Task ALeaseTimerThatIsOverdueByMoreThanTheGrace_IsPublishedAgain_AndTheSagaTakesItAsTheTimer()
    {
        var (fx, sent, rearm) = Make();
        await fx.Start(plan: "docs");
        var due = fx.State.LeaseDue!.Value;
        fx.Now = due.AddMinutes(1);   // late, but within the grace: Wolverine's own timer may still fire
        await rearm.Run(default);
        Assert.Empty(sent);
        fx.Now = due + WorkItemRearm.OverdueGrace + TimeSpan.FromSeconds(1);
        var report = await rearm.Run(default);
        var lease = Assert.IsType<PhaseLeaseExpired>(Assert.Single(sent));
        Assert.Equal((1, 1, 0), (report.Open, report.Timers, report.Commands));
        // the timer's message does what the timer would have done: the lease ran out, the step is entered again under a new arm
        var armBefore = fx.Arm;
        var r = await fx.Send(lease);
        Assert.Contains(r.Decisions, d => d.Action == WorkItemActions.LeaseExpired);
        Assert.NotEqual(armBefore, fx.Arm);
        // and the same message again (the original timer fires after all) changes nothing
        Assert.NotEqual(WorkItemOutcome.Applied, (await fx.Send(lease)).Decision.Outcome);
    }

    [Fact]
    public async Task AParkedItem_AndACompletedOne_AreLeftAlone()
    {
        var (fx, sent, rearm) = Make();
        await fx.Start(plan: "docs");
        await fx.Send(new ParkWorkItem(SagaFixture.Item, 0, "park-1", "a person said so"));
        fx.Now = fx.Now.AddDays(1);
        var report = await rearm.Run(default);
        Assert.Empty(sent);
        Assert.Equal(0, report.Open);
    }

    [Fact]
    public async Task TheCommandsOfTheLastTransition_AreSentAgainOnlyWhenTheItemHasBeenSilentForLong()
    {
        var (fx, sent, rearm) = Make();
        await fx.Start(plan: "docs");
        // a merge call the saga asked for and whose publish was lost: the state says it was emitted
        var rec = (await fx.Store.Load(fx.Key, default))!;
        rec.State.MergeRequest = new MergeRequestState { Attempt = 1, PullRequest = 11, Head = "aaa1111" };
        rec.State.MergeRequestEmitted = true;
        Assert.True(await fx.Store.Save(fx.Key, rec.State, rec.Revision, default));
        fx.Now = fx.Now.AddMinutes(5);
        await rearm.Run(default);
        Assert.DoesNotContain(sent, m => m is MergeRequested);   // not silent long enough
        fx.Now = fx.Now.AddMinutes(10);
        var report = await rearm.Run(default);
        var request = Assert.IsType<MergeRequested>(Assert.Single(sent, m => m is MergeRequested));
        Assert.Equal((1, 11, "aaa1111"), (request.Attempt, request.PullRequest, request.Head));
        Assert.True(report.Commands >= 1);
        Assert.Equal(report, rearm.Last);
    }

    [Fact]
    public async Task AfterABreak_AnItemTheAdoptionHasNotExamined_GetsNoTimerAndNoCommand_UntilItHas()
    {
        // W9.9b (review): every row is silent after a break; without the gate the re-arm would send the old generation's commands (a session start, a merge call) for all of them
        var fx = new SagaFixture(); fx.Store.Clock = () => fx.Now;
        var sent = new List<object>();
        var gate = new Fworker.Workflows.Adoption.ReadoptionGate();
        var rearm = new WorkItemRearm(fx.Store, Policy, m => { sent.Add(m); return Task.FromResult(true); }, () => fx.Now, gate.Refuses);
        await fx.Start(plan: "docs");
        var rec = (await fx.Store.Load(fx.Key, default))!;
        rec.State.MergeRequest = new MergeRequestState { Attempt = 1, PullRequest = 11, Head = "aaa1111" };
        rec.State.MergeRequestEmitted = true;
        Assert.True(await fx.Store.Save(fx.Key, rec.State, rec.Revision, default));
        fx.Now = fx.State.LeaseDue!.Value.AddHours(1);   // the lease timer is lost and the item has been silent for long

        gate.Hold("v1 ran", newTerm: true);
        var held = await rearm.Run(default);
        Assert.Empty(sent);
        Assert.Equal((0, 0, 0, 1), (held.Timers, held.Commands, held.Failed, held.Held));

        gate.Adopted(fx.Key);   // the adoption examined it
        var released = await rearm.Run(default);
        Assert.Contains(sent, m => m is PhaseLeaseExpired);
        Assert.Contains(sent, m => m is MergeRequested);
        Assert.Equal(0, released.Held);
    }

    [Fact]
    public async Task AHostThatIsNotUp_IsCountedAsFailed_AndTheNextRunTriesAgain()
    {
        var fx = new SagaFixture(); fx.Store.Clock = () => fx.Now;
        var up = false; var sent = new List<object>();
        var rearm = new WorkItemRearm(fx.Store, Policy, m => { if (up) sent.Add(m); return Task.FromResult(up); }, () => fx.Now);
        await fx.Start(plan: "docs");
        fx.Now = fx.State.LeaseDue!.Value.AddMinutes(10);
        Assert.Equal(1, (await rearm.Run(default)).Failed);
        up = true;
        Assert.Equal(1, (await rearm.Run(default)).Timers);
        Assert.Single(sent);
    }
}

/// <summary>W9.10o: the docs plan counts the merge answers that are about the pull request, not the gateway's own failures to ask.</summary>
public sealed class MergeAnswerCountTests
{
    const string Head = "aaa1111";

    static async Task<SagaFixture> AtMerge(int max)
    {
        var fx = new SagaFixture(WorkItemPolicy.Default with { Docs = new DocsPlanRules(MaxMergeAttempts: max) });
        await fx.Start(plan: "docs");
        await fx.Review(); await fx.Done();
        await fx.PullRequest(Head); await fx.Review(); await fx.Done();
        await fx.Verdict(true, Head);
        return fx;
    }

    static async Task AskAgain(SagaFixture fx)
    {
        fx.Now = fx.Now.AddSeconds(61);
        await fx.Send(new MergeRetryDue(SagaFixture.Item, 1, fx.Arm, fx.State.MergeRetryAt!.Value));
    }

    [Theory]
    [InlineData("merge-error")] [InlineData("merge-unavailable")] [InlineData("github-budget")]
    public async Task TheGatewaysOwnFailuresToAsk_DoNotUseUpTheAttempts_ButAreBounded(string reason)
    {
        var fx = await AtMerge(3);
        // five times the limit in calls, none of them about the pull request: the item waits, then the bound parks it
        for (var i = 1; i < 3 * DocsPlanRules.CallsPerAnswer; i++)
        {
            await fx.Attempted(new MergeResult(MergeResultKind.Blocked, reason, "the gateway could not ask", Transient: true));
            Assert.False(fx.State.Parked);
            Assert.Equal((i, 0), (fx.State.MergeAttempt, fx.State.MergeAnswers));
            await AskAgain(fx);
        }
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, reason, "the gateway could not ask", Transient: true));
        Assert.True(fx.State.Parked);
        Assert.StartsWith($"merge-blocked: {3 * DocsPlanRules.CallsPerAnswer} merge calls without a merge ({reason}); 0 of them answered about the pull request", fx.State.ParkReason);
    }

    [Fact]
    public async Task OnlyTheAnswersAboutThePullRequest_CountTowardsThePark()
    {
        var fx = await AtMerge(3);
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "merge-unavailable", "no credential", Transient: true)); await AskAgain(fx);
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", "the checks", Transient: true)); await AskAgain(fx);
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "github-budget", "low", Transient: true)); await AskAgain(fx);
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", "the checks", Transient: true)); await AskAgain(fx);
        Assert.False(fx.State.Parked);
        Assert.Equal((5, 2), (fx.State.MergeAttempt, fx.State.MergeAnswers));
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", "the checks", Transient: true));
        Assert.True(fx.State.Parked);   // the third answer about the pull request, the fifth call
        Assert.StartsWith("merge-blocked: 5 merge calls without a merge (checks-pending); 3 of them answered about the pull request", fx.State.ParkReason);
    }

    [Fact]
    public async Task AMergeThatHappens_NeedsNoCount_AndANewMergeStepStartsFromZero()
    {
        var fx = await AtMerge(3);
        await fx.Attempted(new MergeResult(MergeResultKind.Blocked, "checks-pending", Transient: true)); await AskAgain(fx);
        Assert.Equal(1, fx.State.MergeAnswers);
        // the head moved: back to Verify with a new verdict, and the next Merge step counts from zero
        await fx.Attempted(new MergeResult(MergeResultKind.HeadMoved, "head-moved", "someone pushed"));
        Assert.Equal(StepKind.Verify, fx.State.Step);
        await fx.PullRequest("bbb2222"); await fx.Verdict(true, "bbb2222");
        Assert.Equal((1, 0), (fx.State.MergeAttempt, fx.State.MergeAnswers));
    }
}
