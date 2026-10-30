using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Incident;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DeliveredToMessage = Fworker.Contracts.Messaging.DeliveredTo;

namespace Gateway.Tests;

/// <summary>A fake GitHub for the incident saga's issue port: issues with labels and a body, comments, closing, and the two GraphQL calls that put an issue on the board.</summary>
sealed class FakeIncidentApi : IDisposable
{
    readonly HttpListener listener = new();
    readonly string repoPath;
    public readonly string Url;
    public readonly List<JsonObject> Issues = [];
    public readonly List<(int Issue, string Body)> Comments = [];
    public readonly List<string> Requests = [];
    public readonly List<string> Graphql = [];
    /// <summary>The number of requests that are answered 502 before the API works again.</summary>
    public volatile int Failures;
    int next = 900;

    public FakeIncidentApi(string repoPath)
    {
        this.repoPath = repoPath;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/"); listener.Start();
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    async Task Serve(HttpListenerContext ctx)
    {
        string body;
        using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        var (code, json) = Answer(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, body);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    (int, string) Answer(string method, string path, string query, string body)
    {
        lock (Requests)
        {
            Requests.Add($"{method} {path}");
            if (Failures > 0) { Failures--; return (502, """{"message":"Bad Gateway"}"""); }
            var repo = $"/repos/{repoPath}/";
            if (method == "POST" && path == "/graphql")
            {
                Graphql.Add(body);
                return body.Contains("addProjectV2ItemById", StringComparison.Ordinal)
                    ? (200, """{"data":{"addProjectV2ItemById":{"item":{"id":"PVTI_1"}}}}""")
                    : (200, """{"data":{"repository":{"issue":{"id":"I_1"}},"organization":{"projectV2":{"id":"P_1"}}}}""");
            }
            if (!path.StartsWith(repo, StringComparison.Ordinal)) return (404, """{"message":"Not Found"}""");
            var rest = path[repo.Length..];
            if (method == "GET" && rest == "issues")
            {
                var label = Regex.Match(query, @"labels=([^&]+)") is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : "";
                var found = Issues.Where(i => label.Length == 0 || i["labels"]!.AsArray().Any(l => l!.ToString() == label)).OrderByDescending(i => (int)i["number"]!).Select(i => i.DeepClone()).ToArray();
                return (200, new JsonArray(found).ToJsonString());
            }
            if (method == "POST" && rest == "issues")
            {
                var req = JsonNode.Parse(body)!;
                var issue = new JsonObject { ["number"] = next++, ["title"] = req["title"]!.ToString(), ["body"] = req["body"]!.ToString(), ["labels"] = req["labels"]!.DeepClone(), ["state"] = "open" };
                Issues.Add(issue);
                return (201, issue.ToJsonString());
            }
            var comments = Regex.Match(rest, @"^issues/(\d+)/comments$");
            if (comments.Success && method == "GET") return (200, new JsonArray(Comments.Where(c => c.Issue == int.Parse(comments.Groups[1].Value)).Select(c => (JsonNode)new JsonObject { ["body"] = c.Body }).ToArray()).ToJsonString());
            if (comments.Success && method == "POST") { Comments.Add((int.Parse(comments.Groups[1].Value), JsonNode.Parse(body)!["body"]!.ToString())); return (201, """{"id":1}"""); }
            var issueOnly = Regex.Match(rest, @"^issues/(\d+)$");
            if (issueOnly.Success && method == "PATCH")
            {
                var n = int.Parse(issueOnly.Groups[1].Value);
                Issues.First(i => (int)i["number"]! == n)["state"] = JsonNode.Parse(body)!["state"]!.ToString();
                return (200, """{"state":"closed"}""");
            }
            return (404, """{"message":"Not Found"}""");
        }
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}

/// <summary>W8.1: the incident issue and the hotfix item on GitHub (Integrations' IncidentGitHub behind the gateway's adapter): idempotent by marker, a fault is retried, the hotfix is put on the board.</summary>
public sealed class IncidentIssuesTests
{
    static readonly RepoId Home = new("acme", "work");
    static readonly string Token = "ghp_" + new string('A', 36);   // composed at run time, so the secret scan has nothing to match

    static (GitHubIncidentIssues Issues, FakeIncidentApi Api, IncidentGitHub Github) Make(bool board = true)
    {
        var api = new FakeIncidentApi(Home.ToString());
        var http = new HttpClient { BaseAddress = new Uri(api.Url + "/") };
        var credentials = new StaticCredentialSource([], new GitHubCredential("test", Token));
        var github = new IncidentGitHub(new GitHubClientFactory(http, credentials, new Fworker.Integrations.GitHub.GitHubBudget()), http, credentials, () => board ? new BoardRef("acme", 5) : null);
        return (new GitHubIncidentIssues(github, Home, ["hotfix", "priority:top", "Auto Merge"]), api, github);
    }

    [Fact]
    public async Task TheIncidentIssue_IsOpenedOnce_WithItsMarker_AndFoundAgain()
    {
        var (issues, api, _) = Make();
        using var _a = api;
        var first = await issues.Open("orders/tdd", 1, "Incident: orders in tdd is failing (alert)", "details", "incident", default);
        var again = await issues.Open("orders/tdd", 1, "Incident: orders in tdd is failing (alert)", "details", "incident", default);
        Assert.Equal((900, 900), (first, again));
        var issue = Assert.Single(api.Issues);
        Assert.Equal(["incident"], issue["labels"]!.AsArray().Select(l => l!.ToString()));
        Assert.Contains("<!-- fworker-incident:orders/tdd#1 -->", issue["body"]!.ToString());
        // the next incident of the pair is another issue
        Assert.Equal(901, await issues.Open("orders/tdd", 2, "Incident again", "details", "incident", default));
    }

    [Fact]
    public async Task ACommentWithAKey_IsWrittenOnce_AndTheCloseCommentsThenClosesTheIssueByItsNumberOrItsMarker()
    {
        var (issues, api, _) = Make();
        using var _a = api;
        var number = await issues.Open("orders/tdd", 1, "Incident", "details", "incident", default);
        await issues.Comment("orders/tdd", 1, number, "escalated|1|4", "@owner decide", default);
        await issues.Comment("orders/tdd", 1, number, "escalated|1|4", "@owner decide", default);
        await issues.Comment("orders/tdd", 1, 0, "escalated|1|9", "again, found by its marker", default);   // (the number is not known: the marker finds the issue)
        Assert.Equal(2, api.Comments.Count);
        Assert.All(api.Comments, c => Assert.Equal(number, c.Issue));
        Assert.Contains("<!-- fworker-comment:escalated|1|4 -->", api.Comments[0].Body);

        await issues.Close("orders/tdd", 1, 0, "resolved after 11 minutes", default);
        Assert.Equal("closed", api.Issues[0]["state"]!.ToString());
        Assert.Contains(api.Comments, c => c.Body.StartsWith("resolved after 11 minutes", StringComparison.Ordinal));
        // an incident whose issue was never opened has nothing to comment on or close
        await issues.Close("orders/tdd", 7, 0, "x", default);
        await issues.Comment("orders/tdd", 7, 0, "k", "x", default);
        Assert.Single(api.Issues);
    }

    [Fact]
    public async Task TheHotfixItem_IsCreatedOnce_LabelledToRankAndMerge_AndPutOnTheBoard()
    {
        var (issues, api, _) = Make();
        using var _a = api;
        var item = await issues.CreateHotfix("orders/tdd", 1, "Hotfix: orders", "body", default);
        Assert.Equal(new ItemId(Home, 900), item);
        Assert.Equal(item, await issues.CreateHotfix("orders/tdd", 1, "Hotfix: orders", "body", default));
        var issue = Assert.Single(api.Issues);
        Assert.Equal(["hotfix", "priority:top", "Auto Merge"], issue["labels"]!.AsArray().Select(l => l!.ToString()));
        Assert.Contains("<!-- fworker-hotfix:orders/tdd#1 -->", issue["body"]!.ToString());
        Assert.Equal(2, api.Graphql.Count(g => g.Contains("addProjectV2ItemById", StringComparison.Ordinal)));   // (adding an item that is on the board already does nothing there)
        // a gateway that knows no board creates the issue and leaves the card to the board's own rules
        var (noBoard, api2, _) = Make(board: false);
        using var _b = api2;
        await noBoard.CreateHotfix("orders/tdd", 1, "Hotfix", "body", default);
        Assert.Empty(api2.Graphql);
    }

    [Fact]
    public async Task AFaultOfGitHub_IsAnIssueFault_ThatARetryGetsPast()
    {
        var (issues, api, _) = Make();
        using var _a = api;
        api.Failures = 1;
        var fault = await Assert.ThrowsAsync<IncidentIssueFault>(() => issues.Open("orders/tdd", 1, "Incident", "details", "incident", default));
        Assert.Contains("502", fault.Message);
        Assert.Empty(api.Issues);
        Assert.Equal(900, await issues.Open("orders/tdd", 1, "Incident", "details", "incident", default));
    }
}

/// <summary>W8.1: the health reads of the confirmation rule.</summary>
public sealed class IncidentHealthJobTests
{
    sealed class Handler(Func<string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            var (code, body) = answer(request.RequestUri.ToString());
            if (code == 0) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    static (IncidentHealthJob Job, List<HealthObserved> Reads, Handler Http, bool[] Control) Make(Func<string, (HttpStatusCode, string)> answer, bool leader = true, bool hostUp = true)
    {
        var fixture = new FakeTrainPolicies();
        var reads = new List<HealthObserved>(); var http = new Handler(answer); var control = new[] { leader, hostUp };
        var settings = new IncidentSettings(new Dictionary<string, IncidentEnvironment> { ["tdd"] = new("tdd", RollbackMode.Automatic, false), ["prod"] = new("prod", RollbackMode.Off, false) });
        var job = new IncidentHealthJob(settings, fixture.Policies, new HttpClient(http), m => { if (!control[1]) return Task.FromResult(false); reads.Add(m); return Task.FromResult(true); }, () => control[0], () => DateTimeOffset.Parse("2026-10-29T10:00:00Z"));
        return (job, reads, http, control);
    }

    [Fact]
    public async Task EachWatchedEnvironmentWithAHealthUrl_IsReadOnce_AndTheOverallStatusIsTheRead()
    {
        var (job, reads, http, _) = Make(url => url.StartsWith("https://tdd.example", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"overallStatus":"Healthy"}""") : (HttpStatusCode.ServiceUnavailable, """{"overallStatus":"Unhealthy"}"""));
        Assert.Equal("2 read", await job.Run(default));
        Assert.Equal(["https://prod.example/_healthcheck/detailed", "https://tdd.example/_healthcheck/detailed"], http.Urls.Order());   // (uat is not watched)
        Assert.Equal([("orders/prod", "Unhealthy"), ("orders/tdd", "Healthy")], reads.Select(r => (r.Key, r.Status)).Order());
        Assert.All(reads, r => Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:00:00Z"), r.At));
    }

    [Fact]
    public async Task AnAppThatDoesNotAnswer_IsUnreachable_AndAnAnswerWithoutAStatusIsTheCodeOfTheAnswer()
    {
        var (job, reads, _, _) = Make(url => url.Contains("tdd", StringComparison.Ordinal) ? (0, "") : (HttpStatusCode.InternalServerError, "not json"));
        await job.Run(default);
        Assert.Equal([("orders/prod", "status 500"), ("orders/tdd", "unreachable")], reads.Select(r => (r.Key, r.Status)).Order());
    }

    [Fact]
    public async Task OnlyTheLeaderReads_AndAHostThatIsNotUpTakesNoRead()
    {
        var follower = Make(_ => (HttpStatusCode.OK, """{"overallStatus":"Healthy"}"""), leader: false);
        Assert.Equal("not-leader", await follower.Job.Run(default));
        Assert.Empty(follower.Http.Urls);
        var down = Make(_ => (HttpStatusCode.OK, """{"overallStatus":"Healthy"}"""), hostUp: false);
        Assert.Equal("none", await down.Job.Run(default));
        Assert.Empty(down.Reads);
    }
}

/// <summary>The policies of one deployable "orders" with health URLs in the three environments, as the delivery train's fixtures build them.</summary>
sealed class FakeTrainPolicies
{
    public readonly IDeliveryPolicies Policies;

    public FakeTrainPolicies()
    {
        var repo = new RepoId("acme", "orders");
        var settings = new DeliveryPolicySettings("orders-project", Promoter.Gateway, ["uat", "prod"],
            new Dictionary<string, string> { ["tdd"] = "Tdd", ["uat"] = "Uat", ["prod"] = "Prod" },
            new Dictionary<string, string> { ["tdd"] = "https://tdd.example", ["uat"] = "https://uat.example/", ["prod"] = "https://prod.example" });
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", repo, null, [new(repo, [new DeployableEntry("orders", repo, null, null, null, null, false)])], []);
        Policies = new CatalogDeliveryPolicies(() => snapshot, n => n == "orders" ? settings : null);
    }
}

/// <summary>What the gateway reads of config/instance.json for the incident saga (W8.0), and what the deployed configuration leaves alone.</summary>
public sealed class IncidentSettingsReaderTests
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    [Fact]
    public void TheSourceDefaults_WatchTddOnly_AndRollBackThereAutomatically()
    {
        var s = IncidentSettings.From(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json"))));
        Assert.Equal(["tdd"], s.Environments.Keys);
        Assert.Equal(RollbackMode.Automatic, s.RollbackOf("tdd"));
        Assert.False(s.Watches("uat")); Assert.False(s.Watches("prod"));
    }

    [Fact]
    public void TheDeployedOverrides_LeaveTheIncidentPolicyToTheDefaults()
    {
        var overrides = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")))!.AsObject();
        Assert.False(overrides.ContainsKey("incidents"));
    }
}

/// <summary>
/// The incident saga exists only where the engine acts (Engine.V2Acts): IncidentWiring.Attach is called once, from the V2Acts branch of the Wolverine registration, and no other gateway file refers to the saga's handlers or
/// topology. The engines that do not act (v1, the shadow run, the kill switch) register no part of it.
/// </summary>
public sealed class IncidentInertTests
{
    static readonly string Root = new Func<string>(() =>
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "gateway", "Program.cs"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    })();

    static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "gateway"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(Root, f).Replace('\\', '/'), ArchitectureTests.Code(File.ReadAllText(f))));

    [Fact]
    public void OnlyTheIncidentWiring_AttachesTheSaga_AndTheGatewayCallsThatOnlyWhereTheEngineActs()
    {
        var mentions = Sources().Where(s => Regex.IsMatch(s.Text, @"\bIncident(Topology|Handlers|Routing|IssueHandlers|HistoryHandler)\b")).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["gateway/Ports/IncidentPort.cs"], mentions);
        var caller = Assert.Single(Sources().Where(s => s.Text.Contains("IncidentWiring.Attach(", StringComparison.Ordinal)));
        Assert.Equal("gateway/Composition/GatewayWolverineExtensions.cs", caller.Path);
        var attach = caller.Text.IndexOf("IncidentWiring.Attach(", StringComparison.Ordinal);
        var acts = caller.Text.LastIndexOf("if (c.Engine.V2Acts)", attach, StringComparison.Ordinal);
        Assert.True(acts >= 0, "IncidentWiring.Attach is not under `if (c.Engine.V2Acts)`");
        Assert.DoesNotContain("\n        }\n", caller.Text[acts..attach].Replace("\r", ""));
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_ThereIsNoIncidentSaga()
    {
        if (!StubPwsh.Supported) return;
        foreach (var kind in new[] { "v1", "shadow", "kill-switch" })
        {
            using var env = new GatewayEnvironment();
            if (kind != "v1") env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
            if (kind == "shadow") WolverineHostTestSupport.Shadow(env);
            if (kind == "kill-switch")
            {
                env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
                env.Set("FACTORY_V2_OFF", "1");
            }
            env.Apply();
            using var host = new GatewayHost();
            _ = host.Server;
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType == typeof(ShadowOctopusPort) && kind != "shadow");
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType.Namespace == "Fworker.Workflows.Incident");
        }
    }
}

/// <summary>
/// W8.1 in a gateway whose engine acts, over a real SQL Server and a fake GitHub on a local port: the generated handlers of the incident saga (its inputs on the durable sequential queue, the issue commands on theirs,
/// the router of the Octopus, work item and delivery facts, the history) run an incident end to end. A release deployed shortly before an alert is rolled back by the policy (the train's hold and rollback request find no
/// train in this host and are dropped; the answer is published as the train would), and a defect the triage cannot roll back is fixed forward through a hotfix item that is created, started on the hotfix plan, delivered and verified.
/// </summary>
public sealed class IncidentWiringTests
{
    static readonly string Token = "ghp_" + new string('A', 36);   // composed at run time, so the secret scan has nothing to match
    const string Key = "orders/tdd";

    static string With(string cs, string database) => new SqlConnectionStringBuilder(cs) { InitialCatalog = database }.ConnectionString;

    static async Task<List<string>> Rows(string cs, string sql)
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c); await using var r = await cmd.ExecuteReaderAsync();
        var l = new List<string>(); while (await r.ReadAsync()) l.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
        return l;
    }

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds)) { if (await cond()) return true; await Task.Delay(200); }
        return false;
    }

    /// <summary>The status of the pair's incident row; empty before the row exists.</summary>
    static async Task<string> Status(string cs) => (await Rows(cs, $"SELECT Status FROM factory.Incidents WHERE [Key] = '{Key}'")).SingleOrDefault() ?? "";

    static async Task<IncidentState> Row(string cs) => IncidentState.FromJson((await Rows(cs, $"SELECT StateJson FROM factory.Incidents WHERE [Key] = '{Key}'"))[0]);

    static async Task<string> Diagnosis(string cs) =>
        " | incident: " + string.Join(";", await Rows(cs, "SELECT StateJson FROM factory.Incidents"))
        + " | history: " + string.Join(",", await Rows(cs, "SELECT JSON_VALUE(Detail, '$.Action') + ':' + ItemKey FROM factory.ItemHistory ORDER BY CAST(JSON_VALUE(Detail, '$.Sequence') AS int)"))
        + " | envelopes: " + string.Join(",", await Rows(cs, "SELECT message_type + ' ' + status FROM wolverine.wolverine_incoming_envelopes"))
        + " | dead letters: " + string.Join(";", await Rows(cs, "SELECT exception_type + ' ' + LEFT(exception_message, 300) FROM wolverine.wolverine_dead_letters"));

    /// <summary>A gateway whose engine acts on its own database, with the fake GitHub; the body runs and the database is dropped.</summary>
    static async Task Run(Func<string, RepoId, FakeIncidentApi, WolverineHostService, Task> body)
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w81_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            env.Set("FACTORY_DB_CONNECTION", cs); env.Set("FACTORY_SWEEP_SECONDS", "3600");
            env.Apply();
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            using var api = new FakeIncidentApi(home.ToString());
            env.Set("GITHUB_API_URL", api.Url); env.Set("GH_TOKEN", Token); env.Apply();
            await using var gateway = new GatewayHost { Extra = services => services.AddSingleton(WolverineHostTestSupport.Fast()) };
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");
            await body(cs, home, api, wolverine);
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    [Fact]
    public async Task ARecentReleaseIsRolledBack_ThenVerified_AndTheIncidentIsResolved()
    {
        await Run(async (cs, home, api, wolverine) =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert.True(await wolverine.PublishAsync(new DeploymentObserved(Key, "D-102", "1.0.2", "Started", now.AddMinutes(-12))));
            Assert.True(await wolverine.PublishAsync(new DeploymentObserved(Key, "D-102", "1.0.2", "Succeeded", now.AddMinutes(-10))));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.Incidents"))[0] == "1", 30));
            Assert.True(await wolverine.PublishAsync(new AlertReceived(Key, "alert-1", "http-5xx", "Sev1", "errors above the threshold", now)));
            Assert.True(await Until(async () => await Status(cs) == "rolling-back" && (await Row(cs)).IssueNumber == 900, 40), "the incident did not roll back: " + await Diagnosis(cs));

            // the issue the owner is notified by, with the marker the repeated command would find again
            var issue = Assert.Single(api.Issues);
            Assert.Equal(["incident"], issue["labels"]!.AsArray().Select(l => l!.ToString()));
            Assert.Contains("orders in tdd is failing (alert)", issue["title"]!.ToString());
            // the timeline is in the item history, one row per decision
            Assert.Equal(["detected", "rolling-back"], (await Rows(cs, "SELECT JSON_VALUE(Detail, '$.Action') FROM factory.ItemHistory WHERE ItemKey = 'incident:orders/tdd' ORDER BY CAST(JSON_VALUE(Detail, '$.Sequence') AS int)")));

            // the train answers as it would (W8.3): the rollback is under way, then done
            Assert.True(await wolverine.PublishAsync(new RollbackAnswered(Key, "orders/tdd#1", 1, "started", "", "1.0.1", "", DateTimeOffset.UtcNow)));
            Assert.True(await Until(async () => (await Row(cs)).RollbackTo == "1.0.1", 30), "the rollback was not recorded: " + await Diagnosis(cs));
            Assert.True(await wolverine.PublishAsync(new DeploymentObserved(Key, "D-101", "1.0.1", "Started", DateTimeOffset.UtcNow.AddMinutes(-1))));
            Assert.True(await wolverine.PublishAsync(new DeploymentObserved(Key, "D-101", "1.0.1", "Succeeded", DateTimeOffset.UtcNow)));
            Assert.True(await Until(async () => await Status(cs) == "verifying", 30), "the incident did not verify: " + await Diagnosis(cs));

            // the app is healthy for the verification window: reads whose times are ten minutes apart
            var t = DateTimeOffset.UtcNow;
            Assert.True(await wolverine.PublishAsync(new HealthObserved(Key, "Healthy", "", t)));
            Assert.True(await wolverine.PublishAsync(new HealthObserved(Key, "Healthy", "", t.AddMinutes(11))));
            Assert.True(await Until(async () => await Status(cs) == "quiet", 40), "the incident was not resolved: " + await Diagnosis(cs));
            Assert.True(await Until(() => Task.FromResult(api.Issues[0]["state"]!.ToString() == "closed"), 30), "the issue was not closed: " + await Diagnosis(cs));
            Assert.Contains(api.Comments, c => c.Body.Contains("Incident orders/tdd#1 is resolved", StringComparison.Ordinal));
            Assert.Equal(["detected", "rolling-back", "rollback-started", "verifying", "resolved"], await Rows(cs, "SELECT JSON_VALUE(Detail, '$.Action') FROM factory.ItemHistory WHERE ItemKey = 'incident:orders/tdd' ORDER BY CAST(JSON_VALUE(Detail, '$.Sequence') AS int)"));
        });
    }

    [Fact]
    public async Task ADefectIsFixedForward_ThroughAHotfixItemOnTheHotfixPlan_UntilItIsDeliveredAndVerified()
    {
        await Run(async (cs, home, api, wolverine) =>
        {
            Assert.True(await wolverine.PublishAsync(new AlertReceived(Key, "alert-1", "http-5xx", "Sev1", "errors above the threshold", DateTimeOffset.UtcNow)));
            Assert.True(await Until(async () => await Status(cs) == "triaging", 30), "the incident did not open: " + await Diagnosis(cs));
            Assert.True(await wolverine.PublishAsync(new TriageReported(Key, 1, "analyst-1", "fix-forward", "a defect in the last release")));
            var hotfix = new ItemId(home, 901);
            Assert.True(await Until(async () => (await Row(cs)).HotfixItem == hotfix.ToString(), 40), "the hotfix item was not created: " + await Diagnosis(cs));

            // the hotfix item: an issue labelled to rank and merge, a saga on the hotfix plan, and the incident knows it
            var issue = api.Issues.Single(i => i["labels"]!.AsArray().Any(l => l!.ToString() == "hotfix"));
            Assert.Equal(["hotfix", "priority:top", "Auto Merge"], issue["labels"]!.AsArray().Select(l => l!.ToString()));
            Assert.True(await Until(async () => (await Rows(cs, $"SELECT [Plan] + '|' + [Step] FROM factory.WorkItems WHERE ItemKey = '{hotfix}'")).SingleOrDefault() == "hotfix|Develop", 40), "the hotfix saga did not start: " + await Diagnosis(cs));
            Assert.Equal(hotfix.ToString(), (await Rows(cs, "SELECT HotfixItem FROM factory.Incidents"))[0]);

            // its delivery to tdd (the delivery train's fact) is what the incident waits for, then the health of the app
            Assert.True(await wolverine.PublishAsync(new DeliveredToMessage(hotfix, "uat", "1.0.3")));   // (another environment: no news for this incident)
            Assert.True(await wolverine.PublishAsync(new DeliveredToMessage(hotfix, "tdd", "1.0.3")));
            Assert.True(await Until(async () => await Status(cs) == "verifying", 30), "the incident did not verify: " + await Diagnosis(cs));
            var t = DateTimeOffset.UtcNow;
            Assert.True(await wolverine.PublishAsync(new HealthObserved(Key, "Healthy", "", t)));
            Assert.True(await wolverine.PublishAsync(new HealthObserved(Key, "Healthy", "", t.AddMinutes(11))));
            Assert.True(await Until(async () => await Status(cs) == "quiet", 40), "the incident was not resolved: " + await Diagnosis(cs));
            var actions = await Rows(cs, "SELECT JSON_VALUE(Detail, '$.Action') FROM factory.ItemHistory WHERE ItemKey = 'incident:orders/tdd' ORDER BY CAST(JSON_VALUE(Detail, '$.Sequence') AS int)");
            Assert.Equal(["detected", "triaging", "fixing", "hotfix-created", "verifying", "resolved"], actions);
            Assert.Equal("", (await Rows(cs, "SELECT ISNULL(HotfixItem, '') FROM factory.Incidents"))[0]);
        });
    }

    [Fact]
    public async Task TheFactsThatNameNoIncident_AreRoutedAndEndQuietly()
    {
        await Run(async (cs, home, api, wolverine) =>
        {
            // an Octopus deployment event of a gateway whose catalog has no deployable, the decision of an ordinary item's saga, a delivery nobody waits for, a train's answer for an incident that never was
            Assert.True(await wolverine.PublishAsync(new OctopusDeploymentChanged("D1", "T1", "Releases-1", "1.0.2", "Tdd", OctopusDeploymentPhase.Failed, null, DateTimeOffset.UtcNow)));
            Assert.True(await wolverine.PublishAsync(new DeliveredToMessage(new ItemId(home, 5), "tdd", "1.0.2")));
            Assert.True(await wolverine.PublishAsync(new WorkItemDecided(new ItemId(home, 5), 1, 3, WorkItemActions.Parked, "hotfix", "Merge", "1.2", [], DateTimeOffset.UtcNow)));
            Assert.True(await wolverine.PublishAsync(new RollbackAnswered(Key, "orders/tdd#4", 4, "done", "", "1.0.1", "", DateTimeOffset.UtcNow)));
            Assert.True(await wolverine.PublishAsync(new AlertReceived("orders/prod", "alert-9", "rule", "Sev1", "prod is not watched", DateTimeOffset.UtcNow)));
            await Task.Delay(3000);
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM factory.Incidents WHERE Status <> 'quiet'"))[0]);
            Assert.Empty(api.Issues);
            // the host still takes an alert after them
            Assert.True(await wolverine.PublishAsync(new AlertReceived(Key, "alert-1", "http-5xx", "Sev1", "errors", DateTimeOffset.UtcNow)));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.Incidents WHERE Status <> 'quiet'"))[0] == "1", 30), await Diagnosis(cs));
        });
    }
}
