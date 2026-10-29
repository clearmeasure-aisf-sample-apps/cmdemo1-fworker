namespace Fworker.Domain;

// Host headroom and footprint (D7), docs/design/delivery-speed.md section 4.2 (two-level admission). v1 has no such rule: its plan picks the host with
// the fewest workers and never looks at memory, so these are specification tests, not parity tests. Pure values: no clock (the caller passes "now"), no I/O,
// no config. Nothing calls this yet; W7 wires it into the scheduler and W7.4 delivers the metrics the host report carries.

/// <summary>
/// The defaults of section 4.2 as constants. They become config keys with W7.0; until then nothing reads config here. <see cref="Defaults"/> is what a
/// caller without settings uses.
/// </summary>
public sealed record HeadroomSettings(
    int ReserveMb = HeadroomSettings.DefaultReserveMb,
    int MinDiskMb = HeadroomSettings.DefaultMinDiskMb,
    int HostFullSeconds = HeadroomSettings.DefaultHostFullSeconds,
    int MinFootprintSamples = HeadroomSettings.DefaultMinFootprintSamples)
{
    /// <summary>Memory kept free for the host itself on top of the footprint of the new session.</summary>
    public const int DefaultReserveMb = 2048;
    /// <summary>Free disk a burst slot needs.</summary>
    public const int DefaultMinDiskMb = 4096;
    /// <summary>How long a host that answered host-full is blocked from new starts (section 4.2, "Start (host, authoritative)").</summary>
    public const int DefaultHostFullSeconds = 30;
    /// <summary>Sessions needed before a footprint exists; with fewer there is no burst.</summary>
    public const int DefaultMinFootprintSamples = 10;

    public static readonly HeadroomSettings Defaults = new();
}

public static class HostFootprint
{
    /// <summary>
    /// The footprint of a session: the p95 of the <c>memPeakMb</c> samples (one per finished session), by the nearest-rank method: the sample at rank
    /// ceil(0.95 * n) of the sorted samples. It is always an observed value (never interpolated below the real peak), so with 10 samples it is the largest.
    /// Null when there are fewer than <see cref="HeadroomSettings.MinFootprintSamples"/> samples: until then there is no burst.
    /// </summary>
    public static int? P95(IReadOnlyCollection<int> memPeakMbSamples, HeadroomSettings? settings = null)
    {
        var s = settings ?? HeadroomSettings.Defaults;
        if (memPeakMbSamples.Count < s.MinFootprintSamples || memPeakMbSamples.Count == 0) return null;
        var sorted = memPeakMbSamples.OrderBy(x => x).ToArray();
        // 95 * n / 100 in integers: ceil(0.95 * n) without floating point rounding (0.95 * 20 is 19.000000000000004 in doubles).
        var rank = (95 * sorted.Length + 99) / 100;
        return sorted[rank - 1];
    }
}

/// <summary>What the host report says about a host now (<c>reconcile-hosts.sh</c>, section 4.2 "Report").</summary>
public sealed record HostMetrics(int MemAvailableMb, int Cpus, double Load1, int DiskFreeMb);

/// <summary>Why a burst slot is or is not admitted. <see cref="Admitted"/> is the only yes; the others are checked in this order and the first that fails is reported.</summary>
public enum BurstVerdict { Admitted, NoFootprint, Memory, Cpu, Disk }

public static class BurstAdmission
{
    /// <summary>
    /// A burst slot (a slot above <c>perHostCapacity</c>) is admitted only when all of these hold:
    /// <c>memAvailableMb - reserveMb &gt;= footprint</c>, <c>load1 + containerCpus &lt;= cpus</c> and <c>diskFreeMb &gt;= minDiskMb</c>; each inequality
    /// is inclusive (equality admits). With no footprint (<paramref name="footprintMb"/> null, fewer than 10 sessions measured) it is never admitted.
    /// <paramref name="containerCpus"/> is the CPU limit one session container runs with (<c>--cpus</c>).
    /// </summary>
    public static BurstVerdict Check(HostMetrics host, int? footprintMb, double containerCpus, HeadroomSettings? settings = null)
    {
        var s = settings ?? HeadroomSettings.Defaults;
        if (footprintMb is null) return BurstVerdict.NoFootprint;
        if ((long)host.MemAvailableMb - s.ReserveMb < footprintMb.Value) return BurstVerdict.Memory;
        // Written as the negated admit test, not as "> cpus": with an unknown load (NaN, a host report that could not read /proc/loadavg) every comparison
        // is false, so "> cpus" would admit the host. An unknown load (or container cpus) fails closed.
        if (!(host.Load1 + containerCpus <= host.Cpus)) return BurstVerdict.Cpu;
        if (host.DiskFreeMb < s.MinDiskMb) return BurstVerdict.Disk;
        return BurstVerdict.Admitted;
    }
}
