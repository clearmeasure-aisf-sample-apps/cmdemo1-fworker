using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// W9.7 (#359): `metrics` on v2, from plugin/scripts/lib/Metrics.ps1 (Verb_metrics, Get-ItemCycle, Get-MetricsSince, Get-MetricsStats) with the reads of Board.ps1 it uses (Get-BoardList, Get-State) and Core.ps1 (Get-ServerLogin).
// Cycle-time metrics (enhancement 13) computed from what lasts on GitHub, never from the gateway's memory: per item the journal comments of the plant (<!-- plant event=E key=K -->, their created_at) and the item's state
// comment (dispatched_at per column, merge.merged_at, merge.done_at, flow). The same board query, the same reads of the comments, the same arithmetic and rounding (to the nearest second, halves to the even one), the same
// refusals. Read only: it writes nothing to GitHub. GET /api/v1/metrics (MetricsEndpoints) caches its answer and guards who may read it, as in v1.
static class MetricsVerb
{
    const string BoardQuery = """
        query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
          nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
            content{ ... on Issue{ number state title body repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } } } } } }
        """;

    sealed record Card(int Number, bool Open, string? Column, List<string> Labels);

    public static async Task<JsonObject> Run(VerbArgs a, ServiceGitHub.CallScope s, Func<DateTime> utcNow)
    {
        var gh = s.Gh;
        var label = gh.Cfg("label"); var now = utcNow();
        var since = Since(a.Has("since") ? VerbArgs.Text(a.Get("since")) : "", now);
        var max = a.Has("max") ? Math.Clamp(VerbArgs.Int(a.Get("max")), 1, 300) : 100;
        var login = await gh.ServerLogin(s);
        var cands = (await BoardList(s)).Where(c => c.Labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase))).OrderByDescending(c => c.Number).Take(max).ToList();
        var items = new List<JsonObject>(); var failed = new JsonArray();
        foreach (var it in cands)
        {
            JsonObject? r;
            try { r = ItemCycle(gh, it.Number, await State(s, it.Number, login), login); }
            catch (Exception e) when (e is not OperationCanceledException) { failed.Add(it.Number); continue; }
            if (r is not null && FromIso(r["last"]) >= since) { r["column"] = it.Column ?? ""; r["open"] = it.Open; items.Add(r); }
        }
        var agg = new JsonObject();
        foreach (var k in new[] { "matchToAccept", "acceptToMerge", "matchToMerge", "mergeToDone", "matchToDone" })
            agg[k] = Stats(items.Select(i => ((JsonObject)i["durations"]!)[k]).Where(v => v is not null).Select(v => (double)(long)v!).ToList());
        var cols = new JsonObject();
        foreach (var col in items.SelectMany(i => ((JsonObject)i["columns"]!).Select(kv => kv.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            cols[col] = Stats(items.SelectMany(i => ((JsonObject)i["columns"]!).Where(kv => string.Equals(kv.Key, col, StringComparison.OrdinalIgnoreCase)).Select(kv => (double)(long)kv.Value!)).ToList());
        var visits = items.SelectMany(i => ((JsonArray)i["flow"]!).Select(f => (JsonObject)f!)).Where(f => f.ContainsKey("seconds")).ToList();
        var stages = new JsonObject();
        foreach (var st in visits.Select(f => f["stage"]!.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            stages[st] = Stats(visits.Where(f => string.Equals(f["stage"]!.ToString(), st, StringComparison.OrdinalIgnoreCase)).Select(f => (double)(long)f["seconds"]!).ToList());
        var sorted = new JsonArray(); foreach (var i in items.OrderByDescending(i => (int)i["item"]!)) sorted.Add(i);
        return new JsonObject
        {
            ["ok"] = true, ["since"] = Iso(since), ["computedAt"] = Iso(now), ["scanned"] = cands.Count, ["unreadable"] = failed,
            ["items"] = sorted, ["aggregate"] = agg, ["columns"] = cols, ["stages"] = stages, ["unit"] = "seconds",
        };
    }

    // ---- times ----
    static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    // ConvertFrom-Iso: empty is none, anything that is not a time throws
    static DateTime? FromIso(JsonNode? n)
    {
        var t = VerbArgs.Text(n);
        return t == "" ? null : DateTime.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary>Get-MetricsSince: an ISO time or a duration like 30m, 24h, 7d; none is 7 days; never more than 90 days back.</summary>
    internal static DateTime Since(string since, DateTime now)
    {
        if (since == "") return now.AddDays(-7);
        DateTime d;
        var m = Regex.Match(since, @"^\s*(\d{1,4})\s*([mhd])\s*$");
        if (m.Success)
        {
            var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            d = m.Groups[2].Value switch { "m" => now.AddMinutes(-v), "h" => now.AddHours(-v), _ => now.AddDays(-v) };
        }
        else
        {
            try { d = DateTime.Parse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal); }
            catch (FormatException) { throw new VerbReject(2, "bad-since", $"since is an ISO date or a duration like 30m, 24h, 7d; got '{since}'"); }
        }
        return d < now.AddDays(-90) ? now.AddDays(-90) : d;
    }

    // [Math]::Round of a number of seconds
    static long Round(double seconds) => (long)Math.Round(seconds, MidpointRounding.ToEven);

    /// <summary>Get-MetricsStats: count, median, 90th percentile, smallest and largest of seconds; just the count where there are none.</summary>
    internal static JsonObject Stats(List<double> values)
    {
        var v = values.Order().ToList();
        if (v.Count == 0) return new JsonObject { ["count"] = 0 };
        var median = v.Count % 2 == 1 ? v[(v.Count - 1) / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        var p90 = v[Math.Min(v.Count - 1, (int)Math.Ceiling(0.9 * v.Count) - 1)];
        return new JsonObject { ["count"] = v.Count, ["median"] = Round(median), ["p90"] = Round(p90), ["min"] = Round(v[0]), ["max"] = Round(v[^1]) };
    }

    // ---- the board (Get-BoardList) ----
    // Environment scope (docs/design/environment-test-factories.md 3): with scoping on, only the items that carry exactly one env: label, and that label is env:<this environment>
    static string EnvLabel(ServiceGitHub gh)
    {
        var e = (gh.Env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant(); if (e == "") e = "tdd";
        var flag = gh.Env("FACTORY_ENV_SCOPE");
        var on = flag is "0" or "1" ? flag == "1" : gh.CfgNode("envScope") is JsonArray list && list.Any(x => VerbArgs.Text(x).ToLowerInvariant() == e);
        return on ? "env:" + e : "";
    }

    static bool InScope(string mine, List<string> labels)
    {
        if (mine == "") return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && string.Equals(envs[0], mine, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<List<Card>> BoardList(ServiceGitHub.CallScope s)
    {
        var gh = s.Gh; var mine = EnvLabel(gh); var cards = new List<Card>();
        string? after = null; bool more;
        var project = VerbArgs.Int(gh.CfgNode("project"));
        do
        {
            var d = await s.GraphQl(BoardQuery, new JsonObject { ["o"] = gh.Owner, ["n"] = project, ["after"] = after });
            var items = d?["organization"]?["projectV2"]?["items"] ?? throw new InvalidOperationException("The property 'items' cannot be found on this object. Verify that the property exists.");
            foreach (var n in items["nodes"] as JsonArray ?? [])
            {
                if (n?["content"] is not JsonObject content || !content.ContainsKey("number")) continue;
                // a card of another repository linked to the board is never seen here
                var repo = content["repository"] is JsonObject r ? VerbArgs.Text(r["nameWithOwner"]) : "";
                if (!string.Equals(repo, $"{gh.Owner}/{gh.Repo}", StringComparison.OrdinalIgnoreCase)) continue;
                var labels = (content["labels"]?["nodes"] as JsonArray ?? []).Select(x => VerbArgs.Text(x?["name"])).ToList();
                if (!InScope(mine, labels)) continue;   // another environment's card
                cards.Add(new Card(VerbArgs.Int(content["number"]), VerbArgs.Text(content["state"]) == "OPEN", n["fieldValueByName"] is JsonObject f ? f["name"]?.ToString() : null, labels));
            }
            after = items["pageInfo"]?["endCursor"]?.ToString();
            more = items["pageInfo"]?["hasNextPage"] is JsonValue hv && hv.TryGetValue<bool>(out var hb) && hb;
        } while (more);
        return cards;
    }

    // ---- the state comment (Get-State, Get-Comments) ----
    sealed record StateRead(JsonObject State, List<JsonNode?> Comments);

    static string LoginOf(JsonNode? comment) => VerbArgs.Text(comment?["user"]?["login"]);
    static string BodyOf(JsonNode? comment) => VerbArgs.Text(comment?["body"]);

    static async Task<StateRead> State(ServiceGitHub.CallScope s, int item, string login)
    {
        var gh = s.Gh; var all = new List<JsonNode?>(); var page = 1; List<JsonNode?> chunk;
        do
        {
            chunk = RevocationVerbs.Rows((await s.Get($"{gh.RepoPath}/issues/{item}/comments?per_page=100&page={page}")).Json);
            all.AddRange(chunk); page++;
        } while (chunk.Count == 100);
        var marker = $"<!-- factory-state:{gh.Name} v1 -->";
        var found = all.FirstOrDefault(c => string.Equals(LoginOf(c), login, StringComparison.OrdinalIgnoreCase) && BodyOf(c).StartsWith(marker, StringComparison.Ordinal));
        JsonObject Empty() => new() { ["schema"] = 1, ["item"] = item };
        if (found is null)
        {
            // a change of identity must not orphan the items in flight: the newest state written by one of the logins the instance names in stateAuthors is adopted
            var trusted = (gh.CfgNode("stateAuthors") as JsonArray ?? []).Select(x => VerbArgs.Text(x)).ToList();
            var old = trusted.Count == 0 ? null : all.LastOrDefault(c => trusted.Any(t => string.Equals(t, LoginOf(c), StringComparison.OrdinalIgnoreCase)) && BodyOf(c).StartsWith(marker, StringComparison.Ordinal));
            if (old is not null && Block(BodyOf(old)) is { } adopted) return new StateRead(AsObject(adopted), all);
            return new StateRead(Empty(), all);
        }
        return new StateRead(Block(BodyOf(found)) is { } json ? AsObject(json) : Empty(), all);
    }

    // the JSON of the first fenced block of a comment, parsed (a block that is not JSON throws)
    static string? Block(string body) { var m = Regex.Match(body, @"(?s)```json\s*(.*?)\s*```"); return m.Success ? m.Groups[1].Value : null; }
    static JsonObject AsObject(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    // ---- one item (Get-ItemCycle) ----
    static JsonObject? ItemCycle(ServiceGitHub gh, int item, StateRead read, string login)
    {
        var first = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var cm in read.Comments)
        {
            if (!string.Equals(LoginOf(cm), login, StringComparison.OrdinalIgnoreCase)) continue;
            var mm = Regex.Match(BodyOf(cm), @"<!-- plant event=([a-z-]+) key=(.*?) -->");
            if (!mm.Success) continue;
            var ev = mm.Groups[1].Value; var key = mm.Groups[2].Value;
            if (ev == "assigned") { if (key.StartsWith("owner|", StringComparison.Ordinal)) ev = "accepted"; else continue; }
            if (ev is not ("matched" or "offered" or "accepted")) continue;
            var at = FromIso(cm?["created_at"]);
            if (at is { } t && (!first.TryGetValue(ev, out var have) || t < have)) first[ev] = t;
        }
        var s = read.State;
        var merge = s["merge"] as JsonObject;
        if (merge?["merged_at"] is not null) first["merged"] = FromIso(merge["merged_at"]) ?? default;
        if (merge?["done_at"] is not null) first["done"] = FromIso(merge["done_at"]) ?? default;
        if (first.Count == 0) return null;
        var times = new JsonObject(); foreach (var kv in first) times[kv.Key] = Iso(kv.Value);
        var o = new JsonObject { ["item"] = item, ["times"] = times };
        var durations = new JsonObject();
        foreach (var (name, a, b) in new[] { ("matchToAccept", "matched", "accepted"), ("acceptToMerge", "accepted", "merged"), ("matchToMerge", "matched", "merged"), ("mergeToDone", "merged", "done"), ("matchToDone", "matched", "done") })
            if (first.TryGetValue(a, out var f) && first.TryGetValue(b, out var tt) && tt >= f) durations[name] = Round((tt - f).TotalSeconds);
        o["durations"] = durations;
        var columns = new JsonObject();
        if (s["dispatched_at"] is { } dispatched)
        {
            if (dispatched is not JsonObject dobj) throw new InvalidOperationException("The property 'Keys' cannot be found on this object. Verify that the property exists.");
            var ds = dobj.Select(kv => (Column: kv.Key, At: FromIso(kv.Value))).Where(x => x.At is not null).Select(x => (x.Column, At: x.At!.Value)).OrderBy(x => x.At).ToList();
            for (var i = 0; i < ds.Count; i++)
            {
                DateTime? end = i + 1 < ds.Count ? ds[i + 1].At : first.TryGetValue("merged", out var mg) && mg > ds[i].At ? mg : null;
                if (end is { } e) columns[ds[i].Column] = Round((e - ds[i].At).TotalSeconds);
            }
        }
        o["columns"] = columns;
        var flow = new JsonArray();
        if (s["flow"] is { } rawFlow)
        {
            var entries = rawFlow is JsonArray fa ? fa.ToList() : [rawFlow];
            var fl = entries.Select(x => x is JsonObject eo && eo.ContainsKey("c") && eo.ContainsKey("at") ? (Stage: VerbArgs.Text(eo["c"]), At: FromIso(eo["at"])) : throw new InvalidOperationException("The property 'c' or 'at' cannot be found on this object. Verify that the property exists."))
                .Where(x => x.Stage != "" && x.At is not null).Select(x => (x.Stage, At: x.At!.Value)).ToList();
            for (var i = 0; i < fl.Count; i++)
            {
                var e = new JsonObject { ["stage"] = fl[i].Stage, ["at"] = Iso(fl[i].At) };
                DateTime? end = i + 1 < fl.Count ? fl[i + 1].At : first.TryGetValue("merged", out var mg) ? mg : null;
                if (end is { } en && en >= fl[i].At) e["seconds"] = Round((en - fl[i].At).TotalSeconds);
                flow.Add(e);
            }
        }
        o["flow"] = flow;
        if (merge?["pr"] is not null) o["pr"] = VerbArgs.Int(merge["pr"]);
        o["last"] = Iso(first.Values.Max());
        return o;
    }
}
