using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The golden-write recorder of Tier 1 (docs: tests/scenarios/golden/README.md). With FAKE_RECORD_DIR set, every write that reaches the fake
// (POST, PUT, PATCH, DELETE and GraphQL mutations; not /_fake/*, not the credential mints) is kept for the open scenario, and
// /_fake/scenario-end writes it normalized to <dir>/<suite>/<slug>.writes.txt (the sorted, distinct lines) and raw to <slug>.raw.jsonl.
// Recording only observes: the fake answers every request exactly as it does without it. Every member is called under the fake's one lock.
sealed class WriteRecorder
{
    public const string RepoPath = "/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders";
    // readable lines: no \u escapes for quotes, angle brackets and the like (the files are compared as text, never parsed by a browser)
    static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed record Entry(long Seq, long Ms, string Method, string Path, string Query, int Status, string Source, JsonNode? Body, string? Field);

    readonly string dir;
    readonly List<Entry> entries = new();
    readonly Stopwatch clock = new();
    Dictionary<string, long> bases = new();
    string? suite, scenario, slug;
    long seq;

    /// <summary>Writes made while no scenario was open (between scenarios, or before the first): counted, not kept.</summary>
    public long Unlabelled { get; private set; }

    public WriteRecorder(string dir) { this.dir = Path.GetFullPath(dir); }

    /// <summary>The recorder of FAKE_RECORD_DIR, or null when it is unset (recording off).</summary>
    public static WriteRecorder? FromEnvironment() => Environment.GetEnvironmentVariable("FAKE_RECORD_DIR") is { Length: > 0 } d ? new WriteRecorder(d) : null;

    /// <summary>Whether a request is a write the recorder keeps (the fake's own control and the credential mints are not).</summary>
    public static bool IsWrite(string method, string path, JsonNode? body, out string? field)
    {
        field = null;
        if (path.StartsWith("/_fake/") || path == "/token/v1" || Regex.IsMatch(path, "^/app/installations/[^/]+/access_tokens$")) return false;
        if (path == "/graphql" && method == "POST")
        {
            string q; try { q = body?["query"]?.ToString() ?? ""; } catch { q = ""; }
            var t = q.TrimStart();
            if (!t.StartsWith("mutation", StringComparison.Ordinal)) return false;
            var brace = t.IndexOf('{');
            var fm = brace < 0 ? Match.Empty : Regex.Match(t[(brace + 1)..], "^\\s*(?:[A-Za-z_][A-Za-z0-9_]*\\s*:\\s*)?([A-Za-z_][A-Za-z0-9_]*)");
            field = fm.Success ? fm.Groups[1].Value : "?";
            return true;
        }
        return method is "POST" or "PUT" or "PATCH" or "DELETE";
    }

    public static string SourceOf(string userAgent) =>
        userAgent.StartsWith("factory-worker-gateway", StringComparison.Ordinal) ? "rule" : userAgent.Contains("PowerShell", StringComparison.Ordinal) ? "harness" : "gateway";

    public void Record(string method, string path, string query, int status, string userAgent, JsonNode? body, string? field)
    {
        if (scenario == null) { Unlabelled++; return; }
        entries.Add(new Entry(++seq, clock.ElapsedMilliseconds, method, path, query, status, SourceOf(userAgent), body, field));
    }

    public JsonObject Begin(JsonNode? b, Dictionary<string, long> worldBases)
    {
        suite = Safe(b?["suite"]?.ToString() ?? "");
        scenario = b?["scenario"]?.ToString() ?? "";
        slug = Safe(b?["scenarioSlug"]?.ToString() ?? "");
        if (suite == "") suite = "unnamed";
        if (slug == "") slug = "scenario-" + Hash8(scenario);
        bases = new Dictionary<string, long>(worldBases);
        entries.Clear(); seq = 0; clock.Restart();
        return new JsonObject { ["ok"] = true, ["recording"] = true };
    }

    public (int, object?) End()
    {
        if (scenario == null) return (200, new JsonObject { ["ok"] = false, ["recording"] = true, ["reason"] = "no scenario is open" });
        var norm = new Normalizer(bases);
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        var raw = new StringBuilder();
        int kept = 0, leftover = 0, timing = 0;
        // A factory-state comment is created once and then rewritten many times, and how many snapshots there are (and so whether the last
        // one is the POST or a PATCH) depends on timing. So the accepted writes of one (source, item's state comment) give one line,
        // "<source> STATE <kind> #k"; a refused one (status 300 or more) keeps its own line with the marker body.
        var state = entries.Select(e => norm.StateOf(e.Body)).ToList();
        var lastState = new Dictionary<string, long>();
        for (var i = 0; i < entries.Count; i++) if (state[i] != null && entries[i].Status < 300) lastState[entries[i].Source + " " + state[i]] = entries[i].Seq;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var path = norm.Path(e.Path, e.Query);
            var left = path.Contains("{#old}", StringComparison.Ordinal) || state[i] is { } st && st.Contains("{#old}", StringComparison.Ordinal);
            var head = $"{e.Source} {e.Method} {path}{(e.Field != null ? " " + e.Field : "")} {e.Status} ";
            string line; var add = true;
            if (state[i] == null) line = head + (e.Body == null ? "-" : norm.Body(e.Body).ToJsonString(Plain));
            else if (e.Status >= 300) line = head + new JsonObject { ["body"] = "{factory-state " + state[i] + "}" }.ToJsonString(Plain);
            // the last snapshot itself is not compared: where the background loops have got to when the scenario ends is timing
            else if (lastState[e.Source + " " + state[i]] == e.Seq) line = $"{e.Source} STATE {state[i]}";
            else { line = head + new JsonObject { ["body"] = "{factory-state " + state[i] + "}" }.ToJsonString(Plain); add = false; }
            // the plant's sweep observations (matched, waiting) and the fold edits of a repeated journal line come and go with the sweep's
            // and the plant's timing: kept raw, counted, not compared
            var timed = !left && Normalizer.IsTimingJournal(e.Body);
            if (left) leftover++; else if (timed) timing++; else { kept++; if (add) lines.Add(line); }
            raw.Append(new JsonObject
            {
                ["seq"] = e.Seq, ["ms"] = e.Ms, ["method"] = e.Method, ["path"] = e.Path, ["query"] = e.Query, ["status"] = e.Status, ["source"] = e.Source,
                ["field"] = e.Field, ["body"] = e.Body?.DeepClone(), ["leftover"] = left, ["timing"] = timed, ["line"] = line
            }.ToJsonString(Plain)).Append('\n');
        }
        var sd = Path.Combine(dir, suite!);
        Directory.CreateDirectory(sd);
        var file = Path.Combine(sd, slug + ".writes.txt"); var rawFile = Path.Combine(sd, slug + ".raw.jsonl");
        var text = new StringBuilder();
        text.Append("# suite: ").Append(suite).Append('\n').Append("# scenario: ").Append(OneLine(scenario!)).Append('\n')
            .Append("# writes: ").Append(kept).Append('\n').Append("# leftover: ").Append(leftover).Append('\n').Append("# timing: ").Append(timing).Append('\n')
            .Append("# bases: ").Append(string.Join(' ', bases.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => b.Key + "=" + b.Value))).Append('\n');
        foreach (var l in lines) text.Append(l).Append('\n');
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(file, text.ToString(), utf8);
        File.WriteAllText(rawFile, raw.ToString(), utf8);
        scenario = null; suite = null; slug = null; entries.Clear();
        return (200, new JsonObject { ["ok"] = true, ["file"] = file, ["raw"] = rawFile, ["writes"] = kept, ["leftover"] = leftover, ["timing"] = timing, ["lines"] = lines.Count });
    }

    public JsonObject Open() => new()
    {
        ["open"] = scenario != null, ["suite"] = suite, ["scenario"] = scenario, ["unlabelledWrites"] = Unlabelled,
        ["writes"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["seq"] = e.Seq, ["ms"] = e.Ms, ["method"] = e.Method, ["path"] = e.Path, ["query"] = e.Query, ["status"] = e.Status, ["source"] = e.Source, ["field"] = e.Field, ["body"] = e.Body?.DeepClone() }).ToArray())
    };

    static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
    static string OneLine(string s) => Regex.Replace(s, "[\r\n]+", " ");
    static string Hash8(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    // The normalization of tests/scenarios/golden/README.md: numbers relative to the scenario's start, masked times, ids, tokens and ports.
    sealed class Normalizer(Dictionary<string, long> bases)
    {
        static readonly HashSet<string> ItemKeys = new(StringComparer.OrdinalIgnoreCase) { "number", "item", "issue", "pr", "pull_number" };
        // string values that are item numbers: the Octopus runbook's variable and the runtime job's environment entry
        static readonly HashSet<string> StringItemKeys = new(StringComparer.Ordinal) { "Fworker.Item", "FWORKER_ITEM" };
        static readonly string[] MaskedSuffixes = { "seconds", "ms", "minutes", "at", "time", "epoch", "since", "until", "expires", "ttl", "reads", "attempt", "attempts", "size", "len", "length", "bytes" };
        static readonly Regex Fenced = new("```json\\s*(.*?)\\s*```", RegexOptions.Singleline);
        static readonly Regex Placeholder = new("\\{[^{}]*\\}");
        static readonly string[] StateTimingKeys = { "journal" };
        static readonly Regex TimingJournal = new("<!-- plant event=(?:(?:matched|waiting)\\b|[\\w-]+ [^>]*\\bfold=\\d+ -->)");
        static readonly HashSet<string> SortedArrays = new(StringComparer.Ordinal) { "failed", "failed_checks" };
        static readonly Regex StateHead = new("^<!-- factory-state:([^>]*?)-->\\s*\\nstate of #(\\d+)");
        static readonly Regex ParenList = new("\\(((?:[^()\\n]|\\([^()\\n]*\\))*)\\)");
        static readonly Regex PassingList = new("\\bwith ([^,()\\n\"]+(?:, [^,()\\n\"]+)+) passing\\b");

        string Rel(string kind, string digits)
        {
            if (!long.TryParse(digits, out var n)) return digits;
            var b = bases.TryGetValue(kind, out var x) ? x : 1;
            // a number far above the start is not one the scenario created (a fixed number of the harness): {#far}, whatever the start
            return n < b ? "{#old}" : n - b >= 1000 ? "{#far}" : "{#" + (n - b + 1) + "}";
        }

        public string Path(string path, string query)
        {
            var p = path.Replace(RepoPath, "/repos/{repo}", StringComparison.Ordinal);
            p = IdSegment.Replace(p, m => $"/{m.Groups[1].Value}/{{id}}");
            p = Text(p);
            if (path.StartsWith("/_uploads/", StringComparison.Ordinal) && UploadName.Match(query) is { Success: true } nm)
                p += "?name=" + Text(Uri.UnescapeDataString(nm.Groups[1].Value));
            return p;
        }

        public JsonNode Body(JsonNode node) => Node(node, "");

        /// <summary>Whether a write is a plant journal comment of a sweep observation (matched, waiting), or the fold edit of a repeated
        /// journal line (its marker carries fold=N).</summary>
        public static bool IsTimingJournal(JsonNode? body)
        {
            try { return body is JsonObject o && o["body"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && TimingJournal.IsMatch(v.GetValue<string>()); }
            catch { return false; }
        }

        /// <summary>For a write of a factory-state comment (a body whose text starts with the marker), its kind and item, as "factory-worker v1 #{#k}"; else null.</summary>
        public string? StateOf(JsonNode? body)
        {
            string? t = null;
            try { if (body is JsonObject o && o["body"] is JsonValue v && v.GetValueKind() == JsonValueKind.String) t = v.GetValue<string>(); } catch { }
            if (t == null || StateHead.Match(t) is not { Success: true } m) return null;
            return m.Groups[1].Value.Trim() + " #" + Rel("number", m.Groups[2].Value);
        }

        JsonNode Node(JsonNode node, string key)
        {
            switch (node)
            {
                case JsonObject o:
                {
                    var r = new JsonObject();
                    // an environment entry {"name":"FWORKER_ITEM","value":"12"} carries an item number as its value
                    var env = o["name"] is JsonValue nv && nv.GetValueKind() == JsonValueKind.String && nv.GetValue<string>() == "FWORKER_ITEM";
                    foreach (var kv in o.OrderBy(x => x.Key, StringComparer.Ordinal))
                        r[kv.Key] = kv.Value == null ? null : Node(kv.Value, env && kv.Key == "value" ? "FWORKER_ITEM" : kv.Key);
                    return r;
                }
                case JsonArray a:
                {
                    var items = a.Select(x => x == null ? null : Node(x, key)).ToArray();
                    // lists of check names come in the order the checks were read, which varies: compared as sorted
                    if (SortedArrays.Contains(key) && items.All(x => x is JsonValue xv && xv.GetValueKind() == JsonValueKind.String))
                        items = items.OrderBy(x => x!.GetValue<string>(), StringComparer.Ordinal).ToArray();
                    return new JsonArray(items);
                }
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                {
                    var t = v.GetValue<string>();
                    if (StringItemKeys.Contains(key) && t.Length is > 0 and < 10 && t.All(char.IsAsciiDigit)) return JsonValue.Create(Rel("number", t))!;
                    return JsonValue.Create(Long(Text(t)))!;
                }
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                {
                    var raw = v.ToJsonString();
                    var lk = key.ToLowerInvariant();
                    if (ItemKeys.Contains(key) && long.TryParse(raw, out _)) return JsonValue.Create(Rel("number", raw))!;
                    if (lk == "id" || MaskedSuffixes.Any(s => lk.EndsWith(s, StringComparison.Ordinal))) return JsonValue.Create("{n}")!;
                    if (!long.TryParse(raw, out var n) || Math.Abs(n) >= 1000) return JsonValue.Create("{n}")!;
                    return node.DeepClone();
                }
                default:
                    return node.DeepClone();
            }
        }

        static string Long(string s)
        {
            if (s.Length <= 400) return s;
            var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
            return $"{{len:{s.Length} sha256:{h} \"{s[..80]}…\"}}";
        }

        public string Text(string s)
        {
            // 1. the factory-state comment's JSON block, normalized as JSON
            s = Fenced.Replace(s, m =>
            {
                try
                {
                    var n = JsonNode.Parse(m.Groups[1].Value);
                    if (n == null) return m.Value;
                    var r = Node(n, "");
                    // the state's own copy of the journal (counts, folds, keys) follows the plant's timing; the journal comments carry it
                    if (r is JsonObject ro) foreach (var k in StateTimingKeys) ro.Remove(k);
                    return "```json\n" + r.ToJsonString(Plain) + "\n```";
                }
                catch { }
                return m.Value;
            });
            // tokens, links and the ids of the fake's sequences (before the hex runs inside them are masked)
            foreach (var (re, to) in Ids) s = re.Replace(s, to);
            // issue and pull request numbers, relative to the scenario's start; comment ids
            s = CommentId.Replace(s, "/issues/comments/{c}");
            s = IssuePath.Replace(s, m => $"/{m.Groups[1].Value}/{Rel("number", m.Groups[2].Value)}");
            s = Branch.Replace(s, m => $"factory-worker{m.Groups[1].Value}{Rel("number", m.Groups[2].Value)}-");
            s = NodeId.Replace(s, m => $"{m.Groups[1].Value}_{Rel("number", m.Groups[2].Value)}");
            s = Hash.Replace(s, m => "#" + Rel("number", m.Groups[1].Value));
            s = Labelled.Replace(s, m => m.Groups[1].Value + Rel("number", m.Groups[2].Value));
            // 2.-3. times, GUIDs, SHAs and 32-hex runs; 6. ports
            foreach (var (re, to) in Masks) s = re.Replace(s, to);
            // 7.-8. durations, then every remaining run of 4 or more digits and every decimal (never inside a placeholder)
            s = MaskOutside(s, t => Digits.Replace(ShortHex.Replace(Decimal.Replace(Duration.Replace(t, "{dur}"), "{n}"), "{sha}"), "{n}"));
            // 9. comma lists of check names, which come in varying order, sorted: the list that opens a parenthesis ("(B, A; …)", "(B, A)")
            // and the list of "with B, A passing"
            s = ParenList.Replace(s, m =>
            {
                var inner = m.Groups[1].Value;
                var semi = TopLevelIndex(inner, ';');
                var head = semi < 0 ? inner : inner[..semi];
                return "(" + SortList(head) + (semi < 0 ? "" : inner[semi..]) + ")";
            });
            return PassingList.Replace(s, m => "with " + SortList(m.Groups[1].Value) + " passing");
        }

        static string SortList(string list)
        {
            var parts = SplitTopLevel(list);
            if (parts.Count < 2) return list;
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts);
        }

        static int TopLevelIndex(string s, char c)
        {
            var depth = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] is '(' or '[') depth++; else if (s[i] is ')' or ']') depth--;
                else if (s[i] == c && depth == 0) return i;
            }
            return -1;
        }

        static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>(); var depth = 0; var at = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] is '(' or '[') depth++; else if (s[i] is ')' or ']') depth--;
                else if (depth == 0 && s[i] == ',' && i + 1 < s.Length && s[i + 1] == ' ') { parts.Add(s[at..i]); at = i + 2; i++; }
            }
            parts.Add(s[at..]);
            return parts;
        }

        static readonly (Regex, string)[] Ids =
        {
            (new("\\b(?:w1|r1|b1|i1|n1|c1)\\.[A-Za-z0-9._~+/=-]+"), "{token}"),
            (new("\\beyJ[A-Za-z0-9._-]+"), "{token}"),
            (new("\\bfake-app-[A-Za-z0-9._-]+"), "{token}"),
            (new("\\boat-[0-9a-fA-F]+"), "{token}"),
            (new("\\bAPI-[A-Za-z0-9]+"), "{token}"),
            (new("https?://[^/\\s]+/sessions/[^\\s\"')#?]+"), "{session-link}"),
            (new("https://cursor\\.com/agents/[^\\s\"')]+"), "{agent-url}"),
            (new("https://github\\.com/[^\\s\"')]*/tasks/[^\\s\"')]+"), "{agent-url}"),
            (new("\\bcaj-test-[0-9a-f]{6}\\b"), "{exec}"),
            (new("\\btask-[0-9a-f]{8}\\b"), "{task}"),
            (new("\\bbc-[0-9a-f]{8}\\b"), "{agent}"),
            (new("\\brun-[0-9a-f]{8}\\b"), "{run}"),
            (new("\\bServerTasks-P?\\d+"), "ServerTasks-{n}"),
            (new("\\bRunbookRuns-\\d+"), "RunbookRuns-{n}"),
            (new("\\b((?:mgr-)?(?:cae|oct|cur|cop))-(?:\\d+-)?[0-9a-f]{6}\\b"), "$1-{session}"),
            (new("\\bInterruptions-\\d+"), "Interruptions-{n}"),
            (new("\\bfwt1:[A-Za-z0-9+/=]+"), "fwt1:{blob}"),
            (new("\\ba-[0-9a-f]{4,8}\\b"), "a-{id}"),
            (new("\\brv-[0-9a-f]{6,}\\b"), "rv-{sha}"),
            (new("`[0-9a-f]{7,40}`"), "`{sha}`"),
        };
        static readonly Regex CommentId = new("/issues/comments/\\d+");
        static readonly Regex IssuePath = new("/(issues|pulls|pull|claims|qc)/(\\d+)\\b");
        static readonly Regex Branch = new("factory-worker(/|%2F|%2f)(\\d+)-");
        static readonly Regex NodeId = new("\\b(I|PR|card)_(\\d+)\\b");
        // other places an item or pull request number is written without '#': the harness's pull request titles, Copilot branches and demo
        // video names (lib.ps1, scenarios.ps1, copilot.ps1), command lines, the QC journal's keys and links, and JSON carried inside a string
        // (the plant snapshot)
        static readonly Regex Labelled = new("(\\bTrial |\\bBack-merge |\\bcopilot/[a-z]+-|\\bdemo-|--item |--pr |\\bpr=|\\bplant event=qc-[\\w-]+ key=|\"(?:item|issue|pr|number|pull_number)\":)(\\d+)\\b");
        static readonly Regex Hash = new("(?<![{}])#(\\d+)\\b");   // not "{task}#2", a session's ordinal
        static readonly (Regex, string)[] Masks =
        {
            (new("\\d{4}-\\d{2}-\\d{2}(?:[T ]\\d{2}:\\d{2}(?::\\d{2}(?:[.,]\\d+)?)?(?:Z|[+-]\\d{2}:?\\d{2})?)?"), "{time}"),
            (new("\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b"), "{guid}"),
            (new("\\b[0-9a-fA-F]{40}\\b"), "{sha}"),
            (new("\\b[0-9a-fA-F]{32}\\b"), "{hex32}"),
            (new("\\b(127\\.0\\.0\\.1|localhost):\\d+"), "$1:{port}"),
        };
        static readonly Regex Duration = new("\\b\\d+(?:\\.\\d+)?\\s?(?:ms|s|sec|seconds?|m|min|minutes?|h|hours?)\\b");
        static readonly Regex Decimal = new("(?<![\\d.])\\d+\\.\\d+(?![\\d.])");   // a decimal, not an address or a version (127.0.0.1, 7.5.4)
        static readonly Regex Digits = new("\\d{4,}");
        // a hex run of 7 or more with a digit and a letter, or exactly 7 digits (the fake's short SHAs; a 7-digit number is never a count here)
        static readonly Regex ShortHex = new("\\b(?:(?=[0-9a-f]*[a-f])(?=[0-9a-f]*[0-9])[0-9a-f]{7,}|\\d{7})\\b");
        static readonly Regex IdSegment = new("/(reviews|check-runs|jobs|runs|artifacts|releases|assets)/(\\d+)");
        static readonly Regex UploadName = new("(?:^|[?&])name=([^&]*)");

        static string MaskOutside(string s, Func<string, string> f)
        {
            var sb = new StringBuilder(); var at = 0;
            foreach (Match m in Placeholder.Matches(s)) { sb.Append(f(s[at..m.Index])).Append(m.Value); at = m.Index + m.Length; }
            return sb.Append(f(s[at..])).ToString();
        }
    }
}
