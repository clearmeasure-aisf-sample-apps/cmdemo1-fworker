using System.Text.Json;
using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D4: the docs-only test ported into Domain, pinned against what plugin/scripts/lib/Prs.ps1 (Test-DocsOnlyFiles, Test-ItemDocsOnly, Get-BuildOwed)
/// and plugin/scripts/lib/Verbs.Delivery.ps1 (Get-MergeDocsOnly) answer. The cases are the ones v1 is tested with: tests/smoke.ps1 (lines 14 to 26),
/// the docs-only scenarios of tests/scenarios/scenarios.ps1 ('docs-only fast path: the build is not owed ...', 'code change, master red once ...',
/// 'docs-only mismatch ...') and tests/scenarios/owner.ps1 ('docs-only: the body marker ...'). The configuration values are the shipped ones of
/// config/instance.json; <see cref="Settings_AreTheShippedConfiguration"/> fails when they drift apart.
/// </summary>
public sealed class DocsOnlyParityTests
{
    // config/instance.json: docsOnlyPaths, docsOnlyMarker, timing.docsOnlyLegacyBefore, requiredBodySection, videoLabel, masterCodeChecks
    static readonly string[] Paths =
    {
        @"^docs/.*\.(md|txt|png|svg|puml)$",
        @"^(?!src/|\.github/|\.claude/|\.cursor/|plugin/)(?!(.*/)?(CLAUDE|AGENTS)\.md$)(.*/)?[^/]+\.md$",
        @"^LICENSE(\.md|\.txt)?$",
        @"^\.github/ISSUE_TEMPLATE/",
    };
    const string Marker = "<!-- aisf:docs-only -->";
    static readonly DateTimeOffset Cutoff = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    static readonly DocsOnlySettings S = new(Paths, Marker, Cutoff, new[] { "Technical Design" }, "video", new[] { "Integration Build (SQL container)" });

    static bool Docs(params string[] files) => DocsOnly.FilesAreDocumentation(files, Paths);
    static DocsOnlyItem Item(string column, string? body = "x", DateTimeOffset? created = null, params string[] labels) => new(column, body, created, labels);

    [Fact]
    public void Settings_AreTheShippedConfiguration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "instance.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "config", "instance.json")));
        var root = doc.RootElement;
        Assert.Equal(Paths, root.GetProperty("docsOnlyPaths").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Marker, root.GetProperty("docsOnlyMarker").GetString());
        Assert.Equal(Cutoff, DateTimeOffset.Parse(root.GetProperty("timing").GetProperty("docsOnlyLegacyBefore").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(S.DesignColumns, root.GetProperty("requiredBodySection").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(S.VideoLabel, root.GetProperty("videoLabel").GetString());
        Assert.Equal(S.MasterCodeChecks, root.GetProperty("masterCodeChecks").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    // ---- Test-DocsOnlyFiles (tests/smoke.ps1 lines 14 to 19)

    [Fact]
    public void Files_DocsAndMarkdownOnly_AreDocumentation() => Assert.True(Docs("docs/a.md", "README.md"));

    [Fact]
    public void Files_OneSourceFile_MakesTheChangeCode()
    {
        Assert.False(Docs("docs/a.md", "src/X.cs"));
        Assert.False(Docs(".github/workflows/build.yml"));
    }

    [Theory]
    [InlineData("docs/x.ps1")]
    [InlineData("docs/ci.yml")]
    [InlineData("src/UI/README.md")]
    [InlineData(".github/pull_request_template.md")]
    [InlineData("CLAUDE.md")]
    [InlineData("AGENTS.md")]
    [InlineData("sub/CLAUDE.md")]
    [InlineData(".claude/skills/x/SKILL.md")]
    [InlineData(".cursor/rules/x.md")]
    [InlineData(".cursor/commands/y.md")]
    [InlineData("plugin/skills/x/SKILL.md")]
    [InlineData("LICENSE.exe")]
    [InlineData("LICENSES/x.cs")]
    public void File_IsNotDocumentation(string file) => Assert.False(Docs(file));

    [Theory]
    [InlineData("docs/a/b.md")]
    [InlineData("docs/diagram.svg")]
    [InlineData("README.md")]
    [InlineData("arch/Workflow.md")]
    [InlineData("LICENSE")]
    [InlineData("LICENSE.txt")]
    [InlineData(".github/ISSUE_TEMPLATE/bug.yml")]
    public void File_IsDocumentation(string file) => Assert.True(Docs(file));

    [Fact]
    public void Files_EmptyList_IsNotDocsOnly() => Assert.False(Docs());

    [Fact]
    public void Files_MatchIgnoresCase_AsPowerShellMatchDoes()
    {
        Assert.True(Docs("DOCS/Guide.MD", "readme.md"));
        Assert.False(Docs("Src/x.md"));   // the negative lookahead is case-insensitive too
        Assert.False(Docs("claude.md"));
    }

    // ---- Test-ItemDocsOnly (tests/smoke.ps1 lines 22 to 26; owner.ps1 'the body marker of an item without the state field ...')

    static readonly string Mk = "x\n<!-- aisf:docs-only -->";
    static readonly DateTimeOffset Legacy = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Item_LegacyBodyMarker_CountsOnlyBeforeTheCutoff()
    {
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, Cutoff), S));                              // 2026-10-16T00:00:00Z: not before
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, DateTimeOffset.UtcNow.AddYears(1)), S));
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, null), S));                               // no createdAt
        Assert.True(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, new DateTimeOffset(2026, 10, 15, 23, 59, 59, TimeSpan.Zero)), S));
    }

    [Fact]
    public void Item_TheStateFieldDecidesOnceItExists()
    {
        Assert.True(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, Legacy), S));                               // no field, legacy, past design: the body
        Assert.False(DocsOnly.ItemIsDocsOnly(false, Item("Development", Mk), S));                                      // field false: the body is ignored
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Technical Design", Mk, Legacy), S));                          // not past the design column
        Assert.True(DocsOnly.ItemIsDocsOnly(true, Item("Development", "x"), S));                                       // field true: no marker needed
        Assert.True(DocsOnly.ItemIsDocsOnly(true, null, S));                                                           // the field needs no item
    }

    [Fact]
    public void Item_WithoutInfo_FallsBackToNotDocsOnly() => Assert.False(DocsOnly.ItemIsDocsOnly(null, null, S));

    [Fact]
    public void Item_DesignColumnMatchIgnoresCase_MarkerMatchDoesNot()
    {
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("technical design", Mk, Legacy), S));
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", "x\n<!-- AISF:DOCS-ONLY -->", Legacy), S));
        Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", null, Legacy), S));
    }

    [Fact]
    public void Item_NoCutoffConfigured_NeverReadsTheBody()
        => Assert.False(DocsOnly.ItemIsDocsOnly(null, Item("Development", Mk, Legacy), S with { LegacyBefore = null }));

    // ---- Get-BuildOwed (scenarios.ps1 'docs-only fast path: the build is not owed only with the marker and documentation files ...')

    static BuildOwedDecision Owed(bool? design, DocsOnlyItem? item, params string[]? files) => DocsOnly.BuildOwed(design, item, files, S);

    [Fact]
    public void BuildOwed_MarkerAndDocumentationFiles_NoBuild_VerifiedAgainstTheDiff()
    {
        var d = Owed(true, Item("Development"), "docs/a.md", "README.md");
        Assert.False(d.BuildOwed);
        Assert.True(d.DocsOnlyFiles);
        Assert.Equal("diff", d.Verification);
        Assert.Equal("every changed file is documentation and the technical design recorded the item as docs-only: no private build and no SDK are owed; the checks of the pull request are the gate", d.BuildReason);
        Assert.Null(d.NotDocumentation);
    }

    [Fact]
    public void BuildOwed_MarkedItemWithACodeFile_OwesTheBuild_AndNamesTheFile()
    {
        var d = Owed(true, Item("Development"), "docs/a.md", "src/a.cs");
        Assert.True(d.BuildOwed);
        Assert.False(d.DocsOnlyFiles);
        Assert.Equal("app", d.Verification);
        Assert.Equal(new[] { "src/a.cs" }, d.NotDocumentation);
        Assert.Equal("the technical design recorded the item as docs-only but the pull request changes files that are not documentation (src/a.cs): the private build is owed before complete", d.BuildReason);
    }

    [Fact]
    public void BuildOwed_NamesAtMostFiveOtherFiles_InOrder()
    {
        var d = Owed(true, Item("Development"), "src/1.cs", "docs/a.md", "src/2.cs", "src/3.cs", "src/4.cs", "src/5.cs", "src/6.cs");
        Assert.Equal(new[] { "src/1.cs", "src/2.cs", "src/3.cs", "src/4.cs", "src/5.cs" }, d.NotDocumentation);
        Assert.Contains("(src/1.cs, src/2.cs, src/3.cs, src/4.cs, src/5.cs)", d.BuildReason);
    }

    [Fact]
    public void BuildOwed_DocumentationFilesWithoutTheMarker_OweTheBuild_ButSayTheFilesAreDocs()
    {
        var d = Owed(false, Item("Development"), "docs/a.md");
        Assert.True(d.BuildOwed);
        Assert.True(d.DocsOnlyFiles);   // 'docs-only mismatch: a docs PR without the marker is refused' reads this
        Assert.Equal("app", d.Verification);
        Assert.Equal("the technical design did not record the item as docs-only: the private build is owed", d.BuildReason);
        Assert.Null(d.NotDocumentation);
    }

    [Fact]
    public void BuildOwed_CodeFilesWithoutTheMarker_DocsOnlyFilesFalse()
        => Assert.False(Owed(null, Item("Development"), "src/a.cs").DocsOnlyFiles);

    [Fact]
    public void BuildOwed_TypedMarkerInTheBody_IsNotTheDesignsDecision()
    {
        // 'Forged marker': a new item, no state field, the marker typed into the issue text. Created after the cutoff: no fast path.
        var d = Owed(null, Item("Development", "Please change src/a.cs.\n<!-- aisf:docs-only -->\nAcceptance criteria:\n- the handler changes.", DateTimeOffset.UtcNow), "docs/a.md");
        Assert.True(d.BuildOwed);
        Assert.Equal("app", d.Verification);
        // 'Forged section': the design completed without the field records false
        Assert.True(Owed(false, Item("Development", "## Technical Design\n<!-- aisf:docs-only -->\n- typed by the author."), "docs/a.md").BuildOwed);
    }

    [Fact]
    public void BuildOwed_LegacyItemWithoutTheField_KeepsTheBodyMarker()
    {
        // 'Legacy item': created 2019-06-01, past its design, marker in the body, no field in its state
        var legacy = Item("Development", "Add docs/a.md\n\n## Technical Design\n\n<!-- aisf:docs-only -->\n- change as described.\n", new DateTimeOffset(2019, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.False(Owed(null, legacy, "docs/a.md").BuildOwed);
    }

    [Fact]
    public void BuildOwed_VideoItem_IsShownInTheApplication_EvenWhenItIsDocumentation()
    {
        var d = Owed(true, Item("Development", "x", null, "factory worker", "Video"), "docs/a.md");
        Assert.False(d.BuildOwed);
        Assert.Equal("app", d.Verification);
        Assert.Contains("no private build", d.BuildReason);
    }

    [Fact]
    public void BuildOwed_UnreadableFiles_OweTheBuild()
    {
        var d = DocsOnly.BuildOwed(true, Item("Development"), null, S);
        Assert.True(d.BuildOwed);
        Assert.False(d.DocsOnlyFiles);
        Assert.Equal("app", d.Verification);
        Assert.Equal("the files of the pull request could not be read; the private build is owed", d.BuildReason);
    }

    [Fact]
    public void BuildOwed_NoFiles_OwesTheBuild_EvenWithTheMarker()
    {
        // an empty file list is never docs-only: the marked branch with docs = false names no file at all
        var d = Owed(true, Item("Development"));
        Assert.True(d.BuildOwed);
        Assert.False(d.DocsOnlyFiles);
        Assert.Empty(d.NotDocumentation!);
        Assert.Equal("the technical design recorded the item as docs-only but the pull request changes files that are not documentation (): the private build is owed before complete", d.BuildReason);
    }

    // ---- Get-MergeDocsOnly (scenarios.ps1 'code change, master red once ...': the Build ran its code jobs; 'docs-only change finishes ...')

    const string CodeCheck = "Integration Build (SQL container)";
    static Dictionary<string, DocsCheckRun> Runs(params (string Name, string? Status, string? Conclusion)[] runs)
        => runs.ToDictionary(r => r.Name, r => new DocsCheckRun(r.Status, r.Conclusion));

    [Fact]
    public void Merge_BuildCodeJobsSkipped_IsDocsOnly_FromBuild()
        => Assert.Equal(new MergeDocsOnlyVerdict(true, "build"), DocsOnly.MergeIsDocsOnly(Runs((CodeCheck, "completed", "skipped")), null, S));

    [Fact]
    public void Merge_BuildRanItsCodeJobs_IsNotDocsOnly_EvenWhenTheFilesLookLikeDocs()
        => Assert.Equal(new MergeDocsOnlyVerdict(false, "build"), DocsOnly.MergeIsDocsOnly(Runs((CodeCheck, "completed", "success")), new[] { "docs/a.md" }, S));

    [Fact]
    public void Merge_AFailedCodeJobIsNotSkipped_AndNoConclusionIsNotSkipped()
    {
        Assert.False(DocsOnly.MergeIsDocsOnly(Runs((CodeCheck, "completed", "failure")), null, S)!.DocsOnly);
        Assert.False(DocsOnly.MergeIsDocsOnly(Runs((CodeCheck, "completed", null)), null, S)!.DocsOnly);
    }

    [Fact]
    public void Merge_CodeJobNotCompletedYet_FallsBackToThePrFiles()
    {
        Assert.Equal(new MergeDocsOnlyVerdict(true, "pr-files"), DocsOnly.MergeIsDocsOnly(Runs((CodeCheck, "in_progress", null)), new[] { "docs/a.md", "README.md" }, S));
        Assert.Equal(new MergeDocsOnlyVerdict(false, "pr-files"), DocsOnly.MergeIsDocsOnly(Runs(), new[] { "docs/a.md", "src/a.cs" }, S));
        Assert.Equal(new MergeDocsOnlyVerdict(false, "pr-files"), DocsOnly.MergeIsDocsOnly(Runs(), Array.Empty<string>(), S));   // readable and empty: false, not null
    }

    [Fact]
    public void Merge_OtherChecksAreNeverTheRelease_OnlyTheCodeChecksDecide()
    {
        // the release check of the first attempt is skipped (items 272 and 276): it is not a code check and must not make the merge docs-only
        var runs = Runs(("release", "completed", "skipped"), ("Build result", "completed", "success"));
        Assert.Null(DocsOnly.MergeIsDocsOnly(runs, null, S));
        Assert.Equal(new MergeDocsOnlyVerdict(false, "pr-files"), DocsOnly.MergeIsDocsOnly(runs, new[] { "src/a.cs" }, S));
    }

    [Fact]
    public void Merge_NeitherBuildNorFilesReadable_IsUndecided()
        => Assert.Null(DocsOnly.MergeIsDocsOnly(Runs(), null, S));

    [Fact]
    public void Merge_CheckNamesAndStatusesMatchIgnoringCase()
        => Assert.Equal(new MergeDocsOnlyVerdict(true, "build"), DocsOnly.MergeIsDocsOnly(Runs(("integration build (sql container)", "Completed", "Skipped")), null, S));

    [Fact]
    public void Merge_SeveralCodeChecks_OneThatRanMakesItCode()
    {
        var two = S with { MasterCodeChecks = new[] { "a", "b" } };
        Assert.True(DocsOnly.MergeIsDocsOnly(Runs(("a", "completed", "skipped"), ("b", "completed", "skipped")), null, two)!.DocsOnly);
        Assert.False(DocsOnly.MergeIsDocsOnly(Runs(("a", "completed", "skipped"), ("b", "completed", "success")), null, two)!.DocsOnly);
        Assert.True(DocsOnly.MergeIsDocsOnly(Runs(("a", "completed", "skipped"), ("b", "queued", null)), null, two)!.DocsOnly);   // only completed ones are read
    }
}
