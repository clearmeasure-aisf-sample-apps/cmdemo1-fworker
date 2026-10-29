using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.3 with D6: the file-overlap hold in the invoke. An owner session goes from Technical Design to Development without a plan pass, so the item that holds a design slot and asks for the next phase is compared
/// with the items in flight at that request: held, its slot is given up (the session ends) and it waits like any request; otherwise the same session goes on and the item is a holder from then on.
/// </summary>
public class CapacityCarryOnTests
{
    const string Copilot = "copilot-cloud";

    [Fact]
    public async Task TheHolderOfADesignSlot_ThatAsksForDevelopment_KeepsItsSlot_WhenNothingOverlaps_AndIsInFlightFromThen()
    {
        var fx = new CapacityFixture();
        var design = (await fx.Request(1, Copilot, CapacityPhases.Design, files: ["src/a.cs"])).Messages.OfType<SlotGranted>().Single();
        var r = await fx.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"], depth: 2);
        var again = Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Equal(design.SlotId, again.SlotId);   // the same session goes on
        Assert.Equal(CapacityPhases.Develop, fx.SlotOf(1)!.Request.Phase);
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);
        Assert.True(fx.SlotOf(1)!.EnteredFlight);
        // and it holds the others up now
        var other = await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        Assert.Equal(CapacityFixture.I(1), Assert.Single(other.Messages.OfType<Held>()).By);
    }

    [Fact]
    public async Task TheHolderOfADesignSlot_ThatAsksForDevelopment_IsHeldByAnItemInFlight_ItsSlotIsGivenUpAndItWaits()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs", "src/b.cs"]);
        var design = (await fx.Request(2, Copilot, CapacityPhases.Design)).Messages.OfType<SlotGranted>().Single();
        var executions = fx.State.ExecutionsToday[Copilot];

        var r = await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/b.cs"], depth: 2);
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal(("file-overlap", CapacityFixture.I(1)), (held.Code, held.By));
        Assert.Empty(r.Messages.OfType<SlotGranted>());
        Assert.Null(fx.SlotOf(2));                                         // the session ended: the slot is free
        Assert.Equal(executions - 1, fx.State.ExecutionsToday[Copilot]);   // and its execution is given back, the next grant counts again
        Assert.Contains("acme/orders#2", fx.State.Queue.Keys);
        Assert.Equal(1, fx.State.Holds["acme/orders#2"].By);
        Assert.DoesNotContain("acme/orders#2", fx.State.InFlight.Keys);

        // the holder leaves: the item is granted a new slot, a new session
        var released = await fx.Clear(1);
        var granted = Assert.Single(released.Messages.OfType<SlotGranted>());
        Assert.Equal(2, granted.Item.Number);
        Assert.NotEqual(design.SlotId, granted.SlotId);
        Assert.Contains("acme/orders#2", fx.State.InFlight.Keys);
    }

    [Fact]
    public async Task TheHold_EndsAtMaxHoldMinutes_AndTheItemThenGoesOn()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        await fx.Request(2, Copilot, CapacityPhases.Design);
        await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        Assert.Null(fx.SlotOf(2));
        fx.Now = fx.Now.AddMinutes(121);
        var r = await fx.Wake(fx.State.WakeDue);
        Assert.Equal(2, Assert.Single(r.Messages.OfType<SlotGranted>()).Item.Number);   // (the hold ran out: the limit is on the whole wait)
    }

    [Fact]
    public async Task WithTheRuleOff_OrNoFootprint_OrAnItemAlreadyPastDesign_NothingIsCompared()
    {
        var off = new CapacityFixture(cfg => cfg["schedule"]!["fileOverlap"]!["enabled"] = false);
        await off.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        await off.Request(2, Copilot, CapacityPhases.Design);
        var r = await off.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Empty(r.Messages.OfType<Held>());

        var none = new CapacityFixture();
        await none.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        await none.Request(2, Copilot, CapacityPhases.Design);
        Assert.Empty((await none.Request(2, Copilot, CapacityPhases.Develop)).Messages.OfType<Held>());   // (no Files line: nothing to compare)

        // a holder is never held: asking for Functional Testing after Development compares nothing again
        var holder = new CapacityFixture();
        await holder.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        await holder.Request(2, Copilot, CapacityPhases.Develop, files: ["src/b.cs"]);
        await holder.Send(new FootprintChanged(CapacityFixture.I(2), holder.K(), ["src/a.cs"]));
        var verify = await holder.Request(2, Copilot, CapacityPhases.Verify, files: ["src/a.cs"], depth: 3);
        Assert.Empty(verify.Messages.OfType<Held>());
        Assert.NotNull(holder.SlotOf(2));
    }

    [Fact]
    public async Task AnItemOfAnotherRepository_OrTheSameItemAgain_IsNotAnObstacle()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, Copilot, CapacityPhases.Develop, files: ["src/a.cs"], repo: "billing");
        await fx.Request(2, Copilot, CapacityPhases.Design);
        var r = await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);
        Assert.Empty(r.Messages.OfType<Held>());   // (the same path in another repository is another file)
        var third = await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"]);   // asking again changes nothing
        Assert.Single(third.Messages.OfType<SlotGranted>());
    }
}
