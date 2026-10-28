using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D2: the health evidence composite ported from Test-EnvHealth (Verbs.Promote.ps1). Expected values are derived from the script line by line
/// (the read filter at or after the deployment, the first non-Healthy read, the settle and spacing arithmetic, the gap texts); the script's
/// Select-Object -Last 30 and the check order (URL, version, reads) are pinned as well.
/// </summary>
public sealed class EvidenceCompositeParityTests
{
    static readonly DateTimeOffset Fin = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    static readonly HealthWindow V1 = new(5, 10);
    const string Path = "/_healthcheck/detailed";

    static HealthRead R(double minAfter, string status = "Healthy") => new(Fin.AddMinutes(minAfter), status);

    static string Gap(string? running, IEnumerable<HealthRead> stored, HealthRead fresh, string? url = "https://x", HealthWindow? w = null, string version = "1.0.7")
        => EvidenceComposite.Evaluate("uat", url, version, running, Path, stored, Fin, fresh, w ?? V1).Gap;

    [Fact]
    public void NoUrl_IsTheFirstGap()
        => Assert.Equal("no health URL for uat (octopus.uatUrl is empty)", Gap("1.0.7", [], R(11), url: ""));

    [Fact]
    public void WrongVersion_IsReportedBeforeAnyRead()
    {
        Assert.Equal("the uat app does not run release 1.0.7 yet (/_build: 1.0.6)", Gap("1.0.6", [], R(11, "Unhealthy")));
        Assert.Equal("the uat app does not run release 1.0.7 yet (/_build: no version)", Gap("", [], R(11)));
        Assert.Equal("the uat app does not run release 1.0.7 yet (/_build: no version)", Gap(null, [], R(11)));
    }

    [Fact]
    public void Version_IsComparedWithoutCase_LikePowerShellNe()
        => Assert.Equal("", Gap("1.0.7-RC", [R(0), R(5)], R(10), version: "1.0.7-rc"));

    [Fact]
    public void FirstRead_IsNotEnough()
        => Assert.Equal("the uat app has not been Healthy long enough yet (two Healthy reads 5 min apart, the later 10 min after the deployment finished)",
            Gap("1.0.7", [], R(11)));

    [Fact]
    public void TwoReadsSpacedAndTheLaterSettled_Passes()
        => Assert.Equal("", Gap("1.0.7", [R(2), R(6)], R(10)));

    [Fact]
    public void LaterReadBeforeTheSettle_Waits()
        => Assert.StartsWith("the uat app has not been Healthy long enough", Gap("1.0.7", [R(0), R(5)], R(9.9)));

    [Fact]
    public void SpacingTooShort_Waits()
        => Assert.StartsWith("the uat app has not been Healthy long enough", Gap("1.0.7", [R(8)], R(12.9)));

    [Fact]
    public void Boundaries_AreInclusive()
        => Assert.Equal("", Gap("1.0.7", [R(5)], R(10)));

    [Fact]
    public void ReadsBeforeTheDeploymentFinished_AreDropped_SoAnOldBadReadDoesNotCount()
        => Assert.Equal("", Gap("1.0.7", [R(-3, "Unhealthy"), R(1), R(5)], R(10)));

    [Fact]
    public void ReadAtTheFinishInstant_IsKept()
        => Assert.StartsWith("the uat app answered Degraded on /_healthcheck/detailed at 2026-10-28T10:00:00.000Z, after its deployment of 1.0.7 finished",
            Gap("1.0.7", [R(0, "Degraded")], R(11)));

    [Fact]
    public void FirstNonHealthyRead_IsNamed_EvenWhenSettled()
        => Assert.Equal("the uat app answered unreachable on /_healthcheck/detailed at 2026-10-28T10:03:00.000Z, after its deployment of 1.0.7 finished: the release waits for a new deployment there",
            Gap("1.0.7", [R(1), R(3, "unreachable"), R(5, "Degraded")], R(12)));

    [Fact]
    public void ANonHealthyFreshRead_Counts()
        => Assert.StartsWith("the uat app answered status 502 without overallStatus on", Gap("1.0.7", [R(1), R(5)], R(11, EvidenceComposite.StatusOfAnswer(502, null))));

    [Fact]
    public void Healthy_IsComparedWithoutCase()
        => Assert.Equal("", Gap("1.0.7", [R(2, "healthy")], R(10, "HEALTHY")));

    [Fact]
    public void ConfiguredWindow_ShowsInTheGap_AndDecides()
    {
        var w = new HealthWindow(1, 2);
        Assert.Equal("", Gap("1.0.7", [R(1)], R(2), w: w));
        Assert.Equal("the uat app has not been Healthy long enough yet (two Healthy reads 1 min apart, the later 2 min after the deployment finished)", Gap("1.0.7", [], R(2.5), w: w));
        Assert.Contains("1.5 min apart", Gap("1.0.7", [], R(0), w: new HealthWindow(1.5, 2)));
    }

    [Fact]
    public void Collect_KeepsTheLast30_AndTheFreshReadLast()
    {
        var stored = Enumerable.Range(0, 40).Select(i => R(i)).ToList();
        var all = EvidenceComposite.Collect(stored, Fin, R(41));
        Assert.Equal(41, all.Count);
        var got = EvidenceComposite.Keep(all);
        Assert.Equal(30, got.Count);
        Assert.Equal(R(41), got[^1]);
        Assert.Equal(R(11), got[0]);
    }

    [Fact]
    public void OldestOfThirtyOneReads_StillCounts_ForTheVerdict()
    {
        var stored = new[] { R(0, "Degraded") }.Concat(Enumerable.Range(1, 29).Select(i => R(i))).ToList();
        Assert.StartsWith("the uat app answered Degraded", Gap("1.0.7", stored, R(31)));
    }

    [Fact]
    public void StatusOfAnswer_UsesTheOverallStatus_OrThePlainCode()
    {
        Assert.Equal("Healthy", EvidenceComposite.StatusOfAnswer(200, "Healthy"));
        Assert.Equal("Degraded", EvidenceComposite.StatusOfAnswer(503, "Degraded"));
        Assert.Equal("status 404 without overallStatus", EvidenceComposite.StatusOfAnswer(404, null));
    }

    [Fact]
    public void HealthPath_FallsBackToTheDetailedEndpoint()
    {
        Assert.Equal("/_healthcheck/detailed", EvidenceComposite.HealthPath(null));
        Assert.Equal("/_healthcheck/detailed", EvidenceComposite.HealthPath(""));
        Assert.Equal("/h", EvidenceComposite.HealthPath("/h"));
    }

    [Fact]
    public void Iso_MatchesConvertToIso()
        => Assert.Equal("2026-10-28T10:00:00.000Z", EvidenceComposite.Iso(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(2))));

    [Fact]
    public void Verdict_Complete_FollowsTheGap()
    {
        Assert.True(new EvidenceVerdict("").Complete);
        Assert.False(new EvidenceVerdict("x").Complete);
    }
}
