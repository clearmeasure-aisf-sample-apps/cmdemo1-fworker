using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// `dotnet FakeGitHub.dll merge-golden --from <dir>[,<dir>...] [--from <dir>] --out <dir>`: makes golden files from the records of several runs of
// the same code (tests/scenarios/golden/README.md: Recording a new baseline from CI). A port of Merge-GoldenWrites, Read-WritesSections -Later,
// Get-WriteObject, Group-CauseLines and Compare-CauseLines of tests/scenarios/lib.ps1, which now only calls this (so a baseline is made without
// a PowerShell runtime): the same files found, the same text written byte for byte, the same messages. Where the PowerShell compared or
// sorted with the current culture (Sort-Object, -cne, a hashtable's case-insensitive keys) this does too, so the two never disagree.
static class GoldenMerge
{
    // Each record of a scenario: cause ('= ', in order), background ('+ ', a set), tail ('~ ', a set), later (the '~ ' lines of its
    // <slug>.later.txt) and other (a line of no section: an older format)
    sealed record Sections(List<string> Cause, HashSet<string> Background, HashSet<string> Tail, HashSet<string> Later, List<string> Other);
    sealed record Diff(string Object, int Index, string? Expected, string? Actual);

    static readonly UTF8Encoding Utf8 = new(false);
    static readonly Regex ObjectToken = new("\\{#(?:pr\\d+|\\d+)\\}");
    // Sort-Object -CaseSensitive, -cne: the current culture, case-sensitive; Sort-Object and a hashtable's keys: the current culture, ignoring case
    static readonly StringComparer CultureCase = StringComparer.Create(CultureInfo.CurrentCulture, false);
    static readonly StringComparer CultureNoCase = StringComparer.Create(CultureInfo.CurrentCulture, true);

    /// <summary>The command line after "merge-golden". 0: merged; 2: a usage error (nothing written).</summary>
    public static int Run(string[] args)
    {
        var from = new List<string>(); string? outDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--from" && i + 1 < args.Length) from.Add(args[++i]);
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else return Usage($"unknown or incomplete argument '{args[i]}'");
        }
        var dirs = from.SelectMany(f => f.Split(',')).Select(d => d.Trim()).Where(d => d != "").ToList();
        if (dirs.Count == 0) return Usage("name at least one --from directory");
        if (string.IsNullOrEmpty(outDir)) return Usage("name the --out directory");
        foreach (var d in dirs) if (!Directory.Exists(d)) return Usage($"{d} is not a directory");
        Merge(dirs, outDir);
        return 0;
    }

    static int Usage(string why)
    {
        Console.Error.WriteLine($"merge-golden: {why}");
        Console.Error.WriteLine("usage: dotnet FakeGitHub.dll merge-golden --from <dir>[,<dir>...] [--from <dir>] --out <dir>");
        return 2;
    }

    static void Merge(List<string> dirs, string outDir)
    {
        // runs[i][key]: the records of that scenario in run i (more than one when a run recorded it twice: each is compared like a run of its own);
        // keyed as the PowerShell's hashtables were, ignoring case
        var runs = new List<Dictionary<string, List<Sections>>>();
        var headers = new Dictionary<string, List<string>>(CultureNoCase);
        // (Get-ChildItem -Recurse -File: hidden files and directories are skipped, as the default enumeration options do)
        var walk = new EnumerationOptions { RecurseSubdirectories = true };
        foreach (var d in dirs)
        {
            var map = new Dictionary<string, List<Sections>>(CultureNoCase);
            foreach (var f in Directory.EnumerateFiles(d, "*.writes.txt", walk).Select(Path.GetFullPath).OrderBy(f => f, CultureNoCase))
            {
                var key = $"{Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)}";
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Sections>();
                list.Add(ReadSections(f, later: true));
                if (!headers.ContainsKey(key))
                    headers[key] = File.ReadAllLines(f, Utf8).Where(l => l.StartsWith("# suite: ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("# scenario: ", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            runs.Add(map);
        }
        // (Sort-Object -Unique -CaseSensitive: the keys of every run, in the current culture's order, each spelling once)
        var keys = runs.SelectMany(m => m.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, CultureCase).ToList();
        int merged = 0, left = 0, unstable = 0;
        foreach (var key in keys)
        {
            var absent = Enumerable.Range(0, runs.Count).Where(i => !runs[i].ContainsKey(key)).Select(i => dirs[i]).ToList();
            if (absent.Count > 0) { Console.WriteLine($"  left out: {key} (not recorded in {string.Join(", ", absent)})"); left++; continue; }
            var recs = runs.SelectMany(m => m[key]).ToList();
            var path = Path.Combine(outDir, key);
            if (recs.Any(r => r.Other.Count > 0)) { Console.WriteLine($"  left out: {key} (a record has lines without '= ', '+ ' or '~ ': not the format of this recorder)"); left++; continue; }
            Diff? diff = null;
            for (var i = 1; i < recs.Count && diff == null; i++) diff = CompareCauseLines(recs[0].Cause, recs[i].Cause);
            if (diff != null)
            {
                unstable++;
                Console.WriteLine($"  unstable: {key} (caused writes of {diff.Object}, write {diff.Index}: '{diff.Expected}' / '{diff.Actual}')");
                if (File.Exists(path)) { File.Delete(path); Console.WriteLine($"    removed its golden file {path}"); }
                continue;
            }
            var background = new HashSet<string>(recs[0].Background, StringComparer.Ordinal);
            HashSet<string>? group = null;
            foreach (var x in recs)
            {
                var gs = new HashSet<string>(x.Background, StringComparer.Ordinal); gs.UnionWith(x.Tail); gs.UnionWith(x.Later);
                background.IntersectWith(x.Background);
                if (group == null) group = gs; else group.IntersectWith(gs);
            }
            group!.ExceptWith(background); group.ExceptWith(recs[0].Cause);
            var bl = background.ToList(); bl.Sort(StringComparer.Ordinal);
            var tl = group.ToList(); tl.Sort(StringComparer.Ordinal);
            var text = new List<string>(headers.TryGetValue(key, out var h) ? h : new List<string>()) { $"# runs: {recs.Count}" };
            text.AddRange(recs[0].Cause.Select(l => "= " + l)); text.AddRange(bl.Select(l => "+ " + l)); text.AddRange(tl.Select(l => "~ " + l));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, string.Join("\n", text) + "\n", Utf8);
            merged++;
        }
        Console.WriteLine($"merged {merged} scenarios from {runs.Count} runs into {outDir}; {unstable} unstable (no golden file), {left} left out");
    }

    // Read-WritesLines: every line but the empty ones and the '# ' header lines
    static IEnumerable<string> WritesLines(string path) => File.ReadAllLines(path, Utf8).Where(l => l != "" && !l.StartsWith("# ", StringComparison.Ordinal));

    // Read-WritesSections -Later
    static Sections ReadSections(string path, bool later)
    {
        var s = new Sections(new List<string>(), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), new List<string>());
        foreach (var l in WritesLines(path))
        {
            if (l.StartsWith("= ", StringComparison.Ordinal)) s.Cause.Add(l[2..]);
            else if (l.StartsWith("+ ", StringComparison.Ordinal)) s.Background.Add(l[2..]);
            else if (l.StartsWith("~ ", StringComparison.Ordinal)) s.Tail.Add(l[2..]);
            else s.Other.Add(l);
        }
        if (later && path.EndsWith(".writes.txt", StringComparison.Ordinal))
        {
            var lp = path[..^".writes.txt".Length] + ".later.txt";
            if (File.Exists(lp)) foreach (var l in WritesLines(lp)) if (l.StartsWith("~ ", StringComparison.Ordinal)) s.Later.Add(l[2..]);
        }
        return s;
    }

    // Get-WriteObject: the first item or pull request of a line ({#k} or {#prk}), '*' for a line that names none
    static string WriteObject(string line) { var m = ObjectToken.Match(line); return m.Success ? m.Value : "*"; }

    // Group-CauseLines: the lines of each object, in their order
    static Dictionary<string, List<string>> GroupCauseLines(List<string> lines)
    {
        var g = new Dictionary<string, List<string>>(CultureNoCase);
        foreach (var l in lines) { var k = WriteObject(l); if (!g.TryGetValue(k, out var list)) g[k] = list = new List<string>(); list.Add(l); }
        return g;
    }

    // Compare-CauseLines: the first difference of two cause sequences, per object (null when they are equal)
    static Diff? CompareCauseLines(List<string> expected, List<string> actual)
    {
        var ge = GroupCauseLines(expected); var ga = GroupCauseLines(actual);
        foreach (var o in ge.Keys.Concat(ga.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, CultureCase))
        {
            var a = ge.TryGetValue(o, out var la) ? la : new List<string>(); var b = ga.TryGetValue(o, out var lb) ? lb : new List<string>();
            var n = Math.Max(a.Count, b.Count);
            for (var i = 0; i < n; i++)
            {
                var x = i < a.Count ? a[i] : null; var y = i < b.Count ? b[i] : null;
                if (x == null || y == null || CultureCase.Compare(x, y) != 0) return new Diff(o, i + 1, x, y);
            }
        }
        return null;
    }
}
