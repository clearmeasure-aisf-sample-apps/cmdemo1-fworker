using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Fworker.Database;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>Where the repository is, found from the test binary upwards.</summary>
internal static class RepoPaths
{
    public static readonly string Root = Find();
    static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "FactoryWorker.sln"))) return d.FullName;
        throw new InvalidOperationException("the repository root (FactoryWorker.sln) was not found above " + AppContext.BaseDirectory);
    }
    public static string ScriptsDir => Path.Combine(Root, "src", "Fworker.Database", "scripts", "Update");
}

/// <summary>
/// The scripts of Fworker.Database as files (no database needed): numbered, unique, idempotent, expand-only and encoded the way the journal and the rules expect. The rules are in
/// docs/design/fworker-database.md; what a script builds is checked against the EF model and Wolverine's own model in the SQL tests.
/// </summary>
public class DatabaseScriptsTests
{
    static IEnumerable<string> Files() => Directory.EnumerateFiles(RepoPaths.ScriptsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>The code of a script: line comments and block comments blanked.</summary>
    static string Code(string sql) => Regex.Replace(Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"--[^\n]*", " ");

    static string Code(string name, bool embedded) => Code(embedded ? EmbeddedText(name) : File.ReadAllText(Path.Combine(RepoPaths.ScriptsDir, name)));

    static string EmbeddedText(string name)
    {
        using var s = typeof(FactorySchema).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>The batches of a script (the lines "GO" separate them), as DbUp's splitter sees them.</summary>
    static IEnumerable<string> Batches(string code) => Regex.Split(code, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b));

    [Fact]
    public void EveryNameIsNumberedAndDescribed_TheNumbersAreUniqueAndContiguous()
    {
        Assert.NotEmpty(FactorySchema.Scripts);
        var rx = new Regex(@"^(\d{3})_[A-Za-z][A-Za-z0-9_]*\.sql$");
        foreach (var n in FactorySchema.Scripts) Assert.Matches(rx, n);
        var numbers = FactorySchema.Scripts.Select(n => int.Parse(rx.Match(n).Groups[1].Value)).ToList();
        Assert.Equal(numbers.Distinct().Count(), numbers.Count);
        // the main series 001..099 is contiguous; the parallel waves of the implementation plan each have a reserved block of ten from 100 (W8.1: 100-109, W8.2: 110-119, W8.3: 120-129), which is not
        var main = numbers.Where(n => n < 100).Order().ToList();
        Assert.Equal(Enumerable.Range(1, main.Count), main);
        Assert.All(numbers.Where(n => n >= 100), n => Assert.InRange(n, 100, 999));
        Assert.Equal(numbers.Max(), FactorySchema.LatestVersion);
    }

    [Fact]
    public void TheEmbeddedSetIsTheFilesOfTheFolder()
    {
        Assert.Equal(Files().Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal), FactorySchema.Scripts);
        foreach (var f in Files()) Assert.Equal(File.ReadAllText(f).Replace("\r\n", "\n"), EmbeddedText(Path.GetFileName(f)!).Replace("\r\n", "\n"));
    }

    [Fact]
    public void NoScriptDropsOrTruncates_AndOnlyProcedureBodiesDeleteOrUpdate()
    {
        foreach (var n in FactorySchema.Scripts)
        {
            var code = Code(n, embedded: true);
            Assert.DoesNotMatch(@"(?i)\bDROP\b", code);
            Assert.DoesNotMatch(@"(?i)\bTRUNCATE\b", code);
            // DELETE and UPDATE exist only in the bodies of Wolverine's procedures (script 011): a script never changes a row
            foreach (var b in Batches(code))
                if (!Regex.IsMatch(b, @"^\s*CREATE\s+OR\s+ALTER\s+PROCEDURE\b", RegexOptions.IgnoreCase))
                    Assert.DoesNotMatch(@"(?i)\b(DELETE|UPDATE)\b", Regex.Replace(Regex.Replace(b, @"'[^']*'", "''"), @"(?i)\bON\s+(DELETE|UPDATE)\s+(CASCADE|NO\s+ACTION|SET\s+\w+)", ""));   // (not in a string: PRINT 'The database update succeeded')
        }
    }

    [Fact]
    public void EveryCreateAndAlterIsGuarded_SoTheScriptsAreNoOpsOnADatabaseThatHasThemAlready()
    {
        foreach (var n in FactorySchema.Scripts)
        {
            var code = Code(n, embedded: true);
            foreach (Match m in Regex.Matches(code, @"\bCREATE\s+TABLE\s+\[?(?<s>\w+)\]?\.\[?(?<t>\w+)\]?", RegexOptions.IgnoreCase))
                Assert.True(Guarded(code, m.Index, $@"IF\s+OBJECT_ID\s*\(\s*N?'{m.Groups["s"].Value}\.{m.Groups["t"].Value}'\s*(,\s*N'U'\s*)?\)\s+IS\s+NULL"), $"{n}: CREATE TABLE {m.Groups["s"]}.{m.Groups["t"]} has no IF OBJECT_ID guard");
            foreach (Match m in Regex.Matches(code, @"\bCREATE\s+(UNIQUE\s+)?(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\s+\[?(?<i>\w+)\]?\s+ON\s+\[?(?<s>\w+)\]?\.\[?(?<t>\w+)\]?", RegexOptions.IgnoreCase))
                Assert.True(Guarded(code, m.Index, $@"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.indexes\s+WHERE\s+name\s*=\s*N'{m.Groups["i"].Value}'\s+AND\s+object_id\s*=\s*OBJECT_ID\s*\(\s*N'{m.Groups["s"].Value}\.{m.Groups["t"].Value}'\s*\)\s*\)"), $"{n}: CREATE INDEX {m.Groups["i"]} has no sys.indexes guard");
            foreach (Match m in Regex.Matches(code, @"\bALTER\s+TABLE\s+\[?(?<s>\w+)\]?\.\[?(?<t>\w+)\]?\s+ADD\s+(?<what>CONSTRAINT\s+\[?(?<c>\w+)\]?|\[?(?<col>\w+)\]?)", RegexOptions.IgnoreCase))
            {
                var guard = m.Groups["c"].Success ? $@"IF\s+OBJECT_ID\s*\(\s*N'{m.Groups["s"].Value}\.{m.Groups["c"].Value}'\s*,\s*N'F'\s*\)\s+IS\s+NULL"
                    : $@"IF\s+COL_LENGTH\s*\(\s*N'{m.Groups["s"].Value}\.{m.Groups["t"].Value}'\s*,\s*N'{m.Groups["col"].Value}'\s*\)\s+IS\s+NULL";
                Assert.True(Guarded(code, m.Index, guard), $"{n}: ALTER TABLE {m.Groups["s"]}.{m.Groups["t"]} ADD {m.Groups["what"]} has no guard");
            }
            foreach (Match m in Regex.Matches(code, @"\bCREATE\s+SCHEMA\b", RegexOptions.IgnoreCase))
                Assert.True(Guarded(code, m.Index, @"IF\s+(NOT\s+EXISTS\s*\(\s*SELECT\s+\*\s+FROM\s+sys\.schemas|SCHEMA_ID\s*\(\s*N'\w+'\s*\)\s+IS\s+NULL)"), $"{n}: CREATE SCHEMA has no guard");
            foreach (Match m in Regex.Matches(code, @"\bCREATE\s+TYPE\b", RegexOptions.IgnoreCase))
                Assert.True(Guarded(code, m.Index, @"IF\s+TYPE_ID\s*\("), $"{n}: CREATE TYPE has no guard");
        }
    }

    // the guard is the text just before the statement (the statement is its body or sits in the BEGIN of it)
    static bool Guarded(string code, int at, string guard) => Regex.IsMatch(code[Math.Max(0, at - 320)..at], guard + @"[\s\S]{0,60}$", RegexOptions.IgnoreCase);

    [Fact]
    public void EveryProcedureIsCreateOrAlter()
    {
        foreach (var n in FactorySchema.Scripts)
            Assert.DoesNotMatch(@"(?i)\bCREATE\s+PROCEDURE\b|\bCREATE\s+PROC\b", Code(n, embedded: true));
    }

    [Fact]
    public void TheHandWrittenScriptsOwnTheirTransaction()
    {
        // the modern convention of cmdemo1-workorders: BEGIN TRY / BEGIN TRANSACTION ... COMMIT / CATCH ROLLBACK; THROW. Script 011 is generated and cannot (CREATE OR ALTER PROCEDURE starts its batch).
        foreach (var n in FactorySchema.Scripts.Where(n => !n.Contains("Wolverine", StringComparison.Ordinal)))
        {
            var code = Code(n, embedded: true);
            Assert.Contains("BEGIN TRY", code);
            Assert.Contains("BEGIN TRANSACTION", code);
            Assert.Contains("COMMIT TRANSACTION", code);
            Assert.Matches(@"IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\s+THROW;", code);
        }
    }

    [Fact]
    public void EveryFileIsUtf8WithoutBom()
    {
        foreach (var f in Files())
        {
            var bytes = File.ReadAllBytes(f);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, Path.GetFileName(f) + " starts with a BOM");
            Assert.False(bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)), Path.GetFileName(f) + " is UTF-16");
            _ = new UTF8Encoding(false, true).GetString(bytes);   // throws on an invalid sequence
        }
    }

    [Fact]
    public void ThePlantedViolationsAreSeen()
    {
        // the lint is not vacuous
        Assert.False(Guarded("CREATE TABLE factory.X (a int)", 0, @"IF\s+OBJECT_ID"));
        Assert.True(Guarded("IF OBJECT_ID(N'factory.X', N'U') IS NULL\nBEGIN\nCREATE TABLE factory.X (a int)", "IF OBJECT_ID(N'factory.X', N'U') IS NULL\nBEGIN\n".Length, @"IF\s+OBJECT_ID\s*\(\s*N?'factory\.X'\s*(,\s*N'U'\s*)?\)\s+IS\s+NULL"));
        Assert.Matches(@"(?i)\bDROP\b", Code("-- drop in a comment\nDROP TABLE x"));
        Assert.DoesNotMatch(@"(?i)\bDROP\b", Code("-- drop in a comment\nSELECT 1 /* DROP */"));
    }
}
