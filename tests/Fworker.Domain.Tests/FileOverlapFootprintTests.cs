using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>D6: specification of the footprint of an item: the `Files:` line, the replacement by the pull request's files and the ignore patterns.</summary>
public sealed class FileOverlapFootprintTests
{
    static Footprint Design(params string[] entries) => new(entries, FootprintSource.Design);

    [Fact]
    public void ParseFilesLine_SplitsPathsAndGlobs()
    {
        var body = "Files: gateway/Plant.cs, ui/*.css, tests/scenarios/**\nDepends on: #12\n\nThe design...";
        Assert.Equal(new[] { "gateway/Plant.cs", "ui/*.css", "tests/scenarios/**" }, Footprint.ParseFilesLine(body));
    }

    [Fact]
    public void ParseFilesLine_StripsBackticksQuotesAndRemarks()
    {
        var body = "Files: `src/a.cs`, \"src/b.cs\", src/c.cs (new), ./src/d.cs, src\\e.cs";
        Assert.Equal(new[] { "src/a.cs", "src/b.cs", "src/c.cs", "src/d.cs", "src/e.cs" }, Footprint.ParseFilesLine(body));
    }

    [Fact]
    public void ParseFilesLine_DropsBlanksAndRepeats()
    {
        Assert.Equal(new[] { "a.cs", "b.cs" }, Footprint.ParseFilesLine("Files: a.cs, , b.cs, ./a.cs,"));
    }

    [Fact]
    public void ParseFilesLine_IsCaseInsensitiveAboutTheKeyword_AndToleratesCrLf()
    {
        Assert.Equal(new[] { "a.cs", "b.cs" }, Footprint.ParseFilesLine("files: a.cs, b.cs\r\nmore\r\n"));
    }

    [Fact]
    public void ParseFilesLine_WhenSeveralDesignsAreAppended_TheLatestCounts()
    {
        var body = "Files: old/a.cs\nfirst design\n\nFiles: new/b.cs, new/c.cs\nsecond design";
        Assert.Equal(new[] { "new/b.cs", "new/c.cs" }, Footprint.ParseFilesLine(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A design with no file line.\nIt mentions Files: only in the middle of a sentence.")]
    [InlineData("Files:")]
    [InlineData("Files:  ,  ")]
    public void ParseFilesLine_WithoutFiles_IsEmpty(string? body)
        => Assert.Empty(Footprint.ParseFilesLine(body));

    [Fact]
    public void FromDesign_IsTheDesignSource_OrEmpty()
    {
        var fp = Footprint.FromDesign("Files: a.cs");
        Assert.Equal(FootprintSource.Design, fp.Source);
        Assert.Equal(new[] { "a.cs" }, fp.Entries);
        Assert.True(Footprint.FromDesign("nothing").IsEmpty);
        Assert.Equal(FootprintSource.None, Footprint.FromDesign("nothing").Source);
    }

    [Fact]
    public void Resolve_BeforeTheFirstPush_KeepsTheDesign()
    {
        var design = Design("src/a.cs", "ui/*.css");
        Assert.Same(design, Footprint.Resolve(design, null));
    }

    [Fact]
    public void Resolve_AfterTheFirstPush_TheChangedFilesReplaceTheDesignCompletely()
    {
        var design = Design("src/a.cs", "ui/*.css");
        var fp = Footprint.Resolve(design, new[] { "src/b.cs", "./src/c.cs", "src/b.cs" });
        Assert.Equal(FootprintSource.PullRequest, fp.Source);
        Assert.Equal(new[] { "src/b.cs", "src/c.cs" }, fp.Entries);   // nothing of the design is kept
        Assert.False(fp.Overlaps(Design("src/a.cs")));                  // the file the design named but the PR did not touch no longer holds anyone
        Assert.True(fp.Overlaps(Design("src/*.cs")));
    }

    [Fact]
    public void Resolve_APushThatChangesNothingKnown_KeepsTheDesign()
    {
        var design = Design("src/a.cs");
        Assert.Same(design, Footprint.Resolve(design, Array.Empty<string>()));
        Assert.Same(design, Footprint.Resolve(design, new[] { " ", "" }));
    }

    [Fact]
    public void Resolve_APullRequestFootprintCanReplaceAnUnknownDesign()
    {
        var fp = Footprint.Resolve(Footprint.Empty, new[] { "a.cs" });
        Assert.Equal(FootprintSource.PullRequest, fp.Source);
        Assert.Equal(new[] { "a.cs" }, fp.Entries);
    }

    [Theory]
    [InlineData("package-lock.json", "package-lock.json", true)]
    [InlineData("package-lock.json", "ui/package-lock.json", true)]      // no '/': a file in any directory
    [InlineData("*.md", "docs/guide/a.md", true)]
    [InlineData("*.md", "src/a.cs", false)]
    [InlineData("docs/**", "docs/guide/a.md", true)]
    [InlineData("docs/**", "src/docs/a.md", false)]                      // with '/': anchored at the root
    [InlineData("docs/", "docs/a.md", true)]
    [InlineData("docs/**", "docs/*.md", true)]                           // a glob entry covered by the pattern
    [InlineData("docs/**", "**/*.md", false)]                            // a glob that reaches outside is kept
    [InlineData("", "a.cs", false)]
    public void IsIgnored_FollowsTheOverlapIgnorePatterns(string pattern, string entry, bool expected)
        => Assert.Equal(expected, Footprint.IsIgnored(entry, new[] { pattern }));

    [Fact]
    public void IsIgnored_NoPatterns_IgnoresNothing()
    {
        Assert.False(Footprint.IsIgnored("a.cs", null));
        Assert.False(Footprint.IsIgnored("a.cs", Array.Empty<string>()));
    }

    [Fact]
    public void WithoutIgnored_DropsOnlyTheIgnoredEntries()
    {
        var fp = Design("src/a.cs", "package-lock.json", "docs/readme.md").WithoutIgnored(new[] { "package-lock.json", "docs/**" });
        Assert.Equal(new[] { "src/a.cs" }, fp.Entries);
        Assert.Equal(FootprintSource.Design, fp.Source);
    }

    [Fact]
    public void WithoutIgnored_WhenEverythingIsIgnored_IsEmpty()
    {
        var fp = Design("package-lock.json").WithoutIgnored(new[] { "package-lock.json" });
        Assert.True(fp.IsEmpty);
        Assert.Equal(FootprintSource.None, fp.Source);
    }

    [Fact]
    public void Overlaps_NeedsOneSharedFile()
    {
        Assert.True(Design("a.cs", "ui/*.css").Overlaps(Design("b.cs", "ui/site.css")));
        Assert.False(Design("a.cs", "ui/*.css").Overlaps(Design("b.cs", "ui/site.js")));
        Assert.False(Footprint.Empty.Overlaps(Design("a.cs")));
        Assert.False(Design("a.cs").Overlaps(Footprint.Empty));
    }
}
