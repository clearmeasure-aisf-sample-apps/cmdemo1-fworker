using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Keeper;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.6 (#358) in the gateway: the route file registers the seven keeper and manager verbs only where the engine acts and the gateway has a GitHub identity and a home repository; the adapters answer the way the rule scripts' helpers
/// did (Invoke-Gh's refusals, the locks' files, the state comment and the journal of the W3.2 ledger). The verbs themselves are Fworker.Workflow.Tests (KeeperCommandTests, ManagerCommandTests); the scenarios "act: ..." and "keeper: ..."
/// of the manager suite are the same checks against the real gateway, in the v2 job.
/// </summary>
public sealed class KeeperRoutesTests : IDisposable
{
    // composed at run time, so the secret scan has nothing to match
    static readonly string Token = "ghp_" + new string('B', 36);
    readonly string dir = Directory.CreateTempSubdirectory("keeper-routes-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static readonly string[] SevenVerbs = ["backlog", "amend-criteria", "split-item", "create-item", "set-priority", "link-dependency", "manager-action"];

    const string Config = """
        { "name": "factory-worker", "owner": "acme", "repo": "work", "project": 1, "label": "factory", "autoMergeLabel": "Auto Merge", "testLabel": "test", "videoLabel": "video",
          "columns": { "ai": ["Technical Design", "Development"] }, "requiredBodySection": { "Technical Design": "## Technical design" } }
        """;

    InstanceConfig Instance(string json = Config)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return InstanceConfig.Load(path, _ => null);
    }

    IServiceProvider Services(InstanceConfig? instance) => new ServiceCollection().AddSingleton(instance ?? Instance()).BuildServiceProvider();

    Func<string, string?> Env(string? token = null, string? stateDir = null) => name => name switch
    {
        "GH_TOKEN" => token ?? Token, "FACTORY_SERVER_LOGIN" => "factory-bot", "FACTORY_STATE_DIR" => stateDir ?? Path.Combine(dir, "state"), "FACTORY_ENVIRONMENT" => "tdd", _ => null,
    };

    /// <summary>GitHub as a function: the requests it was sent, in order, and the answers a test scripts by method and path.</summary>
    sealed class Scripted : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public readonly List<string> Bodies = [];
        public Func<HttpMethod, string, string, HttpResponseMessage?> Answer = (_, _, _) => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content == null ? "" : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            lock (Requests) { Requests.Add($"{request.Method} {path}"); Bodies.Add(body); }
            var r = Answer(request.Method, path, body);
            if (r != null) return Task.FromResult(r);
            // the defaults: no comments yet, a comment created, anything else empty and fine
            if (request.Method == HttpMethod.Get && path.Contains("/comments")) return Task.FromResult(Json("[]"));
            if (request.Method == HttpMethod.Post && path.EndsWith("/comments")) return Task.FromResult(Json("""{"id":9001}""", HttpStatusCode.Created));
            return Task.FromResult(Json("{}"));
        }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static string Issue(int n, string labels, string column = "Technical Design", string state = "OPEN")
    {
        var names = JsonNode.Parse("[" + labels + "]")!.AsArray().Select(l => (JsonNode)new JsonObject { ["name"] = l!["name"]!.ToString() }).ToArray();
        var issue = new JsonObject
        {
            ["id"] = "I_" + n, ["number"] = n, ["state"] = state, ["title"] = "An item", ["body"] = "Body", ["url"] = "u", ["createdAt"] = "2026-10-29T10:00:00Z",
            ["labels"] = new JsonObject { ["nodes"] = new JsonArray(names) },
            ["projectItems"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "C_" + n, ["project"] = new JsonObject { ["number"] = 1 }, ["fieldValueByName"] = new JsonObject { ["name"] = column } }) },
        };
        return new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issue"] = issue } } }.ToJsonString();
    }

    KeeperVerbs Verbs(Scripted handler, Func<string, string?>? env = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        return KeeperWiring.Make(Services(null), env ?? Env(), http)!;
    }

    // ---- registration ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSevenVerbsAreTheKeeperGroup_AndAreRegisteredWhenTheGatewayHasAnIdentityAndAHome()
    {
        Assert.Equal(SevenVerbs, KeeperRoutes.Verbs);
        Assert.Equal(SevenVerbs, KeeperVerbs.Verbs);
        var routes = new V2VerbRoutes();
        var verbs = KeeperWiring.Make(Services(null), Env(), new HttpClient(new Scripted()) { BaseAddress = new Uri("https://api.example.test/") });
        Assert.NotNull(verbs);
        foreach (var verb in KeeperRoutes.Verbs) routes.Add(KeeperRoutes.Verbs, verb, (stdin, timeout) => verbs!.Run(verb, stdin, timeout));
        Assert.Equal(SevenVerbs.Order(), routes.Verbs.Order());
    }

    [Fact]
    public void WithoutTheInstanceConfigurationAGitHubIdentityOrAHome_NothingIsRegistered_AndV1Answers()
    {
        var routes = new V2VerbRoutes();
        KeeperRoutes.Register(routes, new ServiceCollection().BuildServiceProvider());   // no configuration in the container: the stub tests of W9.0 run this way
        Assert.Equal(0, routes.Count);
        Assert.Null(KeeperWiring.Make(Services(null), Env("unset")));                       // GH_TOKEN "unset" is none
        Assert.Null(KeeperWiring.Make(Services(Instance("""{ "name": "x", "label": "factory" }""")), Env()));   // no owner and repo
    }

    // ---- an end to end call: the verb, the adapters and GitHub as a function ------------------------------------------------------------------

    [Fact]
    public async Task SetPriority_GoesToGitHubWithTheCallsOfTheScript_AndAnswersItsFields()
    {
        var h = new Scripted
        {
            Answer = (m, p, b) => m == HttpMethod.Post && p == "graphql" ? Json(Issue(5, """{"name":"factory"},{"name":"priority:high"}""")) : null,
        };
        var a = await Verbs(h).Run("set-priority", """{"item":5,"level":"top","policy":"backlog-low","reason":"first","by":"backlog-keeper"}""", TimeSpan.FromSeconds(20));
        Assert.Equal(0, a.Code);
        var o = JsonNode.Parse(a.Output)!;
        Assert.Equal("top", o["level"]!.ToString()); Assert.Equal("high", o["was"]!.ToString()); Assert.Equal(5, o["item"]!.GetValue<int>());
        Assert.True(o["ok"]!.GetValue<bool>()); Assert.Equal("set-priority", o["verb"]!.ToString()); Assert.Equal(0, o["exit"]!.GetValue<int>());
        var writes = h.Requests.Where(r => !r.StartsWith("GET") && r != "POST graphql").ToList();
        Assert.Equal("DELETE repos/acme/work/issues/5/labels/priority%3Ahigh", writes[0]);
        Assert.Equal("POST repos/acme/work/issues/5/labels", writes[1]);
        Assert.Contains("""{"labels":["priority:top"]}""", h.Bodies);
        // the journal line of the keeper, and the state comment that carries its ledger (the bodies are JSON: read the text)
        var texts = h.Bodies.Select(b => { try { return JsonNode.Parse(b)?["body"]?.ToString() ?? ""; } catch { return ""; } }).ToList();
        Assert.Contains(texts, t => t.Contains("the backlog keeper (**backlog-keeper**) set its priority to **top** (was high), under policy `backlog-low`: first.") && t.Contains("<!-- plant event=keeper-action key=set-priority|"));
        Assert.Contains(texts, t => t.Contains("<!-- factory-state:factory-worker v1 -->") && t.Contains("keeper-action|set-priority|"));
    }

    [Fact]
    public async Task AnIssueThatIsNotThere_IsTheItemNotFoundRefusal_AndAnOutOfScopeOneToo()
    {
        var gone = new Scripted { Answer = (m, p, b) => p == "graphql" ? Json("""{"data":{"repository":{"issue":null}}}""") : null };
        var a = await Verbs(gone).Run("set-priority", """{"item":5,"level":"top","policy":"backlog-low"}""", TimeSpan.FromSeconds(20));
        Assert.Equal(12, a.Code);
        Assert.Equal("item-not-found", JsonNode.Parse(a.Output)!["code"]!.ToString());

        var other = new Scripted { Answer = (m, p, b) => p == "graphql" ? Json(Issue(5, """{"name":"factory"},{"name":"env:uat"}""")) : null };
        var scoped = Env(); var env = (string n) => n == "FACTORY_ENV_SCOPE" ? "1" : scoped(n);
        var b2 = await Verbs(other, env).Run("set-priority", """{"item":5,"level":"top","policy":"backlog-low"}""", TimeSpan.FromSeconds(20));
        Assert.Equal(10, b2.Code);
        Assert.Equal("out-of-scope", JsonNode.Parse(b2.Output)!["code"]!.ToString());
    }

    [Theory]
    [InlineData(404, "", 12, "github-not-found")]
    [InlineData(500, "", 20, "github-error")]
    [InlineData(403, "{\"message\":\"Resource not accessible by integration\"}", 30, "github-forbidden")]
    [InlineData(403, "{\"message\":\"You have exceeded a secondary rate limit\"}", 20, "github-rate-limited")]
    [InlineData(429, "{}", 20, "github-rate-limited")]
    public async Task GitHubsRefusalsAreInvokeGhsRefusals(int status, string body, int exit, string code)
    {
        var h = new Scripted
        {
            Answer = (m, p, b) => p == "graphql" ? Json(Issue(5, """{"name":"factory"}""")) : m == HttpMethod.Post && p.EndsWith("/labels") ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body.Length == 0 ? "{}" : body, Encoding.UTF8, "application/json") } : null,
        };
        var a = await Verbs(h).Run("set-priority", """{"item":5,"level":"top","policy":"backlog-low"}""", TimeSpan.FromSeconds(30));
        Assert.Equal(exit, a.Code);
        var o = JsonNode.Parse(a.Output)!;
        Assert.Equal(code, o["code"]!.ToString()); Assert.False(o["ok"]!.GetValue<bool>());
        if (exit == 20) Assert.True(o["retryAfterSeconds"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task ABackendFault_IsTriedThreeTimesAndThenUnreachable()
    {
        var h = new Scripted { Answer = (m, p, b) => throw new HttpRequestException("connection refused") };
        var http = new HttpClient(h) { BaseAddress = new Uri("https://api.example.test/") };
        var github = new KeeperGitHub(http, FactoryRules.From(JsonNode.Parse(Config), _ => null), 1, null, _ => Task.CompletedTask);
        var e = await Assert.ThrowsAsync<VerbRejection>(() => github.GetItemInfo(5));
        Assert.Equal("github-unreachable", e.Code); Assert.Equal(20, e.Exit);
        Assert.Equal(3, h.Requests.Count);
    }

    // ---- the locks ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLocksAreTheFilesOfTheScripts_AndAHeldOneIsBusyAfterTheWait()
    {
        var locks = new KeeperFileLocks(new FileItemLock(Path.Combine(dir, "locks")), Env(null, Path.Combine(dir, "locks")), TimeSpan.FromMilliseconds(500));
        Assert.Equal(Path.Combine(dir, "locks"), locks.StateDir());
        await using (await locks.Item(5))
        {
            Assert.True(File.Exists(Path.Combine(dir, "locks", "item-5.lock")));
            var e = await Assert.ThrowsAsync<VerbRejection>(() => locks.Item(5));
            Assert.Equal("item-busy", e.Code); Assert.Equal(20, e.Exit); Assert.Equal(5, e.Extra["retryAfterSeconds"]);
            await using (await locks.Item(6)) { }                // another item is free
        }
        await using (await locks.Item(5)) { }                    // released
        await using (await locks.Intake()) { Assert.True(File.Exists(Path.Combine(dir, "locks", "board-intake.lock"))); }
        // a script that holds the file excludes the verb, as it excludes another script
        using var script = new FileStream(Path.Combine(dir, "locks", "item-7.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<VerbRejection>(() => locks.Item(7));
    }

    [Fact]
    public async Task TheVerbsWaitOnTheirItemLock_AndAnswerItemBusyWhenItStaysHeld()
    {
        var stateDir = Path.Combine(dir, "busy"); Directory.CreateDirectory(stateDir);
        using var script = new FileStream(Path.Combine(stateDir, "item-5.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var h = new Scripted();
        var http = new HttpClient(h) { BaseAddress = new Uri("https://api.example.test/") };
        var rules = FactoryRules.From(JsonNode.Parse(Config), _ => null);
        var verbs = new KeeperVerbs(new VerbContext(rules, new KeeperGitHub(http, rules, 1), new NoState(), new KeeperFileLocks(new FileItemLock(stateDir), Env(null, stateDir), TimeSpan.FromMilliseconds(400)), new NoSaga()));
        var a = await verbs.Run("set-priority", """{"item":5,"level":"top","policy":"backlog-low"}""", TimeSpan.FromSeconds(20));
        Assert.Equal(20, a.Code);
        Assert.Equal("item-busy", JsonNode.Parse(a.Output)!["code"]!.ToString());
        Assert.Empty(h.Requests);   // nothing was read before the lock
    }

    sealed class NoState : IItemStateStore
    {
        public Task<StateHolder> Read(int item) => throw new NotSupportedException();
        public Task Save(int item, StateHolder holder) => throw new NotSupportedException();
        public Task<bool> Journal(int item, StateHolder? holder, string ev, JsonObject fields, string key) => Task.FromResult(false);
    }
}
