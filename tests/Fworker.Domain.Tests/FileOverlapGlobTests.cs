using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>D6: specification of the path patterns of the file-overlap hold (delivery-speed.md section 7). There is no v1 code; these tests are the definition.</summary>
public sealed class FileOverlapGlobTests
{
    [Theory]
    [InlineData("src/a.cs", "src/a.cs", true)]
    [InlineData("src/a.cs", "src/b.cs", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/sub/a.cs", false)]          // '*' stays inside one segment
    [InlineData("src/*.cs", "src/.cs", true)]                // '*' may be empty
    [InlineData("src/a?.cs", "src/ab.cs", true)]
    [InlineData("src/a?.cs", "src/a.cs", false)]             // '?' is exactly one character
    [InlineData("src/a?.cs", "src/a/.cs", false)]            // '?' never crosses '/'
    [InlineData("src/**", "src/a.cs", true)]
    [InlineData("src/**", "src/x/y/z.cs", true)]
    [InlineData("src/**", "other/a.cs", false)]
    [InlineData("**/site.css", "site.css", true)]            // '**' matches no segment
    [InlineData("**/site.css", "ui/web/site.css", true)]
    [InlineData("**/site.css", "ui/web/other.css", false)]
    [InlineData("ui/**/site.css", "ui/site.css", true)]
    [InlineData("ui/**/site.css", "ui/a/b/site.css", true)]
    [InlineData("ui/**/site.css", "docs/a/site.css", false)]
    [InlineData("src/**/*.cs", "src/a/b/c.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b/c.ts", false)]
    [InlineData("a/**/**/b", "a/b", true)]                   // consecutive '**' mean one
    [InlineData("src/Foo*", "src/FooBar.cs", true)]
    [InlineData("src/foo.cs", "src/Foo.cs", false)]          // ordinal: case counts
    public void IsMatch_FollowsTheGlobRules(string pattern, string path, bool expected)
        => Assert.Equal(expected, FileGlob.IsMatch(pattern, path));

    [Theory]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("/src/a.cs", "src/a.cs")]
    [InlineData("src\\sub\\a.cs", "src/sub/a.cs")]
    [InlineData("src//sub/./a.cs", "src/sub/a.cs")]
    [InlineData("  src/a.cs  ", "src/a.cs")]
    [InlineData("docs/", "docs/**")]                         // a trailing '/' names a directory
    [InlineData("docs/**/", "docs/**")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(".", "")]
    public void Normalize_GivesTheCanonicalForm(string entry, string expected)
        => Assert.Equal(expected, FileGlob.Normalize(entry));

    [Fact]
    public void Normalize_Null_IsEmpty() => Assert.Equal("", FileGlob.Normalize(null));

    [Theory]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("src\\a.cs", "src/a.cs")]
    [InlineData("docs/", "docs/guide/a.md")]
    public void IsMatch_NormalizesBothSides(string pattern, string path)
        => Assert.True(FileGlob.IsMatch(pattern, path));

    [Theory]
    [InlineData("", "src/a.cs")]
    [InlineData("src/a.cs", "")]
    [InlineData(null, "src/a.cs")]
    [InlineData("**", null)]
    public void IsMatch_BlankMatchesNothing(string? pattern, string? path)
        => Assert.False(FileGlob.IsMatch(pattern, path));

    [Theory]
    // literal against literal
    [InlineData("src/a.cs", "src/a.cs", true)]
    [InlineData("src/a.cs", "src/b.cs", false)]
    // literal against glob, both ways
    [InlineData("src/a.cs", "src/*.cs", true)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/sub/a.cs", "src/*.cs", false)]
    // glob against glob
    [InlineData("src/**", "src/a/*.cs", true)]
    [InlineData("src/a/*.cs", "src/**", true)]
    [InlineData("src/**", "docs/**", false)]
    [InlineData("ui/*.css", "ui/site*", true)]               // both can name ui/site.css
    [InlineData("ui/*.css", "ui/*.js", false)]
    [InlineData("ui/a*.css", "ui/b*.css", false)]
    [InlineData("ui/a*", "ui/*z", true)]                     // "az"
    [InlineData("ui/a?c", "ui/?bc", true)]                   // "abc"
    [InlineData("ui/a?c", "ui/ab", false)]
    [InlineData("**/site.css", "ui/**", true)]
    [InlineData("**/site.css", "docs/*.md", false)]
    [InlineData("**/*.cs", "**/*.ts", false)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("a/**/z", "a/b/**", true)]                   // a/b/z
    [InlineData("a/**/z", "b/**", false)]
    // a directory entry
    [InlineData("docs/", "docs/guide/a.md", true)]
    [InlineData("docs/", "src/a.cs", false)]
    public void Intersects_DecidesWhetherOneFileCanMatchBoth(string a, string b, bool expected)
    {
        Assert.Equal(expected, FileGlob.Intersects(a, b));
        Assert.Equal(expected, FileGlob.Intersects(b, a));   // symmetric
    }

    [Fact]
    public void Intersects_BlankOverlapsNothing()
    {
        Assert.False(FileGlob.Intersects("", "**"));
        Assert.False(FileGlob.Intersects("**", null));
    }

    [Fact]
    public void IsMatch_ManyStarsAgainstALongNonMatchingName_StaysFast()
    {
        // Footprints are worker-written text and run in the sweep: a backtracking matcher takes exponential time on this pair.
        var pattern = "src/" + string.Concat(Enumerable.Repeat("a*", 40)) + "b";
        var path = "src/" + new string('a', 200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(FileGlob.IsMatch(pattern, path));
        Assert.True(FileGlob.IsMatch(pattern, path + "b"));
        Assert.False(FileGlob.Intersects(pattern, "src/" + new string('a', 200)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Theory]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*a", "baba", true)]                          // the star must be able to take more after a partial match
    [InlineData("*ab", "aab", true)]
    [InlineData("a*?c", "abbc", true)]
    [InlineData("**x", "x", true)]
    public void IsMatch_StarBacktracksWithinASegment(string pattern, string name, bool expected)
        => Assert.Equal(expected, FileGlob.IsMatch(pattern, name));

    [Fact]
    public void Intersects_OnLiteralsAgreesWithIsMatch()
    {
        string[] patterns = { "src/*.cs", "src/**", "**/a.cs", "src/a?.cs", "ui/**/x.css", "docs/" };
        string[] paths = { "src/a.cs", "src/ab.cs", "src/x/a.cs", "ui/x.css", "ui/a/b/x.css", "docs/g/a.md", "other.txt" };
        foreach (var p in patterns)
            foreach (var f in paths)
                Assert.Equal(FileGlob.IsMatch(p, f), FileGlob.Intersects(p, f));
    }
}
