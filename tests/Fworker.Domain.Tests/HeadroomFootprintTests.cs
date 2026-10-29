using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D7 specification tests (no v1 counterpart), docs/design/delivery-speed.md section 4.2 "Footprint": learned per session, the p95 of memPeakMb over at
/// least 10 sessions; until then there is no burst. The p95 is the nearest-rank value (rank ceil(0.95 n)).
/// </summary>
public sealed class HeadroomFootprintTests
{
    static int[] Samples(int n) => Enumerable.Range(1, n).Select(i => i * 100).ToArray(); // 100, 200 ... n*100

    [Fact]
    public void Defaults_AreTheConstantsOfSection42()
    {
        var d = HeadroomSettings.Defaults;
        Assert.Equal(2048, d.ReserveMb);
        Assert.Equal(4096, d.MinDiskMb);
        Assert.Equal(30, d.HostFullSeconds);
        Assert.Equal(10, d.MinFootprintSamples);
    }

    [Fact]
    public void NineSamples_NoFootprint()
        => Assert.Null(HostFootprint.P95(Samples(9)));

    [Fact]
    public void NoSamples_NoFootprint()
        => Assert.Null(HostFootprint.P95(Array.Empty<int>()));

    [Fact]
    public void TenSamples_FootprintIsTheLargest()
        => Assert.Equal(1000, HostFootprint.P95(Samples(10))); // ceil(9.5) = rank 10

    [Fact]
    public void FiftySamples_FootprintIsRank48()
        => Assert.Equal(4800, HostFootprint.P95(Samples(50))); // ceil(47.5) = rank 48

    [Fact]
    public void TwentySamples_RankIsExactlyNineteen()
        => Assert.Equal(1900, HostFootprint.P95(Samples(20))); // 0.95 * 20 = 19 exactly, no rounding up to 20

    [Fact]
    public void OrderOfTheSamplesDoesNotMatter()
    {
        var shuffled = Samples(50).Reverse().Take(25).Concat(Samples(50).Take(25)).ToArray();
        Assert.Equal(HostFootprint.P95(Samples(50)), HostFootprint.P95(shuffled));
    }

    [Fact]
    public void OneOutlierAmongFifty_IsNotTheFootprint()
    {
        var s = Enumerable.Repeat(3000, 49).Append(9000).ToArray();
        Assert.Equal(3000, HostFootprint.P95(s)); // the p95 ignores the top 2 of 50
    }

    [Fact]
    public void MinSamplesFromSettings_IsHonored()
    {
        var s = new HeadroomSettings(MinFootprintSamples: 3);
        Assert.Null(HostFootprint.P95(new[] { 1, 2 }, s));
        Assert.Equal(3, HostFootprint.P95(new[] { 1, 2, 3 }, s));
    }
}
