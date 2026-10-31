using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery.Verbs;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.4 (gateway/Ports/DeliveryVerbHost.cs): the delivery verbs on v2 through the gateway's own HTTP side, against a GitHub that is a message handler. A call is what the script run was: the same answer shape
/// (verb, ok, exit), the item's lock, the lease checked before a write of the verbs v1 fences, the state comment saved through the revision check.
/// </summary>
public class DeliveryVerbHostTests
{
    sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body)> Calls = [];
        public JsonObject State = new() { ["schema"] = 1, ["item"] = 5 };
        public int Rev;
        public string Column = "Development";
        public readonly SemaphoreSlim Hold = new(0);
        public bool HoldStateRead;
        public int CommentsStatus = 200;

        string StateComment() => ItemStateComment.Body("factory-worker", 5, State);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.Method, path, body));
            JsonNode Json(object o) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!;
            HttpResponseMessage Ok(JsonNode n, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Post && path == "graphql")
            {
                var q = JsonNode.Parse(body)!["query"]!.ToString();
                if (q.Contains("projectV2(number")) return Ok(JsonNode.Parse("""{"data":{"organization":{"projectV2":{"id":"P1","fields":{"nodes":[{"id":"F1","name":"Status","options":[{"id":"o1","name":"Development"},{"id":"o2","name":"Done"}]}]}}}}}""")!);
                var item = JsonNode.Parse("""{"data":{"repository":{"issue":{"id":"I5","number":5,"state":"OPEN","title":"T","body":"b","url":"u","createdAt":"2026-10-20T00:00:00Z","labels":{"nodes":[]},"projectItems":{"nodes":[{"id":"C5","project":{"number":1},"fieldValueByName":{"name":"x"}}]}}}}}""")!;
                item["data"]!["repository"]!["issue"]!["projectItems"]!["nodes"]![0]!["fieldValueByName"]!["name"] = Column;
                return Ok(item);
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("repos/acme/orders/issues/5/comments"))
            {
                if (HoldStateRead) await Hold.WaitAsync(ct);
                if (CommentsStatus != 200) return Ok(new JsonObject { ["message"] = "Not Found" }, CommentsStatus);
                return Ok(Json(new[] { new { id = 77, user = new { login = "factory-bot" }, body = StateComment() } }));
            }
            if (request.Method == HttpMethod.Get && path.Contains("/check-runs")) return Ok(JsonNode.Parse("""{"check_runs":[{"name":"Build result","status":"completed","conclusion":"success","started_at":"2026-10-29T11:00:00Z"}]}""")!);
            if (request.Method == HttpMethod.Get && path == "repos/acme/orders/issues/comments/77") return Ok(Json(new { id = 77, body = StateComment() }));
            if (request.Method == HttpMethod.Patch && path == "repos/acme/orders/issues/comments/77")
            {
                State = ItemStateComment.Parse(JsonNode.Parse(body)!["body"]!.ToString())!; Rev = State["rev"]!.GetValue<int>();
                return Ok(Json(new { id = 77 }));
            }
            if (request.Method == HttpMethod.Post && path == "repos/acme/orders/issues/5/comments") return Ok(Json(new { id = 78 }), 201);
            return Ok(new JsonObject { ["message"] = "Not Found" }, 404);
        }
    }

    static readonly JsonObject Config = JsonNode.Parse("""
        { "name": "factory-worker", "owner": "acme", "repo": "orders", "project": 1, "defaultBranch": "master",
          "columns": { "development": "Development", "validation": "Functional Testing" }, "ciGates": ["Build result"],
          "timing": { "redChecksGate": 2, "masterGreenTimeoutMinutes": 30, "releaseCandidateTimeoutMinutes": 20, "parkHours": 24 },
          "octopus": { "url": "https://octopus.example", "space": "Spaces-1", "project": "orders-ui" } }
        """)!.AsObject();

    static DeliveryVerbHost Host(FakeGitHub gh, Func<bool>? mayAct = null, V2ActivityTap? tap = null, Func<string, string?>? env = null, bool token = true) =>
        new(Config, env ?? (n => n == "FACTORY_SERVER_LOGIN" ? "factory-bot" : null), mayAct ?? (() => true), _ => { },
            (verb, args, ct) => Task.FromResult((0, "{}")), tap, gh, token ? GitHubTokenProvider.FromEnvironment(n => n == "GH_TOKEN" ? "tok" : null) : null, delay: (_, _) => Task.CompletedTask);

    static JsonObject Answer(VerbAnswer a) => JsonNode.Parse(a.Output)!.AsObject();

    [Fact]
    public async Task PrivateBuild_AnswersAsTheScriptDid_AndSavesTheStateThroughTheRevisionCheck()
    {
        var gh = new FakeGitHub();
        var a = await Host(gh).Run("private-build", new JsonObject { ["item"] = 5, ["attest"] = new string('a', 40), ["holder"] = "w1" }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, a.Code);
        var o = Answer(a);
        Assert.Equal(("private-build", true, 0, 5, true, 2), (o["verb"]!.ToString(), o["ok"]!.GetValue<bool>(), o["exit"]!.GetValue<int>(), o["id"]!.GetValue<int>(), o["attested"]!.GetValue<bool>(), o["gate"]!.GetValue<int>()));
        Assert.Equal(new string('a', 40), gh.State["private_build"]!["sha"]!.ToString());
        Assert.Equal(1, gh.Rev);
        // the calls, in the order the script makes them: the item (GraphQL), the comments (Get-State), the comment's revision (Save-State), the save
        var order = gh.Calls.Select(c => c.Method.Method + " " + (c.Path == "graphql" ? "graphql" : c.Path.Split('?')[0])).ToArray();
        Assert.Equal(["POST graphql", "GET repos/acme/orders/issues/5/comments", "GET repos/acme/orders/issues/comments/77", "PATCH repos/acme/orders/issues/comments/77"], order);
        Assert.All(gh.Calls, c => Assert.DoesNotContain("tok", c.Body));   // (the token is in a header, never in a body)
    }

    [Fact]
    public async Task ARefusalOfTheRuleIsTheScriptsRefusal_ExitCodeAndAll()
    {
        var gh = new FakeGitHub();
        var a = await Host(gh).Run("private-build", new JsonObject { ["item"] = 5, ["attest"] = "abc" }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(2, a.Code);
        var o = Answer(a);
        Assert.Equal((false, "private-build", "usage", 2), (o["ok"]!.GetValue<bool>(), o["verb"]!.ToString(), o["code"]!.ToString(), o["exit"]!.GetValue<int>()));
        Assert.Equal("attest takes the full 40-character sha of the commit the private build passed on", o["reason"]!.ToString());
        Assert.Equal(0, gh.Rev);
    }

    [Theory]
    [InlineData(404, 12, "github-not-found")]
    [InlineData(403, 30, "github-forbidden")]
    [InlineData(422, 20, "github-error")]
    public async Task AStateCommentGitHubRefuses_IsRefusedWithInvokeGhsCodes(int status, int exit, string code)
    {
        var gh = new FakeGitHub { CommentsStatus = status };
        var a = await Host(gh).Run("copilot-owner", new JsonObject { ["item"] = 5, ["action"] = "get" }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(exit, a.Code);
        Assert.Equal((false, code), (Answer(a)["ok"]!.GetValue<bool>(), Answer(a)["code"]!.ToString()));
    }

    [Fact]
    public async Task WithoutATokenEveryVerbSaysSo_AsTheScriptDid()
    {
        var a = await Host(new FakeGitHub(), token: false).Run("settle", new JsonObject { ["item"] = 5 }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(30, a.Code);
        Assert.Equal(("secret-missing", "GH_TOKEN is not set on the gateway"), (Answer(a)["code"]!.ToString(), Answer(a)["reason"]!.ToString()));
    }

    sealed class FlakyCredentials(int failures) : IGitHubCredentialSource
    {
        int calls;
        public int Calls => calls;
        public ValueTask<GitHubCredential?> For(string owner, CancellationToken ct = default) =>
            Interlocked.Increment(ref calls) <= failures ? throw new GitHubTokenException("the installation token could not be minted (503)") : new((GitHubCredential?)new GitHubCredential("test", "tok"));
    }

    [Fact]
    public async Task ATransientFaultOnTheFirstCall_IsARetryableExit20_AndTheNextCallSucceeds()
    {
        // the client of the state comment (and the token mint behind it) is resolved inside Run: the construction of the host reaches nothing, so a fault is not kept by the route's Lazy
        var gh = new FakeGitHub();
        var credentials = new FlakyCredentials(failures: 1);
        var host = new DeliveryVerbHost(Config, n => n == "FACTORY_SERVER_LOGIN" ? "factory-bot" : null, () => true, _ => { }, (verb, args, ct) => Task.FromResult((0, "{}")), null, gh,
            GitHubTokenProvider.FromEnvironment(n => n == "GH_TOKEN" ? "tok" : null), (_, _) => Task.CompletedTask, credentials);
        Assert.Equal(0, credentials.Calls);   // (construction made no token call)
        var args = new JsonObject { ["item"] = 5, ["attest"] = new string('a', 40), ["holder"] = "w1" }.ToJsonString();

        var first = await host.Run("private-build", args, TimeSpan.FromSeconds(30));
        Assert.Equal(20, first.Code);
        Assert.Equal((false, 20), (Answer(first)["ok"]!.GetValue<bool>(), Answer(first)["exit"]!.GetValue<int>()));
        Assert.Equal(30, Answer(first)["retryAfterSeconds"]!.GetValue<int>());

        var second = await host.Run("private-build", args, TimeSpan.FromSeconds(30));
        Assert.Equal(0, second.Code);
        Assert.Equal(new string('a', 40), gh.State["private_build"]!["sha"]!.ToString());
    }

    [Fact]
    public async Task ACallersFieldsOfTheGateway_AreTakenOut_AndTheSessionFooterEndsTheComment()
    {
        var gh = new FakeGitHub();
        gh.State["copilot_task"] = new JsonObject { ["id"] = "t0" };
        var stdin = new JsonObject
        {
            ["item"] = 5, ["action"] = "set", ["task"] = new JsonObject { ["id"] = "t1" }, ["_lease"] = new JsonObject { ["epoch"] = 9 }, ["_cause"] = "x.1",
            ["_session"] = new JsonObject { ["name"] = "w1", ["url"] = "https://sessions.example/w1" },
        };
        var a = await Host(gh).Run("copilot-owner", stdin.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, a.Code);
        var comment = gh.Calls.Single(c => c.Method == HttpMethod.Post && c.Path.EndsWith("/comments")).Body;
        Assert.Contains("AI session: [w1](https://sessions.example/w1)", JsonNode.Parse(comment)!["body"]!.ToString());
        Assert.DoesNotContain("_session", Answer(a).ToJsonString());
    }

    [Fact]
    public async Task ASettleOfAGatewayThatLostItsLease_IsRefusedBeforeAnyWrite_AndAnUnfencedVerbStillWrites()
    {
        var gh = new FakeGitHub { Column = "Functional Testing" };
        gh.State["merge"] = new JsonObject { ["sha"] = new string('c', 40), ["awaiting"] = "master-green", ["merged_at"] = "2026-10-29T11:50:00.000Z" };
        var lost = await Host(gh, mayAct: () => false).Run("settle", new JsonObject { ["item"] = 5 }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(20, lost.Code);
        Assert.Equal("lease-lost", Answer(lost)["code"]!.ToString());
        Assert.DoesNotContain(gh.Calls, c => c.Method != HttpMethod.Get && c.Path != "graphql");   // no comment, no state save
        // private-build is not a fenced verb of v1: it writes whatever the lease says
        Assert.Equal(0, (await Host(gh, mayAct: () => false).Run("private-build", new JsonObject { ["item"] = 5, ["attest"] = new string('b', 40) }.ToJsonString(), TimeSpan.FromSeconds(30))).Code);
        gh.Calls.Clear();
        var held = await Host(gh).Run("settle", new JsonObject { ["item"] = 5 }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, held.Code);
        Assert.Equal(("advanced", "release-candidate"), (Answer(held)["outcome"]!.ToString(), Answer(held)["awaiting"]!.ToString()));
        Assert.Contains(gh.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/comments"));
    }

    [Fact]
    public async Task OneVerbAtATimePerItem_ASecondCallWaitsForTheFirst()
    {
        var gh = new FakeGitHub { HoldStateRead = true };
        var host = Host(gh);
        var args = new JsonObject { ["item"] = 5, ["attest"] = new string('a', 40) }.ToJsonString();
        var first = host.Run("private-build", args, TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        var second = host.Run("private-build", args, TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        Assert.Equal(1, gh.Calls.Count(c => c.Method == HttpMethod.Get && c.Path.Contains("/comments")));   // the second has not read the state yet
        gh.Hold.Release(2);
        var both = await Task.WhenAll(first, second);
        Assert.Equal(new[] { 0, 0 }, both.Select(a => a.Code).ToArray());
        Assert.Equal(2, gh.Rev);   // two saves, one after the other, each at the revision it read
    }

    [Fact]
    public async Task APrivateBuildIsASignOfLifeForTheItemsSaga_ButARefusedOneIsNot()
    {
        var gh = new FakeGitHub();
        var seen = new List<string>();
        var tap = new V2ActivityTap();
        tap.Connect((verb, stdin, output) => { seen.Add(verb); return Task.CompletedTask; });
        var host = Host(gh, tap: tap);
        await host.Run("private-build", new JsonObject { ["item"] = 5, ["attest"] = new string('a', 40) }.ToJsonString(), TimeSpan.FromSeconds(30));
        await host.Run("private-build", new JsonObject { ["item"] = 5, ["attest"] = "bad" }.ToJsonString(), TimeSpan.FromSeconds(30));
        await host.Run("copilot-owner", new JsonObject { ["item"] = 5, ["action"] = "get" }.ToJsonString(), TimeSpan.FromSeconds(30));
        Assert.Equal(["private-build"], seen);
    }

    // ---- the gateway's own calls of these verbs (W9.9a: the Runner asks the routes, there is no bridge) -------------------------------------------------------------

    [Fact]
    public async Task TheRunner_AnswersAVerbOfTheGroupFromItsRoute_AndOnlyWhenTheRoutesAreSet()
    {
        var runner = new Runner("/nonexistent/factory.ps1");
        var routes = new V2VerbRoutes();
        routes.Add(DeliveryRoutes.Verbs, "ci-release", (stdin, timeout) => Task.FromResult(new VerbAnswer(0, "{\"from\":\"v2\"}")));
        Assert.NotEqual(0, (await runner.RunAsync("ci-release", "{}", TimeSpan.FromSeconds(5))).Code);   // no routes: the rule script, which this test does not have
        runner.Routes = routes;
        Assert.Equal((0, "{\"from\":\"v2\"}"), await runner.RunAsync("ci-release", "{}", TimeSpan.FromSeconds(1)));
        Assert.NotEqual(0, (await runner.RunAsync("settle", "{}", TimeSpan.FromSeconds(5))).Code);      // (no route for it)
    }

    // ---- the executor's outcome (retry rule and messages) --------------------------------------------------------------------------------------------------

    sealed class Octopus(Func<HttpRequestMessage, (int, string)> answer) : HttpMessageHandler
    {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static DeliveryExecutorService Executor(Func<HttpRequestMessage, (int, string)> answer, out Octopus octo)
    {
        octo = new Octopus(answer);
        var promoter = new OctopusPromoter(new HttpClient(octo) { BaseAddress = new Uri("https://octopus.example/") }, new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string>(), () => "k"));
        var ledger = new InMemoryIdempotencyLedger();
        return new DeliveryExecutorService(new PromoteReleaseHandler(ledger, promoter), new AnswerSignOffHandler(ledger, promoter));
    }

    static readonly PromoteRelease Cmd = new("PromoteRelease|orders|1.0.5|uat", "orders", "orders-ui", "1.0.5", "uat");

    [Fact]
    public async Task TheExecutor_ASettledPromotionIsReportedFinished()
    {
        var ex = Executor(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/Spaces-1/projects/orders-ui" => (200, """{"Id":"Projects-1"}"""),
            "/api/Spaces-1/projects/Projects-1/releases/1.0.5" => (200, """{"Id":"R5"}"""),
            "/api/Spaces-1/environments" => (200, """{"Items":[]}"""),
            "/api/Spaces-1/releases/R5/deployments" => (200, """{"Items":[]}"""),
            "/api/Spaces-1/deployments/create/untenanted/v1" => (200, """{"DeploymentServerTasks":[{"ServerTaskId":"T1"}]}"""),
            _ => (404, "{}"),
        }, out _);
        var m = await ex.PromoteMessages(Cmd, 1, CancellationToken.None);
        var fin = Assert.IsType<DeliveryCommandFinished>(Assert.Single(m));
        Assert.Equal(("PromoteRelease", Cmd.IdempotencyKey, true), (fin.Name, fin.IdempotencyKey, fin.Ok));
    }

    [Fact]
    public async Task TheExecutor_APermanentRefusalGoesToTheSagaAsIntegrationFailed_ATransientOneIsRetriedThenSettled()
    {
        var refused = Executor(r => r.Method == HttpMethod.Post ? (403, """{"ErrorMessage":"Missing permission: DeploymentCreate"}""") : r.RequestUri!.AbsolutePath switch
        {
            "/api/Spaces-1/projects/orders-ui" => (200, """{"Id":"Projects-1"}"""),
            "/api/Spaces-1/projects/Projects-1/releases/1.0.5" => (200, """{"Id":"R5"}"""),
            "/api/Spaces-1/environments" => (200, """{"Items":[]}"""),
            _ => (200, """{"Items":[]}"""),
        }, out _);
        var m = await refused.PromoteMessages(Cmd, 1, CancellationToken.None);
        Assert.Equal(2, m.Count);
        var failed = Assert.IsType<IntegrationFailed>(m[0]);
        Assert.Equal((FailureKind.Permanent, "PromoteRelease"), (failed.Kind, failed.CommandName));
        Assert.False(Assert.IsType<DeliveryCommandFinished>(m[1]).Ok);

        var flaky = Executor(_ => (503, "{}"), out _);
        await Assert.ThrowsAsync<CommandRetryException>(() => flaky.PromoteMessages(Cmd, 1, CancellationToken.None));
        var last = await flaky.PromoteMessages(Cmd, RuntimeCommandService.MaxAttempts, CancellationToken.None);   // the run after the last pause settles as a failed result
        Assert.IsType<IntegrationFailed>(last[0]);
        Assert.StartsWith(RuntimeOutcome.ExhaustedPrefix, Assert.IsType<DeliveryCommandFinished>(last[1]).Error);
    }
}
