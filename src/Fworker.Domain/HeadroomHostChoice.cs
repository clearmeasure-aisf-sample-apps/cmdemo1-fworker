namespace Fworker.Domain;

// Host choice (D7), delivery-speed.md section 4.2: "Among hosts below their limit, the plan picks the one with most headroom (today: fewest workers)".
// Specification rules, no v1 counterpart. The host start script stays authoritative (it counts the running containers itself and answers host-full);
// this is the gateway's plan, which only decides where to ask first.

/// <summary>
/// One host as the plan sees it. <paramref name="Running"/> counts every running <c>fw-*</c> container, an exiting one included.
/// <paramref name="PerHostBurst"/> is the limit with headroom (default equal to <paramref name="PerHostCapacity"/>: no burst).
/// <paramref name="FootprintSamples"/> are the <c>memPeakMb</c> values of this host's finished sessions. <paramref name="HostFullAt"/> is when the host
/// last answered host-full (null: never).
/// </summary>
public sealed record HostState(
    string Name,
    int PerHostCapacity,
    int PerHostBurst,
    int Running,
    HostMetrics Metrics,
    IReadOnlyCollection<int> FootprintSamples,
    DateTimeOffset? HostFullAt = null);

/// <summary>The kind of slot a host offers for the next session.</summary>
public enum SlotKind { None, Guaranteed, Burst }

/// <summary>A host chosen for the next session and the kind of slot it gives.</summary>
public sealed record HostChoice(string Name, SlotKind Slot);

public static class HostChoiceRule
{
    /// <summary>
    /// A host that answered host-full is blocked for <see cref="HeadroomSettings.HostFullSeconds"/> from that answer (not a failure: no failure counts, section
    /// 4.2). It is open again at the second the block ends.
    /// </summary>
    public static bool IsBlocked(HostState host, DateTimeOffset now, HeadroomSettings? settings = null)
    {
        var s = settings ?? HeadroomSettings.Defaults;
        return host.HostFullAt is { } at && now < at.AddSeconds(s.HostFullSeconds);
    }

    /// <summary>
    /// The slot the next session on this host would take. Up to <c>perHostCapacity</c> it is a guaranteed slot with no headroom test at all (no memory
    /// test, and v1's behavior is unchanged), also when <c>perHostBurst</c> is lower than the capacity (the limit is max(capacity, burst)). Above it and up to <c>perHostBurst</c> it is a burst slot, only when <see cref="BurstAdmission.Check"/>
    /// admits. Otherwise none (also while the host is blocked).
    /// </summary>
    public static SlotKind SlotFor(HostState host, double containerCpus, DateTimeOffset now, HeadroomSettings? settings = null)
    {
        if (IsBlocked(host, now, settings)) return SlotKind.None;
        var next = host.Running + 1;
        // The effective limit is max(capacity, burst): a burst configured below the capacity never lowers the guaranteed slots (capacity is the floor,
        // v1's behavior), it only means there is no burst. Stated here so the order of the two tests below is not what decides it.
        var limit = Math.Max(host.PerHostCapacity, host.PerHostBurst);
        if (next > limit) return SlotKind.None;
        if (next <= host.PerHostCapacity) return SlotKind.Guaranteed;
        var footprint = Footprint.P95(host.FootprintSamples, settings);
        return BurstAdmission.Check(host.Metrics, footprint, containerCpus, settings) == BurstVerdict.Admitted ? SlotKind.Burst : SlotKind.None;
    }

    /// <summary>
    /// The host for the next session, or null when none has a slot. Hosts with a guaranteed slot come before hosts that would only burst (a burst is
    /// extra, it is not taken while a guaranteed slot is free). Within a kind the host with the most headroom wins, headroom being
    /// <c>memAvailableMb - reserveMb</c>; a tie goes to the host with fewer running containers, then to the lower name (ordinal), so the answer does not
    /// depend on the order of <paramref name="hosts"/>.
    /// </summary>
    public static HostChoice? Pick(IEnumerable<HostState> hosts, double containerCpus, DateTimeOffset now, HeadroomSettings? settings = null)
    {
        var s = settings ?? HeadroomSettings.Defaults;
        return hosts
            .Select(h => (Host: h, Slot: SlotFor(h, containerCpus, now, s)))
            .Where(x => x.Slot != SlotKind.None)
            .OrderBy(x => x.Slot)
            .ThenByDescending(x => (long)x.Host.Metrics.MemAvailableMb - s.ReserveMb)
            .ThenBy(x => x.Host.Running)
            .ThenBy(x => x.Host.Name, StringComparer.Ordinal)
            .Select(x => new HostChoice(x.Host.Name, x.Slot))
            .FirstOrDefault();
    }
}
