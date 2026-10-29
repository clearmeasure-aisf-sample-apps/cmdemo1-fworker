using System.Text.Json.Nodes;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.1 with D6: the file-overlap rule inside the admission chain. An item entering Development is compared with the items in flight (granted a slot for a phase past Technical Design, not yet cleared); a holder
/// is never held; the hold ends when the holder leaves, when the pull request's files no longer overlap, or at maxHoldMinutes; waiters on the same files are released in rank order. All on copilot-cloud (5 slots),
/// so that capacity is never the reason an item waits.
/// </summary>
public class CapacityOverlapTests
{
    const string Copilot = "copilot-cloud";

    static Task<CapacityResult> Dev(CapacityFixture fx, int n, string[] files, string repo = "orders", string? priority = null, string phase = CapacityPhases.Develop)
        => fx.Request(n, Copilot, phase, files: files, repo: repo, priority: priority);

    [Fact]
    public async Task AnItemEnteringDevelopment_IsHeldByAnItemInFlightThatSharesAFile_AndGoesWhenTheHolderLeaves()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs", "src/b.cs"]);
        var r = await Dev(fx, 2, ["src/b.cs"]);
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal(("file-overlap", "acme/orders#1", "file-overlap acme/orders#1"), (held.Code, held.Detail, held.Waiting));
        Assert.Equal(CapacityFixture.I(1), held.By);
        Assert.Equal(1, fx.State.Holds["acme/orders#2"].By);
        Assert.Equal([1], fx.Holders);

        var cleared = await fx.Clear(1);   // merged, parked or closed
        Assert.Equal(2, Assert.Single(cleared.Messages.OfType<SlotGranted>()).Item.Number);
        Assert.Empty(fx.State.Holds);
        Assert.Contains("acme/orders#2", fx.State.InFlight.Keys);   // and it is a holder now
    }

    [Fact]
    public async Task ASessionOfTechnicalDesign_IsNotCompared_ItIsNotPastDesignYet()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        var r = await fx.Request(2, Copilot, CapacityPhases.Design, files: ["src/a.cs"]);
        Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.DoesNotContain("acme/orders#2", fx.State.InFlight.Keys);   // and a design session holds nobody up
        var third = await Dev(fx, 3, ["src/a.cs"]);
        Assert.Equal(CapacityFixture.I(1), Assert.Single(third.Messages.OfType<Held>()).By);   // (held by the item in flight, not by the design session)
    }

    [Fact]
    public async Task AnItemInFlight_IsNeverHeld_EvenWhenAnotherOneNowSharesItsFiles()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 2, ["src/b.cs"]);
        await fx.Send(new FootprintChanged(CapacityFixture.I(2), fx.K(), ["src/a.cs"]));   // its pull request now touches #1's file
        await fx.End(1);
        var r = await Dev(fx, 1, ["src/a.cs"]);   // #1 asks for its next session
        Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Empty(r.Messages.OfType<Held>());
        Assert.Empty(fx.State.Holds);
    }

    [Fact]
    public async Task WaitersOnTheSameFiles_AreReleasedInRankOrder_OneAtATime()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 3, ["src/a.cs"]);                      // none
        await Dev(fx, 2, ["src/a.cs"], priority: "high");   // asked later, ranks first
        Assert.Equal(["file-overlap acme/orders#1", "file-overlap acme/orders#1"], new[] { fx.Waiting(2), fx.Waiting(3) });

        var r = await fx.Clear(1);
        Assert.Equal([2], r.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));   // the higher rank goes
        var again = Assert.Single(r.Messages.OfType<Held>());                             // the other is now held by it
        Assert.Equal((3, 2), (again.Item.Number, again.By!.Value.Number));

        var last = await fx.Clear(2);
        Assert.Equal([3], last.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));
    }

    [Fact]
    public async Task AHold_EndsAtMaxHoldMinutes_AndTheTimerIsAskedForIt()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 2, ["src/a.cs"]);
        Assert.Equal(CapacityFixture.T0.AddMinutes(120), fx.State.Holds["acme/orders#2"].Until);

        // a look shortly before the end of the hold asks for the next one at the end of the hold, not ten minutes later
        fx.Now = CapacityFixture.T0.AddMinutes(115);
        var near = await fx.Wake(fx.State.WakeDue);
        Assert.Equal(CapacityFixture.T0.AddMinutes(120), near.WakeAt);
        Assert.Empty(near.Messages);

        fx.Now = CapacityFixture.T0.AddMinutes(120);
        var r = await fx.Wake(near.WakeAt);
        Assert.Equal(2, Assert.Single(r.Messages.OfType<SlotGranted>()).Item.Number);   // at exactly maxHoldMinutes the hold has ended, though #1 still shares the file
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);
    }

    [Fact]
    public async Task TheHoldKeepsItsClock_WhileTheItemWaits()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 2, ["src/a.cs"]);
        var since = fx.State.Holds["acme/orders#2"].Since;
        fx.Now = fx.Now.AddMinutes(30);
        await Dev(fx, 3, ["src/a.cs"]);   // a third item waits for the same files
        await fx.Request(2, Copilot, CapacityPhases.Develop, files: ["src/a.cs"], requested: CapacityFixture.T0);
        Assert.Equal(since, fx.State.Holds["acme/orders#2"].Since);   // asking again does not start the hold again
    }

    [Fact]
    public async Task TheFilesOfThePullRequest_ReplaceTheDesign_AndAnEndedOverlapReleasesTheWaiter()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 2, ["src/a.cs"]);
        var r = await fx.Send(new FootprintChanged(CapacityFixture.I(1), fx.K(), ["src/z.cs"]));   // what was pushed is what the item touches
        Assert.Equal(2, Assert.Single(r.Messages.OfType<SlotGranted>()).Item.Number);

        // an empty list is no evidence: the footprint stays; and an item that is not in flight has none to change
        var kept = await fx.Send(new FootprintChanged(CapacityFixture.I(1), fx.K(), []));
        Assert.Equal(CapacityOutcome.Applied, kept.Outcome);
        Assert.Equal(["src/z.cs"], fx.State.InFlight["acme/orders#1"].Footprint.Entries);
        Assert.Equal(CapacityOutcome.NotFound, (await fx.Send(new FootprintChanged(CapacityFixture.I(8), fx.K(), ["x"]))).Outcome);
    }

    [Fact]
    public async Task GlobsOverlapTheFilesTheyCover_AndTheIgnoredPathsNeverHold()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/**"]);
        await Dev(fx, 2, ["src/x/y.cs"]);
        Assert.Equal("file-overlap acme/orders#1", fx.Waiting(2));
        await Dev(fx, 3, ["docs/readme.md"]);   // elsewhere
        Assert.Contains(3, fx.Holders);

        var lockfile = new CapacityFixture();
        await Dev(lockfile, 1, ["package-lock.json"]);
        var r = await Dev(lockfile, 2, ["package-lock.json"]);   // schedule.overlapIgnore: it holds nobody
        Assert.Single(r.Messages.OfType<SlotGranted>());
    }

    [Fact]
    public async Task APathInAnotherRepository_IsAnotherFile()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        var r = await Dev(fx, 5, ["src/a.cs"], repo: "billing");
        Assert.Single(r.Messages.OfType<SlotGranted>());
        var same = await Dev(fx, 2, ["src/a.cs"]);
        Assert.Equal(CapacityFixture.I(1), Assert.Single(same.Messages.OfType<Held>()).By);
    }

    [Fact]
    public async Task WithTheRuleOff_NothingIsHeld()
    {
        var fx = new CapacityFixture(c => c["schedule"]!["fileOverlap"]!["enabled"] = false);
        await Dev(fx, 1, ["src/a.cs"]);
        var r = await Dev(fx, 2, ["src/a.cs"]);
        Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Empty(fx.State.Holds);
    }

    [Fact]
    public async Task AnItemWithNoFootprint_IsNeverHeld_AndHoldsNobody()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, Copilot, CapacityPhases.Develop);   // no Files line, no pull request yet
        var r = await Dev(fx, 2, ["src/a.cs"]);
        Assert.Single(r.Messages.OfType<SlotGranted>());
    }

    [Fact]
    public async Task AHeldItemThatIsCleared_TakesItsHoldWithIt()
    {
        var fx = new CapacityFixture();
        await Dev(fx, 1, ["src/a.cs"]);
        await Dev(fx, 2, ["src/a.cs"]);
        Assert.Contains("acme/orders#2", fx.State.Holds.Keys);
        await fx.Clear(2);
        Assert.Empty(fx.State.Holds);
        Assert.Empty(fx.State.Queue);
    }

    [Fact]
    public void TheHoldsOfTheChainAndOfTheRule_EndAtTheSameTime()
    {
        // the rule (D6) stamps Until = Since + maxHoldMinutes; the chain (D1) reads the hold as expired after the same minutes: one setting, one end
        var settings = CapacitySettings.From((JsonObject)JsonNode.Parse(CapacityFixture.DefaultConfig)!, "tdd");
        Assert.Equal(120, settings.Overlap.MaxHoldMinutes);
        Assert.Equal(120, settings.Admission.MaxHoldMinutes);
        Assert.True(AdmissionRules.HoldExpired(new FileOverlapHold(CapacityFixture.I(1), CapacityFixture.T0), CapacityFixture.T0.AddMinutes(120), settings.Admission.MaxHoldMinutes));
    }
}
