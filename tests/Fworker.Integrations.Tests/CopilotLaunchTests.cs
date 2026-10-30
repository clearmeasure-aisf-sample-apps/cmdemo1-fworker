using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W3.3: the Copilot launch reservation (one task per item and phase, replay, unknown outcomes) and its reconciler.</summary>
public class CopilotLaunchTests
{
    sealed class Inner : ICommandPort
    {
        public int Calls; public Func<CommandResult> Answer = () => CommandResult.Success("task-1", "http://task/1"); public Exception? Throw;
        public Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default) { Calls++; if (Throw != null) throw Throw; return Task.FromResult(Answer()); }
    }

    sealed class Lookup : ICopilotTaskLookup
    {
        public (string, string)? Found, FoundResume; public bool Fail; public int Calls, ResumeCalls; public DateTime Since;
        public Task<(string TaskId, string Url)?> FindAsync(int item, string phase, DateTime since, CancellationToken ct)
        { Calls++; Since = since; if (Fail) throw new HttpRequestException("down"); return Task.FromResult(Found is { } f ? ((string, string)?)f : null); }
        public Task<(string TaskId, string Url)?> FindResumeAsync(int item, string phase, DateTime since, CancellationToken ct)
        { ResumeCalls++; Since = since; if (Fail) throw new HttpRequestException("down"); return Task.FromResult(FoundResume is { } f ? ((string, string)?)f : null); }
    }

    sealed class Recorder : ICopilotOwnerRecorder
    {
        public readonly List<CopilotReservation> Recorded = []; public bool Fail;
        public Task RecordAsync(CopilotReservation settled, CancellationToken ct) { if (Fail) throw new InvalidOperationException("owner verb down"); Recorded.Add(settled); return Task.CompletedTask; }
    }

    static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    DateTime now = T0;
    readonly Inner inner = new(); readonly Lookup lookup = new(); readonly InMemoryCopilotReservations store = new(); readonly Recorder recorder = new();
    readonly CopilotLaunchPort port;
    readonly CopilotReconciler reconciler;

    public CopilotLaunchTests()
    {
        reconciler = new CopilotReconciler(store, lookup, () => now, recorder);
        port = new CopilotLaunchPort(inner, store, reconciler, () => now);
    }

    static StartRuntimeSession Start(string worker = "w1", int item = 7, string runtime = "copilot-cloud") => new(RuntimeKeys.Start(runtime, worker), runtime, worker, item, "Development", "p", "n");

    [Fact]
    public async Task Launch_Reserves_ThenMarksLaunched_WithTheTask()
    {
        var r = await port.SendAsync(Start());
        Assert.True(r.Ok);
        var res = store.Get(7, "Development")!;
        Assert.Equal((ReservationState.Launched, "w1", "task-1", "http://task/1"), (res.State, res.Worker, res.TaskId, res.Url));
    }

    [Fact]
    public async Task Replay_OfALaunchedWorker_DoesNotLaunchAgain_AndAnswersTheSame()
    {
        await port.SendAsync(Start());
        var again = await port.SendAsync(Start());
        Assert.Equal(1, inner.Calls);
        Assert.Equal(("task-1", "http://task/1"), (again.Execution, again.Url));
    }

    [Fact]
    public async Task OtherRuntimes_PassThrough_WithoutAReservation()
    {
        await port.SendAsync(Start(runtime: "cursor-cloud"));
        Assert.Equal(1, inner.Calls); Assert.Null(store.Get(7, "Development"));
    }

    [Fact]
    public async Task ThrownLaunch_LeavesItReserved_AndBlocksAnotherLaunchOfTheItemAndPhase()
    {
        inner.Throw = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => port.SendAsync(Start("w1")));
        inner.Throw = null; lookup.Fail = true; now = T0.AddMinutes(5);
        var blocked = await port.SendAsync(Start("w2"));
        Assert.False(blocked.Ok); Assert.Equal(FailureKind.RateLimited, blocked.Failure); Assert.Equal(CopilotLaunchPort.ReservedError, blocked.Error);   // no failure of the item: it waits for the reconciler
        Assert.Equal(1, inner.Calls); Assert.Equal(ReservationState.Reserved, store.Get(7, "Development")!.State);
    }

    [Fact]
    public async Task RetryAfterAnUnknownOutcome_FindsTheTask_AndDoesNotLaunchASecond()
    {
        inner.Throw = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => port.SendAsync(Start("w1")));
        inner.Throw = null; lookup.Found = ("task-9", "http://task/9"); now = T0.AddMinutes(5);
        var r = await port.SendAsync(Start("w1"));
        Assert.True(r.Ok); Assert.Equal("task-9", r.Execution); Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task RefusedLaunch_ReleasesTheReservation_SoTheNextCanLaunch()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "launch-422");
        Assert.False((await port.SendAsync(Start("w1"))).Ok);
        Assert.Equal(ReservationState.Released, store.Get(7, "Development")!.State);
        inner.Answer = () => CommandResult.Success("task-2", "u");
        Assert.True((await port.SendAsync(Start("w2"))).Ok);
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData("launch-HttpRequestException")]
    [InlineData("launch-no-id")]
    public async Task AFailedCallThatMayHaveCreatedTheTask_StaysReserved(string error)
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, error);
        await port.SendAsync(Start());
        Assert.Equal(ReservationState.Reserved, store.Get(7, "Development")!.State);
    }

    [Fact]
    public async Task ANewWorker_AfterALaunch_IsAllowed_AndTakesTheReservation()
    {
        await port.SendAsync(Start("w1"));
        inner.Answer = () => CommandResult.Success("task-2", "u2");
        Assert.True((await port.SendAsync(Start("w2"))).Ok);
        Assert.Equal(("w2", "task-2"), (store.Get(7, "Development")!.Worker, store.Get(7, "Development")!.TaskId));
    }

    async Task Unknown()
    {
        inner.Throw = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => port.SendAsync(Start()));
    }

    [Fact]
    public async Task Reconciler_TaskExists_SettlesLaunched()
    {
        await Unknown(); lookup.Found = ("task-3", "u3"); now = T0.AddMinutes(5);
        var done = await reconciler.Run(TimeSpan.FromMinutes(2));
        Assert.Equal(ReconcileOutcome.Launched, Assert.Single(done).Outcome);
        Assert.Equal(("task-3", ReservationState.Launched), (store.Get(7, "Development")!.TaskId, store.Get(7, "Development")!.State));
        Assert.Equal(T0.AddMinutes(-1), lookup.Since);
    }

    [Fact]
    public async Task Reconciler_NoTask_ReleasesForARetry()
    {
        await Unknown(); now = T0.AddMinutes(5);
        Assert.Equal(ReconcileOutcome.Released, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        inner.Throw = null;
        Assert.True((await port.SendAsync(Start("w2"))).Ok);
    }

    [Fact]
    public async Task Reconciler_GitHubDown_LeavesItReserved()
    {
        await Unknown(); lookup.Fail = true; now = T0.AddMinutes(5);
        Assert.Equal(ReconcileOutcome.Unknown, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        Assert.Equal(ReservationState.Reserved, store.Get(7, "Development")!.State);
    }

    [Fact]
    public async Task Reconciler_LeavesALaunchStillInFlight_Alone()
    {
        await Unknown(); now = T0.AddSeconds(30);
        Assert.Empty(await reconciler.Run(TimeSpan.FromMinutes(2)));
        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public async Task LaunchInFlight_IsNotSettledInline_AndBlocksAnotherCommand()
    {
        await Unknown(); now = T0.AddSeconds(10);
        var r = await port.SendAsync(Start("w2"));
        Assert.False(r.Ok); Assert.Equal(0, lookup.Calls); Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void TryReserve_IsAtomic_OnlyOneOfManyWins()
    {
        var wins = 0;
        Parallel.For(0, 50, i => { if (store.TryReserve(new CopilotReservation(7, "Development", "w" + i, ReservationState.Reserved, T0))) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
    }

    // ---- W9.10c: a resume that times out is unknown, not failed; the reconciler writes the owner record; the settle is conditional ----

    [Fact]
    public async Task AResumeThatTimesOut_IsUnknown_NotFailed_TheReservationStays_AndNoSecondCommentIsPosted()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "resume-TaskCanceledException");
        var r = await port.SendAsync(Start("w1"));
        Assert.Equal((false, FailureKind.RateLimited, "reserved"), (r.Ok, r.Failure, r.Error));   // not Transient: the caller does not retry it into a failure of the item
        var res = store.Get(7, "Development")!;
        Assert.Equal((ReservationState.Reserved, CopilotReservation.ResumeMode), (res.State, res.Mode));
        // the item is offered again with another worker name: the comment may be on the pull request, so nothing is posted until the reconciler has decided
        inner.Answer = () => CommandResult.Success("task-1#2", "u");
        var again = await port.SendAsync(Start("w2"));
        Assert.Equal(FailureKind.RateLimited, again.Failure); Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AResumeRefusedByGitHub_IsAFailure_AndReleasesTheReservation()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "resume-422");
        var r = await port.SendAsync(Start("w1"));
        Assert.Equal("resume-422", r.Error);
        Assert.Equal(ReservationState.Released, store.Get(7, "Development")!.State);
    }

    [Fact]
    public async Task AFastUnknownLaunch_AnswersReserved_RateLimited_SoItIsNeverExhaustedIntoAFailure()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "launch-HttpRequestException");
        var r = await port.SendAsync(Start("w1"));
        Assert.Equal(FailureKind.RateLimited, r.Failure);
        Assert.Equal(CopilotReservation.LaunchMode, store.Get(7, "Development")!.Mode);
    }

    [Fact]
    public async Task Reconciler_ResumeComment_Found_RecordsTheOwner_AndSettlesLaunched()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "resume-TaskCanceledException");
        await port.SendAsync(Start("w1")); lookup.FoundResume = ("task-1#2", "u1"); now = T0.AddMinutes(5);
        Assert.Equal(ReconcileOutcome.Launched, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        Assert.Equal(0, lookup.Calls);   // a resume looks for the comment, not for a task
        var rec = Assert.Single(recorder.Recorded);
        Assert.Equal((7, CopilotReservation.ResumeMode, "task-1#2"), (rec.Item, rec.Mode, rec.TaskId));
        Assert.Equal(ReservationState.Launched, store.Get(7, "Development")!.State);
    }

    [Fact]
    public async Task Reconciler_ResumeComment_NotThere_Releases_SoTheResumeIsPostedOnce()
    {
        inner.Answer = () => CommandResult.Failed(FailureKind.Transient, "resume-TaskCanceledException");
        await port.SendAsync(Start("w1")); now = T0.AddMinutes(5);
        Assert.Equal(ReconcileOutcome.Released, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        Assert.Empty(recorder.Recorded);
        inner.Answer = () => CommandResult.Success("task-1#2", "u");
        Assert.True((await port.SendAsync(Start("w2"))).Ok); Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Reconciler_Launch_Found_WritesTheOwnerRecord_BeforeItSettles_SoTheNextWorkerResumesThatTask()
    {
        await Unknown(); lookup.Found = ("task-3", "u3"); now = T0.AddMinutes(5);
        await reconciler.Run(TimeSpan.FromMinutes(2));
        var rec = Assert.Single(recorder.Recorded);
        Assert.Equal((CopilotReservation.LaunchMode, "task-3", "u3"), (rec.Mode, rec.TaskId, rec.Url));
        Assert.Equal(ReservationState.Launched, rec.State);
    }

    [Fact]
    public async Task Reconciler_OwnerRecordCannotBeWritten_LeavesItReserved_ForTheNextPass()
    {
        await Unknown(); lookup.Found = ("task-3", "u3"); recorder.Fail = true; now = T0.AddMinutes(5);
        Assert.Equal(ReconcileOutcome.Unknown, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        Assert.Equal(ReservationState.Reserved, store.Get(7, "Development")!.State);
        recorder.Fail = false;
        Assert.Equal(ReconcileOutcome.Launched, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
    }

    [Fact]
    public async Task Reconciler_UnknownMode_ReleasedOnlyWhenNeitherATaskNorAResumeIsThere()
    {
        await Unknown(); lookup.FoundResume = ("task-1#1", "u1"); now = T0.AddMinutes(5);   // a reservation of before W9.10c has no mode
        Assert.Equal(ReconcileOutcome.Launched, Assert.Single(await reconciler.Run(TimeSpan.FromMinutes(2))).Outcome);
        Assert.Equal(CopilotReservation.ResumeMode, Assert.Single(recorder.Recorded).Mode);
    }

    [Fact]
    public void Transition_IsConditional_AStaleSettleNeverOverwritesANewerRecord()
    {
        var a = new CopilotReservation(7, "Development", "w1", ReservationState.Reserved, T0);
        store.Put(a);
        var newer = a with { State = ReservationState.Launched, TaskId = "t" };
        Assert.True(store.Transition(a, newer));
        Assert.False(store.Transition(a, a with { State = ReservationState.Released }));   // the stale reservation no longer matches
        Assert.Equal(newer, store.Get(7, "Development"));
    }

    [Fact]
    public async Task Reconciler_TheLaunchSettledItselfWhileGitHubWasAsked_IsNotOverwritten()
    {
        await Unknown(); lookup.Found = null; now = T0.AddMinutes(5);
        var stale = store.Get(7, "Development")!;
        store.Put(stale with { State = ReservationState.Launched, TaskId = "task-own", Url = "u", Worker = "w1" });   // the launch finished meanwhile
        Assert.Equal(ReconcileOutcome.Unknown, await reconciler.Settle(stale));
        Assert.Equal("task-own", store.Get(7, "Development")!.TaskId);
    }
}
