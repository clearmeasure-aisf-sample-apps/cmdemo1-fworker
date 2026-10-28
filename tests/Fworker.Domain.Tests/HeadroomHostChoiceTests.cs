using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D7 specification tests, delivery-speed.md section 4.2: perHostCapacity is the guaranteed number per host with no memory test at or below it;
/// above it (up to perHostBurst) only with headroom; among hosts with a slot the one with most headroom; a host that answered host-full is blocked for
/// hostFullSeconds (a non-failure).
/// </summary>
public sealed class HeadroomHostChoiceTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly int[] TenSessions = Enumerable.Repeat(3000, 10).ToArray();
    static readonly HostMetrics Roomy = new(MemAvailableMb: 8000, Cpus: 4, Load1: 0, DiskFreeMb: 50_000);
    static readonly HostMetrics NoMemory = new(MemAvailableMb: 0, Cpus: 4, Load1: 99, DiskFreeMb: 0);

    static HostState Host(string name, int running, HostMetrics? m = null, int capacity = 2, int burst = 3, int[]? samples = null, DateTimeOffset? full = null)
        => new(name, capacity, burst, running, m ?? Roomy, samples ?? TenSessions, full);

    static SlotKind Slot(HostState h) => HostChoiceRule.SlotFor(h, containerCpus: 2, Now);

    // ---- capacity floor: no memory (or any headroom) test at or below perHostCapacity

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // the second container is still at capacity (2)
    public void AtOrBelowCapacity_GuaranteedWithoutAnyHeadroomTest(int running)
        => Assert.Equal(SlotKind.Guaranteed, Slot(Host("a", running, NoMemory, samples: Array.Empty<int>())));

    [Fact]
    public void FirstSlotAboveCapacity_NeedsHeadroom()
    {
        Assert.Equal(SlotKind.Burst, Slot(Host("a", running: 2)));
        Assert.Equal(SlotKind.None, Slot(Host("a", running: 2, NoMemory)));
    }

    [Fact]
    public void AboveCapacity_NoFootprintYet_NoBurst()
        => Assert.Equal(SlotKind.None, Slot(Host("a", running: 2, samples: TenSessions.Take(9).ToArray())));

    [Fact]
    public void BurstDefault_EqualToCapacity_MeansNoBurstEver()
        => Assert.Equal(SlotKind.None, Slot(Host("a", running: 2, burst: 2)));

    [Theory]
    [InlineData(0, SlotKind.Guaranteed)]
    [InlineData(1, SlotKind.Guaranteed)] // capacity 2, burst 1: the burst is below the capacity and does not lower it
    [InlineData(2, SlotKind.None)]       // at the effective limit max(2, 1) = 2
    public void BurstBelowCapacity_IsIgnored_TheLimitIsTheLarger(int running, SlotKind expected)
        => Assert.Equal(expected, Slot(Host("a", running, capacity: 2, burst: 1)));

    [Fact]
    public void AtTheBurstLimit_NoSlot()
        => Assert.Equal(SlotKind.None, Slot(Host("a", running: 3, burst: 3)));

    [Fact]
    public void BurstSlotUsesTheHostsOwnFootprint()
    {
        // 8000 - 2048 = 5952 available for a session: a 5953 MB footprint does not fit, 5952 does.
        Assert.Equal(SlotKind.None, Slot(Host("a", running: 2, samples: Enumerable.Repeat(5953, 10).ToArray())));
        Assert.Equal(SlotKind.Burst, Slot(Host("a", running: 2, samples: Enumerable.Repeat(5952, 10).ToArray())));
    }

    // ---- host-full block

    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, false)] // blocked for 30 s, open again at the second it ends
    [InlineData(31, false)]
    public void HostFull_BlocksTheHostForHostFullSeconds(int secondsAgo, bool blocked)
    {
        var h = Host("a", running: 0, full: Now.AddSeconds(-secondsAgo));
        Assert.Equal(blocked, HostChoiceRule.IsBlocked(h, Now));
        Assert.Equal(blocked ? SlotKind.None : SlotKind.Guaranteed, Slot(h));
    }

    [Fact]
    public void NeverHostFull_NotBlocked()
        => Assert.False(HostChoiceRule.IsBlocked(Host("a", 0), Now));

    [Fact]
    public void HostFullSecondsFromSettings_IsHonored()
    {
        var h = Host("a", running: 0, full: Now.AddSeconds(-5));
        Assert.False(HostChoiceRule.IsBlocked(h, Now, new HeadroomSettings(HostFullSeconds: 5)));
        Assert.True(HostChoiceRule.IsBlocked(h, Now, new HeadroomSettings(HostFullSeconds: 6)));
    }

    // ---- host choice

    static HostChoice? Pick(params HostState[] hosts) => HostChoiceRule.Pick(hosts, containerCpus: 2, Now);

    [Fact]
    public void NoHosts_Null()
        => Assert.Null(Pick());

    [Fact]
    public void AllHostsFullOrBlocked_Null()
        => Assert.Null(Pick(Host("a", running: 3), Host("b", running: 0, full: Now)));

    [Fact]
    public void MostHeadroomWins()
    {
        var small = Host("a", 0, new HostMetrics(4000, 4, 0, 50_000));
        var big = Host("b", 0, new HostMetrics(9000, 4, 0, 50_000));
        Assert.Equal(new HostChoice("b", SlotKind.Guaranteed), Pick(small, big));
        Assert.Equal(new HostChoice("b", SlotKind.Guaranteed), Pick(big, small));
    }

    [Fact]
    public void GuaranteedSlotBeatsABurstOnAHostWithMoreMemory()
    {
        var bursting = Host("big", running: 2, new HostMetrics(30_000, 16, 0, 50_000));
        var guaranteed = Host("small", running: 1, new HostMetrics(1000, 4, 0, 50_000));
        Assert.Equal(new HostChoice("small", SlotKind.Guaranteed), Pick(bursting, guaranteed));
    }

    [Fact]
    public void WithNoGuaranteedSlot_TheBurstHostWithMostHeadroomWins()
    {
        var a = Host("a", running: 2, new HostMetrics(7000, 4, 0, 50_000));
        var b = Host("b", running: 2, new HostMetrics(9000, 4, 0, 50_000));
        var noRoom = Host("c", running: 2, NoMemory);
        Assert.Equal(new HostChoice("b", SlotKind.Burst), Pick(a, noRoom, b));
    }

    [Fact]
    public void Tie_FewerRunningContainersThenLowerName()
    {
        var fewer = Host("z", running: 0);
        var more = Host("a", running: 1);
        Assert.Equal("z", Pick(more, fewer)!.Name);

        var x = Host("beta", running: 1);
        var y = Host("alpha", running: 1);
        Assert.Equal("alpha", Pick(x, y)!.Name);
        Assert.Equal("alpha", Pick(y, x)!.Name);
    }

    [Fact]
    public void ABlockedHostIsSkippedEvenWithMostHeadroom()
    {
        var blocked = Host("big", 0, new HostMetrics(30_000, 16, 0, 50_000), full: Now.AddSeconds(-1));
        var open = Host("small", 0, new HostMetrics(3000, 4, 0, 50_000));
        Assert.Equal("small", Pick(blocked, open)!.Name);
    }
}
