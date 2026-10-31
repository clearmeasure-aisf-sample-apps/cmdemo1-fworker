using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Domain;

namespace Fworker.Workflows.ReadModels;

/// <summary>The checks of one head as the gateway judges them (<c>Get-ChecksFor</c>): the verdict, the names that failed or are pending, the state of each gate, and the latest run of every check by its name.</summary>
public sealed class ChecksFacts
{
    public string Status { get; init; } = "pending";
    public int Total { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> FailedChecks { get; init; } = [];
    public IReadOnlyList<string> PendingChecks { get; init; } = [];
    /// <summary>The state of each configured gate (success, failure, pending, absent), in the order of the configuration.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Gates { get; init; } = [];
    public IReadOnlyList<string> NongateFailed { get; init; } = [];
    public IReadOnlyList<string> NongateCancelled { get; init; } = [];
    /// <summary>The newest run of each check by name (names compare without regard to case).</summary>
    public IReadOnlyDictionary<string, JsonNode> Runs { get; init; } = new Dictionary<string, JsonNode>();

    /// <summary><c>Get-PublicChecks</c>: what a caller is told of the checks.</summary>
    public JsonObject Public()
    {
        var gates = new JsonObject();
        foreach (var g in Gates) gates[g.Key] = g.Value;
        return new JsonObject
        {
            ["status"] = Status, ["total"] = Total, ["failed"] = Failed, ["failed_checks"] = Strings(FailedChecks), ["pending_checks"] = Strings(PendingChecks),
            ["gates"] = gates, ["nongate_failed"] = Strings(NongateFailed),
        };
    }

    public static JsonArray Strings(IEnumerable<string> items) { var a = new JsonArray(); foreach (var s in items) a.Add(s); return a; }
}

/// <summary>The rules of Prs.ps1 and Checks.ps1 that need no GitHub: how the runs of a head become a verdict, and how a log, a check's text and a diff are cut down, cleaned and redacted before they are returned.</summary>
public static class CheckRules
{
    /// <summary>The conclusions that mean a check failed (<c>$script:CkFailed</c>).</summary>
    public static readonly string[] Failed = ["failure", "timed_out", "action_required", "startup_failure"];

    public const int LogBudget = 60000;        // characters of log excerpts in one answer, shared by the failed jobs
    public const int PageChars = 48000;        // characters of one page of a whole log or of an artifact file
    public const long MaxTextBytes = 5L * 1024 * 1024;   // hard cap of a whole log or an artifact file: of a longer log only the end is served
    public const long MaxZipBytes = 25L * 1024 * 1024;   // a larger artifact is listed, never downloaded
    public const int MaxDetailed = 6;          // failed checks that get their detail in one answer; the others are named
    public const int MaxAnnotations = 50;      // annotations returned per check (all are counted, up to 300)
    public const string TextExtensions = @"\.(sarif|json|txt|log|xml|trx|md|csv|tsv|html?|ya?ml|out|err|props|targets|config|ini|toml|diff|patch)$";

    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);
    static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex LineBreaks = new("\r\n?", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Control = new(@"[\x00-\x08\x0B-\x1F\x7F\u0080-\u009F\u200B-\u200F\u202A-\u202E\u2060-\u2069\uFEFF]", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex ErrorLine = new(@"(?i:##\[error\]|\berror\b|warning as error|test run failed|\b\d+\s+failed\b|\bfailed:\s*[1-9])|FAILED|\bFailed\b|\[FAIL\]|\bFAIL\b", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Stamp = new(@"^\uFEFF?(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.\d+)?Z ?", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Hunk = new(@"^@@ -\d+(?:,\d+)? \+(\d+)", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex LineSplit = new("\r?\n", RegexOptions.CultureInvariant, RegexTimeout);

    static DateTime Started(JsonNode run) => Ps.FromIso(Ps.Str(run, "started_at")) ?? DateTime.MinValue;

    /// <summary>
    /// <c>Get-ChecksFor</c>: strict classification. A head with zero check runs is pending, never green, and every configured gate must be present and successful. Only the gates (ciGates, the checks the ruleset requires) decide
    /// (issue 102): a failed check that is not a gate is listed (failed_checks, nongate_failed) and never makes the head red by itself: failure when a gate failed, pending while a gate is absent or running, success when every gate
    /// succeeded. Without configured gates any failed check is a failure. <paramref name="ignore"/>: check names left out of the verdict; they stay readable in the runs.
    /// </summary>
    public static ChecksFacts Classify(IEnumerable<JsonNode> checkRuns, IReadOnlyList<string> ciGates, IReadOnlyCollection<string>? ignore = null)
    {
        ignore ??= [];
        var latest = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in checkRuns)
        {
            var k = Ps.Str(r, "name");
            if (!latest.TryGetValue(k, out var cur) || Started(r) > Started(cur)) latest[k] = r;
        }
        var failed = new List<string>(); var pending = new List<string>(); var cancelled = new List<string>();
        var judged = latest.Keys.Where(k => !Ps.NameIn(k, ignore)).ToList();
        foreach (var k in judged)
        {
            var r = latest[k];
            if (!Ps.Str(r, "status").Equals("completed", StringComparison.OrdinalIgnoreCase)) { pending.Add(k); continue; }
            var conclusion = Ps.Str(r, "conclusion");
            if (Failed.Any(f => f.Equals(conclusion, StringComparison.OrdinalIgnoreCase))) failed.Add(k);
            else if (conclusion.Equals("cancelled", StringComparison.OrdinalIgnoreCase)) { pending.Add(k); cancelled.Add(k); }
        }
        var gateList = ciGates.Where(g => g.Length > 0).ToList();
        var gates = new List<KeyValuePair<string, string>>();
        foreach (var g in gateList)
        {
            if (Ps.NameIn(g, ignore)) continue;   // an ignored check is no gate either
            string state;
            if (!latest.ContainsKey(g)) state = "absent";
            else if (Ps.NameIn(g, failed)) state = "failure";
            else if (Ps.NameIn(g, pending)) state = "pending";
            else state = "success";
            gates.Add(new(g, state));
        }
        var status = "success";
        if (gateList.Count == 0)
        {
            if (failed.Count > 0) status = "failure"; else if (judged.Count == 0 || pending.Count > 0) status = "pending";
        }
        else if (gates.Any(g => g.Value == "failure")) status = "failure";
        else if (judged.Count == 0 || gates.Any(g => g.Value is "absent" or "pending")) status = "pending";
        var nongate = failed.Where(f => !Ps.NameIn(f, gateList)).OrderBy(x => x, Ps.SortOrder).ToList();
        var nongateCancelled = cancelled.Where(f => !Ps.NameIn(f, gateList)).OrderBy(x => x, Ps.SortOrder).ToList();
        return new ChecksFacts { Status = status, Total = judged.Count, Failed = failed.Count, FailedChecks = failed, PendingChecks = pending, Gates = gates, NongateFailed = nongate, NongateCancelled = nongateCancelled, Runs = latest };
    }

    // ---- text that a pull request controls --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Protect-CheckText</c>: text of a check, a log or an artifact, made safe to return: no ANSI sequences, no control characters (line breaks and tabs stay), no direction or zero-width marks, secrets redacted. <paramref name="max"/> cuts it and says so.
    /// </summary>
    public static string Protect(string? text, IReadOnlyList<string> secretPatterns, int max = 0)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Ansi.Replace(text, "");
        t = LineBreaks.Replace(t, "\n");
        t = Control.Replace(t, "");
        t = JournalText.Protect(t, secretPatterns);
        if (max > 0 && t.Length > max) t = t[..max] + $" [cut: {t.Length - max} more characters]";
        return t;
    }

    /// <summary><c>Get-TextPage</c>: one page of a text. Pages are cut from the cleaned and redacted text, so a secret never straddles two pages unredacted.</summary>
    public static (int Page, int Pages, int TotalChars, string Text) TextPage(string text, string? pageArg)
    {
        var page = 1;
        if (!string.IsNullOrEmpty(pageArg) && !int.TryParse(pageArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out page)) throw new VerbRefusal(2, "usage", "page must be a number, from 1");
        var pages = Math.Max(1, (int)Math.Ceiling(text.Length / (double)PageChars));
        if (page < 1 || page > pages) throw new VerbRefusal(2, "page-out-of-range", $"there are {pages} pages; page {page} does not exist", new JsonObject { ["pages"] = pages });
        var from = (page - 1) * PageChars;
        return (page, pages, text.Length, text.Substring(from, Math.Min(PageChars, text.Length - from)));
    }

    /// <summary><c>Get-DiffMap</c>: the added lines of every changed file of a pull request: file name to the line numbers in the new file.</summary>
    public static Dictionary<string, HashSet<int>> DiffMap(IEnumerable<JsonNode> files)
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var set = new HashSet<int>(); var ln = 0;
            foreach (var l in Ps.Str(f, "patch").Split('\n'))
            {
                if (Hunk.Match(l) is { Success: true } m) { ln = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                if (ln <= 0 || l.StartsWith('-') || l.StartsWith('\\')) continue;
                if (l.StartsWith('+')) set.Add(ln);
                ln++;
            }
            map[Ps.Str(f, "filename")] = set;
        }
        return map;
    }

    /// <summary>
    /// <c>Get-CheckAnnotations</c> without the reading: the annotations of one check run, split by where they point: a line the pull request added, another line of a file it changed, or elsewhere. Findings elsewhere were not made by this change:
    /// they point to a problem of the base branch. The ones on the change come first, at most <see cref="MaxAnnotations"/> are returned.
    /// </summary>
    public static (JsonObject Counts, JsonArray List) Annotations(IReadOnlyList<JsonNode> all, Dictionary<string, HashSet<int>>? diff, IReadOnlyList<string> secretPatterns)
    {
        int lines = 0, files = 0, other = 0;
        var rows = new List<(int Order, JsonObject Row)>();
        foreach (var an in all)
        {
            var path = Ps.Str(an, "path");
            var s = Ps.ToInt("0" + Ps.Str(an, "start_line")); var e = Ps.ToInt("0" + Ps.Str(an, "end_line")); if (e < s) e = s;
            var where = "unknown";
            if (diff is not null)
            {
                where = "elsewhere";
                if (diff.TryGetValue(path, out var set))
                {
                    where = "changed-file";
                    if (e - s <= 2000) for (var k = s; k <= e; k++) if (set.Contains(k)) { where = "changed-line"; break; }
                }
                switch (where) { case "changed-line": lines++; break; case "changed-file": files++; break; default: other++; break; }
            }
            rows.Add((where switch { "changed-line" => 0, "changed-file" => 1, "elsewhere" => 2, _ => 3 }, new JsonObject
            {
                ["path"] = Protect(path, secretPatterns, 300), ["line"] = s, ["endLine"] = e, ["level"] = Ps.Str(an, "annotation_level"), ["where"] = where,
                ["title"] = Protect(Ps.Str(an, "title"), secretPatterns, 200), ["message"] = Protect(Ps.Str(an, "message"), secretPatterns, 400),
            }));
        }
        var shown = new JsonArray();
        foreach (var r in rows.OrderBy(r => r.Order).Take(MaxAnnotations)) shown.Add(r.Row);
        var counts = new JsonObject { ["available"] = true, ["total"] = all.Count, ["returned"] = shown.Count, ["countedAtMost"] = 300 };
        if (diff is not null) { counts["onChangedLines"] = lines; counts["onChangedFiles"] = files; counts["elsewhere"] = other; }
        else counts["diff"] = "unknown: the call named no pull request, so the annotations are not compared with a diff";
        return (counts, shown);
    }

    /// <summary>A failed step of a job and the span of its time stamps, to match the lines of the log to it.</summary>
    public sealed record StepRange(string From, string To);

    public sealed record Excerpt(string Text, int TotalLines, int ShownLines, int CutChars, int ErrorLines, int ErrorLinesNotShown, bool WholeJob);

    /// <summary>
    /// <c>Get-LogExcerpt</c>: the last <paramref name="tail"/> lines of every failed step (the lines are matched to a step by their time stamps; when no step can be matched, the last <paramref name="tail"/> lines of the job), plus every line that
    /// looks like an error with two lines around it, in log order, cleaned and redacted, and cut from the start when it is longer than <paramref name="budget"/> characters. The time stamps are removed to save room.
    /// </summary>
    public static Excerpt LogExcerpt(string text, IReadOnlyList<StepRange> failedSteps, int tail, int budget, IReadOnlyList<string> secretPatterns)
    {
        var lines = LineSplit.Split(text);
        var n = lines.Length; if (n > 0 && lines[n - 1] == "") n--;
        var sec = new string[n]; var body = new string[n]; var cur = "";
        for (var i = 0; i < n; i++)
        {
            var m = Stamp.Match(lines[i]);
            if (m.Success) { cur = m.Groups[1].Value; body[i] = lines[i][m.Length..]; } else body[i] = lines[i];
            sec[i] = cur;
        }
        var pick = new SortedSet<int>();
        foreach (var s in failedSteps)
        {
            if (string.IsNullOrEmpty(s.From)) continue;
            var idx = new List<int>();
            for (var i = 0; i < n; i++) if (sec[i].Length > 0 && string.CompareOrdinal(sec[i], s.From) >= 0 && string.CompareOrdinal(sec[i], s.To) <= 0) idx.Add(i);
            for (var k = Math.Max(0, idx.Count - tail); k < idx.Count; k++) pick.Add(idx[k]);
        }
        var whole = pick.Count == 0;
        if (whole) for (var i = Math.Max(0, n - tail); i < n; i++) pick.Add(i);
        int found = 0, shownErr = 0;
        for (var i = 0; i < n; i++)
        {
            if (!ErrorLine.IsMatch(body[i])) continue;
            found++; if (shownErr >= 150) continue;
            shownErr++;
            for (var k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) pick.Add(k);
        }
        var sb = new StringBuilder(); var prev = -1;
        foreach (var i in pick)
        {
            if (i != prev + 1) sb.Append($"[... {i - prev - 1} lines not shown ...]\n");
            sb.Append(body[i]).Append('\n'); prev = i;
        }
        if (prev < n - 1) sb.Append($"[... {n - 1 - prev} lines not shown ...]\n");
        var t = Protect(sb.ToString(), secretPatterns); var cut = 0;
        if (t.Length > budget)
        {
            cut = t.Length - budget; t = t[cut..];
            var nl = t.IndexOf('\n');
            if (nl >= 0 && nl < t.Length - 1) { cut += nl + 1; t = t[(nl + 1)..]; }
            t = $"[... cut: the first {cut} characters of this excerpt are not shown (limit {budget}) ...]\n{t}";
        }
        return new Excerpt(t, n, pick.Count, cut, found, Math.Max(0, found - shownErr), whole);
    }

    // ---- one check run -----------------------------------------------------------------------------------------------------------------------------

    /// <summary><c>ConvertTo-IsoOrNull</c>.</summary>
    public static string? IsoOrNull(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        try { return Ps.ToIso(Ps.FromIso(v)!.Value); } catch (FormatException) { return v; }
    }

    /// <summary><c>Get-CheckBrief</c>.</summary>
    public static JsonObject Brief(JsonNode run, IReadOnlyList<string> secretPatterns) => new()
    {
        ["name"] = Ps.Str(run, "name"), ["status"] = Ps.Str(run, "status"), ["conclusion"] = Ps.Prop(run, "conclusion")?.DeepClone(),
        ["startedAt"] = IsoOrNull(Ps.Str(run, "started_at")), ["completedAt"] = IsoOrNull(Ps.Str(run, "completed_at")),
        ["detailsUrl"] = Protect(Ps.Str(run, "details_url"), secretPatterns, 500),
    };

    /// <summary><c>Test-CheckNeedsDetail</c>: finished and failed (or cancelled).</summary>
    public static bool NeedsDetail(JsonNode run) =>
        Ps.Str(run, "status").Equals("completed", StringComparison.OrdinalIgnoreCase) && (Failed.Append("cancelled").Any(c => c.Equals(Ps.Str(run, "conclusion"), StringComparison.OrdinalIgnoreCase)));

    /// <summary><c>Test-ActionsJob</c>: a check run made by GitHub Actions.</summary>
    public static bool ActionsJob(JsonNode run) => Ps.Str(Ps.Prop(run, "app"), "slug").Equals("github-actions", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Get-CheckBaseline</c>: the baseline of one check: its newest finished conclusion on the base branch and how many of the last 5 commits failed it.</summary>
    public static JsonObject Baseline(string name, BaseHistory history)
    {
        if (!history.Available) return new JsonObject { ["available"] = false, ["branch"] = history.Branch, ["code"] = history.Code, ["reason"] = history.Reason };
        string? newest = null; var failed = 0; var ran = 0;
        foreach (var c in history.Commits)
        {
            if (!c.Runs.TryGetValue(name, out var run)) continue;
            if (!Ps.Str(run, "status").Equals("completed", StringComparison.OrdinalIgnoreCase)) continue;
            ran++; var con = Ps.Str(run, "conclusion");
            newest ??= con;
            if (Failed.Any(f => f.Equals(con, StringComparison.OrdinalIgnoreCase))) failed++;
        }
        return new JsonObject { ["available"] = true, ["branch"] = history.Branch, ["conclusion"] = newest ?? "absent", ["failedOfLast5"] = failed, ["ranOfLast5"] = ran, ["commits"] = history.Commits.Count };
    }
}

/// <summary>The checks of the last 5 commits of the base branch (<c>Get-BaseHistory</c>), newest first, or why they could not be read.</summary>
public sealed class BaseHistory
{
    public bool Available { get; init; }
    public string Branch { get; init; } = "";
    public string Code { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<(string Sha, IReadOnlyDictionary<string, JsonNode> Runs)> Commits { get; init; } = [];
}

/// <summary>Which of some checks fail on the base branch now (<c>Get-BaseFailures</c>, Prs.ps1): the ones whose newest finished conclusion there failed, and the ones that passed. Not available when the base branch's checks could not be read.</summary>
public sealed record BaseFailures(bool Available, IReadOnlyList<string> Preexisting, IReadOnlyList<string> Green);
