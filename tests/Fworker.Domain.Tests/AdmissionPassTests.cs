using Fworker.Contracts.Ids;
using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D1: specification tests of the admission chain and the plan pass (src/Fworker.Domain/AdmissionPass.cs): the order of the rules (docs/design/delivery-speed.md
/// 3.2), generated items last and only into a free slot (3.1 rank 6, 6), the slot taken by each admitted item. No v1 function to match.
/// </summary>
public sealed class AdmissionPassTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly AdmissionSettings Settings = new();

    static ItemId Item(int n) => new("acme", "app", n);

    static AdmissionFacts Facts(int running = 0, int max = 2, int today = 0, int budget = 10, string[]? tools = null, Dictionary<string, DateTimeOffset>? hostFull = null)
        => new(T0, new Dictionary<string, AdmissionTarget> { ["octopus"] = new(new RuntimeLoad(running, max, today, budget), tools ?? new[] { "dotnet", "pwsh" }) }, hostFull);

    static AdmissionRequest Req(int n, bool generated = false, string[]? tools = null, string? host = null, FileOverlapHold? hold = null)
        => new(Item(n), "octopus", generated, tools, host, hold);

    [Fact]
    public void Check_AnItemWithNothingInTheWay_IsAdmitted()
        => Assert.True(AdmissionPass.Check(Req(1), Facts(), Settings).Admitted);

    [Fact]
    public void Check_AnUnknownRuntime_IsRuntimeOff()
    {
        var d = AdmissionPass.Check(new AdmissionRequest(Item(1), "cae-job"), Facts(), Settings);
        Assert.Equal("runtime-off", d.Code);
    }

    [Fact]
    public void Check_TheHoldComesFirst_BeforeCapacity()
    {
        var d = AdmissionPass.Check(Req(1, hold: new FileOverlapHold(Item(9), T0)), Facts(running: 2), Settings);
        Assert.Equal("file-overlap acme/app#9", d.Waiting);
    }

    [Fact]
    public void Check_AnExpiredHold_FallsThroughToTheNextRule()
    {
        var d = AdmissionPass.Check(Req(1, hold: new FileOverlapHold(Item(9), T0.AddMinutes(-120))), Facts(running: 2), Settings);
        Assert.Equal("runtime-at-capacity", d.Code);
    }

    [Fact]
    public void Check_ToolchainIsReportedBeforeCapacity()
    {
        var d = AdmissionPass.Check(Req(1, tools: new[] { "terraform" }), Facts(running: 2), Settings);
        Assert.Equal("toolchain terraform", d.Waiting);
    }

    [Fact]
    public void Check_CapacityThenBudget()
    {
        Assert.Equal("runtime-at-capacity", AdmissionPass.Check(Req(1), Facts(running: 2, today: 10), Settings).Code);
        Assert.Equal("runtime-budget", AdmissionPass.Check(Req(1), Facts(today: 10), Settings).Code);
    }

    [Fact]
    public void Check_GeneratedWaitsWhileARealItemWaitsForASlot()
        => Assert.Equal("generated-last", AdmissionPass.Check(Req(1, generated: true), Facts(), Settings, realItemsWaitingForSlot: 1).Code);

    [Fact]
    public void Check_HostFull_BlocksOnlyThatHost()
    {
        var facts = Facts(hostFull: new() { ["omarchy"] = T0.AddSeconds(-10) });
        Assert.Equal("host-full", AdmissionPass.Check(Req(1, host: "omarchy"), facts, Settings).Code);
        Assert.True(AdmissionPass.Check(Req(1, host: "other"), facts, Settings).Admitted);
        Assert.True(AdmissionPass.Check(Req(1), facts, Settings).Admitted);
    }

    [Fact]
    public void Check_HostFull_EndsAfterHostFullSeconds()
    {
        var facts = Facts(hostFull: new() { ["omarchy"] = T0.AddSeconds(-30) });
        Assert.True(AdmissionPass.Check(Req(1, host: "omarchy"), facts, Settings).Admitted);
    }

    // ---- the pass ----

    [Fact]
    public void Run_EachAdmittedItemTakesASlot_TheNextOneWaits()
    {
        var v = AdmissionPass.Run(new[] { Req(1), Req(2), Req(3) }, Facts(running: 0, max: 2), Settings);
        Assert.Equal(new[] { true, true, false }, v.Select(x => x.Decision.Admitted));
        Assert.Equal("runtime-at-capacity", v[2].Decision.Code);
    }

    [Fact]
    public void Run_EachAdmittedItemTakesADayExecution()
    {
        var v = AdmissionPass.Run(new[] { Req(1), Req(2) }, Facts(max: 5, today: 9, budget: 10), Settings);
        Assert.True(v[0].Decision.Admitted);
        Assert.Equal("runtime-budget", v[1].Decision.Code);
    }

    [Fact]
    public void Run_DoesNotChangeTheFactsItWasGiven()
    {
        var facts = Facts(running: 0, max: 2);
        AdmissionPass.Run(new[] { Req(1), Req(2) }, facts, Settings);
        Assert.Equal(0, facts.Runtimes["octopus"].Load.Running);
    }

    [Fact]
    public void Run_GeneratedItemsAreEvaluatedLast_EvenWhenGivenFirst()
    {
        // one free slot: the real item takes it although the generated item came first in the list
        var v = AdmissionPass.Run(new[] { Req(10, generated: true), Req(1) }, Facts(running: 1, max: 2), Settings);
        Assert.Equal(new[] { Item(1), Item(10) }, v.Select(x => x.Item));
        Assert.True(v[0].Decision.Admitted);
        Assert.False(v[1].Decision.Admitted);
    }

    [Fact]
    public void Run_GeneratedGoesIntoAFreeSlot_WhenNoRealItemWaits()
    {
        var v = AdmissionPass.Run(new[] { Req(1), Req(10, generated: true) }, Facts(running: 0, max: 2), Settings);
        Assert.All(v, x => Assert.True(x.Decision.Admitted));
    }

    [Fact]
    public void Run_GeneratedWaits_WhenARealItemWaitsForASlot_EvenIfAnotherRuntimeHasRoom()
    {
        var facts = new AdmissionFacts(T0, new Dictionary<string, AdmissionTarget>
        {
            ["octopus"] = new(new RuntimeLoad(2, 2, 0, 10), Array.Empty<string>()),
            ["cae-job"] = new(new RuntimeLoad(0, 2, 0, 10), Array.Empty<string>()),
        });
        var v = AdmissionPass.Run(new[] { Req(1), new AdmissionRequest(Item(10), "cae-job", Generated: true) }, facts, Settings);
        Assert.Equal("runtime-at-capacity", v[0].Decision.Code);
        Assert.Equal("generated-last", v[1].Decision.Code);
    }

    [Fact]
    public void Run_ARealItemHeldForAnotherReason_DoesNotHoldBackGenerated()
    {
        // a hold and a toolchain mismatch are not waits for a slot: a free slot is of no use to those items
        var v = AdmissionPass.Run(new[] { Req(1, hold: new FileOverlapHold(Item(9), T0)), Req(2, tools: new[] { "terraform" }), Req(10, generated: true) }, Facts(), Settings);
        Assert.Equal("file-overlap", v[0].Decision.Code);
        Assert.Equal("toolchain", v[1].Decision.Code);
        Assert.True(v[2].Decision.Admitted);
    }

    [Fact]
    public void Run_HostFullIsAWaitForASlot_ForTheGeneratedItems()
    {
        var facts = Facts(hostFull: new() { ["omarchy"] = T0 });
        var v = AdmissionPass.Run(new[] { Req(1, host: "omarchy"), Req(10, generated: true) }, facts, Settings);
        Assert.Equal("host-full", v[0].Decision.Code);
        Assert.Equal("generated-last", v[1].Decision.Code);
    }

    [Fact]
    public void Run_RuntimeBudgetIsAWaitForASlot_ForTheGeneratedItems()
    {
        var facts = new AdmissionFacts(T0, new Dictionary<string, AdmissionTarget>
        {
            ["octopus"] = new(new RuntimeLoad(0, 2, 10, 10), Array.Empty<string>()),
            ["cae-job"] = new(new RuntimeLoad(0, 2, 0, 10), Array.Empty<string>()),
        });
        var v = AdmissionPass.Run(new[] { Req(1), new AdmissionRequest(Item(10), "cae-job", Generated: true) }, facts, Settings);
        Assert.Equal("runtime-budget", v[0].Decision.Code);
        Assert.Equal("generated-last", v[1].Decision.Code);
    }

    [Fact]
    public void Run_ARealItemOnARuntimeThatIsOff_DoesNotHoldBackGenerated()
    {
        var v = AdmissionPass.Run(new[] { new AdmissionRequest(Item(1), "cae-job"), Req(10, generated: true) }, Facts(), Settings);
        Assert.Equal("runtime-off", v[0].Decision.Code);
        Assert.True(v[1].Decision.Admitted);
    }

    [Fact]
    public void Run_AGeneratedItemBlockedByCapacity_IsNotCountedAsWaiting_ForOtherGeneratedItems()
    {
        // both generated items see the capacity block themselves, never generated-last (only real items hold generated ones back)
        var v = AdmissionPass.Run(new[] { Req(10, generated: true), Req(11, generated: true) }, Facts(running: 1, max: 2), Settings);
        Assert.True(v[0].Decision.Admitted);
        Assert.Equal("runtime-at-capacity", v[1].Decision.Code);
    }

    [Fact]
    public void Check_UsesTheGivenSettings()
    {
        var facts = Facts(hostFull: new() { ["omarchy"] = T0.AddSeconds(-30) });
        Assert.Equal("host-full", AdmissionPass.Check(Req(1, host: "omarchy"), facts, new AdmissionSettings(HostFullSeconds: 31)).Code);
        var hold = new FileOverlapHold(Item(9), T0.AddMinutes(-60));
        Assert.True(AdmissionPass.Check(Req(1, hold: hold), Facts(), new AdmissionSettings(MaxHoldMinutes: 60)).Admitted);
        Assert.False(AdmissionPass.Check(Req(1, hold: hold), Facts(), new AdmissionSettings(MaxHoldMinutes: 61)).Admitted);
    }

    [Fact]
    public void Run_KeepsTheGivenOrderWithinRealItems_RankingIsNotRedone()
    {
        var v = AdmissionPass.Run(new[] { Req(3), Req(1), Req(2) }, Facts(max: 1), Settings);
        Assert.Equal(new[] { Item(3), Item(1), Item(2) }, v.Select(x => x.Item));
        Assert.True(v[0].Decision.Admitted);
    }

    [Fact]
    public void Run_NoItems_NoVerdicts() => Assert.Empty(AdmissionPass.Run(Array.Empty<AdmissionRequest>(), Facts(), Settings));
}
