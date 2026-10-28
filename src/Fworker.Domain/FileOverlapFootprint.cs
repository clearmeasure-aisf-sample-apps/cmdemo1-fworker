using System.Text.RegularExpressions;

namespace Fworker.Domain;

// File-overlap hold (D6, docs/design/delivery-speed.md section 7), part 2: the footprint of an item, the set of paths and globs its change touches.
// It starts as the `Files:` line of the Technical Design and is replaced by the pull request's changed files after the first push.

public enum FootprintSource
{
    /// <summary>No `Files:` line was found and no pull request has been pushed: the footprint is unknown (empty).</summary>
    None,
    /// <summary>Parsed from the Technical Design's `Files:` line (paths and globs).</summary>
    Design,
    /// <summary>The changed files of the pull request (paths).</summary>
    PullRequest,
}

public sealed record Footprint(IReadOnlyList<string> Entries, FootprintSource Source)
{
    public static readonly Footprint Empty = new(Array.Empty<string>(), FootprintSource.None);

    public bool IsEmpty => Entries.Count == 0;

    static readonly Regex FilesLine = new(@"^\s*Files\s*:(?<list>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Annotation = new(@"\s+\([^)]*\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The entries of the `Files: path, path` line of a Technical Design body (the convention of factory-technical-design). The line is read at the start
    /// of a line, entries are split at commas, stripped of backticks, quotes and a trailing "(new)" style remark, and normalized (<see cref="FileGlob.Normalize"/>);
    /// blank entries and repeats go. A body can hold several designs (a later design is appended, the latest counts): the LAST `Files:` line wins.
    /// No such line: empty.
    /// </summary>
    public static IReadOnlyList<string> ParseFilesLine(string? body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<string>();
        string? list = null;
        foreach (var line in body.Split('\n'))
        {
            var m = FilesLine.Match(line.TrimEnd('\r'));
            if (m.Success) list = m.Groups["list"].Value;
        }
        if (list is null) return Array.Empty<string>();
        var entries = new List<string>();
        foreach (var raw in list.Split(','))
        {
            var e = Annotation.Replace(raw.Trim().Trim('`', '"', '\'').Trim(), "");
            e = FileGlob.Normalize(e.Trim('`', '"', '\'').Trim());
            if (e != "" && !entries.Contains(e)) entries.Add(e);
        }
        return entries;
    }

    /// <summary>The footprint a Technical Design body declares (<see cref="FootprintSource.None"/> and empty when it names no files).</summary>
    public static Footprint FromDesign(string? body)
    {
        var entries = ParseFilesLine(body);
        return entries.Count == 0 ? Empty : new Footprint(entries, FootprintSource.Design);
    }

    /// <summary>
    /// The footprint in force: once the pull request has changed files (<paramref name="pullRequestFiles"/> not null and not empty) they replace the design's
    /// entries completely (what was pushed is what the item touches, whatever the design said); before the first push, or when the list is empty or unknown,
    /// the design's footprint stays (an empty list is no evidence, and holding more is the safe side).
    /// </summary>
    public static Footprint Resolve(Footprint design, IEnumerable<string>? pullRequestFiles)
    {
        var files = (pullRequestFiles ?? Array.Empty<string>()).Select(FileGlob.Normalize).Where(x => x != "").Distinct().ToList();
        return files.Count == 0 ? design : new Footprint(files, FootprintSource.PullRequest);
    }

    /// <summary>
    /// Is the entry covered by one of the ignore patterns (`schedule.overlapIgnore`)? An ignore pattern without '/' ("package-lock.json", "*.md") names a file
    /// in any directory; one with '/' is anchored at the repository root ("docs/**", "ui/generated/"). The pattern is matched against the entry's own text, so a
    /// glob entry is ignored only when the pattern covers it ("docs/**" covers "docs/*.md"); a glob that reaches outside an ignored area is kept.
    /// </summary>
    public static bool IsIgnored(string entry, IEnumerable<string>? ignore)
    {
        if (ignore is null) return false;
        foreach (var raw in ignore)
        {
            var p = FileGlob.Normalize(raw);
            if (p == "") continue;
            if (!p.Contains('/')) p = "**/" + p;
            if (FileGlob.IsMatch(p, entry)) return true;
        }
        return false;
    }

    /// <summary>The footprint without the ignored entries.</summary>
    public Footprint WithoutIgnored(IEnumerable<string>? ignore)
    {
        var patterns = ignore?.ToList();
        if (patterns is null || patterns.Count == 0) return this;
        var kept = Entries.Where(e => !IsIgnored(e, patterns)).ToList();
        return kept.Count == Entries.Count ? this : new Footprint(kept, kept.Count == 0 ? FootprintSource.None : Source);
    }

    /// <summary>Do the two footprints share a file? True when any entry of one can name a file that an entry of the other names too.</summary>
    public bool Overlaps(Footprint other)
        => Entries.Any(a => other.Entries.Any(b => FileGlob.Intersects(a, b)));
}
