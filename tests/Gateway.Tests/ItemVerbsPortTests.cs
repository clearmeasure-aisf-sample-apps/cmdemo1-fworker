using System.Net;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;
using Fworker.Workflows.WorkItem.Verbs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.2 (#354): the gateway's side of the item verbs (gateway/Ports/ItemVerbsPort.cs, gateway/Verbs/Routes/Item.cs): the route file, the wiring and what it reads of the environment, the wire to GitHub, the lock file, the
/// refusals file and the feed of a park to the saga. The verbs themselves are tested in Fworker.Workflow.Tests (ItemVerbsTests) and on the fake GitHub (ItemVerbsFakeGitHubTests).
/// </summary>
public sealed class ItemVerbsPortTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("item-verbs-port-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    static JsonNode Config() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "config", "instance.json")))!;

    sealed class Router(string name) : IVerbRouter
    {
        public readonly List<string> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls.Add(verb); return Task.FromResult(new VerbAnswer(0, name + ":" + verb)); }
    }

    // ---- the route file and the wiring -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRouteFile_RegistersTheEighteenVerbsOfTheGroup_EachToTheRouter()
    {
        var routes = new V2VerbRoutes();
        var router = new Router("item");
        ItemRoutes.Register(routes, router);
        Assert.Equal(18, routes.Count);
        Assert.Equal(ItemRoutes.Verbs.OrderBy(x => x), routes.Verbs.OrderBy(x => x));
        foreach (var verb in ItemRoutes.Verbs)
        {
            Assert.True(routes.TryGet(verb, out var handler));
            Assert.Equal(new VerbAnswer(0, "item:" + verb), await handler(@"{""item"":1}", TimeSpan.FromSeconds(1)));
        }
        Assert.Equal(ItemRoutes.Verbs, router.Calls);
        // the same list as the verbs of the port
        Assert.Equal(ItemVerbs.Verbs, ItemRoutes.Verbs);
    }

    [Fact]
    public void WithoutTheConfigurationOrACredential_TheVerbsAreNotRegistered_TheyStayV1s()
    {
        var env = new Dictionary<string, string?>();
        Assert.Null(ItemVerbsWiring.Make(new ServiceCollection().BuildServiceProvider(), n => env.GetValueOrDefault(n)));   // no configuration
        var instance = InstanceConfig.Load(Path.Combine(Root(), "config", "instance.json"), n => env.GetValueOrDefault(n));
        var services = new ServiceCollection().AddSingleton(instance).BuildServiceProvider();
        Assert.Null(ItemVerbsWiring.Make(services, n => env.GetValueOrDefault(n)));                                               // no GH_TOKEN and no author App
        env["GH_TOKEN"] = "unset";
        Assert.Null(ItemVerbsWiring.Make(services, n => env.GetValueOrDefault(n)));                                               // the vault's placeholder is none
        env["GH_TOKEN"] = "a-token";
        Assert.NotNull(ItemVerbsWiring.Make(services, n => env.GetValueOrDefault(n)));
        // and the route file registers nothing for the group of a table built without them
        var routes = new V2VerbRoutes();
        ItemRoutes.Register(routes, new ServiceCollection().BuildServiceProvider());
        Assert.Equal(0, routes.Count);
    }

    static readonly string Token = "ghp_" + new string('A', 36);   // (composed at run time, so the secret scan has nothing to match)

    static void V2Config(GatewayEnvironment env) =>
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };

    [Fact]
    public void WhereTheEngineActs_WithACredential_TheEighteenVerbsAreRoutedToV2_AndNowhereElse()
    {
        if (!StubPwsh.Supported) return;
        using (var env = new GatewayEnvironment())
        {
            env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("GH_TOKEN", Token); V2Config(env); env.Apply();
            using var host = new GatewayHost();
            Assert.True(Task.Run(() => host.Server).Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
            var routes = host.Services.GetRequiredService<V2VerbRoutes>();
            Assert.All(ItemRoutes.Verbs, v => Assert.True(routes.TryGet(v, out _), v));
            Assert.False(routes.TryGet("merge", out _));   // another group's verb is not registered by this one
            Assert.IsType<V2VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        }
        // no credential, no routes: the verbs stay v1's (behind the saga's feed, as W6.6 left them)
        using (var env = new GatewayEnvironment())
        {
            env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); V2Config(env); env.Apply();
            using var host = new GatewayHost();
            Assert.True(Task.Run(() => host.Server).Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
            // (the read models and the service verbs register without GH_TOKEN, on the App or the credentials they have: only the item verbs are pinned here)
            var noCredential = host.Services.GetRequiredService<V2VerbRoutes>();
            Assert.All(ItemRoutes.Verbs, v => Assert.False(noCredential.TryGet(v, out _), v));
            Assert.IsType<V2VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());   // (the other groups' routes put it in front; the item verbs fall through to v1 behind the saga's feed)
        }
        // the kill switch puts the environment back on v1: no route, the router of v1
        using (var env = new GatewayEnvironment())
        {
            env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("GH_TOKEN", Token); env.Set("FACTORY_V2_OFF", "1"); V2Config(env); env.Apply();
            using var host = new GatewayHost();
            _ = host.Server;
            Assert.Equal(0, host.Services.GetRequiredService<V2VerbRoutes>().Count);
            Assert.IsType<V1VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        }
    }

    [Theory]
    [InlineData(null, null, "", "")]                        // the source default: no scope
    [InlineData("tdd", null, "tdd", "env:tdd")]
    [InlineData("TDD ", null, "tdd", "env:tdd")]
    [InlineData("prod", null, "tdd", "")]                   // not in the list
    [InlineData("prod", "1", "tdd", "env:prod")]            // FACTORY_ENV_SCOPE 1 turns it on for any name
    [InlineData("tdd", "0", "tdd", "")]
    [InlineData(null, "1", "tdd", "env:tdd")]               // no name: tdd
    public void TheEnvironmentLabel_IsGetEnvLabelOfTheScript(string? environment, string? scope, string listed, string expected)
    {
        var root = Config().AsObject();
        root["envScope"] = listed.Length == 0 ? new JsonArray() : new JsonArray(listed);
        var env = new Dictionary<string, string?> { ["FACTORY_ENVIRONMENT"] = environment, ["FACTORY_ENV_SCOPE"] = scope };
        Assert.Equal(expected, ItemVerbsWiring.EnvLabel(root, n => env.GetValueOrDefault(n)));
    }

    [Theory]
    [InlineData(false, false, null, null, false)]
    [InlineData(true, false, null, null, true)]
    [InlineData(false, true, null, null, true)]
    [InlineData(true, true, "0", null, true)]               // messaging forced off, the supervisor still on
    [InlineData(true, true, "0", "0", false)]
    [InlineData(false, false, "1", null, true)]
    public void MessagingIsOn_WhenMessagingOrTheSupervisorIs_TheEnvironmentOverrides(bool messaging, bool manager, string? messagingEnv, string? managerEnv, bool expected)
    {
        var root = Config().AsObject();
        root["messaging"]!["enabled"] = messaging; root["manager"]!["enabled"] = manager;
        var env = new Dictionary<string, string?> { ["FACTORY_MESSAGING"] = messagingEnv, ["FACTORY_MANAGER"] = managerEnv };
        Assert.Equal(expected, ItemVerbsWiring.MessagingOn(root, n => env.GetValueOrDefault(n)));
    }

    [Theory]
    [InlineData("peer-approved", "1", "0123456789abcdef0123", null, null, true)]
    [InlineData("peer-approved", "1", "0123456789abcdef0123", false, false, false)]
    [InlineData("peer-approved", "1", "0123456789abcdef0123", false, true, true)]
    [InlineData("auto", "1", "0123456789abcdef0123", null, null, false)]
    [InlineData("peer-approved", null, "0123456789abcdef0123", null, null, false)]    // owner mode only
    [InlineData("peer-approved", "1", "short", null, null, false)]                    // and a token secret
    public void TheQcGate_IsActiveInOwnerModeUnderPeerApprovalWithARequirementOn(string policy, string? ownerMode, string secret, bool? requireRun, bool? requireApproval, bool expected)
    {
        var root = Config().AsObject();
        root["mergePolicy"] = policy;
        root["qc"]!["requireRun"] = requireRun; root["qc"]!["requireApproval"] = requireApproval;
        var env = new Dictionary<string, string?> { ["FACTORY_OWNER_MODE"] = ownerMode, ["FACTORY_TOKEN_SECRET"] = secret };
        // (a null in the configuration means "default", which is on under peer-approved; the script's default for the other policies is off, and so is its answer)
        Assert.Equal(expected, ItemVerbsWiring.QcGateActive(root, n => env.GetValueOrDefault(n)));
    }

    // ---- the wire --------------------------------------------------------------------------------------------------------------------------------------------

    sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<HttpRequestMessage> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return await answer(request, ++Calls); }
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    [Fact]
    public async Task TheWire_SendsTheHeadersOfTheScripts_AndHandsTheRateNumbersToTheBudget()
    {
        var seen = new List<(string, string?, string?, string?)>();
        var h = new Handler((req, _) => Task.FromResult(Json(HttpStatusCode.OK, @"{""a"":1}", ("x-ratelimit-resource", "core"), ("x-ratelimit-remaining", "4000"), ("x-ratelimit-reset", "1700000000"), ("x-ratelimit-limit", "5000"))));
        var wire = new ItemVerbsWire(new HttpClient(h) { BaseAddress = new Uri("https://api.example/") }, (r, rem, reset, limit) => seen.Add((r, rem, reset, limit)));
        var a = await wire.Send(new ItemGhCall("PATCH", "repos/o/r/issues/5", new JsonObject { ["body"] = "x" }), default);
        Assert.Equal(200, a.Status); Assert.Equal(1, (int)a.Body!["a"]!);
        var req = h.Requests.Single();
        Assert.Equal("PATCH", req.Method.Method); Assert.Equal("https://api.example/repos/o/r/issues/5", req.RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", req.Headers.Accept.ToString()); Assert.Equal("2022-11-28", req.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Equal("factory-worker-gateway", req.Headers.UserAgent.ToString());
        Assert.Equal([("core", "4000", "1700000000", "5000")], seen.Select(s => (s.Item1, s.Item2, s.Item3, s.Item4)));
    }

    [Fact]
    public async Task TheWire_TriesAServerFaultThreeTimes_AndAnUnreachableGitHubIsARefusalOf20()
    {
        var h = new Handler((req, n) => Task.FromResult(n < 3 ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, @"{""ok"":true}")));
        var wire = new ItemVerbsWire(new HttpClient(h) { BaseAddress = new Uri("https://api.example/") }, null, _ => TimeSpan.Zero);
        Assert.Equal(200, (await wire.Send(new ItemGhCall("GET", "repos/o/r"), default)).Status);
        Assert.Equal(3, h.Calls);
        // a fault of the network on every try
        var down = new Handler((_, _) => throw new HttpRequestException("connection refused"));
        var wire2 = new ItemVerbsWire(new HttpClient(down) { BaseAddress = new Uri("https://api.example/") }, null, _ => TimeSpan.Zero);
        var e = await Assert.ThrowsAsync<ItemRefusal>(() => wire2.Send(new ItemGhCall("GET", "repos/o/r"), default));
        Assert.Equal(20, e.Exit); Assert.Equal("github-unreachable", e.Code); Assert.Equal(3, down.Calls);
        // a 4xx is the caller's to judge: it is answered, not retried
        var h404 = new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, @"{""message"":""Not Found""}")));
        var a = await new ItemVerbsWire(new HttpClient(h404) { BaseAddress = new Uri("https://api.example/") }, null, _ => TimeSpan.Zero).Send(new ItemGhCall("GET", "repos/o/r/pulls/9"), default);
        Assert.Equal(404, a.Status); Assert.Equal(1, h404.Calls);
    }

    [Fact]
    public async Task TheWire_AnAnswerThatIsNotJsonIsItsText_AndAnEmptyOneIsNothing()
    {
        var h = new Handler((_, n) => Task.FromResult(n == 1 ? Json(HttpStatusCode.OK, "plain text") : new HttpResponseMessage(HttpStatusCode.NoContent)));
        var wire = new ItemVerbsWire(new HttpClient(h) { BaseAddress = new Uri("https://api.example/") });
        Assert.Equal("plain text", (await wire.Send(new ItemGhCall("GET", "x"), default)).Body!.ToString());
        Assert.Null((await wire.Send(new ItemGhCall("DELETE", "x"), default)).Body);
    }

    // ---- the lock file and the refusals file -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLock_IsTheItemsFile_OneAtATime_AndABusyOneRefusesWith20()
    {
        var l = new ItemVerbsLock(dir);
        var inside = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var first = l.Use(5, async () => { inside.SetResult(); await release.Task; return 1; }, 5, default);
        await inside.Task;
        Assert.True(File.Exists(Path.Combine(dir, "item-5.lock")));
        // another item is free, the same item is busy
        Assert.Equal(2, await l.Use(6, () => Task.FromResult(2), 1, default));
        var e = await Assert.ThrowsAsync<ItemRefusal>(() => l.Use(5, () => Task.FromResult(3), 1, default));
        Assert.Equal(20, e.Exit); Assert.Equal("item-busy", e.Code); Assert.Equal(5, (int)e.Extra["retryAfterSeconds"]!);
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(4, await l.Use(5, () => Task.FromResult(4), 1, default));   // let go
        // let go after a fault too
        await Assert.ThrowsAsync<InvalidOperationException>(() => l.Use(5, () => Task.FromException<int>(new InvalidOperationException("x")), 1, default));
        Assert.Equal(5, await l.Use(5, () => Task.FromResult(5), 1, default));
    }

    [Fact]
    public async Task TheRefusalsFile_IsTheOneOfTheModelLadder_ObjectOfCountsByItem()
    {
        var r = new ItemVerbsRefusals(dir);
        Assert.Equal(0, await r.Count(7, default));
        await r.Add(7, default); await r.Add(7, default); await r.Add(8, default);
        Assert.Equal(2, await r.Count(7, default));
        Assert.Equal("""{"7":2,"8":1}""", File.ReadAllText(Path.Combine(dir, "refusals.json")));
        await r.Reset(7, default);
        Assert.Equal(0, await r.Count(7, default)); Assert.Equal(1, await r.Count(8, default));
        // a file the scripts wrote (the same shape) is read, an unreadable one is none
        File.WriteAllText(Path.Combine(dir, "refusals.json"), """{"9": 4}""");
        Assert.Equal(4, await new ItemVerbsRefusals(dir).Count(9, default));
        File.WriteAllText(Path.Combine(dir, "refusals.json"), "not json");
        Assert.Equal(0, await new ItemVerbsRefusals(dir).Count(9, default));
    }

    [Fact]
    public void TheStateDirectory_IsTheScriptsOne()
    {
        var env = new Dictionary<string, string?> { ["FACTORY_STATE_DIR"] = Path.Combine(dir, "a") };
        Assert.Equal(Path.Combine(dir, "a"), ItemVerbsFiles.StateDir(n => env.GetValueOrDefault(n)));
        env.Clear(); env["FACTORY_DATA_DIR"] = Path.Combine(dir, "data");
        Assert.Equal(Path.Combine(dir, "data", "state"), ItemVerbsFiles.StateDir(n => env.GetValueOrDefault(n)));
        Assert.True(Directory.Exists(Path.Combine(dir, "data", "state")));
        env.Clear(); env["HOME"] = dir;
        Assert.Equal(Path.Combine(dir, ".local/share/factory-worker", "state"), ItemVerbsFiles.StateDir(n => env.GetValueOrDefault(n)));
    }

    // ---- the feed of a park to the saga ----------------------------------------------------------------------------------------------------------------------

    sealed class Fixed(VerbAnswer answer) : IVerbRouter
    {
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) => Task.FromResult(answer);
    }

    [Fact]
    public async Task APark_AndAnUnpark_OfAnItemTheSagaOwns_AreToldToIt()
    {
        var home = new RepoId("acme", "orders");
        var ownership = new V2Ownership(new InMemoryWorkItemStore(), home);
        ownership.Set([7]);
        var sent = new List<WorkItemInput>();
        Task<bool> Publish(WorkItemInput m) { sent.Add(m); return Task.FromResult(true); }
        var park = new ItemAdminFeed(new Fixed(new VerbAnswer(0, @"{""ok"":true,""parked"":true,""park"":{""reason"":""runtime-failed"",""at"":""2026-10-29T12:00:00.000Z""}}")), ownership, home, Publish);
        await park.RunAsync("park-item", @"{""item"":7,""code"":""runtime-failed""}", TimeSpan.FromSeconds(1));
        var p = Assert.IsType<ParkWorkItem>(Assert.Single(sent));
        Assert.Equal(new ItemId(home, 7), p.Item); Assert.Equal("runtime-failed", p.Reason); Assert.Equal(0, p.Generation); Assert.Equal("park|2026-10-29T12:00:00.000Z", p.Key);
        sent.Clear();
        // the park that was in force already is not a new one
        await new ItemAdminFeed(new Fixed(new VerbAnswer(0, @"{""ok"":true,""parked"":true,""already"":true,""park"":{""reason"":""x"",""at"":""t""}}")), ownership, home, Publish).RunAsync("park-item", @"{""item"":7}", TimeSpan.FromSeconds(1));
        Assert.Empty(sent);
        await new ItemAdminFeed(new Fixed(new VerbAnswer(0, @"{""ok"":true,""unparked"":true,""park"":{""reason"":""runtime-failed"",""at"":""2026-10-29T12:00:00.000Z""}}")), ownership, home, Publish).RunAsync("unpark-item", @"{""item"":7,""reason"":""x""}", TimeSpan.FromSeconds(1));
        var r = Assert.IsType<ResumeWorkItem>(Assert.Single(sent));
        Assert.Equal("resume|2026-10-29T12:00:00.000Z", r.Key);
        sent.Clear();
        // an item the saga does not own, a refusal, another verb, and a host that is down: nothing is sent, and the answer is the verb's
        var other = new VerbAnswer(0, @"{""ok"":true,""parked"":true,""park"":{""reason"":""x"",""at"":""t""}}");
        Assert.Equal(other, await new ItemAdminFeed(new Fixed(other), ownership, home, Publish).RunAsync("park-item", @"{""item"":8}", TimeSpan.FromSeconds(1)));
        var refused = new VerbAnswer(10, @"{""ok"":false}");
        Assert.Equal(refused, await new ItemAdminFeed(new Fixed(refused), ownership, home, Publish).RunAsync("park-item", @"{""item"":7}", TimeSpan.FromSeconds(1)));
        await new ItemAdminFeed(new Fixed(other), ownership, home, Publish).RunAsync("release", @"{""item"":7}", TimeSpan.FromSeconds(1));
        Assert.Empty(sent);
        Assert.Equal(other, await new ItemAdminFeed(new Fixed(other), ownership, home, _ => throw new InvalidOperationException("down")).RunAsync("park-item", @"{""item"":7}", TimeSpan.FromSeconds(1)));
    }
}
