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

    /// <summary>Writes the open scenario's record. <paramref name="worldEnds"/>: the fake's counters now (Bases() at the end), so that a number
    /// the scenario's own counters handed out is told from one of an earlier scenario and from a fixed number of the harness.</summary>
    public (int, object?) End(Dictionary<string, long>? worldEnds = null)
    {
        if (scenario == null) return (200, new JsonObject { ["ok"] = false, ["recording"] = true, ["reason"] = "no scenario is open" });
        var ends = worldEnds == null ? null : new Dictionary<string, long>(worldEnds);
        Normalizer norm;
        try { norm = new Normalizer(bases, ends, entries.Select(e => e.Path + " " + RawText(e.Body))); }
        catch { norm = new Normalizer(bases, ends, Array.Empty<string>()); }
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        var raw = new StringBuilder();
        int kept = 0, leftover = 0, timing = 0, errors = 0;
        // A factory-state comment is created once and then rewritten many times, and how many snapshots there are (and so whether the last
        // one is the POST or a PATCH) depends on timing. So the accepted writes of one (source, item's state comment) give one line,
        // "<source> STATE <kind> #k"; a refused one (status 300 or more) keeps its own line with the marker body.
        var state = entries.Select(e => { try { return norm.StateOf(e.Body); } catch { return null; } }).ToList();
        var lastState = new Dictionary<string, long>();
        for (var i = 0; i < entries.Count; i++) if (state[i] != null && entries[i].Status < 300) lastState[entries[i].Source + " " + state[i]] = entries[i].Seq;
        // which writes are leftover or timing (neither depends on the deployments' ranks), then the ranks from the compared writes only
        var leftOf = new bool[entries.Count]; var timedOf = new bool[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            try
            {
                var e = entries[i];
                leftOf[i] = norm.Path(e.Path, e.Query).Contains("{#old}", StringComparison.Ordinal) || state[i] is { } st && st.Contains("{#old}", StringComparison.Ordinal);
                timedOf[i] = !leftOf[i] && (Normalizer.IsTimingJournal(e.Body) || Normalizer.IsTimingWrite(e.Method, e.Path));
            }
            catch { }
        }
        try { norm.RankDeployments(entries.Where((e, i) => !leftOf[i] && !timedOf[i]).Select(e => e.Path + " " + RawText(e.Body))); } catch { }
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            string line; bool add = true, left = leftOf[i], timed = timedOf[i];
            try
            {
                var path = norm.Path(e.Path, e.Query);
                var head = $"{e.Source} {e.Method} {path}{(e.Field != null ? " " + e.Field : "")} {e.Status} ";
                if (state[i] == null) line = head + (e.Body == null ? "-" : norm.Body(e.Body).ToJsonString(Plain));
                else if (e.Status >= 300) line = head + new JsonObject { ["body"] = "{factory-state " + state[i] + "}" }.ToJsonString(Plain);
                // the last snapshot itself is not compared: where the background loops have got to when the scenario ends is timing
                else if (lastState[e.Source + " " + state[i]] == e.Seq) line = $"{e.Source} STATE {state[i]}";
                else { line = head + new JsonObject { ["body"] = "{factory-state " + state[i] + "}" }.ToJsonString(Plain); add = false; }
                // (timed: the plant's sweep observations (matched, waiting), the fold edits of a repeated journal line and the rewrites of a
                // session's transcript come and go with the sweep's, the plant's and the transcript loop's timing: kept raw, counted, not compared)
            }
            // one write the normalization cannot read (a regex timeout, an odd body) is one line of its own, never the loss of the record
            catch (Exception ex) { line = $"{e.Source} {e.Method} {{normalize-error}} {ex.GetType().Name}"; add = true; left = false; timed = false; errors++; }
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
        if (ends != null) text.Append("# ends: ").Append(string.Join(' ', ends.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => b.Key + "=" + b.Value))).Append('\n');
        if (errors > 0) text.Append("# normalize-errors: ").Append(errors).Append('\n');
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

    static string RawText(JsonNode? body) { try { return body?.ToJsonString(Plain) ?? ""; } catch { return ""; } }
    static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
    static string OneLine(string s) => Regex.Replace(s, "[\r\n]+", " ");
    static string Hash8(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    // The normalization of tests/scenarios/golden/README.md: numbers relative to the scenario's own counters, masked times, ids, tokens and
    // ports. Every regex added or changed for the grouping-independent numbers has a match timeout (T); one write the normalization cannot
    // read is a {normalize-error} line of its own (End).
    sealed class Normalizer
    {
        static readonly TimeSpan T = TimeSpan.FromSeconds(1);
        static readonly HashSet<string> ItemKeys = new(StringComparer.OrdinalIgnoreCase) { "number", "item", "issue", "pr", "pull_number" };
        // keys whose number is a pull request's: the scenario's own number there only when its writes name it as a pull request (Rel)
        static readonly HashSet<string> PrKeys = new(StringComparer.OrdinalIgnoreCase) { "pr", "pull_number" };
        // string values that are item numbers: the Octopus runbook's variable and the runtime job's environment entry
        static readonly HashSet<string> StringItemKeys = new(StringComparer.Ordinal) { "Fworker.Item", "FWORKER_ITEM" };
        static readonly string[] MaskedSuffixes = { "seconds", "ms", "minutes", "at", "time", "epoch", "since", "until", "expires", "ttl", "reads", "attempt", "attempts", "size", "len", "length", "bytes" };
        static readonly Regex Fenced = new("```json\\s*(.*?)\\s*```", RegexOptions.Singleline);
        static readonly Regex Placeholder = new("\\{[^{}]*\\}");
        static readonly string[] StateTimingKeys = { "journal" };
        static readonly Regex TimingJournal = new("<!-- plant event=(?:(?:matched|waiting)\\b|[\\w-]+ [^>]*\\bfold=\\d+ -->)");
        // the rewrite of a session's transcript ref (each flush of the transcript loop after the first): how many flushes there are is timing
        static readonly Regex TimingRef = new("^/repos/[^/]+/[^/]+/git/refs/factory/[^/]+/transcripts/", RegexOptions.None, T);
        static readonly HashSet<string> SortedArrays = new(StringComparer.Ordinal) { "failed", "failed_checks" };
        static readonly Regex StateHead = new("^<!-- factory-state:([^>]*?)-->\\s*\\nstate of #(\\d+)");
        static readonly Regex ParenList = new("\\(((?:[^()\\n]|\\([^()\\n]*\\))*)\\)");
        static readonly Regex PassingList = new("\\bwith ([^,()\\n\"]+(?:, [^,()\\n\"]+)+) passing\\b");
        // a gateway's lease holder, <host>:<pid>:<6 hex> (Leader.cs): the host is the runner's name, the pid and the suffix are the process's
        static readonly Regex Holder = new("\\b([A-Za-z][A-Za-z0-9._-]*):\\d+:[0-9a-f]{6}\\b", RegexOptions.None, T);
        static readonly Regex Deployment = new("\\bDeployments-P?(\\d+)\\b", RegexOptions.None, T);
        // where a write names a number as a pull request: its path or link (/pulls/n, /pull/n), its node id (PR_n), "PR #n"
        static readonly Regex PrNamed = new("(?:/pulls?/|\\bPR_|\\bPR #)(\\d+)\\b", RegexOptions.None, T);

        readonly long nb, ne, pb, pe, ob;
        readonly Regex? hosts;
        readonly Dictionary<long, string> deployments = new();
        readonly HashSet<long> prNamed = new();

        /// <summary><paramref name="ends"/>: the counters at the scenario's end (null for a record made without them: then every number from
        /// the start on counts as the scenario's own, and no pull request as seeded). <paramref name="texts"/>: every write's path and raw body,
        /// read once for the lease holders' host names and the numbers the writes name as pull requests.</summary>
        public Normalizer(Dictionary<string, long> bases, Dictionary<string, long>? ends, IEnumerable<string> texts)
        {
            nb = bases.TryGetValue("number", out var b) ? b : 1;
            ne = ends != null && ends.TryGetValue("number", out var e) ? e : long.MaxValue;
            pb = bases.TryGetValue("pr", out var p) ? p : 1;
            pe = ends != null && ends.TryGetValue("pr", out var q) ? q : pb;
            ob = bases.TryGetValue("octo", out var o) ? o : long.MinValue;
            var hs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var t in texts)
            {
                foreach (Match m in Holder.Matches(t)) hs.Add(m.Groups[1].Value);
                foreach (Match m in PrNamed.Matches(t)) if (long.TryParse(m.Groups[1].Value, out var n)) prNamed.Add(n);
            }
            // the longest first, so that a host that starts another one's name never takes a part of it
            if (hs.Count > 0) hosts = new Regex("\\b(?:" + string.Join("|", hs.OrderByDescending(h => h.Length).ThenBy(h => h, StringComparer.Ordinal).Select(Regex.Escape)) + ")\\b", RegexOptions.None, T);
        }

        /// <summary>A deployment is named by its rank among the deployments the scenario's compared writes name (<paramref name="texts"/>:
        /// their paths and raw bodies; not the leftover and timing writes), from the start of the fake's octo counter on (that counter also
        /// counts runbook runs and releases, whose number depends on the background loops): "{dep:1}" the lowest; one from before "{dep:old}".</summary>
        public void RankDeployments(IEnumerable<string> texts)
        {
            var deps = new SortedSet<long>();
            foreach (var t in texts) foreach (Match m in Deployment.Matches(t)) if (long.TryParse(m.Groups[1].Value, out var d)) deps.Add(d);
            var k = 0;
            foreach (var d in deps) deployments[d] = d < ob ? "{dep:old}" : "{dep:" + (++k) + "}";
        }

        // Where a number stands: anywhere an item may be named (Item), where only a pull request is (PrText: /pulls/n, /pull/n, PR_n, PR #n),
        // or under a pull request's JSON key (PrKey: pr, pull_number).
        enum At { Item, PrText, PrKey }

        // An item or pull request number: the scenario's own (handed out from the fake's number counter between the scenario's start and its
        // end) is "{#k}", k from the start; a pull request the fake seeded in the scenario (its pr counter, /_fake/merged-pr) "{#prk}"; any
        // other number is not the scenario's: "{#far}" 1000 or more above the start (a fixed number of the harness), else "{#old}" (from
        // before the scenario, or a fixed small number of the harness). In an early scenario of a fresh fake both ranges can hold the same
        // number (item 1 and seeded pull request 1), so where only a pull request can stand the seeded range is tried first, elsewhere the
        // scenario's items. None depends on where the scenario's counters started, so a regrouping of the scenarios changes nothing.
        string Rel(string digits, At at = At.Item)
        {
            if (!long.TryParse(digits, out var n)) return digits;
            if (n - nb >= 1000) return "{#far}";
            var seeded = n >= pb && n < pe;
            // under a pull request's key, the scenario's own number counts only when its writes name that number as a pull request: a
            // harness's fixed number there (promote.ps1 writes pr = 7) is not the scenario's item that happens to have that number
            var own = n >= nb && n < ne && (at != At.PrKey || prNamed.Contains(n));
            if (at != At.Item && seeded) return "{#pr" + (n - pb + 1) + "}";
            if (own) return "{#" + (n - nb + 1) + "}";
            if (seeded) return "{#pr" + (n - pb + 1) + "}";
            return "{#old}";
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

        /// <summary>Whether a write is the rewrite (PATCH) of a session's transcript ref, accepted or refused.</summary>
        public static bool IsTimingWrite(string method, string path)
        {
            try { return method == "PATCH" && TimingRef.IsMatch(path); }
            catch { return false; }
        }

        /// <summary>For a write of a factory-state comment (a body whose text starts with the marker), its kind and item, as "factory-worker v1 #{#k}"; else null.</summary>
        public string? StateOf(JsonNode? body)
        {
            string? t = null;
            try { if (body is JsonObject o && o["body"] is JsonValue v && v.GetValueKind() == JsonValueKind.String) t = v.GetValue<string>(); } catch { }
            if (t == null || StateHead.Match(t) is not { Success: true } m) return null;
            return m.Groups[1].Value.Trim() + " #" + Rel(m.Groups[2].Value);
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
                    if (StringItemKeys.Contains(key) && t.Length is > 0 and < 10 && t.All(char.IsAsciiDigit)) return JsonValue.Create(Rel(t))!;
                    // a string that is a JSON object (the lease's and the plant snapshot's commit messages): normalized as JSON, its keys sorted
                    // (the writer's key order is not stable), then hashed when long, like any string
                    if (JsonText(t) is { } inner)
                    {
                        // a plant snapshot (Plant.cs: assignments, owners, ...) is written whenever the plant changed, so which states are captured
                        // follows the plant's timing: its generation and whether it is the final one are compared, its content is not
                        if (inner.ContainsKey("assignments") && inner.ContainsKey("owners"))
                            return JsonValue.Create($"{{plant-snapshot epoch:{Scalar(inner["epoch"])} final:{Scalar(inner["final"])}}}")!;
                        return JsonValue.Create(Long(Node(inner, key).ToJsonString(Plain)))!;
                    }
                    return JsonValue.Create(Long(Text(t)))!;
                }
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                {
                    var raw = v.ToJsonString();
                    var lk = key.ToLowerInvariant();
                    if (ItemKeys.Contains(key) && long.TryParse(raw, out _)) return JsonValue.Create(Rel(raw, PrKeys.Contains(key) ? At.PrKey : At.Item))!;
                    // (an epoch below 1000 is a lease's or a plant snapshot's generation, not a time: kept)
                    if (lk == "id" || MaskedSuffixes.Any(s => lk.EndsWith(s, StringComparison.Ordinal)) && !(lk.EndsWith("epoch", StringComparison.Ordinal) && long.TryParse(raw, out var ep) && ep is >= 0 and < 1000)) return JsonValue.Create("{n}")!;
                    if (!long.TryParse(raw, out var n) || Math.Abs(n) >= 1000) return JsonValue.Create("{n}")!;
                    return node.DeepClone();
                }
                default:
                    return node.DeepClone();
            }
        }

        static string Scalar(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToJsonString() : "-";

        static JsonObject? JsonText(string t)
        {
            if (t.Length < 2 || t[0] != '{' || t[^1] != '}') return null;
            try { return JsonNode.Parse(t) as JsonObject; } catch { return null; }
        }

        // the hash is of the normalized text: every rule above has been applied to it
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
            // lease holders and their host (the runner's name, also the lease's replica); deployments by their rank in the scenario
            s = Holder.Replace(s, "{host}:{n}:{hex}");
            if (hosts != null) s = hosts.Replace(s, "{host}");
            s = Deployment.Replace(s, m => long.TryParse(m.Groups[1].Value, out var d) && deployments.TryGetValue(d, out var r) ? "Deployments-" + r : m.Value);
            // issue and pull request numbers, relative to the scenario's start; comment ids
            s = CommentId.Replace(s, "/issues/comments/{c}");
            s = IssuePath.Replace(s, m => $"/{m.Groups[1].Value}/{Rel(m.Groups[2].Value, m.Groups[1].Value is "pulls" or "pull" ? At.PrText : At.Item)}");
            s = Branch.Replace(s, m => $"factory-worker{m.Groups[1].Value}{Rel(m.Groups[2].Value)}-");
            s = NodeId.Replace(s, m => $"{m.Groups[1].Value}_{Rel(m.Groups[2].Value, m.Groups[1].Value == "PR" ? At.PrText : At.Item)}");
            var hs = s; s = Hash.Replace(s, m => "#" + Rel(m.Groups[1].Value, m.Index >= 3 && string.CompareOrdinal(hs, m.Index - 3, "PR ", 0, 3) == 0 ? At.PrText : At.Item));
            s = Labelled.Replace(s, m => m.Groups[1].Value + Rel(m.Groups[2].Value, m.Groups[1].Value is "\"pr\":" or "\"pull_number\":" ? At.PrKey : At.Item));
            s = KeeperDecision.Replace(s, m => "K-" + Rel(m.Groups[1].Value));
            s = PlantKey.Replace(s, m => m.Groups[1].Value + PlantKeyNumbers(m.Groups[2].Value, m.Groups[3].Value));
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

        // The key of a plant journal line (<!-- plant event=E key=K -->) names an item or pull request number only in the keys known to:
        // qc-...: <pr>|<sha>|<attempt> (the first part; Qc.ps1), skipped: qc|<pr>|<reasons> (Qc.ps1), reassigned: back|<item>|<ticks>
        // (Lease.cs), board-added: <item> (Verbs.Sweep.ps1). Every other key's digits are left to the digit masking (ci-released and
        // ci-release-refused carry a workflow run id of the fake's run counter, never an item). A key with a space in it is left alone.
        string PlantKeyNumbers(string evt, string key)
        {
            var segs = key.Split('|');
            var at = evt.StartsWith("qc-", StringComparison.Ordinal) || evt == "board-added" ? 0
                : evt == "skipped" && segs[0] == "qc" || evt == "reassigned" && segs[0] == "back" ? 1 : -1;
            if (at >= 0 && at < segs.Length && segs[at].Length is > 0 and <= 6 && segs[at].All(char.IsAsciiDigit)) segs[at] = Rel(segs[at]);
            return string.Join('|', segs);
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
        // other places an item or pull request number is written without '#': the harness's pull request titles, Copilot branches (one or
        // more words, then the number: copilot/thing-7, copilot/thing-fix-7, copilot/par2-7) and demo video names (lib.ps1, scenarios.ps1,
        // copilot.ps1), command lines, the QC journal's links, and JSON carried inside a string (the plant snapshot)
        static readonly Regex Labelled = new("(\\bTrial |\\bBack-merge |\\bcopilot/[a-z][a-z0-9]*(?:-[a-z][a-z0-9]*)*-|\\bdemo-|--item |--pr |\\bpr=|\"(?:item|issue|pr|number|pull_number)\":)(\\d+)\\b", RegexOptions.None, T);
        // the backlog keeper's decision ids, K-<item>-<n> (Verbs.Keeper.ps1)
        static readonly Regex KeeperDecision = new("\\bK-(\\d+)(?=-\\d+\\b)", RegexOptions.None, T);
        static readonly Regex PlantKey = new("(<!-- plant event=([\\w-]+) key=)([^\\s>]*)(?= -->)", RegexOptions.None, T);
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
