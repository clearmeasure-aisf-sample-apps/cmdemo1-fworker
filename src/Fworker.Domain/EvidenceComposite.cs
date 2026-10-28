using System.Globalization;

namespace Fworker.Domain;

// The health evidence of one release in one environment (delivery-speed.md 11), ported from Test-EnvHealth in
// plugin/scripts/lib/Verbs.Promote.ps1 as pure values: no HTTP, no state comment, no clock. The caller reads /_build and the health endpoint
// and keeps the reads; this rule judges them. v1 composes three pieces of evidence (version, read, settle); smoke is a plan item (W7.6) and
// is not part of v1, so it is not ported. The gap texts are the ones v1 returns, word for word (the promotion pass tells the items with them).

/// <summary>One recorded read of the health endpoint: when, and the status ("Healthy", another overallStatus, "unreachable", "status 502 without overallStatus").</summary>
public readonly record struct HealthRead(DateTimeOffset At, string Status);

/// <summary>octopus.health.spacingMinutes and settleMinutes (defaults 5 and 10 in config/instance.json).</summary>
public readonly record struct HealthWindow(double SpacingMinutes = 5, double SettleMinutes = 10);

/// <summary>The verdict: <see cref="Gap"/> is empty when the evidence is complete, else the reason the release waits.</summary>
public readonly record struct EvidenceVerdict(string Gap)
{
    public bool Complete => Gap.Length == 0;
}

public static class EvidenceComposite
{
    /// <summary>The reads v1 keeps per release and environment (Select-Object -Last 30).</summary>
    public const int MaxReads = 30;

    /// <summary>The health path when octopus.healthPath is empty or missing.</summary>
    public const string DefaultHealthPath = "/_healthcheck/detailed";

    public static string HealthPath(string? configured) => string.IsNullOrEmpty(configured) ? DefaultHealthPath : configured;

    /// <summary>The status of one answer (Read-EnvHealth): the overallStatus when there is one, else "status N without overallStatus".</summary>
    public static string StatusOfAnswer(int httpStatus, string? overallStatus)
        => overallStatus ?? $"status {httpStatus} without overallStatus";

    public static string NoUrlGap(string envKey) => $"no health URL for {envKey} (octopus.{envKey}Url is empty)";

    /// <summary>Evidence 'version': the environment's /_build shows the release (PowerShell -ne is case-insensitive); null when it does.</summary>
    public static string? VersionGap(string envKey, string expected, string? running)
        => string.Equals(running ?? "", expected, StringComparison.OrdinalIgnoreCase) ? null
            : $"the {envKey} app does not run release {expected} yet (/_build: {(string.IsNullOrEmpty(running) ? "no version" : running)})";

    /// <summary>The reads that count: those taken at or after the deployment finished, then the new one appended, judged in full (v1 judges up to 31); <see cref="Keep"/> is what v1 stores.</summary>
    public static List<HealthRead> Collect(IEnumerable<HealthRead> stored, DateTimeOffset finishedAt, HealthRead fresh)
    {
        var l = stored.Where(r => r.At >= finishedAt).ToList();
        l.Add(fresh);
        return l;
    }

    /// <summary>The reads v1 stores back: the last <see cref="MaxReads"/> ($keep). The caller handles an unparsable finish time (v1 uses now), the trailing slash of the URL and a failed save.</summary>
    public static List<HealthRead> Keep(IReadOnlyList<HealthRead> reads)
        => reads.Count > MaxReads ? reads.Skip(reads.Count - MaxReads).ToList() : reads.ToList();

    /// <summary>Evidence 'read' and 'settle' over the collected reads; null when both hold.</summary>
    public static string? ReadsGap(string envKey, string version, string healthPath, IReadOnlyList<HealthRead> reads, DateTimeOffset finishedAt, HealthWindow w)
    {
        foreach (var r in reads)
            if (!string.Equals(r.Status, "Healthy", StringComparison.OrdinalIgnoreCase))
                return $"the {envKey} app answered {r.Status} on {healthPath} at {Iso(r.At)}, after its deployment of {version} finished: the release waits for a new deployment there";
        var settled = reads.Where(r => (r.At - finishedAt).TotalMinutes >= w.SettleMinutes).Select(r => r.At)
            .Any(late => reads.Any(e => (late - e.At).TotalMinutes >= w.SpacingMinutes));
        return settled ? null
            : $"the {envKey} app has not been Healthy long enough yet (two Healthy reads {Num(w.SpacingMinutes)} min apart, the later {Num(w.SettleMinutes)} min after the deployment finished)";
    }

    /// <summary>The whole composite in v1's order: no URL, version, then the reads. <paramref name="running"/> is the /_build version ("" when unreadable).</summary>
    public static EvidenceVerdict Evaluate(string envKey, string? url, string expectedVersion, string? running, string healthPath,
        IEnumerable<HealthRead> stored, DateTimeOffset finishedAt, HealthRead fresh, HealthWindow window)
    {
        if (string.IsNullOrEmpty(url)) return new(NoUrlGap(envKey));
        var v = VersionGap(envKey, expectedVersion, running);
        if (v is not null) return new(v);
        return new(ReadsGap(envKey, expectedVersion, healthPath, Collect(stored, finishedAt, fresh), finishedAt, window) ?? "");
    }

    // ConvertTo-Iso of Core.ps1: yyyy-MM-ddTHH:mm:ss.fffZ in UTC
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // PowerShell interpolates a [double] with its shortest round-trip form: 5 -> "5", 2.5 -> "2.5"
    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
