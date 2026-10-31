using System.Text.Json.Nodes;
using Fworker.Workflows.Reconcilers;
using Xunit;

/// <summary>
/// W9.10g: the plant-wide steps of the sweep as recurring jobs (gateway/Ports/PlantStepJobs.cs), the intervals of the slots (RecurringJobIntervals), and the sweep leaving the steps out where the jobs exist. The steps themselves
/// (PlantSteps, the C# port of the rule scripts' claim-ref reap, board guard, replenishment and count) are tested in Fworker.Workflow.Tests (PlantStepsTests); here they are a stand-in, and what is checked is what the job asks of
/// them, when it does not, and what it does with the answer.
/// </summary>
public class PlantStepJobsTests
{
    sealed class FakeSteps : IPlantSteps
    {
        public readonly List<string> Calls = [];
        public IReadOnlySet<int>? Skip;
        public int? Waiting; public int Free = -1;
        public PlantStepResult Result = new();
        public Exception? Fails;
        public Action? Inside;
        public Task? Hold;   // the step does not end before this does

        Task<PlantStepResult> Run(string step)
        {
            Calls.Add(step); Inside?.Invoke();
            if (Hold is { } hold) return hold.ContinueWith(_ => Result);
            return Fails is { } f ? Task.FromException<PlantStepResult>(f) : Task.FromResult(Result);
        }

        public Task<PlantStepResult> ClaimRefReap(IReadOnlySet<int> skip) { Skip = skip; return Run("claim-ref-reap"); }
        public Task<PlantStepResult> BoardGuard() => Run("board-guard");
        public Task<PlantStepResult> Replenish(int? waiting, int freeWorkers) { Waiting = waiting; Free = freeWorkers; return Run("replenish"); }
        public Task<PlantStepResult> DesignWaiting() => Run("design-waiting");
    }

    sealed class Rig
    {
        public readonly FakeSteps Steps = new();
        public readonly GitHubBudget Budget = new(300);
        public readonly Plant Plant = new("secret", (_, _, _, _) => Task.CompletedTask, 30);
        public bool May = true, Restored = true, HasSteps = true;
        public PlantStepJobs Jobs;

        public Rig(bool ownerMode = true, TimeSpan? readyWait = null, TimeSpan? passTimeout = null)
        {
            Jobs = new PlantStepJobs(() => HasSteps ? Steps : null, () => Plant, () => null, Budget, null, () => May, () => Restored, ownerMode, readyWait ?? TimeSpan.Zero, passTimeout);
        }
    }

    [Fact]
    public async Task EachJobAsksForItsOwnStep_AndNothingElse()
    {
        var r = new Rig();
        Assert.Equal("ok", await r.Jobs.ClaimRefReap(default));
        Assert.Equal("ok", await r.Jobs.BoardGuard(default));
        Assert.Equal("ok", await r.Jobs.Replenish(default));
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(["claim-ref-reap", "board-guard", "replenish", "design-waiting"], r.Steps.Calls);
        Assert.Empty(r.Steps.Skip!);   // no saga owns an item here
    }

    [Fact]
    public async Task NothingRunsWhileThisProcessMayNotAct_OrBeforeTheRestore_OrWithoutAGitHubIdentity()
    {
        var r = new Rig { May = false };
        Assert.Equal("not-leader", await r.Jobs.ClaimRefReap(default));
        r.May = true; r.Restored = false;
        Assert.Equal("waiting-for-restore", await r.Jobs.Replenish(default));
        Assert.Empty(r.Steps.Calls);
        r.Restored = true; r.HasSteps = false;   // the instance has no GitHub identity: the steps cannot be made (v1 could not act either)
        Assert.Equal("no-github", await r.Jobs.BoardGuard(default));
        Assert.Equal(1, r.Jobs.Outcomes["board-guard:no-github"]);
    }

    [Fact]
    public async Task ALowBudgetStopsTheReapAndTheGuard_NotTheReplenishmentOrTheCount()
    {
        var r = new Rig();
        r.Budget.Observe(new JsonObject { ["core"] = new JsonObject { ["remaining"] = 10, ["reset"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), ["limit"] = 5000 } });
        Assert.True(r.Budget.Low().Low);
        Assert.Equal("budget-low", await r.Jobs.ClaimRefReap(default));
        Assert.Equal("budget-low", await r.Jobs.BoardGuard(default));
        Assert.Equal("ok", await r.Jobs.Replenish(default));
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(["replenish", "design-waiting"], r.Steps.Calls);
    }

    [Fact]
    public async Task ThePlantsFreeSessionsAndWaitingCountGoToTheReplenishment_AndItsAnswerIsKept()
    {
        var r = new Rig();
        r.Steps.Result.Replenish = new JsonObject { ["enabled"] = true, ["open"] = 2, ["targetOpen"] = 5, ["createdToday"] = 1, ["maxPerDay"] = 4, ["skipped"] = "", ["created"] = new JsonArray(41, 42) };
        Assert.Equal("ok", await r.Jobs.Replenish(default));
        Assert.Equal(0, r.Steps.Free);   // the fake plant has no session
        Assert.Null(r.Steps.Waiting);    // no plan has run yet: the step holds back or not as it did for a first sweep
        var status = r.Plant.ReplenishStatus();
        Assert.NotNull(status); Assert.Equal(2, (int)status!["createdSinceStart"]!); Assert.Equal(42, (int)status["lastItem"]!);
    }

    [Fact]
    public async Task TheKeeperIsToldTheBacklogIsLow_FromTheJobsCount()
    {
        var r = new Rig();
        r.Plant.Keeper = new KeeperSettings { Enabled = true, MinTechnicalDesign = 3, Names = ["backlog-keeper"] };
        r.Steps.Result.DesignWaiting = 1;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
        r.Steps.Result.DesignWaiting = 5;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));   // enough waiting: no new job
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
    }

    [Fact]
    public async Task TheKeeperIsToldTheBacklogIsLow_AlsoWhereOwnerSessionsAreOff()
    {
        // the sweep applied the keeper's count whatever the owner mode (the Tier 1 keeper suite runs a gateway without owner sessions)
        var r = new Rig(ownerMode: false);
        r.Plant.Keeper = new KeeperSettings { Enabled = true, MinTechnicalDesign = 3, Names = ["backlog-keeper"] };
        r.Steps.Result.DesignWaiting = 1;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
    }

    [Fact]
    public async Task AJobThatComesBeforeTheRestore_WaitsForItALittle_AndDoesNotLoseItsInterval()
    {
        var r = new Rig(readyWait: TimeSpan.FromSeconds(10)) { Restored = false };
        r.Plant.Keeper = new KeeperSettings { Enabled = true, MinTechnicalDesign = 3, Names = ["backlog-keeper"] };
        r.Steps.Result.DesignWaiting = 0;
        var run = r.Jobs.BacklogLow(default);
        await Task.Delay(700);
        Assert.False(run.IsCompleted);
        r.Restored = true;
        Assert.Equal("ok", await run);
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
        var late = new Rig(readyWait: TimeSpan.FromMilliseconds(600)) { Restored = false };
        Assert.Equal("waiting-for-restore", await late.Jobs.BoardGuard(default));   // never restored: gives up after the wait, as before
    }

    [Fact]
    public async Task APassTheLeaseEnded_IsDropped_AndOnePlantWithoutOwnerModeAppliesNothing()
    {
        var r = new Rig();
        r.Steps.Result.Replenish = new JsonObject { ["enabled"] = true, ["created"] = new JsonArray(7) };
        r.Steps.Inside = () => r.May = false;   // the lease is lost while the step runs
        Assert.Equal("fenced", await r.Jobs.Replenish(default));
        Assert.Null(r.Plant.ReplenishStatus());   // what the pass read was not applied

        var stopped = new Rig();
        stopped.Steps.Fails = new PlantStepFenced();   // the step itself found the lease gone before a write
        Assert.Equal("fenced", await stopped.Jobs.BoardGuard(default));

        var plain = new Rig(ownerMode: false);
        plain.Steps.Result.Replenish = new JsonObject { ["enabled"] = true, ["created"] = new JsonArray(7) };
        Assert.Equal("ok", await plain.Jobs.Replenish(default));
        Assert.Equal(0, plain.Steps.Free); Assert.Null(plain.Steps.Waiting);   // as the sweep: no owner mode, no input from the plant
        Assert.Null(plain.Plant.ReplenishStatus());
    }

    [Fact]
    public async Task AStepThatThrows_IsReportedAsFailed_AndNeverThrows()
    {
        var r = new Rig { Steps = { Fails = new InvalidOperationException("boom") } };
        Assert.Equal("failed", await r.Jobs.BoardGuard(default));
        Assert.Equal("failed", await r.Jobs.ClaimRefReap(default));
        Assert.Equal(1, r.Jobs.Outcomes["board-guard:failed"]);
    }

    [Fact]
    public async Task AStepThatOutlivesTheTimeout_IsNotStartedAgainByTheNextTick_UntilItEnds()
    {
        var gate = new TaskCompletionSource();
        var r = new Rig(passTimeout: TimeSpan.FromMilliseconds(200)) { Steps = { Hold = gate.Task } };
        Assert.Equal("failed", await r.Jobs.Replenish(default));   // the job stops waiting; the step goes on
        Assert.Equal("busy", await r.Jobs.Replenish(default));     // the next tick must not start a second copy beside it
        Assert.Equal("busy", await r.Jobs.Replenish(default));
        Assert.Equal(["replenish"], r.Steps.Calls);
        Assert.Equal(2, r.Jobs.Outcomes["replenish:busy"]);
        gate.SetResult();
        await Task.Delay(100);
        r.Steps.Hold = null;
        Assert.Equal("ok", await r.Jobs.Replenish(default));        // it ended: the job runs again
        Assert.Equal(["replenish", "replenish"], r.Steps.Calls);
    }

    // ---- the intervals --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheReconcilerRunsEveryTwoMinutes_ThePlantStepsAtTheSweepsInterval_AndTheQcSyncAtLeastEveryMinute()
    {
        var slots = RecurringJobSlots.Default(RecurringJobIntervals.From(null, 180)).Slots;
        TimeSpan Every(string name) => slots.Single(s => s.Name == name).Every;
        Assert.Equal(TimeSpan.FromMinutes(2), Every(RecurringJobSlots.BoardReconciler));
        foreach (var n in new[] { RecurringJobSlots.ClaimRefReap, RecurringJobSlots.BoardGuard, RecurringJobSlots.Replenish, RecurringJobSlots.BacklogLow }) Assert.Equal(TimeSpan.FromSeconds(180), Every(n));
        Assert.Equal(TimeSpan.FromSeconds(60), Every(RecurringJobSlots.QcSync));
        Assert.Equal(TimeSpan.FromHours(24), Every(RecurringJobSlots.RetentionPrune));
        // a gateway that sweeps every 2 seconds (the Tier 1 tests) runs the steps every 5 seconds, never faster, and the QC sync no slower than them
        var fast = RecurringJobSlots.Default(RecurringJobIntervals.From(null, 2)).Slots;
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Single(s => s.Name == RecurringJobSlots.QcSync).Every);
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Single(s => s.Name == RecurringJobSlots.Replenish).Every);
        Assert.Equal(TimeSpan.FromSeconds(3), fast.Single(s => s.Name == RecurringJobSlots.ClaimRefReap).First);   // the first run as soon as the leader may act
    }

    [Fact]
    public void TheIntervalsComeFromTheConfigurationBlock_WithinTheirLimits()
    {
        var i = RecurringJobIntervals.From(JsonNode.Parse("""{"recurringJobs":{"boardReconcilerSeconds":60,"plantStepsSeconds":90,"qcSyncSeconds":30,"retentionHours":6}}"""), 180);
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), TimeSpan.FromHours(6)), (i.BoardReconciler, i.PlantSteps, i.QcSync, i.Retention));
        var low = RecurringJobIntervals.From(JsonNode.Parse("""{"recurringJobs":{"boardReconcilerSeconds":1,"plantStepsSeconds":1,"retentionHours":0}}"""), 180);
        Assert.Equal(TimeSpan.FromSeconds(30), low.BoardReconciler);   // never faster than every 30 seconds
        Assert.Equal(TimeSpan.FromSeconds(5), low.PlantSteps);         // nor the plant-wide steps than every 5
        Assert.Equal(TimeSpan.FromHours(1), low.Retention);
    }

    [Fact]
    public async Task TheSchedulerLooksAtTheClockOftenEnoughForTheSlotsItRuns()
    {
        var reg = new RecurringJobRegistry().Add(RecurringJobSlots.Replenish, _ => Task.CompletedTask);
        var sent = new List<string>(); var time = new FakeClock();
        var s = new RecurringJobScheduler(RecurringJobSlots.Default(RecurringJobIntervals.From(null, 2)), reg, r => { sent.Add(r.Name); return Task.FromResult(true); }, () => true, time);
        await s.Tick(); Assert.Empty(sent);   // the first look only sets the first run
        time.Now += TimeSpan.FromSeconds(6); await s.Tick(); Assert.Single(sent);
        time.Now += TimeSpan.FromSeconds(6); await s.Tick(); Assert.Equal(2, sent.Count);
    }

    sealed class FakeClock : TimeProvider { public DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }

    // ---- the sweep leaves the steps to the jobs --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhereTheJobsExist_TheSweepsStepsAreTheOnesTheJobsTake()
    {
        Assert.Equal(["claim-ref-reap", "board-guard", "qc-sync", "replenish"], PlantStepJobs.SweepSkips);
    }
}
