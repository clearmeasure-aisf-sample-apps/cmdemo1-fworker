using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A fake GitHub (REST + GraphQL, only what the factory-worker rule scripts call) and a fake Octopus, in memory, for Tier 1 tests.
// Test control lives under /_fake/*: create items, push branches, set CI profiles, inject faults, read the whole state.
// Octopus lives under /octopus/*. No secret is checked: any token is accepted.

var port = int.TryParse(Environment.GetEnvironmentVariable("FAKE_PORT"), out var p0) ? p0 : 5200;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
var world = new World();
var gate = new object();

app.Run(async ctx =>
{
    var method = ctx.Request.Method; var path = ctx.Request.Path.Value ?? "/"; var query = ctx.Request.Query;
    string body; using (var rd = new StreamReader(ctx.Request.Body)) body = await rd.ReadToEndAsync();
    JsonNode? json = null; try { if (body.Length > 0) json = JsonNode.Parse(body); } catch { }
    (int, object?) res;
    // /_fake/latency: the answer to a matching request is held back, as a slow GitHub would (the test control itself is never slowed)
    int wait; lock (gate) { wait = world.LatencyFor(method, path, body); }
    if (wait > 0) await Task.Delay(wait);
    lock (gate) { res = world.Handle(method, path, query, json, ctx.Request.Headers.Authorization.ToString()); }
    ctx.Response.StatusCode = res.Item1;
    if (res.Item2 is not null) { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync(res.Item2 is string s ? s : JsonSerializer.Serialize(res.Item2)); }
});
app.Run();

sealed class Issue { public int Number; public string Node = ""; public string Title = ""; public string Body = ""; public bool Open = true; public List<string> Labels = new(); public string? Column; public string? Card; public List<Comment> Comments = new(); public DateTime Created = DateTime.UtcNow; }
sealed class Comment { public long Id; public string Body = ""; public string User = "factory-bot"; public DateTime At = DateTime.UtcNow; }
sealed class Pr { public int Number; public string Ref = ""; public string Sha = ""; public string Title = ""; public string Body = ""; public bool Open = true; public bool Merged; public string? MergeSha; public int MergeableNullReads; public int Behind; public bool Conflict; public List<string> Files = new(); public List<JsonObject> FileObjs = new(); public List<Rev> Reviews = new(); public int? Issue; public string Author = "factory-bot"; public string Profile = "green"; public double DelaySeconds; public bool Fork, Draft; public List<Comment> Comments = new(); }
sealed class Check { public string Name = ""; public string Status = "completed"; public string? Conclusion = "success"; public DateTime Started = DateTime.UtcNow; public DateTime? CompleteAt; public int Id; }
sealed class Rev { public long Id; public string State = ""; public string Commit = ""; public string User = ""; public string Body = ""; }
sealed class Fault { public Regex Match = null!; public int Status; public int Remaining; public string Body = "{\"message\":\"injected fault\"}"; }
sealed class Rel { public string Id = ""; public string Version = ""; public string Notes = ""; public DateTime At; public double ProdDelay; }

sealed class World
{
    const string Owner = "clearmeasure-aisf-sample-apps", Repo = "cmdemo1-workorders", Master = "master";
    readonly string[] columns = { "Todo", "In Progress", "Technical Design", "Development", "Functional Testing", "Release Queue", "Done" };
    readonly Dictionary<int, Issue> issues = new();
    readonly Dictionary<int, Pr> prs = new();
    readonly Dictionary<string, List<Check>> checks = new();
    readonly HashSet<string> refs = new();
    readonly Dictionary<string, (string Sha, List<string> Files, List<JsonObject> Objs)> branches = new();
    long nextReview = 1;
    readonly Dictionary<string, (string Profile, double Delay)> branchProfile = new();
    readonly List<Fault> faults = new();
    readonly List<Rel> releases = new();
    readonly List<string> log = new();
    readonly HashSet<string> labelNames = new() { "factory worker", "AI Factory", "Auto Merge", "video", "Demo", "Ready To Move", "Needs Human Review" };
    string masterSha = Sha("master0");
    int nextNumber = 1, nextComment = 1000, nextCheck = 1, nextPr = 1;
    int healthStatus = 200;
    bool masterRed; double prodDelay = 0; string releaseMode = "auto"; string docsRelease = "skipped"; bool masterHold;
    long counter = 0; int appMints;

    static string Sha(string seed) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    string NewSha() => Sha("c" + (++counter) + DateTime.UtcNow.Ticks);
    // The product's own documentation rule (docsOnlyPaths of the instance configuration, as Test-DocsOnlyFiles reads it), not a copy that can drift:
    // from FACTORY_CONFIG when the fake is started with it, else from config/instance.json above the binary.
    static Regex[]? docPats;
    static Regex[] DocPats()
    {
        if (docPats != null) return docPats;
        var path = Environment.GetEnvironmentVariable("FACTORY_CONFIG");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); (path == null || !File.Exists(path)) && d != null; d = d.Parent) { var c = Path.Combine(d.FullName, "config", "instance.json"); if (File.Exists(c)) path = c; }
        if (path == null || !File.Exists(path)) throw new InvalidOperationException("the fake needs the instance configuration for docsOnlyPaths: set FACTORY_CONFIG");
        return docPats = JsonNode.Parse(File.ReadAllText(path))!["docsOnlyPaths"]!.AsArray().Select(x => new Regex(x!.ToString(), RegexOptions.IgnoreCase)).ToArray();
    }
    static bool DocsOnly(IEnumerable<string> files) { var l = files.ToList(); var pats = DocPats(); return l.Count > 0 && l.All(f => pats.Any(p => p.IsMatch(f))); }
    static JsonObject J(params (string, JsonNode?)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }

    static string Who(string auth)
    {
        var t = auth.StartsWith("Bearer ") ? auth[7..] : auth;
        if (t.StartsWith("fake-app-")) return "factory-worker-author[bot]";
        return t switch { "fake-reviewer" => "factory-worker-reviewer[bot]", "fake-testperson" => "factory-worker-testperson[bot]", _ => "factory-bot" };
    }

    public (int, object?) Handle(string method, string path, Microsoft.AspNetCore.Http.IQueryCollection q, JsonNode? b, string auth = "")
    {
        log.Add($"{method} {path}");
        if (path.StartsWith("/_fake/")) return Control(method, path[7..], q, b);
        foreach (var f in faults.ToList())
            if (f.Remaining > 0 && f.Match.IsMatch($"{method} {path}")) { f.Remaining--; return (f.Status, f.Body); }
        if (path.StartsWith("/octopus/")) return Octopus(path[9..], q);
        var root = $"/repos/{Owner}/{Repo}";
        Match m;
        if (path == "/user") return (200, J(("login", "factory-bot")));
        if (path == "/_healthcheck") return (healthStatus, healthStatus == 200 ? "ok" : null);        // stands in for the QC environment's app in the QC scenarios
        if (path == "/graphql") return GraphQl(b!);
        if (path == root && method == "GET") return (200, J(("permissions", J(("push", true), ("admin", true)))));
        if (path == root + "/labels" && method == "POST") { var nm = b!["name"]!.ToString(); return labelNames.Add(nm) ? (201, J(("name", nm))) : (422, "{\"message\":\"Validation Failed\"}"); }
        var am = Regex.Match(path, "^/app/installations/(\\d+)/access_tokens$");
        if (am.Success && method == "POST")
        {   // GitHub App installation token: the request must carry an RS256-shaped JWT whose iss is the App id
            var parts = (auth.StartsWith("Bearer ") ? auth[7..] : auth).Split('.');
            if (parts.Length != 3) return (401, "{\"message\":\"A JSON web token could not be decoded\"}");
            string iss; try { var pl = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='))); iss = JsonNode.Parse(pl)!["iss"]!.ToString(); } catch { return (401, "{\"message\":\"bad payload\"}"); }
            appMints++;
            return (201, J(("token", "fake-app-" + iss), ("expires_at", DateTime.UtcNow.AddHours(1).ToString("o"))));
        }
        if ((m = Regex.Match(path, $"^{root}/issues/(\\d+)/labels(/(.+))?$")).Success && issues.TryGetValue(int.Parse(m.Groups[1].Value), out var li))
        {
            if (method == "POST") { foreach (var l in b!["labels"]!.AsArray()) if (!li.Labels.Contains(l!.ToString())) li.Labels.Add(l!.ToString()); return (200, new JsonArray(li.Labels.Select(x => (JsonNode)J(("name", x))).ToArray())); }
            if (method == "DELETE") return li.Labels.Remove(Uri.UnescapeDataString(m.Groups[3].Value)) ? (200, new JsonArray()) : (404, "{\"message\":\"Label does not exist\"}");
        }
        if (path == root + "/labels" && method == "GET") return (200, new JsonArray(labelNames.Select(n => (JsonNode)J(("name", n))).ToArray()));
        if ((m = Regex.Match(path, $"^{root}/issues/(\\d+)$")).Success)
        {
            var i = issues[int.Parse(m.Groups[1].Value)];
            if (method == "PATCH") { if (b?["body"] is { } bb) i.Body = bb.ToString(); if (b?["state"]?.ToString() == "closed") i.Open = false; return (200, J(("number", i.Number))); }
            return (200, J(("number", i.Number)));
        }
        if ((m = Regex.Match(path, $"^{root}/issues/(\\d+)/comments$")).Success)
        {
            // a pull request is an issue to the comments API: its comments are kept on the PR
            var cn = int.Parse(m.Groups[1].Value);
            var thread = issues.TryGetValue(cn, out var ci) ? ci.Comments : prs.TryGetValue(cn, out var cp) ? cp.Comments : null;
            if (thread == null) return (404, "{\"message\":\"Not Found\"}");
            if (method == "POST") { var c = new Comment { Id = nextComment++, Body = b!["body"]!.ToString() }; thread.Add(c); return (201, J(("id", c.Id))); }
            int page = int.TryParse(q["page"], out var pg) ? pg : 1;
            return (200, new JsonArray(thread.Skip((page - 1) * 100).Take(100).Select(c => (JsonNode)J(("id", c.Id), ("body", c.Body), ("user", J(("login", c.User))), ("created_at", c.At.ToString("o")))).ToArray()));
        }
        if ((m = Regex.Match(path, $"^{root}/issues/comments/(\\d+)$")).Success && method == "PATCH")
        {
            var id = long.Parse(m.Groups[1].Value);
            foreach (var thread in issues.Values.Select(x => x.Comments).Concat(prs.Values.Select(x => x.Comments))) { var c = thread.FirstOrDefault(x => x.Id == id); if (c != null) { c.Body = b!["body"]!.ToString(); return (200, J(("id", id))); } }
            return (404, "{\"message\":\"Not Found\"}");
        }
        if (path == root + $"/git/ref/heads/{Master}") return (200, J(("object", J(("sha", masterSha)))));
        if (path == root + "/git/refs" && method == "POST")
        {
            var r = b!["ref"]!.ToString();
            if (!refs.Add(r)) return (422, "{\"message\":\"Reference already exists\"}");
            return (201, J(("ref", r)));
        }
        if ((m = Regex.Match(path, $"^{root}/git/matching-refs/(.+)$")).Success && method == "GET")
        {
            // a page holds what was asked for (per_page, 100 at most) or fewer when /_fake/ref-page set a smaller size, as a server may
            int rp = int.TryParse(q["page"], out var rpg) ? rpg : 1; int per = Math.Min(Math.Clamp(int.TryParse(q["per_page"], out var pp) ? pp : 30, 1, 100), refPage);
            return (200, new JsonArray(refs.Where(r => r.StartsWith("refs/" + m.Groups[1].Value, StringComparison.Ordinal)).OrderBy(r => r, StringComparer.Ordinal).Skip((rp - 1) * per).Take(per).Select(r => (JsonNode)J(("ref", r))).ToArray()));
        }
        if ((m = Regex.Match(path, $"^{root}/git/refs/(.+)$")).Success && method == "DELETE")
            return refs.Remove("refs/" + m.Groups[1].Value) ? (204, null) : (422, "{\"message\":\"Reference does not exist\"}");
        if (path == root + "/pulls" && method == "GET")
            return (200, new JsonArray(prs.Values.Where(x => x.Open).Select(x => (JsonNode)PrJson(x)).ToArray()));
        if (path == root + "/pulls" && method == "POST")
        {
            var head = b!["head"]!.ToString();
            if (!branches.TryGetValue(head, out var br) || br.Files.Count == 0) return (422, "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"No commits between master and " + head + "\"}]}");
            var bp = branchProfile.TryGetValue(head, out var bpv) ? bpv : ("green", 0.0);
            var pr = new Pr { Profile = bp.Item1, DelaySeconds = bp.Item2, Number = nextNumber++, Ref = head, Sha = br.Sha, Title = b["title"]!.ToString(), Body = b["body"]?.ToString() ?? "", Files = br.Files.ToList(), FileObjs = br.Objs.ToList() };
            var mm = Regex.Match(head, @"/(\d+)-"); if (mm.Success) pr.Issue = int.Parse(mm.Groups[1].Value);
            prs[pr.Number] = pr; ApplyProfile(pr, pr.Sha);
            return (201, PrJson(pr));
        }
        if ((m = Regex.Match(path, $"^{root}/pulls/(\\d+)(/(files|reviews|merge|update-branch))?$")).Success)
        {
            if (!prs.TryGetValue(int.Parse(m.Groups[1].Value), out var pr)) return (404, "{\"message\":\"Not Found\"}");
            switch (m.Groups[3].Value)
            {
                case "files": return (200, new JsonArray(pr.FileObjs.Select(o => (JsonNode)JsonNode.Parse(o.ToJsonString())!).ToArray()));
                case "reviews":
                    if (method == "POST")
                    {
                        var who = Who(auth); var ev = b!["event"]!.ToString();
                        if (ev == "APPROVE" && who == pr.Author) return (422, "{\"message\":\"Unprocessable Entity: Can not approve your own pull request\"}");
                        var rv = new Rev { Id = nextReview++, State = ev == "APPROVE" ? "APPROVED" : ev == "REQUEST_CHANGES" ? "CHANGES_REQUESTED" : "COMMENTED", Commit = b["commit_id"]?.ToString() ?? pr.Sha, User = who, Body = b["body"]?.ToString() ?? "" };
                        pr.Reviews.Add(rv); return (200, J(("id", rv.Id), ("state", rv.State)));
                    }
                    return (200, new JsonArray(pr.Reviews.Select(r => (JsonNode)J(("id", r.Id), ("state", r.State), ("commit_id", r.Commit), ("user", J(("login", r.User))), ("body", r.Body))).ToArray()));
                case "update-branch":
                    if (pr.Conflict) return (422, "{\"message\":\"merge conflict between base and head\"}");
                    pr.Behind = 0; pr.Sha = NewSha(); ApplyProfile(pr, pr.Sha); return (202, "{\"message\":\"Updating pull request branch.\"}");
                case "merge": return Merge(pr, b);
                default:
                    if (method == "PATCH")
                    {   // title and body only, and only while open (as GitHub: a closed pull request's text can be edited there, the product never does)
                        if (b?["body"] is { } nb) pr.Body = nb.ToString();
                        if (b?["title"] is { } nt) pr.Title = nt.ToString();
                    }
                    return (200, PrJson(pr, true));
            }
        }
        if ((m = Regex.Match(path, $"^{root}/commits/([0-9a-f]+)/check-runs$")).Success)
        {
            var sha = m.Groups[1].Value; var list = checks.TryGetValue(sha, out var l) ? l : new();
            foreach (var c in list) if (c.CompleteAt is { } at && DateTime.UtcNow >= at) { c.Status = "completed"; c.CompleteAt = null; }
            return (200, J(("check_runs", new JsonArray(list.Select(c => (JsonNode)J(("id", c.Id), ("name", c.Name), ("status", c.Status), ("conclusion", c.Conclusion), ("started_at", c.Started.ToString("o")))).ToArray()))));
        }
        if (path == root + "/actions/runs" && method == "GET") return (200, J(("workflow_runs", new JsonArray(J(("id", 1), ("conclusion", "failure"))))));
        if (Regex.IsMatch(path, $"^{root}/actions/runs/\\d+/rerun-failed-jobs$"))
        {
            foreach (var l in checks.Values) foreach (var c in l.Where(c => c.Conclusion == "failure")) c.Conclusion = "success";
            return (201, null);
        }
        if (Regex.IsMatch(path, $"^{root}/releases/tags/demo-\\d+$")) return (404, "{\"message\":\"Not Found\"}");
        if (Regex.IsMatch(path, $"^{root}/compare/")) return (200, J(("status", "ahead")));
        return (404, "{\"message\":\"fake has no route " + method + " " + path + "\"}");
    }

    JsonObject PrJson(Pr p, bool detail = false)
    {
        var o = J(("number", p.Number), ("title", p.Title), ("state", p.Open ? "open" : "closed"), ("html_url", $"https://fake/pull/{p.Number}"), ("merged", p.Merged), ("user", J(("login", p.Author))), ("body", p.Body),
            ("head", J(("ref", p.Ref), ("sha", p.Sha), ("repo", J(("full_name", p.Fork ? "someone-else/cmdemo1-workorders" : $"{Owner}/{Repo}"))))), ("draft", p.Draft), ("changed_files", p.Files.Count), ("merge_commit_sha", p.MergeSha));
        if (detail)
        {
            if (p.MergeableNullReads > 0) { p.MergeableNullReads--; o["mergeable"] = null; o["mergeable_state"] = "unknown"; }
            else { o["mergeable"] = !p.Conflict; o["mergeable_state"] = p.Conflict ? "dirty" : (p.Behind > 0 ? "behind" : "clean"); }
        }
        return o;
    }

    void ApplyProfile(Pr p, string sha)
    {
        var l = new List<Check>(); checks[sha] = l;
        var done = p.DelaySeconds > 0 ? (DateTime?)DateTime.UtcNow.AddSeconds(p.DelaySeconds) : null;
        string st = done is null ? "completed" : "in_progress";
        l.Add(new Check { Id = nextCheck++, Name = "Detect code changes" });
        l.Add(new Check { Id = nextCheck++, Name = "secret-scan", Status = st, CompleteAt = done, Conclusion = "success" });
        var build = new Check { Id = nextCheck++, Name = "Build result", Status = st, CompleteAt = done, Conclusion = p.Profile == "red" ? "failure" : "success" };
        if (p.Profile == "stuck") { build.Status = "in_progress"; build.CompleteAt = null; build.Conclusion = null; }
        l.Add(build);
    }

    (int, object?) Merge(Pr pr, JsonNode? b)
    {
        if (!pr.Open) return (405, "{\"message\":\"Pull Request is not open\"}");
        if (pr.Conflict) return (405, "{\"message\":\"Pull Request is not mergeable\"}");
        if (b?["sha"] is { } want && want.ToString() != pr.Sha) return (409, "{\"message\":\"Head branch was modified. Review and try the merge again.\"}");
        if (pr.Behind > 0) { pr.Behind--; return (409, "{\"message\":\"Head branch is out of date. Update the branch and try again.\"}"); }
        var cs = checks.TryGetValue(pr.Sha, out var l) ? l : new();
        foreach (var req in new[] { "Build result", "secret-scan" })
            if (!cs.Any(c => c.Name == req && c.Conclusion == "success" && (c.CompleteAt is null || DateTime.UtcNow >= c.CompleteAt) && c.Status == "completed"))
                return (405, "{\"message\":\"Required status check \\\"" + req + "\\\" is expected.\"}");
        pr.Merged = true; pr.Open = false; masterSha = NewSha(); pr.MergeSha = masterSha;
        var docs = DocsOnly(pr.Files);
        var mc = new List<Check> { new() { Id = nextCheck++, Name = "Build result", Status = masterHold ? "in_progress" : "completed", Conclusion = masterHold ? null : (masterRed ? "failure" : "success") }, new() { Id = nextCheck++, Name = "secret-scan" } };
        if (docs) { if (docsRelease != "absent") mc.Add(new Check { Id = nextCheck++, Name = "release", Conclusion = "skipped" }); }
        else if (releaseMode != "none")
        {
            mc.Add(new Check { Id = nextCheck++, Name = "release" });
            releases.Add(new Rel { Id = "Releases-" + (releases.Count + 1), Version = "2.4." + (100 + releases.Count), Notes = $"Build https://fake/run/1 of {masterSha}", At = DateTime.UtcNow, ProdDelay = prodDelay });
        }
        checks[masterSha] = mc;
        return (200, J(("merged", true), ("sha", masterSha), ("message", "Pull Request successfully merged")));
    }

    (int, object?) GraphQl(JsonNode b)
    {
        var q = b["query"]!.ToString(); var v = b["variables"] ?? new JsonObject();
        JsonObject Data(JsonNode n) => J(("data", n));
        if (q.Contains("updateProjectV2ItemFieldValue"))
        {
            var card = v["i"]!.ToString(); var opt = v["o"]!.ToString();
            var col = columns.First(c => "opt_" + c.Replace(' ', '_') == opt);
            var iss = issues.Values.FirstOrDefault(x => x.Card == card); if (iss == null) return (200, "{\"errors\":[{\"message\":\"card not found\"}]}");
            iss.Column = col; return (200, Data(J(("updateProjectV2ItemFieldValue", J(("projectV2Item", J(("id", card))))))));
        }
        if (q.Contains("addProjectV2ItemById"))
        {
            var node = v["c"]!.ToString(); var iss = issues.Values.First(x => x.Node == node); iss.Card ??= "card_" + iss.Number;
            return (200, Data(J(("addProjectV2ItemById", J(("item", J(("id", iss.Card))))))));
        }
        if (q.Contains("issue(number"))
        {
            var n = v["n"]!.GetValue<int>(); if (!issues.TryGetValue(n, out var i)) return (200, Data(J(("repository", J(("issue", null))))));
            var card = i.Card == null ? new JsonArray() : new JsonArray(J(("id", i.Card), ("project", J(("number", 679))), ("fieldValueByName", i.Column == null ? null : J(("name", i.Column)))));
            return (200, Data(J(("repository", J(("issue", J(("id", i.Node), ("number", i.Number), ("state", i.Open ? "OPEN" : "CLOSED"), ("title", i.Title), ("body", i.Body), ("url", $"https://fake/issues/{i.Number}"), ("createdAt", i.Created.ToString("o")),
                ("labels", J(("nodes", new JsonArray(i.Labels.Select(x => (JsonNode)J(("name", x))).ToArray())))), ("projectItems", J(("nodes", card))))))))));
        }
        if (q.Contains("items(first"))
        {
            var nodes = issues.Values.Where(i => i.Card != null).Select(i => (JsonNode)J(("id", i.Card), ("fieldValueByName", i.Column == null ? null : J(("name", i.Column))),
                ("content", J(("number", i.Number), ("state", i.Open ? "OPEN" : "CLOSED"), ("title", i.Title), ("labels", J(("nodes", new JsonArray(i.Labels.Select(x => (JsonNode)J(("name", x))).ToArray())))))))).ToArray();
            return (200, Data(J(("organization", J(("projectV2", J(("items", J(("pageInfo", J(("hasNextPage", false), ("endCursor", null))), ("nodes", new JsonArray(nodes)))))))))));
        }
        if (q.Contains("fields(first"))
        {
            var opts = new JsonArray(columns.Select(c => (JsonNode)J(("id", "opt_" + c.Replace(' ', '_')), ("name", c))).ToArray());
            return (200, Data(J(("organization", J(("projectV2", J(("id", "PVT_fake"), ("fields", J(("nodes", new JsonArray(J(("id", "FIELD_status"), ("name", "Status"), ("options", opts)))))))))))));
        }
        return (200, "{\"errors\":[{\"message\":\"fake graphql: unrecognised query\"}]}");
    }

    (int, object?) Octopus(string path, Microsoft.AspNetCore.Http.IQueryCollection q)
    {
        if (path == "api/users/me") return (200, J(("Id", "Users-1")));
        if (Regex.IsMatch(path, @"^api/Spaces-355/projects/(cmdemo1-ui|Projects-1)$")) return (200, J(("Id", "Projects-1"), ("Name", "cmdemo1-ui")));
        if (path == "api/Spaces-355/projects/Projects-1/releases")
            return (200, J(("Items", new JsonArray(releases.AsEnumerable().Reverse().Select(r => (JsonNode)J(("Id", r.Id), ("Version", r.Version), ("ReleaseNotes", r.Notes), ("Assembled", r.At.ToString("o")))).ToArray()))));
        if (path == "api/Spaces-355/environments") return (200, J(("Items", new JsonArray(J(("Id", "E-tdd"), ("Name", "tdd")), J(("Id", "E-uat"), ("Name", "uat")), J(("Id", "E-prod"), ("Name", "prod"))))));
        if (path == "api/Spaces-355/deployments")
        {
            var items = new JsonArray();
            foreach (var r in releases) foreach (var (env, idx) in new[] { ("E-tdd", 0), ("E-uat", 1), ("E-prod", 2) })
                items.Add(J(("Id", $"Deployments-{r.Id}-{env}"), ("ReleaseId", r.Id), ("EnvironmentId", env), ("TaskId", $"{r.Id}|{env}"), ("Created", r.At.AddSeconds(idx).ToString("o"))));
            return (200, J(("Items", items)));
        }
        var m = Regex.Match(path, @"^api/Spaces-355/tasks/(.+)$");
        if (m.Success)
        {
            var parts = m.Groups[1].Value.Split('|'); var r = releases.FirstOrDefault(x => x.Id == parts[0]);
            if (r == null) return (404, null);
            var due = parts[1] == "E-prod" ? r.At.AddSeconds(r.ProdDelay) : r.At;
            var done = DateTime.UtcNow >= due;
            return (200, J(("State", done ? "Success" : "Executing"), ("IsCompleted", done)));
        }
        return (404, null);
    }

    int latencyMs; Regex? latencyMatch; int refPage = 100;
    public int LatencyFor(string method, string path, string body) => latencyMs > 0 && !path.StartsWith("/_fake/") && latencyMatch!.IsMatch(method + " " + path + "\n" + body) ? latencyMs : 0;
    (int, object?) Control(string method, string what, Microsoft.AspNetCore.Http.IQueryCollection q, JsonNode? b)
    {
        switch (what)
        {
            case "latency":   // { ms, match }: every request whose "METHOD path" line plus body matches the regular expression waits ms first; ms 0 ends it
                latencyMs = b?["ms"]?.GetValue<int>() ?? 0; latencyMatch = latencyMs > 0 ? new Regex(b?["match"]?.ToString() ?? ".") : null; return (200, J(("ok", true)));
            case "ref-page":   // { size }: the most refs one page of matching-refs holds (100 when not set)
                refPage = Math.Clamp(b?["size"]?.GetValue<int>() ?? 100, 1, 100); return (200, J(("ok", true)));
            case "reset":
                latencyMs = 0; latencyMatch = null; refPage = 100;
                issues.Clear(); prs.Clear(); checks.Clear(); refs.Clear(); branches.Clear(); faults.Clear(); releases.Clear(); log.Clear();
                masterSha = Sha("master0"); healthStatus = 200; masterRed = false; prodDelay = 0; releaseMode = "auto"; docsRelease = "skipped"; masterHold = false; return (200, J(("ok", true)));
            case "issue":
            {
                var i = new Issue { Number = nextNumber++, Title = b!["title"]!.ToString(), Body = b["body"]?.ToString() ?? "", Column = b["column"]?.ToString() };
                if (b["createdAt"] is { } ca) i.Created = DateTime.Parse(ca.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);   // an item of an earlier date
                i.Node = "I_" + i.Number; foreach (var l in b["labels"]?.AsArray() ?? new JsonArray()) i.Labels.Add(l!.ToString());
                issues[i.Number] = i; if (i.Column != null || b["onBoard"]?.GetValue<bool>() == true) i.Card = "card_" + i.Number;
                return (200, J(("number", i.Number)));
            }
            case "push-branch":
            {
                var br = b!["branch"]!.ToString(); var objs = new List<JsonObject>();
                foreach (var x in b["files"]!.AsArray())
                {
                    if (x is JsonValue) objs.Add(J(("filename", x.ToString()), ("additions", 3), ("deletions", 0), ("status", "added"), ("patch", "@@ -0,0 +1,3 @@\n+content")));
                    else { var o = x!.AsObject(); objs.Add(J(("filename", o["name"]!.ToString()), ("additions", o["additions"]?.GetValue<int>() ?? 3), ("deletions", o["deletions"]?.GetValue<int>() ?? 0), ("status", o["status"]?.ToString() ?? "added"), ("patch", o["patch"]?.ToString() ?? "@@ -0,0 +1,3 @@\n+content"))); }
                }
                var files = objs.Select(o => o["filename"]!.ToString()).ToList();
                var sha = NewSha(); branches[br] = (sha, files, objs);
                var existing = prs.Values.FirstOrDefault(x => x.Ref == br && x.Open);
                var prof = b["profile"]?.ToString() ?? "green"; var dly = b["delaySeconds"]?.GetValue<double>() ?? 0; branchProfile[br] = (prof, dly);
                if (existing != null) { existing.Sha = sha; existing.Files = files; existing.FileObjs = objs; existing.Profile = prof; existing.DelaySeconds = dly; ApplyProfile(existing, sha); }
                return (200, J(("sha", sha)));
            }
            case "pr":
            {
                var pr = prs[b!["number"]!.GetValue<int>()];
                if (b["profile"] != null) { pr.Profile = b["profile"]!.ToString(); ApplyProfile(pr, pr.Sha); }
                if (b["behind"] != null) pr.Behind = b["behind"]!.GetValue<int>();
                if (b["author"] != null) pr.Author = b["author"]!.ToString();
                if (b["open"] != null) pr.Open = b["open"]!.GetValue<bool>();
                if (b["fork"] != null) pr.Fork = b["fork"]!.GetValue<bool>();
                if (b["draft"] != null) pr.Draft = b["draft"]!.GetValue<bool>();
                if (b["merged"] != null) { pr.Merged = b["merged"]!.GetValue<bool>(); if (pr.Merged) { pr.Open = false; pr.MergeSha ??= NewSha(); } }
                if (b["conflict"] != null) pr.Conflict = b["conflict"]!.GetValue<bool>();
                if (b["mergeableNullReads"] != null) pr.MergeableNullReads = b["mergeableNullReads"]!.GetValue<int>();
                if (b["delaySeconds"] != null) { pr.DelaySeconds = b["delaySeconds"]!.GetValue<double>(); ApplyProfile(pr, pr.Sha); }
                return (200, J(("ok", true)));
            }
            case "review-state":
            {   // a dismissed review: every review of the PR by that user takes the given state (default DISMISSED)
                var pr = prs[b!["number"]!.GetValue<int>()];
                foreach (var r in pr.Reviews.Where(r => b["user"] == null || r.User == b["user"]!.ToString())) r.State = b["state"]?.ToString() ?? "DISMISSED";
                return (200, J(("ok", true)));
            }
            case "checks":
            {
                var sha = b!["sha"]!.ToString(); var l = checks.TryGetValue(sha, out var x) ? x : (checks[sha] = new());
                var c = l.FirstOrDefault(y => y.Name == b["name"]!.ToString()); if (c == null) { c = new Check { Id = nextCheck++, Name = b["name"]!.ToString() }; l.Add(c); }
                c.Status = b["status"]?.ToString() ?? "completed"; c.Conclusion = b["conclusion"]?.ToString(); c.CompleteAt = null; return (200, J(("ok", true)));
            }
            case "config":
                if (b?["masterRed"] != null) masterRed = b["masterRed"]!.GetValue<bool>();
                if (b?["prodDelaySeconds"] != null) prodDelay = b["prodDelaySeconds"]!.GetValue<double>();
                if (b?["releaseMode"] != null) releaseMode = b["releaseMode"]!.ToString();
                if (b?["docsRelease"] != null) docsRelease = b["docsRelease"]!.ToString();
                if (b?["masterHold"] != null) masterHold = b["masterHold"]!.GetValue<bool>();
                return (200, J(("ok", true)));
            case "fault":
                faults.Add(new Fault { Match = new Regex(b!["match"]!.ToString()), Status = b["status"]!.GetValue<int>(), Remaining = b["count"]?.GetValue<int>() ?? 1, Body = b["body"]?.ToString() ?? "{\"message\":\"injected fault\"}" });
                return (200, J(("ok", true)));
            case "state":
                return (200, J(
                    ("issues", new JsonArray(issues.Values.Select(i => (JsonNode)J(("number", i.Number), ("open", i.Open), ("column", i.Column), ("labels", new JsonArray(i.Labels.Select(x => (JsonNode)x).ToArray())),
                        ("comments", new JsonArray(i.Comments.Select(c => (JsonNode)J(("id", c.Id), ("body", c.Body))).ToArray())))).ToArray())),
                    ("appMints", appMints), ("prs", new JsonArray(prs.Values.Select(p => (JsonNode)J(("number", p.Number), ("ref", p.Ref), ("body", p.Body), ("sha", p.Sha), ("open", p.Open), ("merged", p.Merged), ("mergeSha", p.MergeSha), ("comments", new JsonArray(p.Comments.Select(c => (JsonNode)J(("id", c.Id), ("body", c.Body))).ToArray())), ("reviews", new JsonArray(p.Reviews.Select(r => (JsonNode)J(("user", r.User), ("state", r.State), ("commit", r.Commit))).ToArray())), ("files", new JsonArray(p.Files.Select(f => (JsonNode)f).ToArray())))).ToArray())),
                    ("refs", new JsonArray(refs.Select(r => (JsonNode)r).ToArray())), ("masterSha", masterSha), ("releases", releases.Count), ("requests", log.Count)));
            case "health": healthStatus = b?["status"]?.GetValue<int>() ?? 200; return (200, J(("ok", true)));
            case "log": return (200, new JsonArray(log.Select(l => (JsonNode)l).ToArray()));
        }
        return (404, "{\"message\":\"unknown control\"}");
    }
}
