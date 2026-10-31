using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Qc;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.1: the QC service over the gateway's real clients (QcGitHubClient, the journal store and ledger of the journal command) against a fake of the GitHub endpoints the rule script reads and writes: the
/// requests the verbs make (paths, headers, bodies), the state comment and journal comments they leave, and the way a refusal of GitHub becomes the codes of Invoke-Gh.
/// </summary>
public sealed class QcPortTests
{
    const string Login = "factory-bot";
    const string Sha1 = "1111111111111111111111111111111111111111";
    static readonly string Sha7 = Sha1[..7];

    sealed record Cmt(long Id, string Login, string Body);

    /// <summary>The GitHub of one repository o/r: issues and pull requests share numbers, comments, pulls with files and reviews, the card of each issue, and the GraphQL queries the rule script sends.</summary>
    sealed class FakeApi : HttpMessageHandler
    {
        public readonly Dictionary<int, List<Cmt>> Comments = [];
        public readonly Dictionary<int, JsonObject> Pulls = [];
        public readonly Dictionary<int, string[]> Files = [];
        public readonly Dictionary<int, JsonArray> Reviews = [];
        public readonly Dictionary<int, (bool Open, string Title, string[] Labels, string? Column)> Issues = [];
        public readonly List<(string Method, string Path, string Body, string UserAgent, string? Cause, string? Authorization)> Requests = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept;
        long nextId = 500;

        public List<Cmt> Of(int n) { if (!Comments.TryGetValue(n, out var l)) Comments[n] = l = []; return l; }
        public Cmt Add(int n, string login, string body) { var c = new Cmt(nextId++, login, body); Of(n).Add(c); return c; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var path = req.RequestUri!.AbsolutePath; var query = req.RequestUri.Query;
            Requests.Add((req.Method.Method, path + query, body, req.Headers.UserAgent.ToString(), req.Headers.TryGetValues("X-Fake-Cause", out var cv) ? cv.First() : null, req.Headers.Authorization?.ToString()));
            if (Intercept?.Invoke(req) is { } custom) return Task.FromResult(custom);
            return Task.FromResult(Handle(req.Method.Method, path, query, body));
        }

        static HttpResponseMessage Json(JsonNode n, HttpStatusCode s = HttpStatusCode.OK) => new(s) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
        static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json") };
        static JsonObject C(Cmt c) => new() { ["id"] = c.Id, ["user"] = new JsonObject { ["login"] = c.Login }, ["body"] = c.Body };

        HttpResponseMessage Handle(string method, string path, string query, string body)
        {
            if (path == "/user") return Json(new JsonObject { ["login"] = Login });
            if (path == "/graphql") return GraphQl(JsonNode.Parse(body)!);
            var m = Regex.Match(path, @"^/repos/o/r/issues/(\d+)/comments$");
            if (m.Success)
            {
                var n = int.Parse(m.Groups[1].Value);
                if (method == "GET")
                {
                    var page = int.Parse(Regex.Match(query, @"[?&]page=(\d+)").Groups[1].Value);
                    return Json(new JsonArray(Of(n).Skip((page - 1) * 100).Take(100).Select(c => (JsonNode)C(c)).ToArray()));
                }
                return Json(C(Add(n, Login, JsonNode.Parse(body)!["body"]!.ToString())), HttpStatusCode.Created);
            }
            m = Regex.Match(path, @"^/repos/o/r/issues/comments/(\d+)$");
            if (m.Success)
            {
                var id = long.Parse(m.Groups[1].Value);
                foreach (var list in Comments.Values)
                {
                    var i = list.FindIndex(c => c.Id == id);
                    if (i < 0) continue;
                    if (method == "PATCH") list[i] = list[i] with { Body = JsonNode.Parse(body)!["body"]!.ToString() };
                    return Json(C(list[i]));
                }
                return NotFound();
            }
            if (path == "/repos/o/r/pulls") return Json(new JsonArray(Pulls.Values.Where(p => p["state"]!.ToString() == "open").Select(p => p.DeepClone()).ToArray()));
            m = Regex.Match(path, @"^/repos/o/r/pulls/(\d+)(/files|/reviews)?$");
            if (m.Success)
            {
                var n = int.Parse(m.Groups[1].Value);
                if (!Pulls.TryGetValue(n, out var pr)) return NotFound();
                return m.Groups[2].Value switch
                {
                    "/files" => Json(new JsonArray((Files.TryGetValue(n, out var f) ? f : []).Skip((int.Parse(Regex.Match(query, @"[?&]page=(\d+)").Groups[1].Value) - 1) * 100).Take(100).Select(x => (JsonNode)new JsonObject { ["filename"] = x }).ToArray())),
                    "/reviews" => Json(Reviews.TryGetValue(n, out var r) ? (JsonNode)r.DeepClone() : new JsonArray()),
                    _ => Json(pr.DeepClone()),
                };
            }
            return NotFound();
        }

        HttpResponseMessage GraphQl(JsonNode b)
        {
            var q = b["query"]!.ToString(); var v = b["variables"]!;
            JsonObject Data(JsonNode n) => new() { ["data"] = n };
            if (q.Contains("markPullRequestReadyForReview"))
            {
                var pr = Pulls.Values.FirstOrDefault(p => p["node_id"]!.ToString() == v["id"]!.ToString());
                if (pr is null) return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "pull request not found" }) });
                pr["draft"] = false; return Json(Data(new JsonObject { ["markPullRequestReadyForReview"] = new JsonObject { ["pullRequest"] = new JsonObject { ["number"] = pr["number"]!.DeepClone() } } }));
            }
            if (q.Contains("issue(number"))
            {
                var n = v["n"]!.GetValue<int>();
                if (!Issues.TryGetValue(n, out var i)) return Json(Data(new JsonObject { ["repository"] = new JsonObject { ["issue"] = null } }));
                var card = new JsonArray(new JsonObject { ["id"] = "card_" + n, ["project"] = new JsonObject { ["number"] = 679 }, ["fieldValueByName"] = i.Column is null ? null : new JsonObject { ["name"] = i.Column } });
                return Json(Data(new JsonObject
                {
                    ["repository"] = new JsonObject
                    {
                        ["issue"] = new JsonObject
                        {
                            ["id"] = "I_" + n, ["number"] = n, ["state"] = i.Open ? "OPEN" : "CLOSED", ["title"] = i.Title, ["body"] = "",
                            ["labels"] = new JsonObject { ["nodes"] = new JsonArray(i.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) }, ["projectItems"] = new JsonObject { ["nodes"] = card },
                        },
                    },
                }));
            }
            if (q.Contains("items(first"))
            {
                var nodes = Issues.Select(kv => (JsonNode)new JsonObject
                {
                    ["id"] = "card_" + kv.Key, ["fieldValueByName"] = kv.Value.Column is null ? null : new JsonObject { ["name"] = kv.Value.Column },
                    ["content"] = new JsonObject
                    {
                        ["number"] = kv.Key, ["state"] = kv.Value.Open ? "OPEN" : "CLOSED", ["title"] = kv.Value.Title, ["repository"] = new JsonObject { ["nameWithOwner"] = kv.Key >= 900 ? "other/repo" : "o/r" },
                        ["labels"] = new JsonObject { ["nodes"] = new JsonArray(kv.Value.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) },
                    },
                }).ToArray();
                return Json(Data(new JsonObject { ["organization"] = new JsonObject { ["projectV2"] = new JsonObject { ["items"] = new JsonObject { ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null }, ["nodes"] = new JsonArray(nodes) } } } }));
            }
            return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "unrecognised query" }) });
        }
    }

    sealed class StubRouter(Func<string, string, VerbAnswer> answer) : IVerbRouter
    {
        public readonly List<(string Verb, string Stdin)> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls.Add((verb, stdin)); return Task.FromResult(answer(verb, stdin)); }
    }

    const string Config = """
        { "name": "factory-worker", "owner": "o", "repo": "r", "project": 679, "label": "factory worker", "rivalLabel": "AI Factory", "autoMergeLabel": "Auto Merge",
          "columns": { "validation": "Functional Testing" }, "mergePolicy": "auto", "humanApprovers": ["alice"], "docsOnlyPaths": ["^docs/"],
          "timing": { "qcLinkDays": 7, "qcGraceMinutes": 30, "qcLifetimeMinutes": 480, "qcMaxRelaunches": 3, "qcTokenHours": 16, "qcBuildCapMinutes": 8 } }
        """;

    sealed class Rig
    {
        public readonly FakeApi Api = new();
        public readonly InMemoryQcEnvironmentStore Table = new();
        public readonly InMemoryQcIndex Index = new();
        public readonly StubRouter Router = new((verb, _) => new VerbAnswer(0, "{\"ok\":true,\"verb\":\"inspect-facts\",\"mandatory\":[{\"code\":\"secret-like\"},{\"code\":\"other\"}],\"exit\":0}"));
        public readonly QcService Qc;
        public readonly QcConfig Cfg;
        public readonly GitHubTokenProvider Tokens;
        public readonly HttpClient Http;
        public readonly Func<string, string?> Env;
        public DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

        public Rig(Func<string, string?>? envOverride = null)
        {
            Func<string, string?> env = n => envOverride?.Invoke(n) ?? n switch { "GH_TOKEN" => "fake", "FACTORY_PUBLIC_URL" => "https://gateway.example.test", "FACTORY_TOKEN_SECRET" => "gateway-" + new string('k', 24), "FACTORY_OWNER_MODE" => "1", _ => null };
            Cfg = QcConfig.From(JsonNode.Parse(Config), env);
            var tokens = GitHubTokenProvider.FromEnvironment(env)!;
            var http = new HttpClient(new GitHubAuthHandler(tokens, new QcRequestHandler(Api))) { BaseAddress = new Uri("https://api.test/") };
            Tokens = tokens; Http = http; Env = env;
            Qc = QcWiring.Build(Cfg, new RepoId("o", "r"), tokens, http, env, null, [], () => Router, Table, Index, () => Now);
            // an item in Functional Testing with a PASS verdict for its pull request, and the state comment that says so
            Api.Issues[5] = (true, "Item 5", ["factory worker"], "Functional Testing");
            Api.Pulls[9] = Pr(9, Sha1, "factory-worker/5-development");
            Api.Files[9] = ["src/a.cs"];
            var state = new JsonObject { ["schema"] = 1, ["item"] = 5, ["rev"] = 3, ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = 9, ["processed"] = false, ["head_sha"] = Sha1 } };
            Api.Add(5, "someone", "a comment of a person");
            Api.Add(5, Login, ItemStateComment.Body("factory-worker", 5, state));
        }

        public static JsonObject Pr(int number, string sha, string headRef, string author = "alice") => new()
        {
            ["number"] = number, ["state"] = "open", ["draft"] = false, ["title"] = "Trial", ["merged"] = false, ["node_id"] = "PR_" + number, ["created_at"] = "2026-10-29T08:00:00Z",
            ["user"] = new JsonObject { ["login"] = author }, ["head"] = new JsonObject { ["sha"] = sha, ["ref"] = headRef, ["repo"] = new JsonObject { ["full_name"] = "o/r" } },
        };

        public async Task<(int Code, JsonObject Out)> Call(string verb, object body)
        {
            var a = await Qc.Run(verb, System.Text.Json.JsonSerializer.Serialize(body));
            return (a.Code, (JsonObject)JsonNode.Parse(a.Output)!);
        }

        public JsonObject State(int item)
        {
            var c = Api.Of(item).Single(x => x.Body.StartsWith("<!-- factory-state:factory-worker v1 -->"));
            return ItemStateComment.Parse(c.Body)!;
        }

        public IEnumerable<string> Journal(int item) => Api.Of(item).Select(c => Regex.Match(c.Body, @"<!-- plant event=([\w-]+) key=").Groups[1].Value).Where(x => x != "");
    }

    // ---- a whole environment through the real clients -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEnvironmentFromTheOfferToItsTeardown_ThroughTheRealClients()
    {
        var r = new Rig();
        // the offer on the pull request and the item
        var (c0, o0) = await r.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal((0, "posted"), (c0, o0["outcome"]!.ToString()));
        Assert.Contains($"[Start the QC environment](https://gateway.example.test/qc/9?t=q1.", Assert.Single(r.Api.Of(9)).Body);
        // Start (a person with the operator's confirmation), the gateway places the job, the worker accepts, builds, runs and reports
        var (c1, o1) = await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        Assert.Equal((0, "started"), (c1, o1["outcome"]!.ToString()));
        var state = r.State(5);
        var rec = (JsonObject)state["qc"]!["9"]!;
        Assert.Equal(("requested", Sha1, 1, $"qc-9-{Sha7}-1", true), (rec["state"]!.ToString(), rec["sha"]!.ToString(), rec["attempt"]!.GetValue<int>(), rec["env_id"]!.ToString(), rec["trust_confirmed"]!.GetValue<bool>()));
        Assert.Equal(["qc-offered", "qc-requested"], r.Journal(5));
        Assert.True(((JsonObject)state["qc"]!["9"]!)["trust_confirmed"]!.GetValue<bool>());   // (the inspector said secret-like: the run needed the confirmation it was given)
        var (c2, _) = await r.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw1", assignment = "a-1", mode = "first", sha = Sha1 });
        Assert.Equal(0, c2);
        Assert.Contains("runs on **qw1**'s computer", Assert.Single(r.Api.Of(9)).Body);
        await r.Qc.Accept(5, "a-1", "qw1");
        foreach (var s in new[] { "building", "running" }) Assert.Equal(0, (await r.Call("qc-progress", new { item = 5, pr = 9, assignment = "a-1", state = s })).Code);
        var (c3, o3) = await r.Call("qc-report", new { item = 5, pr = 9, assignment = "a-1", sha = Sha1, env_id = $"qc-9-{Sha7}-1", probe = "verified", url = "http://qw1.test:5050", port = 5050 });
        Assert.Equal((0, "ready"), (c3, o3["outcome"]!.ToString()));
        Assert.Equal(Sha1, ((JsonObject)r.State(5)["qc"]!["9"]!)["ran_sha"]!.ToString());
        Assert.Equal(["qc-offered", "qc-requested", "qc-assigned", "qc-ready"], r.Journal(5));
        // the state comment kept what was in it and is revised by every save
        Assert.True(r.State(5)["rev"]!.GetValue<int>() > 3);
        Assert.Equal(Sha1, r.State(5)["verdict"]!["head_sha"]!.ToString());
        // the teardown asks the worker to stop; the worker confirms
        var (c4, o4) = await r.Call("qc-teardown", new { item = 5, pr = 9, reason = "person" });
        Assert.Equal((0, "stopping"), (c4, o4["outcome"]!.ToString()));
        var (c5, o5) = await r.Call("qc-stopped", new { item = 5, env_id = $"qc-9-{Sha7}-1", assignment = "a-1" });
        Assert.Equal((0, "torn-down"), (c5, o5["outcome"]!.ToString()));
        Assert.Equal("torn-down", ((JsonObject)r.State(5)["qc"]!["9"]!)["state"]!.ToString());
        Assert.Equal(["qc-offered", "qc-requested", "qc-assigned", "qc-ready", "qc-torn-down"], r.Journal(5));
        // the copy in the table follows, the index has no live item
        Assert.Equal("torn-down", r.Table.Rows["o/r!9"].Status);
        Assert.Empty(r.Index.Items());
    }

    [Fact]
    public async Task EveryRequestCarriesTheRuleScriptsUserAgentAndTheGatewaysBearer_AndAnApiVersion()
    {
        var r = new Rig();
        await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        Assert.NotEmpty(r.Api.Requests);
        Assert.All(r.Api.Requests, q => { Assert.Equal("factory-worker-gateway", q.UserAgent); Assert.Equal("Bearer fake", q.Authorization); });
        // none carries a cause id when the test mode is off
        Assert.All(r.Api.Requests, q => Assert.Null(q.Cause));
    }

    [Fact]
    public async Task TheStateCommentIsWrittenInV1sFormat_AndTheJournalLinesAreV1sText()
    {
        var r = new Rig();
        await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        var comment = r.Api.Of(5).Single(c => c.Body.StartsWith("<!-- factory-state:factory-worker v1 -->"));
        Assert.StartsWith("<!-- factory-state:factory-worker v1 -->\nstate of #5 for `factory-worker` (machine-read; edits by others are ignored)\n\n```json\n", comment.Body);
        var line = r.Api.Of(5).Single(c => c.Body.Contains("plant event=qc-requested"));
        Assert.Equal($"\U0001F3ED Plant manager: a person asked for a QC environment for PR #9 at head {Sha7} (attempt 1).\n\n<!-- plant event=qc-requested key=9|{Sha1}|1 -->", line.Body);
        // the writes went to the state comment (a PATCH) and the issue (a POST), in the paths the script writes
        Assert.Contains(r.Api.Requests, q => q.Method == "PATCH" && q.Path == $"/repos/o/r/issues/comments/{comment.Id}");
        Assert.Contains(r.Api.Requests, q => q.Method == "POST" && q.Path == "/repos/o/r/issues/5/comments");
    }

    [Fact]
    public async Task TheInspectorsCodesComeFromTheRouter_AsTheScriptReadsThem()
    {
        var r = new Rig();
        var (code, o) = await r.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "trust-confirm-needed", "secret-like"), (code, o["code"]!.ToString(), string.Join(",", ((JsonArray)o["reasons"]!).Select(x => x!.ToString()))));
        Assert.Equal([("inspect-facts", "{\"item\":5,\"pr\":9}")], r.Router.Calls);
    }

    [Fact]
    public async Task AFailedInspectorIsTheVerbsRefusal()
    {
        var r = new Rig();
        var router = new StubRouter((_, _) => new VerbAnswer(10, "{\"ok\":false,\"code\":\"no-pass-verdict\",\"reason\":\"no verdict\",\"exit\":10}"));
        var tokens = GitHubTokenProvider.FromEnvironment(n => n == "GH_TOKEN" ? "fake" : null)!;
        var http = new HttpClient(new GitHubAuthHandler(tokens, new QcRequestHandler(r.Api))) { BaseAddress = new Uri("https://api.test/") };
        var qc = QcWiring.Build(r.Cfg, new RepoId("o", "r"), tokens, http, _ => null, null, [], () => router, null, new InMemoryQcIndex());
        var a = await qc.Run("qc-request", "{\"item\":5,\"pr\":9}");
        Assert.Equal(10, a.Code);
        Assert.Equal(("no-pass-verdict", "no verdict"), (JsonNode.Parse(a.Output)!["code"]!.ToString(), JsonNode.Parse(a.Output)!["reason"]!.ToString()));
    }

    [Fact]
    public async Task ARecordTheStateCommentLacksComesFromTheTable_ThroughTheClients()
    {
        var r = new Rig();
        await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        var comment = r.Api.Of(5).Single(c => c.Body.StartsWith("<!-- factory-state:"));
        var state = ItemStateComment.Parse(comment.Body)!; state.Remove("qc");
        r.Api.Of(5)[r.Api.Of(5).IndexOf(comment)] = comment with { Body = ItemStateComment.Body("factory-worker", 5, state) };
        var (code, o) = await r.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal(0, code);
        Assert.Equal("requested", ((JsonObject)o["record"]!)["state"]!.ToString());
    }

    // ---- the reads ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListReadsTheBoardOfThisRepositoryAndEnvironmentOnly()
    {
        var r = new Rig();
        r.Api.Issues[900] = (true, "Another repository's", ["factory worker"], "Functional Testing");   // (a card of another repository on the board)
        r.Api.Issues[6] = (true, "Not the factory's", ["bug"], "Functional Testing");
        var (code, o) = await r.Call("qc-list", new { });
        Assert.Equal(0, code);
        var available = Assert.Single((JsonArray)o["available"]!)!;
        Assert.Equal((5, 9), (available["item"]!.GetValue<int>(), available["pr"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AnItemOfAnotherEnvironmentIsOutOfScope()
    {
        var r = new Rig(n => n == "FACTORY_ENV_SCOPE" ? "1" : n == "FACTORY_ENVIRONMENT" ? "prod" : null);
        var (code, o) = await r.Call("qc-request", new { item = 5, pr = 9 });
        Assert.Equal((10, "out-of-scope"), (code, o["code"]!.ToString()));
        Assert.Contains("'env:prod'", o["reason"]!.ToString());
        r.Api.Issues[5] = (true, "Item 5", ["factory worker", "env:prod"], "Functional Testing");
        var (c2, o2) = await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
        Assert.Equal(0, c2);
    }

    [Fact]
    public async Task AnItemOrPullRequestThatDoesNotExistIsRefusedLikeTheScriptRefuses()
    {
        var r = new Rig();
        var (c0, o0) = await r.Call("qc-view", new { item = 77, pr = 9 });
        Assert.Equal((12, "item-not-found", "issue #77 not found"), (c0, o0["code"]!.ToString(), o0["reason"]!.ToString()));
        var (c1, o1) = await r.Call("qc-view", new { item = 5, pr = 70 });
        Assert.Equal((12, "no-such-pr"), (c1, o1["code"]!.ToString()));
        Assert.Contains(r.Api.Requests, q => q.Method == "GET" && q.Path == "/repos/o/r/pulls/70");
    }

    [Fact]
    public async Task AListOf100CommentsOrFilesIsReadToTheEnd()
    {
        var r = new Rig();
        for (var i = 0; i < 230; i++) r.Api.Add(5, "someone", "comment " + i);
        r.Api.Files[9] = Enumerable.Range(0, 205).Select(i => i == 204 ? "src/last.cs" : $"docs/{i}.md").ToArray();
        // 205 files, one of them code: not documentation only; every comment page was read to find the state comment
        var (code, o) = await r.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal((0, true), (code, o["eligible"]!.GetValue<bool>()));
        Assert.Contains(r.Api.Requests, q => q.Path.Contains("/pulls/9/files") && q.Path.Contains("page=3"));
        Assert.Contains(r.Api.Requests, q => q.Path.Contains("/issues/5/comments") && q.Path.Contains("page=3"));
    }

    // ---- how a refusal of GitHub becomes the codes of Invoke-Gh --------------------------------------------------------------------------------------------

    static HttpResponseMessage Status(HttpStatusCode s, string body = "{\"message\":\"x\"}", params (string, string)[] headers)
    {
        var m = new HttpResponseMessage(s) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) m.Headers.TryAddWithoutValidation(k, v);
        return m;
    }

    [Theory]
    [InlineData(404, "{\"message\":\"Not Found\"}", null, 12, "github-not-found")]
    [InlineData(403, "{\"message\":\"API rate limit exceeded\"}", null, 20, "github-rate-limited")]
    [InlineData(429, "{\"message\":\"slow down\"}", null, 20, "github-rate-limited")]
    [InlineData(403, "{\"message\":\"Resource not accessible by integration\"}", null, 30, "github-forbidden")]
    [InlineData(403, "{\"message\":\"forbidden\"}", "remaining0", 20, "github-rate-limited")]
    [InlineData(500, "{\"message\":\"boom\"}", null, 20, "github-error")]
    [InlineData(422, "{\"message\":\"invalid\"}", null, 20, "github-error")]
    public async Task AnAnswerOfGitHubIsReadAsInvokeGhReadsIt(int status, string body, string? flavor, int exit, string code)
    {
        var r = new Rig();
        r.Api.Intercept = req => req.RequestUri!.AbsolutePath.StartsWith("/repos/o/r/pulls/9") && !req.RequestUri.AbsolutePath.EndsWith("/files")
            ? (flavor == "remaining0" ? Status((HttpStatusCode)status, body, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90).ToString())) : Status((HttpStatusCode)status, body))
            : null;
        var (c, o) = await r.Call("qc-view", new { item = 5, pr = 9 });
        if (status == 404) { Assert.Equal((12, "no-such-pr"), (c, o["code"]!.ToString())); return; }    // (Get-QcPr: a pull request that is not there)
        Assert.Equal((exit, code), (c, o["code"]!.ToString()));
        if (exit == 20 && code == "github-rate-limited") Assert.True(o["retryAfterSeconds"]!.GetValue<int>() >= 1);
        if (code == "github-forbidden") Assert.Contains("(403)", o["reason"]!.ToString());
    }

    [Fact]
    public async Task ARemainingZeroRateLimitHasTheSecondsUntilTheReset()
    {
        var r = new Rig();
        r.Api.Intercept = req => req.RequestUri!.AbsolutePath == "/repos/o/r/pulls/9" ? Status(HttpStatusCode.Forbidden, "{\"message\":\"x\"}", ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90).ToString())) : null;
        var (c, o) = await r.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal((20, "github-rate-limited"), (c, o["code"]!.ToString()));
        Assert.InRange(o["retryAfterSeconds"]!.GetValue<int>(), 80, 91);
    }

    [Fact]
    public async Task ARetryAfterHeaderIsTheWait()
    {
        var r = new Rig();
        r.Api.Intercept = req => req.RequestUri!.AbsolutePath == "/repos/o/r/pulls/9" ? Status(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "17")) : null;
        var (_, o) = await r.Call("qc-view", new { item = 5, pr = 9 });
        Assert.Equal(17, o["retryAfterSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheGatewaysLoginIsAskedOnceAndRemembered()
    {
        var r = new Rig();
        await r.Call("qc-link", new { item = 5, pr = 9 });
        await r.Call("qc-link", new { item = 5, pr = 9 });
        Assert.Equal(1, r.Api.Requests.Count(q => q.Path == "/user"));
    }

    [Fact]
    public async Task ADraftIsMarkedReadyThroughGraphQl_AndTheOfferFollows()
    {
        var r = new Rig();
        r.Api.Pulls[9]["draft"] = true;
        var state = r.State(5); state["copilot_task"] = new JsonObject { ["branch"] = "copilot/x" };
        var comment = r.Api.Of(5).Single(c => c.Body.StartsWith("<!-- factory-state:"));
        r.Api.Of(5)[r.Api.Of(5).IndexOf(comment)] = comment with { Body = ItemStateComment.Body("factory-worker", 5, state) };
        var (code, o) = await r.Call("qc-sync", new { });
        Assert.Equal(0, code);
        Assert.False(r.Api.Pulls[9]["draft"]!.GetValue<bool>());
        Assert.Equal("qc-offer", ((JsonArray)o["actions"]!)[0]!["action"]!.ToString());
        Assert.Contains(r.Api.Requests, q => q.Method == "POST" && q.Path == "/graphql" && q.Body.Contains("markPullRequestReadyForReview") && q.Body.Contains("PR_9"));
    }

    // W9.10g (CI run 41142195332, v2 qc-worker shards): the plant's "offered" journal line is written by the journal ledger while the gateway assigns the QC record. Both save the state comment; the store's save reads the
    // revision and then writes, so two savers that both pass the read overwrite one another. The ledger saves under the item's lock; the QC save did not, and the ledger's write (the record as it was read: requested) landed 3 ms
    // after the assignment's and undid it: the worker's accept answered 11 and every later verb of the environment 40 'claim-held'. Now the QC save takes the same lock, so the later saver finds the revision moved and reads again.
    [Fact]
    public async Task ASaveOfTheJournalThatRacesTheAssignmentOfTheRecord_DoesNotUndoTheAssignment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qc-save-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new Rig(n => n == "FACTORY_STATE_DIR" ? dir : null);
            await r.Call("qc-request", new { item = 5, pr = 9, @operator = true, confirm = true });
            var commentId = r.Api.Of(5).Single(c => c.Body.StartsWith("<!-- factory-state:")).Id;
            var clients = new GitHubClientFactory(r.Http, new ProviderCredentialSource(r.Tokens), new Fworker.Integrations.GitHub.GitHubBudget());
            var store = new GitHubJournalStore(await clients.For(new RepoId("o", "r")), new JournalIdentity("factory-worker", _ => Task.FromResult(Login), []), () => r.Now);
            var locks = new FileItemLock(dir);

            // the journal handler has read the state (record: requested) and is about to write it back; its write waits until the assignment's write has landed, or 1.5 seconds
            var holder = await store.Read(5, default);
            holder.State["journal_handler"] = "was here";
            var armed = false; var atWrite = new ManualResetEventSlim(); var assigned = new ManualResetEventSlim();
            r.Api.Intercept = req =>
            {
                if (req.Method != HttpMethod.Patch || req.RequestUri!.AbsolutePath != $"/repos/o/r/issues/comments/{commentId}") return null;
                if (armed) { armed = false; atWrite.Set(); assigned.Wait(TimeSpan.FromMilliseconds(1500)); return null; }
                assigned.Set();
                return null;
            };
            armed = true;
            var journal = Task.Run(() => locks.Use(5, async () => { await store.Save(5, holder, default); return 0; }, default));
            Assert.True(atWrite.Wait(TimeSpan.FromSeconds(10)));
            var (code, _) = await r.Call("qc-transition", new { item = 5, pr = 9, to = "assigned", worker = "qw1", assignment = "a-1", mode = "first", sha = Sha1 });
            await journal;

            Assert.Equal(0, code);
            var state = r.State(5);
            Assert.Equal(("assigned", "a-1"), (state["qc"]!["9"]!["state"]!.ToString(), state["qc"]!["9"]!["assignment"]!.ToString()));
            Assert.Equal("was here", state["journal_handler"]!.ToString());
            Assert.True((await r.Qc.Accept(5, "a-1", "qw1"))["accepted"]!.GetValue<bool>());   // (the worker's accept: 11 stale-assignment when the assignment was undone)
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ASaveOfTheRecordWaitsForTheItemsLock_AndAnItemThatStaysBusyIsTheRefusal20ItemBusy()
    {
        var r = new Rig();
        var clients = new GitHubClientFactory(r.Http, new ProviderCredentialSource(r.Tokens), new Fworker.Integrations.GitHub.GitHubBudget());
        var store = new GitHubJournalStore(await clients.For(new RepoId("o", "r")), new JournalIdentity("factory-worker", _ => Task.FromResult(Login), []), () => r.Now);
        var held = new List<int>();
        var adapter = new QcItemsAdapter(new QcGitHubClient(r.Http, r.Tokens, r.Cfg, r.Env), store, new JournalLedger(store), new BusyLock(held));
        var st = await adapter.ReadState(5, default);
        var e = await Assert.ThrowsAsync<QcReject>(() => adapter.SaveState(5, st, default));
        Assert.Equal((20, "item-busy"), (e.Exit, e.Code));
        Assert.Equal([5], held);
        Assert.DoesNotContain(r.Api.Requests, q => q.Method == "PATCH");   // nothing was written
    }

    sealed class BusyLock(List<int> asked) : IItemLock
    {
        public Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct) { asked.Add(item); throw new ItemBusyException(item); }
    }
}
