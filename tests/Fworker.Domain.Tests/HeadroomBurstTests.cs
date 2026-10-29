using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D7 specification tests, delivery-speed.md section 4.2 "Plan (gateway)": a burst slot is admitted when memAvailableMb - reserveMb >= footprint,
/// load1 + containerCpus <= cpus and diskFreeMb >= minDiskMb. Every inequality is tested at the value, one below and one above.
/// </summary>
public sealed class HeadroomBurstTests
{
    const int Footprint = 3000;
    const double ContainerCpus = 2;
    // memAvailable at the boundary: footprint + reserve (2048) = 5048.
    static HostMetrics Ok => new(MemAvailableMb: 5048, Cpus: 4, Load1: 2.0, DiskFreeMb: 4096);

    static BurstVerdict Check(HostMetrics h, int? fp = Footprint) => BurstAdmission.Check(h, fp, ContainerCpus);

    [Fact]
    public void AllAtTheirBoundaries_Admitted()
        => Assert.Equal(BurstVerdict.Admitted, Check(Ok));

    [Fact]
    public void NoFootprint_NeverAdmitted_EvenWithPlentyOfEverything()
        => Assert.Equal(BurstVerdict.NoFootprint, Check(new HostMetrics(100_000, 64, 0, 1_000_000), fp: null));

    [Theory]
    [InlineData(5047, BurstVerdict.Memory)]   // one below
    [InlineData(5048, BurstVerdict.Admitted)] // equal admits
    [InlineData(5049, BurstVerdict.Admitted)] // one above
    public void Memory_AvailableMinusReserveAgainstFootprint(int memAvailable, BurstVerdict expected)
        => Assert.Equal(expected, Check(Ok with { MemAvailableMb = memAvailable }));

    [Theory]
    [InlineData(2.1, BurstVerdict.Cpu)]       // 2.1 + 2 > 4
    [InlineData(2.0, BurstVerdict.Admitted)]  // 2 + 2 = 4 admits
    [InlineData(1.9, BurstVerdict.Admitted)]
    public void Cpu_Load1PlusContainerCpusAgainstCpus(double load1, BurstVerdict expected)
        => Assert.Equal(expected, Check(Ok with { Load1 = load1 }));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownOrInfiniteLoad_FailsClosed(double load1)
        => Assert.Equal(BurstVerdict.Cpu, Check(Ok with { Load1 = load1 }));

    [Fact]
    public void NaNContainerCpus_FailsClosed()
        => Assert.Equal(BurstVerdict.Cpu, BurstAdmission.Check(Ok, Footprint, double.NaN));

    [Theory]
    [InlineData(4095, BurstVerdict.Disk)]
    [InlineData(4096, BurstVerdict.Admitted)]
    [InlineData(4097, BurstVerdict.Admitted)]
    public void Disk_FreeAgainstMinimum(int diskFree, BurstVerdict expected)
        => Assert.Equal(expected, Check(Ok with { DiskFreeMb = diskFree }));

    [Fact]
    public void FirstFailingCheckIsReported_MemoryBeforeCpuBeforeDisk()
    {
        var all = new HostMetrics(0, 4, 4.0, 0);
        Assert.Equal(BurstVerdict.Memory, Check(all));
        Assert.Equal(BurstVerdict.Cpu, Check(all with { MemAvailableMb = 5048 }));
        Assert.Equal(BurstVerdict.Disk, Check(all with { MemAvailableMb = 5048, Load1 = 0 }));
    }

    [Fact]
    public void MemoryAvailableBelowReserve_NotAdmitted()
        => Assert.Equal(BurstVerdict.Memory, Check(Ok with { MemAvailableMb = 0 }, fp: 1));

    [Fact]
    public void ZeroFootprint_NeedsOnlyTheReserve()
    {
        Assert.Equal(BurstVerdict.Admitted, Check(Ok with { MemAvailableMb = 2048 }, fp: 0));
        Assert.Equal(BurstVerdict.Memory, Check(Ok with { MemAvailableMb = 2047 }, fp: 0));
    }

    [Fact]
    public void CustomSettings_ChangeTheThresholds()
    {
        var s = new HeadroomSettings(ReserveMb: 1000, MinDiskMb: 100);
        var h = new HostMetrics(MemAvailableMb: 4000, Cpus: 4, Load1: 0, DiskFreeMb: 100);
        Assert.Equal(BurstVerdict.Admitted, BurstAdmission.Check(h, 3000, ContainerCpus, s));
        Assert.Equal(BurstVerdict.Memory, BurstAdmission.Check(h with { MemAvailableMb = 3999 }, 3000, ContainerCpus, s));
        Assert.Equal(BurstVerdict.Disk, BurstAdmission.Check(h with { DiskFreeMb = 99 }, 3000, ContainerCpus, s));
    }
}
