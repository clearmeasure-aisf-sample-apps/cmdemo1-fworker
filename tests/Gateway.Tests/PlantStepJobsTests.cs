using System.Text.Json.Nodes;
using Xunit;

/// <summary>
/// W9.10g: the plant-wide steps of the sweep as recurring jobs (gateway/Ports/PlantStepJobs.cs), the intervals of the slots (RecurringJobIntervals), and the sweep leaving the steps out where the jobs exist. The rule script is a
/// fake that answers sync-board and qc-sync as the real ones do; what is checked is what the gateway asks for and what it does with the answer.
/// </summary>
public class PlantStepJobsTests
{
    sealed class Rig
    {
        public readonly List<(string Verb, JsonObject Args)> Calls = [];
        public readonly GitHubBudget Budget = new(300);
        public readonly Plant Plant = new("secret", (_, _, _, _) => Task.CompletedTask, 30);
        public bool May = true, Restored = true;
        public int Code; public string? Output;
        public JsonObject Answer = new() { ["ok"] = true, ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["elapsed_ms"] = 5 };
        public readonly Runner Runner = new("/nonexistent/factory.ps1");
        public PlantStepJobs Jobs;

        public Rig(bool ownerMode = true)
        {
            Runner.V2Own = (verb, stdin, _) => { Calls.Add((verb, (JsonObject)JsonNode.Parse(stdin)!)); return Task.FromResult<(int, string)?>((Code, Output ?? Answer.ToJsonString())); };
            Jobs = new PlantStepJobs(Runner, () => Plant, () => null, Budget, null, () => May, () => Restored, ownerMode);
        }
    }

    static string Steps(JsonObject args) => string.Join(",", ((JsonArray)args["plant_steps"]!).Select(x => x!.ToString()));

    [Fact]
    public async Task EachJobAsksForItsOwnStep_AndNothingElse()
    {
        var r = new Rig();
        Assert.Equal("ok", await r.Jobs.ClaimRefReap(default));
        Assert.Equal("ok", await r.Jobs.BoardGuard(default));
        Assert.Equal("ok", await r.Jobs.Replenish(default));
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(["claim-ref-reap", "board-guard", "replenish", "design-waiting"], r.Calls.Select(c => Steps(c.Args)).ToArray());
        Assert.All(r.Calls, c => Assert.Equal("sync-board", c.Verb));
        Assert.All(r.Calls, c => Assert.False(c.Args.ContainsKey("items")));   // a plant pass is never an incremental one
    }

    [Fact]
    public async Task NothingRunsWhileThisProcessMayNotAct_OrBeforeTheRestore()
    {
        var r = new Rig { May = false };
        Assert.Equal("not-leader", await r.Jobs.ClaimRefReap(default));
        r.May = true; r.Restored = false;
        Assert.Equal("waiting-for-restore", await r.Jobs.Replenish(default));
        Assert.Empty(r.Calls);
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
        Assert.Equal(["replenish", "design-waiting"], r.Calls.Select(c => Steps(c.Args)).ToArray());
    }

    [Fact]
    public async Task ThePlantsFreeSessionsAndWaitingCountGoToTheReplenishment_AndItsAnswerIsKept()
    {
        var r = new Rig();
        r.Answer["replenish"] = new JsonObject { ["enabled"] = true, ["open"] = 2, ["targetOpen"] = 5, ["createdToday"] = 1, ["maxPerDay"] = 4, ["skipped"] = "", ["created"] = new JsonArray(41, 42) };
        Assert.Equal("ok", await r.Jobs.Replenish(default));
        var args = r.Calls.Single().Args;
        Assert.Equal(0, (int)args["free_workers"]!);      // the fake plant has no session
        Assert.False(args.ContainsKey("replenish_waiting"));   // no plan has run yet: the script holds back or not as it did for a first sweep
        var status = r.Plant.ReplenishStatus();
        Assert.NotNull(status); Assert.Equal(2, (int)status!["createdSinceStart"]!); Assert.Equal(42, (int)status["lastItem"]!);
    }

    [Fact]
    public async Task TheKeeperIsToldTheBacklogIsLow_FromTheJobsCount()
    {
        var r = new Rig();
        r.Plant.Keeper = new KeeperSettings { Enabled = true, MinTechnicalDesign = 3, Names = ["backlog-keeper"] };
        r.Answer["design_waiting"] = 1;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
        r.Answer["design_waiting"] = 5;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));   // enough waiting: no new job
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
    }

    [Fact]
    public async Task TheKeeperIsToldTheBacklogIsLow_AlsoWhereOwnerSessionsAreOff()
    {
        // the sweep applied the keeper's count whatever the owner mode (the Tier 1 keeper suite runs a gateway without owner sessions)
        var r = new Rig(ownerMode: false);
        r.Plant.Keeper = new KeeperSettings { Enabled = true, MinTechnicalDesign = 3, Names = ["backlog-keeper"] };
        r.Answer["design_waiting"] = 1;
        Assert.Equal("ok", await r.Jobs.BacklogLow(default));
        Assert.Equal(1, (int)r.Plant.KeeperStatus()["jobsBuffered"]!);
    }

    [Fact]
    public async Task APassTheLeaseEnded_IsDropped_AndOnePlantWithoutOwnerModeAppliesNothing()
    {
        var r = new Rig();
        r.Answer["replenish"] = new JsonObject { ["enabled"] = true, ["created"] = new JsonArray(7) };
        r.Runner.V2Own = (verb, stdin, _) => { r.May = false; r.Calls.Add((verb, (JsonObject)JsonNode.Parse(stdin)!)); return Task.FromResult<(int, string)?>((0, r.Answer.ToJsonString())); };   // the lease is lost while the script runs
        Assert.Equal("fenced", await r.Jobs.Replenish(default));
        Assert.Null(r.Plant.ReplenishStatus());   // what the pass read was not applied

        var plain = new Rig(ownerMode: false);
        plain.Answer["replenish"] = new JsonObject { ["enabled"] = true, ["created"] = new JsonArray(7) };
        Assert.Equal("ok", await plain.Jobs.Replenish(default));
        Assert.False(plain.Calls.Single().Args.ContainsKey("free_workers"));   // as the sweep: no owner mode, no input from the plant
        Assert.Null(plain.Plant.ReplenishStatus());
    }

    [Fact]
    public async Task AScriptThatFails_IsReportedAsFailed_AndNeverThrows()
    {
        var r = new Rig { Code = 20, Output = "not json" };
        Assert.Equal("failed", await r.Jobs.BoardGuard(default));
        r.Code = 0;
        Assert.Equal("failed", await r.Jobs.BoardGuard(default));   // exit 0 with no JSON object
        Assert.Equal(2, r.Jobs.Outcomes["board-guard:failed"]);
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
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Single(s => s.Name == RecurringJobSlots.ClaimRefReap).First);   // the first run as soon as the leader may act
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
