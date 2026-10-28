namespace Fworker.Domain;

// File-overlap hold (D6, docs/design/delivery-speed.md section 7), part 1: the path patterns. A footprint entry is a repository-relative path or a glob:
// '/' separates segments, '*' is any run of characters inside one segment, '?' is one character inside a segment, a segment that is exactly '**' is any
// number of segments (also none). Matching is ordinal, so case counts (git does). There is no v1 code to match: the rule is new, and the specification
// tests in tests/Fworker.Domain.Tests/FileOverlap*.cs are its definition. Pure values: no I/O, no clock.
public static class FileGlob
{
    /// <summary>
    /// The canonical form of one entry: '\' becomes '/', a leading "./" or '/' goes, empty and "." segments go, repeated '/' collapse, and a trailing '/'
    /// (a directory) becomes "/**". Null or blank gives "".
    /// </summary>
    public static string Normalize(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return "";
        var s = entry.Trim().Replace('\\', '/');
        var directory = s.EndsWith('/');
        var segments = s.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToList();
        if (segments.Count == 0) return "";
        if (directory && segments[^1] != "**") segments.Add("**");
        return string.Join('/', segments);
    }

    /// <summary>The pattern as segments; consecutive "**" collapse to one (they mean the same).</summary>
    static string[] Segments(string normalized)
    {
        var result = new List<string>();
        foreach (var s in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == "**" && result.Count > 0 && result[^1] == "**") continue;
            result.Add(s);
        }
        return result.ToArray();
    }

    /// <summary>Does <paramref name="pattern"/> match the path <paramref name="path"/> (both normalized here first)? A blank pattern or path matches nothing.</summary>
    public static bool IsMatch(string? pattern, string? path)
    {
        var p = Normalize(pattern);
        var f = Normalize(path);
        if (p == "" || f == "") return false;
        return MatchSegments(Segments(p), 0, f.Split('/'), 0, new Dictionary<(int, int), bool>());
    }

    static bool MatchSegments(string[] pat, int i, string[] path, int j, Dictionary<(int, int), bool> memo)
    {
        if (memo.TryGetValue((i, j), out var known)) return known;
        bool r;
        if (i == pat.Length) r = j == path.Length;
        else if (pat[i] == "**") r = MatchSegments(pat, i + 1, path, j, memo) || (j < path.Length && MatchSegments(pat, i, path, j + 1, memo));
        else r = j < path.Length && MatchSegment(pat[i], path[j]) && MatchSegments(pat, i + 1, path, j + 1, memo);
        memo[(i, j)] = r;
        return r;
    }

    // Linear-time glob for one segment (worker-written patterns run in the sweep): on a mismatch fall back to the last '*' and let it take one more
    // character. Only the most recent '*' needs revisiting, so the work is at most pattern length times text length, never exponential.
    static bool MatchSegment(string pat, string text)
    {
        int i = 0, j = 0, star = -1, mark = 0;
        while (j < text.Length)
        {
            if (i < pat.Length && pat[i] == '*') { star = i++; mark = j; }
            else if (i < pat.Length && (pat[i] == '?' || pat[i] == text[j])) { i++; j++; }
            else if (star >= 0) { i = star + 1; j = ++mark; }
            else return false;
        }
        while (i < pat.Length && pat[i] == '*') i++;
        return i == pat.Length;
    }

    /// <summary>
    /// Can some path match both patterns? Two footprint entries overlap when they can name one file: a literal path against a glob is plain matching, two
    /// globs ("src/**" and "src/a/*.cs", "ui/*.css" and "ui/site*") are decided without listing files. A blank entry overlaps nothing.
    /// </summary>
    public static bool Intersects(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x == "" || y == "") return false;
        return IntersectSegments(Segments(x), 0, Segments(y), 0, new Dictionary<(int, int), bool>());
    }

    // A wildcard side either produces nothing (skip it) or produces what the other side produces next (the other side advances, the wildcard stays).
    // Two wildcards facing each other are covered by the same two moves. Two plain elements must agree and both advance.
    static bool IntersectSegments(string[] a, int i, string[] b, int j, Dictionary<(int, int), bool> memo)
    {
        if (memo.TryGetValue((i, j), out var known)) return known;
        bool r;
        if (i == a.Length && j == b.Length) r = true;
        else if (i < a.Length && a[i] == "**")
            r = IntersectSegments(a, i + 1, b, j, memo) || (j < b.Length && IntersectSegments(a, i, b, j + 1, memo));
        else if (j < b.Length && b[j] == "**")
            r = IntersectSegments(a, i, b, j + 1, memo) || (i < a.Length && IntersectSegments(a, i + 1, b, j, memo));
        else
            r = i < a.Length && j < b.Length && SegmentsIntersect(a[i], 0, b[j], 0, new Dictionary<(int, int), bool>()) && IntersectSegments(a, i + 1, b, j + 1, memo);
        memo[(i, j)] = r;
        return r;
    }

    static bool SegmentsIntersect(string a, int i, string b, int j, Dictionary<(int, int), bool> memo)
    {
        if (memo.TryGetValue((i, j), out var known)) return known;
        bool r;
        if (i == a.Length && j == b.Length) r = true;
        else if (i < a.Length && a[i] == '*')
            r = SegmentsIntersect(a, i + 1, b, j, memo) || (j < b.Length && SegmentsIntersect(a, i, b, j + 1, memo));
        else if (j < b.Length && b[j] == '*')
            r = SegmentsIntersect(a, i, b, j + 1, memo) || (i < a.Length && SegmentsIntersect(a, i + 1, b, j, memo));
        else
            r = i < a.Length && j < b.Length && (a[i] == '?' || b[j] == '?' || a[i] == b[j]) && SegmentsIntersect(a, i + 1, b, j + 1, memo);
        memo[(i, j)] = r;
        return r;
    }
}
