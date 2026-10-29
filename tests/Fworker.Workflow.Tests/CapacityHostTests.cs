using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.1 with D7: the host choice and the host-full outcome. Guaranteed slots (up to perHostCapacity) need no headroom test; burst slots (above it, up to perHostBurst) need a footprint of ten measured sessions and
/// memory, cpu and disk headroom; a host that answered host-full is blocked for hostFullSeconds and that is no failure; a report that is too old gives no burst. Octopus: 2 guaranteed slots per host, 3 with headroom.
/// </summary>
public class CapacityHostTests
{
    const string Octopus = "octopus";

    static IEnumerable<string?> HostsOf(IEnumerable<SlotGranted> granted) => granted.Select(g => g.Host);

    [Fact]
    public async Task TheHostWithTheMostHeadroomGoesFirst_GuaranteedSlotsBeforeBurst_AndEachSlotKnowsItsHost()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1", metrics: new HostMetrics(8000, 4, 0.5, 50000));
        await fx.Report("h2", metrics: new HostMetrics(6000, 4, 0.5, 50000));
        for (var n = 1; n <= 4; n++) await fx.Request(n, Octopus);
        // h1 has the more headroom: its two guaranteed slots go first; its third would be a burst (no footprint yet), so h2's guaranteed slots follow
        Assert.Equal(["h1", "h1", "h2", "h2"], HostsOf(fx.Granted));
        Assert.All(fx.Granted, g => Assert.Equal("guaranteed", g.Kind));
        Assert.Equal(4, fx.State.SlotsOf(Octopus));
    }

    [Fact]
    public async Task AllGuaranteedSlotsAreTaken_AndThereIsNoFootprint_TheItemWaitsForHeadroom()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Request(1, Octopus); await fx.Request(2, Octopus);
        var r = await fx.Request(3, Octopus);   // the runtime allows 6, the host's third slot would be a burst
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal("host-headroom", held.Waiting);
        Assert.Equal([1, 2], fx.Holders);
        // a slot freed on the host is the waiting item's
        var freed = await fx.End(1);
        Assert.Equal(3, Assert.Single(freed.Messages.OfType<SlotGranted>()).Item.Number);
    }

    [Fact]
    public async Task APlaceWithNoHostReported_WaitsForOne()
    {
        var fx = new CapacityFixture();
        var r = await fx.Request(1, Octopus);
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal(("host-headroom", "no host reported"), (held.Code, held.Detail));
        var reported = await fx.Report("h1");   // the first report is a trigger to plan
        Assert.Equal("h1", Assert.Single(reported.Messages.OfType<SlotGranted>()).Host);
    }

    [Fact]
    public async Task ABurstSlot_NeedsTenMeasuredSessionsAndTheHeadroomOfTheHost()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1", metrics: new HostMetrics(8000, 4, 0.5, 50000));
        await fx.Warm("h1", peak: 1000, sessions: 9);                  // nine sessions: no footprint yet
        await fx.Request(1, Octopus); await fx.Request(2, Octopus);
        Assert.Equal("host-headroom", Assert.Single((await fx.Request(3, Octopus)).Messages.OfType<Held>()).Waiting);
        await fx.Clear(3);
        await fx.End(1); await fx.End(2);
        Assert.Empty(fx.State.Slots);
        await fx.Warm("h1", peak: 1000, sessions: 1);                  // the tenth: the footprint is 1000 MB

        await fx.Request(4, Octopus); await fx.Request(5, Octopus);
        var g = Assert.Single((await fx.Request(6, Octopus)).Messages.OfType<SlotGranted>());
        Assert.Equal(("h1", "burst"), (g.Host, g.Kind));
        // the host's limit is the burst limit, 3
        Assert.Equal("host-headroom", Assert.Single((await fx.Request(7, Octopus)).Messages.OfType<Held>()).Waiting);
    }

    [Fact]
    public async Task ABurstSlotIsRefused_WhenMemoryCpuOrDiskAreShort_AndAdmittedAtExactlyTheHeadroom()
    {
        foreach (var (name, metrics) in new[]
        {
            ("memory", new HostMetrics(3000, 4, 0.5, 50000)),   // 3000 - 2048 reserve < 1000
            ("cpu", new HostMetrics(8000, 4, 2.5, 50000)),      // 2.5 + 2 cpus > 4
            ("disk", new HostMetrics(8000, 4, 0.5, 4000)),      // < 4096 MB free
        })
        {
            var fx = new CapacityFixture();
            await fx.Report("h1");
            await fx.Warm("h1");
            fx.Now = fx.Now.AddSeconds(1);
            await fx.Report("h1", metrics: metrics);
            await fx.Request(1, Octopus); await fx.Request(2, Octopus);
            await fx.Request(3, Octopus);
            Assert.True("host-headroom" == fx.Waiting(3), name);
        }

        // the same host with exactly the headroom (equality admits) gives the burst slot
        var ok = new CapacityFixture();
        await ok.Report("h1");
        await ok.Warm("h1");
        ok.Now = ok.Now.AddSeconds(1);
        await ok.Report("h1", metrics: new HostMetrics(3048, 4, 2.0, 4096));
        await ok.Request(1, Octopus); await ok.Request(2, Octopus);
        Assert.Equal("burst", Assert.Single((await ok.Request(3, Octopus)).Messages.OfType<SlotGranted>()).Kind);
    }

    [Fact]
    public async Task AReportThatIsTooOld_SaysNothingOfTheHeadroom_TheHostGivesItsGuaranteedSlotsOnly()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Warm("h1");
        fx.Now = fx.Now.AddMinutes(11);   // the report is 11 minutes old, the limit is 10
        await fx.Request(1, Octopus); await fx.Request(2, Octopus);
        Assert.Equal("host-headroom", Assert.Single((await fx.Request(3, Octopus)).Messages.OfType<Held>()).Waiting);
        await fx.Report("h1");   // a fresh report brings the burst back
        Assert.Equal("burst", fx.Granted.Last().Kind);
    }

    [Fact]
    public async Task AHostThatHasNotReportedForThreeTimesTheLimit_IsTakenForGone_AndOffersNothing_UntilItReportsAgain()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Report("h2", metrics: new HostMetrics(1000, 4, 0.5, 50000), at: fx.Now);
        fx.Now = fx.Now.AddMinutes(20);   // 20 minutes: too old for a burst, not yet gone (30)
        await fx.Report("h2");
        var a = await fx.Request(1, Octopus);
        Assert.Equal("h1", Assert.Single(a.Messages.OfType<SlotGranted>()).Host);   // (h1 still has the more headroom in its old report)
        fx.Now = fx.Now.AddMinutes(11);   // h1 last reported 31 minutes ago, h2 11
        var b = await fx.Request(2, Octopus);
        Assert.Equal("h2", Assert.Single(b.Messages.OfType<SlotGranted>()).Host);   // h1 is gone, though it has a free guaranteed slot
        var c = await fx.Request(3, Octopus);
        Assert.Equal("h2", Assert.Single(c.Messages.OfType<SlotGranted>()).Host);
        var d = await fx.Request(4, Octopus);
        Assert.Equal("host-headroom", Assert.Single(d.Messages.OfType<Held>()).Waiting);
        var back = await fx.Report("h1");
        Assert.Equal("h1", Assert.Single(back.Messages.OfType<SlotGranted>()).Host);
        Assert.Equal(2, fx.SlotsOnHost("h1"));   // the old slot and the new one
    }

    [Fact]
    public async Task TheRunningSessionsOfAHost_AreTheLedgerPlusWhatTheHostRunsOutsideIt()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1", running: 2);   // two sessions the scheduler did not grant (v1's, or adopted after a restart)
        Assert.Equal(2, fx.State.Hosts["h1"].Outside);
        var r = await fx.Request(1, Octopus);
        Assert.Equal("host-headroom", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        fx.Now = fx.Now.AddSeconds(1);
        var freed = await fx.Report("h1", running: 1);   // one of them has ended
        Assert.Equal(1, fx.State.Hosts["h1"].Outside);
        Assert.Equal("h1", Assert.Single(freed.Messages.OfType<SlotGranted>()).Host);
        // a report counts only what is beyond the ledger's own slots: two containers, one of them the scheduler's
        fx.Now = fx.Now.AddSeconds(1);
        await fx.Report("h1", running: 2);
        Assert.Equal(1, fx.State.Hosts["h1"].Outside);
    }

    [Fact]
    public async Task AReportOlderThanTheLastOne_IsIgnored()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1", running: 1, at: fx.Now);
        var stale = await fx.Report("h1", running: 0, at: fx.Now.AddSeconds(-30));
        Assert.Equal(CapacityOutcome.Stale, stale.Outcome);
        Assert.Equal(1, fx.State.Hosts["h1"].Outside);
    }

    [Fact]
    public async Task TheRuntimesLimit_CountsTheContainersOfAllItsHosts()
    {
        var fx = new CapacityFixture(c => c["runtimes"]!["octopus"]!["maxConcurrent"] = 3);
        await fx.Report("h1"); await fx.Report("h2");
        for (var n = 1; n <= 3; n++) await fx.Request(n, Octopus);
        var r = await fx.Request(4, Octopus);
        Assert.Equal("runtime-at-capacity", Assert.Single(r.Messages.OfType<Held>()).Waiting);   // (the day budget and the hosts have room; the runtime's own limit does not)
    }

    // ---- host-full ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HostFull_IsAWait_NotAFailure_TheSlotIsFreed_TheDaysExecutionIsNotUsed_AndTheItemGoesToAnotherHost()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1", metrics: new HostMetrics(8000, 4, 0.5, 50000));
        await fx.Report("h2", metrics: new HostMetrics(6000, 4, 0.5, 50000));
        var first = await fx.Request(1, Octopus);
        var slot = Assert.Single(first.Messages.OfType<SlotGranted>());
        Assert.Equal("h1", slot.Host);

        var r = await fx.Refuse(1, slot.SlotId, AdmissionCodes.HostFull);   // start-worker.sh counted a container that was still exiting
        var again = Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Equal(("h2", "s2"), (again.Host, again.SlotId));   // the same plan pass: h1 is blocked
        Assert.Empty(r.Messages.OfType<Held>());
        Assert.Equal(1, fx.State.ExecutionsToday[Octopus]);        // the first start never ran: only one execution of the day is used
        Assert.Equal(CapacityFixture.T0, fx.State.Hosts["h1"].HostFullAt);
        Assert.Null(fx.State.Hosts["h2"].HostFullAt);
        Assert.False(AdmissionRules.CountsAsFailure(AdmissionCodes.HostFull));
    }

    [Fact]
    public async Task WhenEveryHostIsFull_TheItemWaitsAsHostFull_AndIsPlannedAgainWhenTheBlockEnds()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        var slot = Assert.Single((await fx.Request(1, Octopus)).Messages.OfType<SlotGranted>());
        var r = await fx.Refuse(1, slot.SlotId, AdmissionCodes.HostFull);
        Assert.Equal("host-full", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        Assert.Equal(0, fx.State.ExecutionsToday[Octopus]);
        Assert.Empty(fx.State.Slots);
        Assert.Equal(CapacityFixture.T0.AddSeconds(30), r.WakeAt);   // the plan runs again when the block ends

        // other requests meanwhile wait for the same reason; nothing is granted on the blocked host
        fx.Now = CapacityFixture.T0.AddSeconds(29);
        var other = await fx.Request(2, Octopus);
        Assert.Equal("host-full", Assert.Single(other.Messages.OfType<Held>()).Waiting);
        Assert.Empty(fx.State.Slots);

        // at exactly hostFullSeconds the host is open again: the item (older, same rank) has the slot, the other one the next
        fx.Now = CapacityFixture.T0.AddSeconds(30);
        var woke = await fx.Wake(r.WakeAt);
        Assert.Equal([1, 2], woke.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));
        Assert.Equal(["h1", "h1"], HostsOf(woke.Messages.OfType<SlotGranted>()));
    }

    [Fact]
    public async Task HostFull_PutsTheItemBackWithItsRank_NotBehindTheOthers()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        var slot = Assert.Single((await fx.Request(1, Octopus, priority: "top")).Messages.OfType<SlotGranted>());
        await fx.Request(2, Octopus);                  // second guaranteed slot of h1
        await fx.Request(3, Octopus);                  // waits (no burst)
        await fx.Refuse(1, slot.SlotId, AdmissionCodes.HostFull);
        Assert.Equal("top", fx.State.Queue["acme/orders#1"].Rank.Priority);
        Assert.Contains("acme/orders#3", fx.State.Queue.Keys);
        fx.Now = fx.Now.AddSeconds(30);
        var woke = await fx.Wake();
        Assert.Equal([1], woke.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));   // (one guaranteed slot is free on h1)
    }

    [Fact]
    public async Task AnotherRefusalOfTheStart_FreesTheSlotAndDropsTheRequest_NoHostIsBlocked_AndTheExecutionStaysUsed()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        var slot = Assert.Single((await fx.Request(1, Octopus)).Messages.OfType<SlotGranted>());
        var r = await fx.Refuse(1, slot.SlotId, "launch-failed");
        Assert.Empty(r.Messages);
        Assert.Empty(fx.State.Slots); Assert.Empty(fx.State.Queue);
        Assert.Null(fx.State.Hosts["h1"].HostFullAt);
        Assert.Equal(1, fx.State.ExecutionsToday[Octopus]);
        Assert.True(AdmissionRules.CountsAsFailure("launch-failed"));
        // the host is free for the next one at once
        Assert.Equal("h1", Assert.Single((await fx.Request(2, Octopus)).Messages.OfType<SlotGranted>()).Host);
    }

    [Fact]
    public async Task ARefusalOfASlotThatIsNotTheItemsCurrentOne_ChangesNothing()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Request(1, Octopus);
        Assert.Equal(CapacityOutcome.Stale, (await fx.Refuse(1, "s9", AdmissionCodes.HostFull)).Outcome);
        Assert.Equal(CapacityOutcome.NotFound, (await fx.Refuse(2, "s1", AdmissionCodes.HostFull)).Outcome);
        Assert.Equal([1], fx.Holders);
        Assert.Null(fx.State.Hosts["h1"].HostFullAt);
    }

    [Fact]
    public async Task AHostFullStartOfAnItemEnteringDevelopment_TakesBackItsPlaceInFlight()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        var slot = Assert.Single((await fx.Request(1, Octopus, CapacityPhases.Develop, files: ["src/a.cs"])).Messages.OfType<SlotGranted>());
        Assert.Contains("acme/orders#1", fx.State.InFlight.Keys);
        await fx.Refuse(1, slot.SlotId, AdmissionCodes.HostFull);
        Assert.Empty(fx.State.InFlight);   // its session never began: it is no holder (it waits again, and is compared again when its turn comes)
        Assert.Contains("acme/orders#1", fx.State.Queue.Keys);
    }

    [Fact]
    public async Task TheSessionsEnd_RecordsTheMemoryPeakAsASampleOfTheHost_AndKeepsTheLatestHundred()
    {
        var fx = new CapacityFixture(c => c["runtimes"]!["octopus"]!["maxExecutionsPerDay"] = 1000);
        await fx.Report("h1");
        for (var i = 1; i <= 105; i++)
        {
            var g = Assert.Single((await fx.Request(i, Octopus)).Messages.OfType<SlotGranted>());
            await fx.End(i, g.SlotId, 100 + i);
        }
        var samples = fx.State.Hosts["h1"].Samples;
        Assert.Equal(CapacitySettings.DefaultMaxFootprintSamples, samples.Count);
        Assert.Equal((106, 205), (samples.First(), samples.Last()));
    }
}
