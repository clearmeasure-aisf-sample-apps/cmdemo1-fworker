using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.5 (#357): the gateway's side of the read models (gateway/Ports/ReadModelsPort.cs, gateway/Verbs/Routes/ReadModels.cs): the wire to GitHub as Invoke-GhRaw makes it, the redirect without the credential, Octopus's read API, the state
/// store, the item lock and the refusal counts of the rule scripts' state directory, the handlers' time and fields, and the registration of the six verbs where the engine acts.
/// </summary>
public sealed class ReadModelsPortTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("rm-port-").FullName;
    readonly Dictionary<string, string?> saved = [];

    // the registration reads the process environment (as the gateway does): no identity, no Octopus key, a state directory of this test, and nothing that reaches the network
    public ReadModelsPortTests()
    {
        foreach (var (k, v) in new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "unset", ["FACTORY_APP_AUTHOR_ID"] = null, ["FACTORY_APP_AUTHOR_INSTALLATION"] = null, ["FACTORY_APP_AUTHOR_KEY"] = null, ["OCTOPUS_API_KEY"] = null,
            ["FACTORY_STATE_DIR"] = Path.Combine(dir, "state"), ["GITHUB_API_URL"] = "http://127.0.0.1:9", ["FACTORY_WEBHOOK_SECRET"] = null,
        })
        { saved[k] = Environment.GetEnvironmentVariable(k); Environment.SetEnvironmentVariable(k, v); }
    }

    public void Dispose()
    {
        foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(dir, true); } catch { }
    }

    sealed class Scripted : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = [];
        public readonly List<string> Bodies = [];
        public Func<HttpRequestMessage, int, HttpResponseMessage> Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return Answer(request, Seen.Count);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    static HttpClient Client(Scripted h) => new(h) { BaseAddress = new Uri("https://api.example/") };

    // ---- Invoke-GhRaw ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWire_SendsTheHeadersAndTheBodyOfInvokeGhRaw_AndReadsJsonOrText()
    {
        var h = new Scripted { Answer = (_, _) => Json(HttpStatusCode.Created, """{"id":5}""", ("x-ratelimit-remaining", "4999"), ("Retry-After", "3")) };
        var api = new HttpGitHubApi(Client(h));
        var a = await api.Send(new GhCall("POST", "repos/o/r/issues/1/comments", new JsonObject { ["body"] = "hi" }, Accept: "application/vnd.github.raw"), default);
        Assert.Equal((201, 5, "4999", "3"), (a.Status, Ps.ToInt(Ps.Prop(a.Body, "id")), a.Header("X-RateLimit-Remaining"), a.Header("retry-after")));
        var req = h.Seen[0];
        Assert.Equal(("POST", "https://api.example/repos/o/r/issues/1/comments", "application/vnd.github.raw", "2022-11-28"),
            (req.Method.Method, req.RequestUri!.ToString(), req.Headers.Accept.ToString(), req.Headers.GetValues("X-GitHub-Api-Version").Single()));
        Assert.Equal("""{"body":"hi"}""", h.Bodies[0]);
        Assert.StartsWith("application/json", req.Content!.Headers.ContentType!.ToString());
        h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("plain words") };
        var text = await api.Send(new GhCall("GET", "x"), default);
        Assert.Equal("plain words", Ps.Text(text.Body));
        h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.Null((await api.Send(new GhCall("GET", "x"), default)).Body);
        Assert.Null(h.Seen[^1].Content);   // no body, no content type
    }

    [Fact]
    public async Task ARequestIsTriedAgainOnAFaultOrA502To504_WithTwoSecondsATry_AndThenRefused()
    {
        var waits = new List<TimeSpan>();
        var h = new Scripted();
        var api = new HttpGitHubApi(Client(h), (t, _) => { waits.Add(t); return Task.CompletedTask; });
        h.Answer = (_, n) => n < 3 ? new HttpResponseMessage(n == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.GatewayTimeout) : Json(HttpStatusCode.OK, "{}");
        Assert.Equal(200, (await api.Send(new GhCall("GET", "x"), default)).Status);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
        // the last try's 503 is the answer
        waits.Clear(); h.Seen.Clear(); h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(503, (await api.Send(new GhCall("GET", "x"), default)).Status);
        Assert.Equal((3, 2), (h.Seen.Count, waits.Count));
        // a 500 or a 404 is an answer at once
        h.Seen.Clear(); h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(500, (await api.Send(new GhCall("GET", "x"), default)).Status); Assert.Single(h.Seen);
        // a network fault on every try is the refusal github-unreachable
        h.Seen.Clear(); waits.Clear(); h.Answer = (_, _) => throw new HttpRequestException("no route to host");
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => api.Send(new GhCall("GET", "x"), default));
        Assert.Equal((20, "github-unreachable", "no route to host", 30), (e.Exit, e.Code, e.Message, e.Extra["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Equal((3, 2), (h.Seen.Count, waits.Count));
        // one try: no second
        h.Seen.Clear();
        await Assert.ThrowsAsync<VerbRefusal>(() => api.Send(new GhCall("GET", "x", Tries: 1), default));
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task ARequestThatTakesLongerThanItMay_IsTheRefusalGithubUnreachable_ACallersCancellationIsNot()
    {
        var h = new Scripted { Answer = (_, _) => throw new InvalidOperationException("unused") };
        var slow = new HttpGitHubApi(new HttpClient(new Slow()) { BaseAddress = new Uri("https://api.example/") });
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => slow.Send(new GhCall("GET", "x", Timeout: TimeSpan.FromMilliseconds(50), Tries: 1), default));
        Assert.Equal(("github-unreachable", "the request to GitHub timed out"), (e.Code, e.Message));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.Send(new GhCall("GET", "x", Tries: 3), cts.Token));
    }

    sealed class Slow : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); }
    }

    [Fact]
    public async Task ABodyThatGoesToAFile_IsKeptOnlyOnSuccess()
    {
        var file = Path.Combine(dir, "log.txt");
        var h = new Scripted { Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("line one\nline two\n") } };
        var api = new HttpGitHubApi(Client(h));
        var ok = await api.Send(new GhCall("GET", "logs", OutFile: file), default);
        Assert.Equal((200, null, "line one\nline two\n"), (ok.Status, ok.Body, File.ReadAllText(file)));
        File.Delete(file);
        h.Answer = (_, _) => Json(HttpStatusCode.Forbidden, """{"message":"no"}""");
        var denied = await api.Send(new GhCall("GET", "logs", OutFile: file), default);
        Assert.Equal((403, null, false), (denied.Status, denied.Body, File.Exists(file)));   // as with Invoke-RestMethod -OutFile: the answer's body is not read
    }

    [Fact]
    public async Task ARedirectIsFollowedWithoutTheCredential_AsInvokeRestMethodDoesIt()
    {
        var api = new Scripted();
        var hop = new Scripted { Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("the signed log") } };
        api.Answer = (r, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("/_blob/logs/7", UriKind.Relative);
            return resp;
        };
        var file = Path.Combine(dir, "redirected.txt");
        var wire = new HttpGitHubApi(Client(api), null, new HttpClient(hop) { BaseAddress = new Uri("https://api.example/") });
        var a = await wire.Send(new GhCall("GET", "repos/o/r/actions/jobs/7/logs", OutFile: file), default);
        Assert.Equal((200, "the signed log"), (a.Status, File.ReadAllText(file)));
        Assert.Single(api.Seen);
        Assert.Equal("https://api.example/_blob/logs/7", hop.Seen[0].RequestUri!.ToString());
        Assert.Null(hop.Seen[0].Headers.Authorization);
        // an absolute address of another store; a chain ends after five; only a GET is followed
        api.Answer = (_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri("https://blob.example/x?sig=1"); return r; };
        hop.Seen.Clear();
        await wire.Send(new GhCall("GET", "a", OutFile: file), default);
        Assert.Equal("https://blob.example/x?sig=1", hop.Seen[0].RequestUri!.ToString());
        hop.Answer = (_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri("https://blob.example/loop"); return r; };
        hop.Seen.Clear();
        Assert.Equal(302, (await wire.Send(new GhCall("GET", "a"), default)).Status);
        Assert.Equal(5, hop.Seen.Count);
        api.Seen.Clear(); hop.Seen.Clear();
        Assert.Equal(302, (await wire.Send(new GhCall("POST", "a", new JsonObject()), default)).Status);
        Assert.Empty(hop.Seen);
    }

    [Fact]
    public async Task TheIdentityOfTheRuleScripts_IsSentWithEveryRequest_AndTheRateLimitGoesToTheBudget()
    {
        var seen = new Scripted { Answer = (_, _) => Json(HttpStatusCode.OK, "{}", ("x-ratelimit-resource", "core"), ("x-ratelimit-remaining", "4000"), ("x-ratelimit-reset", "1799999999"), ("x-ratelimit-limit", "5000")) };
        var reports = new List<(string, string?, string?, string?)>();
        using var http = new HttpClient(new RuleIdentityHandler(seen, (r, rem, reset, limit) => reports.Add((r, rem, reset, limit)))) { BaseAddress = new Uri("https://api.example/") };
        await http.GetAsync("x");
        Assert.Equal("factory-worker-gateway", seen.Seen[0].Headers.UserAgent.ToString());
        Assert.Equal([("core", "4000", "1799999999", "5000")], reports);
        // a request that has its own agent keeps it; an answer with no rate limit reports nothing
        using var own = new HttpRequestMessage(HttpMethod.Get, "y"); own.Headers.UserAgent.ParseAdd("other/1");
        seen.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);
        await http.SendAsync(own);
        Assert.Equal("other/1", seen.Seen[1].Headers.UserAgent.ToString());
        Assert.Single(reports);
    }

    // ---- Octopus ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Octopus_IsReadWithTheKeyOfTheMoment_ANotFoundIsNull_AnythingElseIsExit20()
    {
        var h = new Scripted { Answer = (_, _) => Json(HttpStatusCode.OK, """{"Id":"Projects-1"}""") };
        string? key = null;
        var api = new HttpOctopusApi(new HttpClient(h), "https://octo.example/", k => k == "OCTOPUS_API_KEY" ? key : null);
        var none = await Assert.ThrowsAsync<VerbRefusal>(() => api.Get("api/x", default));
        Assert.Equal((30, "secret-missing", "OCTOPUS_API_KEY is not set on the gateway"), (none.Exit, none.Code, none.Message));
        key = "API-1";
        Assert.Equal("Projects-1", Ps.Str(await api.Get("api/Spaces-1/projects/p", default), "Id"));
        Assert.Equal(("https://octo.example/api/Spaces-1/projects/p", "API-1"), (h.Seen[0].RequestUri!.ToString(), h.Seen[0].Headers.GetValues("X-Octopus-ApiKey").Single()));
        h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await api.Get("api/missing", default));
        h.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => api.Get("api/users/me", default));
        Assert.Equal((20, "octopus-error", "Octopus answered 401 to GET api/users/me", 30), (e.Exit, e.Code, e.Message, e.Extra["retryAfterSeconds"]!.GetValue<int>()));
        h.Answer = (_, _) => throw new HttpRequestException("down");
        Assert.Equal("Octopus answered 0 to GET api/z", (await Assert.ThrowsAsync<VerbRefusal>(() => api.Get("api/z", default))).Message);
        Assert.DoesNotContain("API-1", e.Message + e.Extra.ToJsonString());
    }

    // ---- the state store -------------------------------------------------------------------------------------------------------------------------

    sealed class Journal : IJournalStore
    {
        public ItemState Held = new() { CommentId = 5, State = new JsonObject { ["schema"] = 1, ["item"] = 7 }, Rev = 3 };
        public int Saves, Reads, ConflictsLeft; public Exception? OnSave;
        public Task<ItemState> Read(int item, CancellationToken ct) { Reads++; return Task.FromResult(Held); }
        public Task Save(int item, ItemState holder, CancellationToken ct)
        {
            if (ConflictsLeft-- > 0) throw new StateConflictException("the state of item 7 changed since it was read (revision 3, now 4); read it again and retry");
            if (OnSave is not null) throw OnSave;
            Saves++; return Task.CompletedTask;
        }
        public Task<long?> Post(int item, string body, CancellationToken ct) => Task.FromResult<long?>(1);
        public Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => Task.FromResult(PatchOutcome.Ok);
    }

    [Fact]
    public async Task TheStateStore_SavesWhatTheMutationChanged_AndTurnsAMovedRevisionIntoTheScriptsStateConflict()
    {
        var j = new Journal();
        var store = new JournalStateStore(() => j);
        Assert.Equal(7, Ps.ToInt((await store.Read(7, default))["item"]));
        Assert.False(await store.Update(7, _ => Task.FromResult(false), default)); Assert.Equal(0, j.Saves);
        Assert.True(await store.Update(7, s => { s["x"] = 1; return Task.FromResult(true); }, default)); Assert.Equal(1, j.Saves);
        Assert.Equal(1, Ps.ToInt(j.Held.State["x"]));
        j.OnSave = new StateConflictException("the state of item 7 changed since it was read (revision 3, now 4); read it again and retry");
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => store.Update(7, _ => Task.FromResult(true), default));
        Assert.Equal((11, "state-conflict", "the state of item 7 changed since it was read (revision 3, now 4); read it again and retry"), (e.Exit, e.Code, e.Message));
        Assert.Equal((7, 3, 4, 1), (e.Extra["item"]!.GetValue<int>(), e.Extra["readRev"]!.GetValue<int>(), e.Extra["currentRev"]!.GetValue<int>(), e.Extra["retryAfterSeconds"]!.GetValue<int>()));
        j.OnSave = new InvalidOperationException("other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Update(7, _ => Task.FromResult(true), default));
    }

    [Fact]
    public async Task TheStateStore_ReadsAgainAndAppliesAgain_AsManyTimesAsItIsAskedTo_AsUpdateStateDoes()
    {
        var j = new Journal { ConflictsLeft = 2 };
        var store = new JournalStateStore(() => j);
        var applied = 0;
        Assert.True(await store.Update(7, _ => { applied++; return Task.FromResult(true); }, default, 3));
        Assert.Equal((3, 3, 1), (applied, j.Reads, j.Saves));   // two conflicts, then the save
        j = new Journal { ConflictsLeft = 3 };
        store = new JournalStateStore(() => j);
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => store.Update(7, _ => Task.FromResult(true), default, 3));
        Assert.Equal((11, "state-conflict", 3, 0), (e.Exit, e.Code, j.Reads, j.Saves));   // three reads in all, then the conflict
    }

    // ---- the files of the rule scripts' state directory -------------------------------------------------------------------------------------------

    [Fact]
    public void TheStateDirectory_IsWhereThePathsScriptPutsIt()
    {
        Assert.Equal("/s", RuleStateDirectory.Resolve(k => k == "FACTORY_STATE_DIR" ? "/s" : "/d"));
        Assert.Equal(Path.Combine("/data", "state"), RuleStateDirectory.Resolve(k => k == "FACTORY_DATA_DIR" ? "/data" : null));
        Assert.Equal(Path.Combine(dir, ".local", "share", "factory-worker", "state"), RuleStateDirectory.Resolve(k => k == "HOME" ? dir : null));
        Assert.True(Directory.Exists(Path.Combine(dir, ".local", "share", "factory-worker")));
    }

    [Fact]
    public async Task TheItemLock_IsTheFileTheRuleScriptsLock_OneAtATimePerItem_ReleasedAfterTheBody()
    {
        var state = Path.Combine(dir, "state");
        var waits = new List<TimeSpan>();
        var l = new FileItemLock(state, async (t, ct) => { waits.Add(t); await Task.Delay(400, ct); });
        var inner = "";
        var result = await l.Use(7, async () =>
        {
            Assert.True(File.Exists(Path.Combine(state, "item-7.lock")));
            // the same item again cannot get in: busy after the time it may wait
            var busy = await Assert.ThrowsAsync<ItemBusyException>(() => l.Use(7, () => Task.FromResult(1), 1, default));
            Assert.Equal((7, "another operation on item 7 is running"), (busy.Item, busy.Message));
            Assert.NotEmpty(waits); Assert.All(waits, w => Assert.Equal(TimeSpan.FromMilliseconds(300), w));
            // another item is free
            inner = await l.Use(8, () => Task.FromResult("eight"), default);
            return "seven";
        }, default);
        Assert.Equal(("seven", "eight"), (result, inner));
        Assert.Equal(1, await l.Use(7, () => Task.FromResult(1), default));   // released
        // a body that fails releases it as well
        await Assert.ThrowsAsync<InvalidOperationException>(() => l.Use<int>(7, () => throw new InvalidOperationException(), default));
        Assert.Equal(2, await l.Use(7, () => Task.FromResult(2), default));
    }

    [Fact]
    public void TheRefusalCounts_AreReadFromTheFileOfTheStateDirectory_NoneWhenItIsMissingOrBroken()
    {
        var counts = new RefusalFile(dir);
        Assert.Equal(0, counts.For(12));
        File.WriteAllText(Path.Combine(dir, "refusals.json"), """{"12":3,"13":6}""");
        Assert.Equal((3, 6, 0), (counts.For(12), counts.For(13), counts.For(14)));
        File.WriteAllText(Path.Combine(dir, "refusals.json"), "{ not json");
        Assert.Equal(0, counts.For(12));
        File.WriteAllText(Path.Combine(dir, "refusals.json"), """["x"]""");
        Assert.Equal(0, counts.For(12));
    }

    // ---- the handlers ----------------------------------------------------------------------------------------------------------------------------

    ReadModelParts PartsWithoutGitHub()
    {
        var root = Path.Combine(FakeGitHubRig.Root(), "config", "instance.json");
        return ReadModelParts.Build(InstanceConfig.Load(root, _ => null), null, k => k == "FACTORY_STATE_DIR" ? Path.Combine(dir, "state") : null);
    }

    [Fact]
    public async Task AVerbOfAGatewayWithoutAGitHubIdentity_AnswersTheRefusalTheRuleScriptsGive()
    {
        var h = new ReadModelHandlers(PartsWithoutGitHub());
        var a = await h.Run("pr-checks", """{"item":7}""", TimeSpan.FromSeconds(5), (args, ct) => h.Parts.Checks.PrChecks(args, ct));
        Assert.Equal(30, a.Code);
        Assert.Equal("""{"ok":false,"verb":"pr-checks","code":"secret-missing","reason":"GH_TOKEN is not set on the gateway","exit":30}""", a.Output);
        var octopus = await h.Run("get-delivery", """{"commit":"abc"}""", TimeSpan.FromSeconds(5), (args, ct) => h.Parts.Delivery.GetDelivery(args, ct));
        Assert.Equal(30, octopus.Code);   // (the project is read first, and there is no key)
        Assert.Contains("OCTOPUS_API_KEY is not set on the gateway", octopus.Output);
    }

    [Fact]
    public async Task TheHandler_TakesTheGatewaysFieldsOutOfTheArguments_AndAnswersATimeoutAsTheRunnerDoes()
    {
        var h = new ReadModelHandlers(PartsWithoutGitHub());
        JsonObject? seen = null;
        var ok = await h.Run("pr-checks", """{"item":7,"_session":"s","_cause":"c","_lease":{"epoch":1},"check":"x"}""", TimeSpan.FromSeconds(5), (a, _) => { seen = (JsonObject)a.Body.DeepClone(); return Task.FromResult(new JsonObject { ["x"] = 1 }); });
        Assert.Equal((0, """{"ok":true,"x":1,"verb":"pr-checks","exit":0}"""), (ok.Code, ok.Output));
        Assert.Equal(["item", "check"], seen!.Select(kv => kv.Key));
        var bad = await h.Run("pr-checks", "{ not json", TimeSpan.FromSeconds(5), (_, _) => Task.FromResult(new JsonObject()));
        Assert.Equal(20, bad.Code); Assert.Contains("\"code\":\"internal-error\"", bad.Output); Assert.Contains("\"retryAfterSeconds\":30", bad.Output);
        var slow = await h.Run("wait-for-checks", "{}", TimeSpan.FromMilliseconds(50), async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new JsonObject(); });
        Assert.Equal((20, """{"ok":false,"verb":"wait-for-checks","code":"timeout","reason":"the rule script did not finish in time","retryAfterSeconds":30,"exit":20}"""), (slow.Code, slow.Output));
        var refused = await h.Run("model-tier", "{}", TimeSpan.FromSeconds(5), (a, _) => { a.Need("item"); return Task.FromResult(new JsonObject()); });
        Assert.Equal((2, """{"ok":false,"verb":"model-tier","code":"usage","reason":"missing argument: item","exit":2}"""), (refused.Code, refused.Output));
    }

    // ---- the registration ------------------------------------------------------------------------------------------------------------------------

    static EngineOptions Engine(string dir, string environment, string v2, string? off = null)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EngineOptionsTests.BlockFor(v2, ""));
        Func<string, string?> env = n => n == "FACTORY_V2_OFF" ? off : null;
        return EngineOptions.From(InstanceConfig.Load(path, env), environment, env);
    }

    static ServiceProvider Gateway()
    {
        var s = new ServiceCollection();
        s.AddSingleton(InstanceConfig.Load(Path.Combine(FakeGitHubRig.Root(), "config", "instance.json"), _ => null));
        s.AddSingleton(new GitHubBudget(300));
        return s.BuildServiceProvider();
    }

    sealed class Below : IVerbRouter
    {
        public readonly List<string> Verbs = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Verbs.Add(verb); return Task.FromResult(new VerbAnswer(0, "below:" + verb)); }
    }

    [Fact]
    public async Task WhereTheEngineActs_TheSixVerbsAreV2s_EveryOtherVerbStaysBelow()
    {
        var routes = V2VerbRoutes.Build(Engine(dir, "prod", "\"prod\""), Gateway());
        // (the merge verbs register without the configuration: they find their parts at the first call, W9.3; the other groups need credentials this table does not have)
        // (and the delivery verbs, W9.4: the host is built lazily; auto-promote is the train's executor, not a route)
        Assert.Equal(ReadModelRoutes.Verbs.Concat(MergeRoutes.Verbs).Concat(DeliveryRoutes.Verbs.Where(v => v != "auto-promote")).Order(), routes.Verbs.Order());
        var below = new Below();
        var router = new V2VerbRouter(below, routes);
        // check-secrets answers from v2 (the probes fail without an identity); get-item is not v2's
        var secrets = await router.RunAsync("check-secrets", "{}", TimeSpan.FromSeconds(10));
        Assert.Equal(30, secrets.Code);
        Assert.Contains("\"verb\":\"check-secrets\"", secrets.Output); Assert.Contains("\"code\":\"secrets-check-failed\"", secrets.Output);
        Assert.Equal(new VerbAnswer(0, "below:get-item"), await router.RunAsync("get-item", "{}", TimeSpan.FromSeconds(1)));
        Assert.Equal(["get-item"], below.Verbs);
        var tier = await router.RunAsync("model-tier", "{}", TimeSpan.FromSeconds(10));
        Assert.Equal(2, tier.Code); Assert.Contains("missing argument: item", tier.Output);
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_NothingIsRegistered_AndATableWithoutTheConfigurationRegistersNothing()
    {
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine(dir, "tdd", "\"prod\""), Gateway()));          // v1 acts
        Assert.Same(V2VerbRoutes.None, V2VerbRoutes.Build(Engine(dir, "prod", "\"prod\"", "1"), Gateway()));    // the kill switch
        var bare = V2VerbRoutes.Build(Engine(dir, "prod", "\"prod\""), new ServiceCollection().BuildServiceProvider());
        Assert.All(ReadModelRoutes.Verbs, v => Assert.False(bare.TryGet(v, out _), v));   // (the merge verbs of W9.3 register anyway and find their parts at the first call)
    }

    [Fact]
    public void TheRouteFileIsTheGroupsOwn_AndRegistersEachOfItsVerbsOnce()
    {
        var routes = new V2VerbRoutes();
        ReadModelRoutes.Register(routes, new ReadModelHandlers(PartsWithoutGitHub()));
        Assert.Equal(6, routes.Count);
        Assert.Throws<InvalidOperationException>(() => ReadModelRoutes.Register(routes, new ReadModelHandlers(PartsWithoutGitHub())));
        Assert.True(routes.TryGet("wait-for-checks", out _)); Assert.False(routes.TryGet("merge", out _));
    }
}
