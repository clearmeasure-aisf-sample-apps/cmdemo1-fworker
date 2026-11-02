using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Intake;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// V1.2: the intake's free slots from the scheduler's capacity (<see cref="CapacityScheduler.FreeSlots"/>: maxConcurrent less what runs and waits, the day's budget, the room of the hosts) and the env: scope check of the
/// intake rule (Test-ItemInScope).
/// </summary>
public class IntakeCapacityTests
{
    // ---- the free capacity ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyLedger_GivesTheRuntimesWholeConcurrency_AndAnUnknownOrOffRuntimeGivesNone()
    {
        var fx = new CapacityFixture();
        Assert.Equal(1, await fx.Svc.FreeSlots("cursor-cloud"));
        Assert.Equal(5, await fx.Svc.FreeSlots("copilot-cloud"));
        Assert.Equal(0, await fx.Svc.FreeSlots("cae-job"));   // (not configured: off)
        Assert.Equal(0, await fx.Svc.FreeSlots("nothing"));
    }

    [Fact]
    public async Task AGrantedSlot_AndAWaitingRequest_TakeFromTheConcurrency_AndTheEndGivesItBack()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, "copilot-cloud"); await fx.Request(2, "copilot-cloud");
        Assert.Equal(3, await fx.Svc.FreeSlots("copilot-cloud"));
        var one = new CapacityFixture();
        await one.Request(1); await one.Request(2);   // the second one waits for the only slot of cursor-cloud
        Assert.Equal(0, await one.Svc.FreeSlots("cursor-cloud"));
        await one.End(1);   // the waiting request is granted at once
        Assert.Equal(0, await one.Svc.FreeSlots("cursor-cloud"));
        await one.End(2);
        Assert.Equal(1, await one.Svc.FreeSlots("cursor-cloud"));
    }

    [Fact]
    public async Task WhatThePlantRunsOutsideTheLedger_CountsAsRunning_AndABlockedRuntimeHasNone()
    {
        var fx = new CapacityFixture();
        fx.Outside["copilot-cloud"] = new RuntimeOutside(2, 2);
        Assert.Equal(3, await fx.Svc.FreeSlots("copilot-cloud"));
        fx.Outside["copilot-cloud"] = new RuntimeOutside(0, 0, "quota");
        Assert.Equal(0, await fx.Svc.FreeSlots("copilot-cloud"));
    }

    [Fact]
    public async Task TheDaysBudget_BoundsTheSlots_AndANewDayStartsAgain()
    {
        var fx = new CapacityFixture(cfg => cfg["api"]!["copilot"]!["maxExecutionsPerDay"] = 2);
        Assert.Equal(2, await fx.Svc.FreeSlots("copilot-cloud"));
        await fx.Request(1, "copilot-cloud");
        Assert.Equal(1, await fx.Svc.FreeSlots("copilot-cloud"));   // (the concurrency would leave 4)
        fx.Outside["copilot-cloud"] = new RuntimeOutside(0, 1);
        Assert.Equal(0, await fx.Svc.FreeSlots("copilot-cloud"));
        fx.Outside.Clear();
        fx.Now = fx.Now.AddDays(1);
        Assert.Equal(2, await fx.Svc.FreeSlots("copilot-cloud"));   // (the slot is still held, and the day's count starts at 0 again)
    }

    [Fact]
    public async Task ARuntimeWithHosts_IsBoundedByTheGuaranteedRoomOfTheHostsThatReported()
    {
        var fx = new CapacityFixture();
        Assert.Equal(0, await fx.Svc.FreeSlots("octopus"));   // no host has reported: nothing could be granted
        await fx.Report("host-a");
        Assert.Equal(2, await fx.Svc.FreeSlots("octopus"));   // (two guaranteed slots per host; maxConcurrent is 6)
        await fx.Report("host-b", running: 1);
        Assert.Equal(3, await fx.Svc.FreeSlots("octopus"));
        await fx.Request(1, "octopus");
        Assert.Equal(2, await fx.Svc.FreeSlots("octopus"));
        fx.Now = fx.Now.AddMinutes(31);   // the reports are three times older than a usable report: the hosts are taken for gone
        Assert.Equal(0, await fx.Svc.FreeSlots("octopus"));
    }

    // ---- the env: scope -----------------------------------------------------------------------------------------------------------------

    static IntakeSettings Settings(string envLabel) =>
        new(true, 6, "Todo", [], new Dictionary<string, int>(), [], "engine:v2", "factory worker", "AI Factory", "Demo", envLabel);

    static IntakeCandidate Cand(int n, params string[] labels) => new(new ItemId("acme", "orders", n), true, true, ["factory worker", .. labels]);

    static readonly IReadOnlyDictionary<string, int> None = new Dictionary<string, int>();

    [Fact]
    public void WithScopingOn_OnlyAnItemWithExactlyThisEnvironmentsLabelIsPulled()
    {
        var s = Settings("env:tdd");
        var d = IntakeRule.Decide([Cand(1), Cand(2, "env:uat"), Cand(3, "env:tdd", "env:uat"), Cand(4, "ENV:TDD")], None, 1, false, s);
        Assert.Equal(4, d.Pull!.Item.Number);
        Assert.Equal([(1, "out-of-scope"), (2, "out-of-scope"), (3, "out-of-scope")], d.Passed.Select(p => (p.Item.Number, p.Reason)));
        Assert.Equal(IntakeRule.NoCandidate, IntakeRule.Decide([Cand(1), Cand(2, "env:uat")], None, 1, false, s).Reason);
    }

    [Fact]
    public void WithScopingOff_TheEnvLabelsAreNotLookedAt()
    {
        Assert.Equal(1, IntakeRule.Decide([Cand(1, "env:uat")], None, 1, false, Settings("")).Pull!.Item.Number);
    }

    [Theory]
    [InlineData("""{"envScope":["tdd"]}""", "tdd", "env:tdd")]
    [InlineData("""{"envScope":["tdd"]}""", "uat", "")]
    [InlineData("""{"envScope":["tdd"]}""", "", "env:tdd")]
    [InlineData("""{"envScope":[]}""", "tdd", "")]
    public void TheSettings_TakeTheEnvLabelOfTheEnvironment_AsGetEnvLabelDoes(string json, string environment, string expected)
    {
        var s = IntakeSettings.From(JsonNode.Parse(json), name => name == "FACTORY_ENVIRONMENT" ? environment : null);
        Assert.Equal(expected, s.EnvLabel);
    }
}
