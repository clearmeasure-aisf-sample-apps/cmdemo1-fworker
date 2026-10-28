using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D3: release choice and the content gate ported into Domain, pinned against what plugin/scripts/lib/Verbs.Promote.ps1 (Get-ReleaseContentRead,
/// Get-ItemPromotionGapsRead, Find-CommitItemUncached, Invoke-AutoPromoteInner) and Verbs.Delivery.ps1 (Get-ReleaseCommit, Get-Delivery) answer.
/// The expected values are what those functions return for the same inputs, with v1's texts character for character.
/// </summary>
public sealed class ReleaseChoiceParityTests
{
    static string Sha(char c) => new(c, 40);
    static readonly string Dep = Sha('d'), Rel = Sha('a'), Mid = Sha('b');

    static RangeCommit C(string sha, string? parent, string? msg = null) => new(sha, parent is null ? Array.Empty<string>() : new[] { parent }, msg);
    static CommitItemLookup None(RangeCommit _) => new(0, "");
    static readonly IReadOnlyDictionary<string, int> NoQueue = new Dictionary<string, int>();

    // ---- Get-ReleaseCommit

    [Fact]
    public void ReleaseCommit_BuildInformationWinsOverNotes_LowerCased()
    {
        var upper = new string('A', 40);
        Assert.Equal(new string('a', 40), ContentGate.ReleaseCommit("see " + Sha('c'), new string?[] { "short", upper }));
    }

    [Fact]
    public void ReleaseCommit_FallsBackToTheFirstFortyHexWordOfTheNotes()
    {
        Assert.Equal(Sha('c'), ContentGate.ReleaseCommit($"build {Sha('C')} and {Sha('e')}", null));
        Assert.Null(ContentGate.ReleaseCommit("no commit here " + new string('f', 41), null));
        Assert.Null(ContentGate.ReleaseCommit(null, Array.Empty<string?>()));
    }

    // ---- Get-ReleaseContentRead

    [Fact]
    public void NoReleaseCommit_Waits()
    {
        var j = ContentGate.Judge(null, "UAT", Dep, "1.0.1", null, NoQueue, None);
        Assert.False(j.Skip);
        Assert.Equal(new[] { "the release names no commit (its build information or notes)" }, j.Gaps);
        Assert.Empty(j.Items);
    }

    [Fact]
    public void UnknownDeployedCommit_Waits()
    {
        var j = ContentGate.Judge(Rel, "UAT", null, null, null, NoQueue, None);
        Assert.Equal(new[] { "the commit running in UAT is not known (no successful deployment of a release with a commit)" }, j.Gaps);
    }

    [Fact]
    public void SameCommit_Skips_AndUncomparable_Waits()
    {
        Assert.True(ContentGate.Judge(Rel, "UAT", Rel, "1.0.1", null, NoQueue, None).Skip);
        var j = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", null, NoQueue, None);
        Assert.False(j.Skip);
        Assert.Equal(new[] { "GitHub cannot compare the commit running in UAT (ddddddd, release 1.0.1) with the release's" }, j.Gaps);
    }

    [Theory]
    [InlineData("behind")]
    [InlineData("identical")]
    public void BehindOrIdentical_Skips(string status)
        => Assert.True(ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", new(status, new[] { C(Rel, Dep) }, 1), NoQueue, None).Skip);

    [Theory]
    [InlineData("diverged", 1, 1)]
    [InlineData("ahead", 0, 0)]
    [InlineData("ahead", 1, 250)]
    public void RangeThatCannotAllBeRead_Waits(string status, int listed, int total)
    {
        var cs = Enumerable.Range(0, listed).Select(_ => C(Rel, Dep)).ToArray();
        var j = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", new(status, cs, total), NoQueue, None);
        Assert.Equal(new[] { $"the commits between UAT (ddddddd) and the release cannot all be read (compare: {status}, {listed} of {total})" }, j.Gaps);
    }

    [Fact]
    public void BrokenFirstParentChain_Waits()
    {
        var j = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", new("ahead", new[] { C(Rel, Mid) }, 1), NoQueue, None);
        Assert.Equal(new[] { "the first-parent history from the release back to UAT (ddddddd) cannot be followed" }, j.Gaps);
    }

    [Fact]
    public void ChainOfQueuedItems_YieldsTheirItemsSortedAndUnique()
    {
        var range = new CompareRange("ahead", new[] { C(Rel, Mid), C(Mid, Dep) }, 2);
        var queued = new Dictionary<string, int> { [Rel] = 12, [Mid] = 7 };
        var j = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", range, queued, None);
        Assert.False(j.Skip);
        Assert.Empty(j.Gaps);
        Assert.Empty(j.Hard);
        Assert.Equal(new[] { 7, 12 }, j.Items);
    }

    [Fact]
    public void CommitThatIsNoItems_IsAHardGap_WhileAWaitingLookupIsOnlyAGap()
    {
        var range = new CompareRange("ahead", new[] { C(Rel, Mid, "bump"), C(Mid, Dep, "fix (#9)") }, 2);
        var j = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", range, NoQueue, c => c.Sha == Mid ? new(9, "#9 merge not settled") : new(0, ""));
        var g = "commit aaaaaaa in the release is not the merge commit of a factory work item";
        Assert.Equal(new[] { g, "#9 merge not settled" }, j.Gaps);
        Assert.Equal(new[] { g }, j.Hard);
        Assert.Equal(new[] { 9 }, j.Items);
    }

    [Fact]
    public void ShaCaseIsIgnored_AndTheBudgetDefers()
    {
        var range = new CompareRange("ahead", new[] { C(Rel.ToUpperInvariant(), Dep.ToUpperInvariant()) }, null);
        var queued = new Dictionary<string, int> { [Rel] = 3 };
        Assert.Equal(new[] { 3 }, ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", range, queued, None).Items);
        var d = ContentGate.Judge(Rel, "UAT", Dep, "1.0.1", range, queued, None, () => true);
        Assert.True(d.Deferred);
        Assert.Empty(d.Items);
    }

    // ---- Find-CommitItemUncached's parsing

    [Theory]
    [InlineData("Fix the thing (#42)", 42)]
    [InlineData("Fix the thing (#42)  \nbody (#7)", 42)]
    [InlineData("Fix (#42) later", 0)]
    [InlineData("no number", 0)]
    [InlineData(null, 0)]
    public void SquashPullRequest(string? message, int expected) => Assert.Equal(expected, ContentGate.SquashPullRequest(message));

    [Fact]
    public void CandidateItems_BranchTitleBody_InOrder_NoPrNoRepeats()
    {
        var c = ContentGate.CandidateItems(50, "cm-ai-ops-bot/366-d3", "D3 #366 and #50 and #12 #1234", "Closes #12, fixed #99\nResolves #366");
        Assert.Equal(new[] { 366, 12, 1234, 99 }, c);
        Assert.Empty(ContentGate.CandidateItems(5, "feature", "no refs", null));
    }

    // ---- Get-ItemPromotionGapsRead

    static ItemGateFacts Facts(int n = 5, string[]? labels = null, bool passed = true, int? vpr = 11, int? mpr = 11, string? by = null, bool video = false)
        => new(n, labels ?? new[] { "Auto Merge" }, passed, vpr, mpr, by, video);
    static readonly ItemGateConfig Cfg = new("Auto Merge", "video");

    [Fact]
    public void QualifyingItem_HasNoGaps() => Assert.Empty(ItemGates.Gaps(Facts(), Cfg));

    [Fact]
    public void LabelsAreComparedWithoutCase() => Assert.Empty(ItemGates.Gaps(Facts(labels: new[] { "auto merge" }), Cfg));

    [Fact]
    public void EveryGapHasV1sText_InV1sOrder()
    {
        var g = ItemGates.Gaps(Facts(labels: new[] { "hold", "VIDEO" }, passed: false), Cfg);
        Assert.Equal(new[]
        {
            "#5 has no 'Auto Merge' label",
            "#5 carries the 'Hold' label",
            "#5 has no pull request merged after a Functional Testing PASS verdict",
            "#5 has the 'video' label and no demo video published (release demo-5, asset demo-5.mp4)",
        }, g);
    }

    [Theory]
    [InlineData(11, 12, null, false)]   // merged pull request differs from the verdict's
    [InlineData(11, 11, "person", false)] // a person's merge
    [InlineData(11, null, null, false)] // no merge pr
    [InlineData(null, null, null, false)] // no verdict pr
    [InlineData(0, 0, null, false)]    // zero is no pull request
    [InlineData(11, 11, "gateway", true)]
    public void PassMergedRule(int? vpr, int? mpr, string? by, bool qualifies)
        => Assert.Equal(qualifies, ItemGates.Gaps(Facts(vpr: vpr, mpr: mpr, by: by), Cfg).Count == 0);

    [Fact]
    public void HoldLabels_DefaultAndConfigured_AreReportedAsConfigured()
    {
        Assert.Equal("hold:owner", ItemGates.HoldLabel(new[] { "Hold:Owner" }, null));
        Assert.Equal("", ItemGates.HoldLabel(new[] { "x" }, null));
        Assert.Equal("stop", ItemGates.HoldLabel(new[] { "STOP", "wait" }, new[] { "stop", "wait" }));
        var g = ItemGates.Gaps(Facts(labels: new[] { "Auto Merge", "wait" }), new("Auto Merge", "video", new[] { "stop", "wait" }));
        Assert.Equal(new[] { "#5 carries the 'wait' label" }, g);
    }

    [Fact]
    public void VideoLabelWithPublishedVideo_IsFine()
        => Assert.Empty(ItemGates.Gaps(Facts(labels: new[] { "Auto Merge", "video" }, video: true), Cfg));

    // ---- release choice

    [Fact]
    public void Targets_AreTheConfiguredEnvironmentsAfterTdd_InConfiguredOrder()
    {
        Assert.Equal(new[] { "uat", "prod" }, ReleaseChoice.Targets(new[] { "TDD", "UAT", "prod", "qa" }));
        Assert.Equal(new[] { "prod", "uat" }, ReleaseChoice.Targets(new[] { "prod", "uat" }));
        Assert.Equal("tdd", ReleaseChoice.PreviousEnvironment("uat"));
        Assert.Equal("uat", ReleaseChoice.PreviousEnvironment("prod"));
        Assert.Null(ReleaseChoice.PreviousEnvironment("tdd"));
    }

    [Fact]
    public void NextTarget_IsTheFirstEnvironmentWithoutASuccess()
    {
        string Name(string k) => k == "uat" ? "UAT" : "Production";
        var targets = new[] { "uat", "prod" };
        var none = new Dictionary<string, EnvDeployment>();
        Assert.Equal("uat", ReleaseChoice.NextTarget(targets, Name, none));
        var uatOk = new Dictionary<string, EnvDeployment> { ["uat"] = new("Success", true) };
        Assert.Equal("prod", ReleaseChoice.NextTarget(targets, Name, uatOk));
        var uatFailed = new Dictionary<string, EnvDeployment> { ["uat"] = new("Failed", true) };
        Assert.Equal("uat", ReleaseChoice.NextTarget(targets, Name, uatFailed));
        var all = new Dictionary<string, EnvDeployment> { ["uat"] = new("Success", true), ["production"] = new("Success", true) };
        Assert.Null(ReleaseChoice.NextTarget(targets, Name, all));
    }

    [Fact]
    public void NewestQualifying_FallsBackPastABlockedRelease()
    {
        var t = DateTimeOffset.Parse("2026-10-28T10:00:00Z");
        var r = new[]
        {
            new ReleaseCandidate("1.0.5", t.AddMinutes(-30), true),
            new ReleaseCandidate("1.0.7", t, false),
            new ReleaseCandidate("1.0.6", t.AddMinutes(-10), true),
        };
        Assert.Equal("1.0.6", ReleaseChoice.NewestQualifying(r)!.Version);
        Assert.Null(ReleaseChoice.NewestQualifying(r.Select(x => x with { Qualifies = false })));
        Assert.Null(ReleaseChoice.NewestQualifying(Array.Empty<ReleaseCandidate>()));
    }

    sealed record Rl(string Version, string? Commit, DateTimeOffset At);

    [Fact]
    public void CarryingRelease_ExactCommitFirst_ElseNewestWhenAhead()
    {
        var t = DateTimeOffset.Parse("2026-10-28T10:00:00Z");
        var rels = new[] { new Rl("1.0.9", Sha('9'), t), new Rl("1.0.8", Sha('8'), t.AddHours(-1)) };
        Assert.Equal("1.0.8", ReleaseChoice.CarryingRelease(rels, r => r.Commit, r => r.At, Sha('8').ToUpperInvariant(), _ => false)!.Version);
        Assert.Equal("1.0.9", ReleaseChoice.CarryingRelease(rels, r => r.Commit, r => r.At, Sha('7'), _ => true)!.Version);
        Assert.Null(ReleaseChoice.CarryingRelease(rels, r => r.Commit, r => r.At, Sha('7'), _ => false));
        Assert.Null(ReleaseChoice.CarryingRelease(new[] { new Rl("1.0.9", null, t) }, r => r.Commit, r => r.At, Sha('7'), _ => true));
        Assert.Null(ReleaseChoice.CarryingRelease(Array.Empty<Rl>(), r => r.Commit, r => r.At, Sha('7'), _ => true));
    }
}
