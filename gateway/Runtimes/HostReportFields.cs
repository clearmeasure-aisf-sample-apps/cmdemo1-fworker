using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Domain;

// W7.4 (docs/design/delivery-speed.md 4.2, "Report"): the fields the host report of "Reconcile hosts" (deploy/octopus/scripts/reconcile-hosts.sh) carries besides the containers:
// memTotalMb, memAvailableMb, cpus, load1, diskFreeMb, and per container memPeakMb. A host whose script is older sends none of them, and a host that cannot read one leaves it out, so every
// field is optional and a value that is not a plain non-negative number is treated as absent. Nothing here throws: a report is never refused for its metrics.

/// <summary>What a host report says about the machine; each value is null when the host did not report it (or reported it unreadable).</summary>
sealed record HostFacts(int? MemTotalMb, int? MemAvailableMb, int? Cpus, double? Load1, int? DiskFreeMb)
{
    public static readonly HostFacts None = new(null, null, null, null, null);

    /// <summary>The load the scheduler is given for a host that did not report one: finite (the ledger is JSON, which has no NaN) and larger than any host can carry, so a burst slot is refused.</summary>
    public const double UnknownLoad = 1_000_000;

    /// <summary>True when the host reported at least one metric.</summary>
    public bool Any => MemTotalMb is not null || MemAvailableMb is not null || Cpus is not null || Load1 is not null || DiskFreeMb is not null;

    /// <summary>
    /// The metrics as the scheduler's headroom rule takes them. A value the host did not report fails closed for a burst slot (no memory, no cpus, no disk, a load no host can carry): the guaranteed slots
    /// (up to perHostCapacity) are never tested against them.
    /// </summary>
    public HostMetrics ToMetrics() => new(MemAvailableMb ?? 0, Cpus ?? 0, Load1 ?? UnknownLoad, DiskFreeMb ?? 0);
}

static class HostReportFields
{
    const double MaxNumber = int.MaxValue;

    /// <summary>The host-level fields of the report body.</summary>
    public static HostFacts Parse(JsonObject body) => new(
        Whole(body["memTotalMb"]), Whole(body["memAvailableMb"]), Whole(body["cpus"]), Real(body["load1"]), Whole(body["diskFreeMb"]));

    /// <summary>A container's peak memory in MB (the cgroup's memory.peak), or null.</summary>
    public static int? MemPeakMb(JsonNode? container) => container is JsonObject o ? Whole(o["memPeakMb"]) : null;

    static double? Real(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        double d;
        if (v.TryGetValue<double>(out var dv)) d = dv;
        else if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)) d = sv;
        else if (v.TryGetValue<long>(out var lv)) d = lv;
        else return null;
        return double.IsFinite(d) && d >= 0 && d <= MaxNumber ? d : null;
    }

    // a count of MB or cpus: whole numbers; a fraction is rounded down (the host rounds already), a negative or absurd value is no value
    static int? Whole(JsonNode? n) => Real(n) is { } d ? (int)Math.Floor(d) : null;
}

/// <summary>
/// A host report as the runtime hands it on: <paramref name="Host"/> is the machine's name in Octopus (what a start is pinned to), <paramref name="Running"/> its running fw-* containers and
/// <paramref name="Capabilities"/> the capability labels of its Octopus tags (OctopusRunbookSettings.CapabilityTagPrefix).
/// </summary>
sealed record HostReportEvent(string Machine, string Host, int Running, HostFacts Facts, IReadOnlyList<string> Capabilities, DateTimeOffset At);
