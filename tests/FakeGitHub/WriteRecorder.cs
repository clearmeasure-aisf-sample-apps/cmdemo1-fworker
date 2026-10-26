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
        int kept = 0, leftover = 0;
        foreach (var e in entries)
        {
            var path = norm.Path(e.Path, e.Query);
            var left = path.Contains("{#old}", StringComparison.Ordinal);
            var line = $"{e.Source} {e.Method} {path}{(e.Field != null ? " " + e.Field : "")} {e.Status} {(e.Body == null ? "-" : norm.Body(e.Body).ToJsonString(Plain))}";
            if (left) leftover++; else { kept++; lines.Add(line); }
            raw.Append(new JsonObject
            {
                ["seq"] = e.Seq, ["ms"] = e.Ms, ["method"] = e.Method, ["path"] = e.Path, ["query"] = e.Query, ["status"] = e.Status, ["source"] = e.Source,
                ["field"] = e.Field, ["body"] = e.Body?.DeepClone(), ["leftover"] = left, ["line"] = line
            }.ToJsonString(Plain)).Append('\n');
        }
        var sd = Path.Combine(dir, suite!);
        Directory.CreateDirectory(sd);
        var file = Path.Combine(sd, slug + ".writes.txt"); var rawFile = Path.Combine(sd, slug + ".raw.jsonl");
        var text = new StringBuilder();
        text.Append("# suite: ").Append(suite).Append('\n').Append("# scenario: ").Append(OneLine(scenario!)).Append('\n')
            .Append("# writes: ").Append(kept).Append('\n').Append("# leftover: ").Append(leftover).Append('\n');
        foreach (var l in lines) text.Append(l).Append('\n');
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(file, text.ToString(), utf8);
        File.WriteAllText(rawFile, raw.ToString(), utf8);
        scenario = null; suite = null; slug = null; entries.Clear();
        return (200, new JsonObject { ["ok"] = true, ["file"] = file, ["raw"] = rawFile, ["writes"] = kept, ["leftover"] = leftover, ["lines"] = lines.Count });
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
        static readonly string[] MaskedSuffixes = { "seconds", "ms", "minutes", "at", "time", "epoch", "since", "until", "expires", "ttl", "reads", "attempt", "attempts", "size", "len", "length", "bytes" };
        static readonly Regex Fenced = new("```json\\s*(.*?)\\s*```", RegexOptions.Singleline);
        static readonly Regex Placeholder = new("\\{[^{}]*\\}");

        string Rel(string kind, string digits)
        {
            if (!long.TryParse(digits, out var n)) return digits;
            var b = bases.TryGetValue(kind, out var x) ? x : 1;
            return n >= b ? "{#" + (n - b + 1) + "}" : "{#old}";
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

        JsonNode Node(JsonNode node, string key)
        {
            switch (node)
            {
                case JsonObject o:
                {
                    var r = new JsonObject();
                    foreach (var kv in o.OrderBy(x => x.Key, StringComparer.Ordinal)) r[kv.Key] = kv.Value == null ? null : Node(kv.Value, kv.Key);
                    return r;
                }
                case JsonArray a:
                    return new JsonArray(a.Select(x => x == null ? null : Node(x, key)).ToArray());
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    return JsonValue.Create(Long(Text(v.GetValue<string>())))!;
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
                try { var n = JsonNode.Parse(m.Groups[1].Value); if (n != null) return "```json\n" + Node(n, "").ToJsonString(Plain) + "\n```"; } catch { }
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
            // 2.-3. times, GUIDs, SHAs and 32-hex runs; 6. ports
            foreach (var (re, to) in Masks) s = re.Replace(s, to);
            // 7.-8. durations, then every remaining run of 4 or more digits and every decimal (never inside a placeholder)
            return MaskOutside(s, t => Digits.Replace(Decimal.Replace(Duration.Replace(t, "{dur}"), "{n}"), "{n}"));
        }

        static readonly (Regex, string)[] Ids =
        {
            (new("\\b(?:w1|r1|b1|i1)\\.[A-Za-z0-9._~+/=-]+"), "{token}"),
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
        };
        static readonly Regex CommentId = new("/issues/comments/\\d+");
        static readonly Regex IssuePath = new("/(issues|pulls)/(\\d+)");
        static readonly Regex Branch = new("factory-worker(/|%2F|%2f)(\\d+)-");
        static readonly Regex NodeId = new("\\b(I|PR|card)_(\\d+)\\b");
        static readonly Regex Hash = new("(?<!\\{)#(\\d+)\\b");
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
