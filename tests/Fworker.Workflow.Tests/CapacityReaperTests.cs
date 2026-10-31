using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10j: the scheduler's reaper (ReapStale). An item whose saga never sent ClearItem keeps its place among the items in flight and holds back every item that shares a file with it; the reaper is told which items the
/// sagas have in flight and drops what the ledger keeps of any other item that has been there longer than the grace. A slot is never reaped (it ends with its session), and a request made after the read is never taken for a
/// stale one.
/// </summary>
public class CapacityReaperTests
{
    const string Copilot = "copilot-cloud";
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    static Task<CapacityResult> Dev(CapacityFixture fx, int n, params string[] files) => fx.Request(n, Copilot, CapacityPhases.Develop, files: files);
    static Task<CapacityResult> Reap(CapacityFixture fx, params int[] live) => fx.Send(new ReapStale(fx.Now, live.Select(n => CapacityFixture.I(n).ToString()).ToList(), Grace));

    [Fact]
    public async Task AnItemInFlightNoSagaHas_IsReaped_AndTheItemItHeldBackGoesInTheSameTransition()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, "src/a.cs");
        await Dev(fx, 2, "src/a.cs");                 // held by #1
        Assert.Equal(1, fx.State.Holds["acme/orders#2"].By);
        await fx.End(1);                              // the session ended; nobody cleared the item
        fx.Now = fx.Now.AddMinutes(10);
        var r = await Reap(fx, 2);
        Assert.Equal(CapacityOutcome.Applied, r.Outcome);
        Assert.Equal(2, Assert.Single(r.Messages.OfType<SlotGranted>()).Item.Number);
        Assert.DoesNotContain("acme/orders#1", fx.State.InFlight.Keys);
        Assert.Empty(fx.State.Holds);
    }

    [Fact]
    public async Task AnItemTheSagasHaveInFlight_IsKept_WhateverItsAge()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, "src/a.cs");
        await fx.End(1);
        fx.Now = fx.Now.AddHours(3);
        var r = await Reap(fx, 1);
        Assert.Equal(CapacityOutcome.NotFound, r.Outcome);
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);
    }

    [Fact]
    public async Task AnEntryYoungerThanTheGrace_IsKept_ARequestMadeAfterTheReadIsNeverTakenForAStaleOne()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, "src/a.cs");
        await fx.End(1);
        fx.Now = fx.Now.AddMinutes(2);                // #1 entered two minutes ago: inside the grace
        Assert.Equal(CapacityOutcome.NotFound, (await Reap(fx)).Outcome);
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);

        // a waiting request of an item no saga lists (its saga saved a moment after the read) is kept as well
        var held = new CapacityFixture(cfg => cfg["api"]!["copilot"]!["maxConcurrent"] = 1);
        await Dev(held, 1, "src/a.cs");
        await Dev(held, 2, "src/b.cs");               // waits for capacity
        Assert.Contains("acme/orders#2", held.State.Queue.Keys);
        held.Now = held.Now.AddMinutes(1);
        Assert.Equal(CapacityOutcome.NotFound, (await Reap(held, 1)).Outcome);
        Assert.Contains("acme/orders#2", held.State.Queue.Keys);
        // ... and dropped once it has waited longer than the grace with no saga to ask for it
        held.Now = held.Now.AddMinutes(10);
        Assert.Equal(CapacityOutcome.Applied, (await Reap(held, 1)).Outcome);
        Assert.DoesNotContain("acme/orders#2", held.State.Queue.Keys);
    }

    [Fact]
    public async Task AnItemThatHoldsASlot_IsNeverReaped_ItsSlotEndsWithItsSession_AndTheNextPassTakesTheRest()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, "src/a.cs");
        fx.Now = fx.Now.AddMinutes(10);
        Assert.Equal(CapacityOutcome.NotFound, (await Reap(fx)).Outcome);
        Assert.NotNull(fx.SlotOf(1));
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);
        await fx.End(1);
        Assert.Equal(CapacityOutcome.Applied, (await Reap(fx)).Outcome);
        Assert.Empty(fx.State.InFlight);
    }

    [Fact]
    public async Task ReapingTwice_IsReapingOnce()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, "src/a.cs");
        await fx.End(1);
        fx.Now = fx.Now.AddMinutes(10);
        Assert.Equal(CapacityOutcome.Applied, (await Reap(fx)).Outcome);
        var revision = fx.Store.RevisionOf(CapacityKeys.Factory);
        Assert.Equal(CapacityOutcome.NotFound, (await Reap(fx)).Outcome);
        Assert.Equal(revision, fx.Store.RevisionOf(CapacityKeys.Factory));   // nothing to save the second time
    }
}
