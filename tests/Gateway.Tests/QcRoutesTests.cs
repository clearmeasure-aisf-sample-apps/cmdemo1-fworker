using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Database;
using Fworker.Workflows.Qc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.1: the QC verbs where the engine acts (gateway/Verbs/Routes/Qc.cs): registered with the QcService when there is one, answered by it, and the way QcHub (Qc.cs) reaches them for the persisted half of
/// Start, Reset, Teardown and the page; the index of live items that v1's sweep reads, and the table that waits for a good schema.
/// </summary>
public sealed class QcRoutesTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("qc-routes-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    sealed class Quiet : IQcItems, IQcGitHub, IQcFacts
    {
        public Func<CancellationToken, Task>? OnBoard;
        public Task<QcItemInfo> GetInfo(int item, CancellationToken ct) => throw new QcReject(12, "item-not-found", "no");
        public Task<QcItemState> ReadState(int item, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveState(int item, QcItemState state, CancellationToken ct) => Task.CompletedTask;
        public Task Journal(int item, string ev, JsonObject fields, string key, CancellationToken ct) => Task.CompletedTask;
        public async Task<IReadOnlyList<QcBoardItem>> Board(CancellationToken ct) { if (OnBoard is { } f) await f(ct); return []; }
        public Task<JsonObject?> GetPull(int number, CancellationToken ct) => Task.FromResult<JsonObject?>(null);
        public Task<IReadOnlyList<JsonObject>> OpenPulls(CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonObject>>([]);
        public Task<IReadOnlyList<string>> PullFiles(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<JsonObject>> Reviews(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonObject>>([]);
        public Task<IReadOnlyList<QcComment>> Comments(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<QcComment>>([]);
        public Task PostComment(int number, string body, CancellationToken ct) => Task.CompletedTask;
        public Task PatchComment(long commentId, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<JsonObject> MarkReady(JsonObject pull, CancellationToken ct) => Task.FromResult(pull);
        public Task<string> ServerLogin(CancellationToken ct) => Task.FromResult("bot");
        public Task<IReadOnlyList<string>> MandatoryCodes(int item, int pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static QcService Service(Quiet q) => new(QcConfig.From(JsonNode.Parse("{\"owner\":\"o\",\"repo\":\"r\",\"label\":\"l\",\"columns\":{\"validation\":\"V\"}}"), _ => null), q, q, q, new InMemoryQcIndex());

    static IServiceProvider Provider(QcService? qc)
    {
        var s = new ServiceCollection();
        if (qc is not null) s.AddSingleton(qc);
        return s.BuildServiceProvider();
    }

    // ---- the routes -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAServiceThereAreNoRoutes_TheVerbsStayOnV1()
    {
        var routes = new V2VerbRoutes();
        QcRoutes.Register(routes, Provider(null));
        Assert.Equal(0, routes.Count);
    }

    [Fact]
    public void TheServiceRegistersTheFourteenQcVerbs_AndOnlyThose()
    {
        var routes = new V2VerbRoutes();
        QcRoutes.Register(routes, Provider(Service(new Quiet())));
        Assert.Equal(14, routes.Count);
        Assert.Equal(QcService.Verbs.OrderBy(x => x), routes.Verbs.OrderBy(x => x));
        Assert.All(routes.Verbs, v => Assert.StartsWith("qc-", v));
        // the verbs of the groups of W9.0 are in none of the QC verbs
        Assert.Empty(V2VerbRoutes.Groups.SelectMany(g => g.Verbs).Intersect(routes.Verbs));
    }

    [Fact]
    public void TheServicesVerbsAreTheQcServiceVerbsOfTheInventory()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "design", "verb-inventory.json")));
        var inventory = doc.RootElement.GetProperty("verbs").EnumerateArray().Where(e => e.GetProperty("owner").GetString() == "QcService").Select(e => e.GetProperty("verb").GetString()!).OrderBy(x => x).ToList();
        Assert.Equal(14, inventory.Count);
        Assert.Equal(inventory, QcService.Verbs.OrderBy(x => x));
        // the verbs the script runs without the item lock (NoLockVerbs of factory.ps1) are those the inventory flags
        var noLock = doc.RootElement.GetProperty("verbs").EnumerateArray().Where(e => e.GetProperty("owner").GetString() == "QcService" && e.GetProperty("noLock").GetBoolean()).Select(e => e.GetProperty("verb").GetString()!).OrderBy(x => x).ToList();
        Assert.Equal(noLock, QcService.NoLockVerbs.OrderBy(x => x));
    }

    [Fact]
    public void TheQcVerbsAreRegisteredOnlyWhereTheEngineActs()
    {
        // V2VerbRoutes.Build calls QcRoutes.Register after the groups, and only for an acting engine (the table is the shared empty one otherwise)
        var source = File.ReadAllText(Path.Combine(Root(), "gateway", "Verbs", "V2VerbRoutes.cs"));
        Assert.True(source.IndexOf("if (!engine.V2Acts) return None;", StringComparison.Ordinal) < source.IndexOf("QcRoutes.Register(routes, services);", StringComparison.Ordinal));
        var modules = File.ReadAllText(Path.Combine(Root(), "gateway", "Endpoints", "EndpointModules.cs"));
        Assert.Contains("if (c.Engine.V2Acts) services.AddSingleton(sp => QcWiring.Make(c, sp)!);", modules);
    }

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found");
    }

    [Fact]
    public async Task ARegisteredVerbIsAnsweredByTheService()
    {
        var routes = new V2VerbRoutes();
        QcRoutes.Register(routes, Provider(Service(new Quiet())));
        Assert.True(routes.TryGet("qc-list", out var handler));
        var a = await handler("{}", TimeSpan.FromSeconds(5));
        Assert.Equal(0, a.Code);
        var o = JsonNode.Parse(a.Output)!;
        Assert.Equal(("qc-list", true, 0), (o["verb"]!.ToString(), o["ok"]!.GetValue<bool>(), o["exit"]!.GetValue<int>()));
        Assert.Empty((JsonArray)o["available"]!);
        // a refusal of the service is the answer with its exit code
        Assert.True(routes.TryGet("qc-view", out var view));
        var bad = await view("{\"item\":1}", TimeSpan.FromSeconds(5));
        Assert.Equal(2, bad.Code);
        Assert.Equal("usage", JsonNode.Parse(bad.Output)!["code"]!.ToString());
    }

    [Fact]
    public async Task AVerbThatTakesLongerThanItsTimeIsTheTimeoutAnswerOfTheRuleScript()
    {
        var q = new Quiet { OnBoard = ct => Task.Delay(Timeout.Infinite, ct) };
        var routes = new V2VerbRoutes();
        QcRoutes.Register(routes, Provider(Service(q)));
        Assert.True(routes.TryGet("qc-list", out var handler));
        var a = await handler("{}", TimeSpan.FromMilliseconds(200));
        Assert.Equal(20, a.Code);
        var o = JsonNode.Parse(a.Output)!;
        Assert.Equal(("timeout", false, 30, 20), (o["code"]!.ToString(), o["ok"]!.GetValue<bool>(), o["retryAfterSeconds"]!.GetValue<int>(), o["exit"]!.GetValue<int>()));
    }

    [Fact]
    public async Task ARouteSitsInFrontOfTheRouterBelow_AndOtherVerbsFallThrough()
    {
        var routes = new V2VerbRoutes();
        QcRoutes.Register(routes, Provider(Service(new Quiet())));
        var router = new V2VerbRouter(new Below(), routes);
        Assert.Equal("qc-list", JsonNode.Parse((await router.RunAsync("qc-list", "{}", TimeSpan.FromSeconds(5))).Output)!["verb"]!.ToString());
        Assert.Equal(new VerbAnswer(0, "v1:accept"), await router.RunAsync("accept", "{}", TimeSpan.FromSeconds(1)));
        Assert.Equal(new VerbAnswer(0, "v1:qc-heartbeat"), await router.RunAsync("qc-heartbeat", "{}", TimeSpan.FromSeconds(1)));   // (the gateway's own verb, not the service's)
    }

    sealed class Below : IVerbRouter
    {
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) => Task.FromResult(new VerbAnswer(0, "v1:" + verb));
    }

    // ---- QcHub reaches the routes ---------------------------------------------------------------------------------------------------------------------

    // (W9.9a: the hub calls the Runner, which asks the routes first; there is no table of the hub's own)
    static QcHub Hub(Runner runner) =>
        new(runner, new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30), new EventHub(), new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));

    [Fact]
    public async Task WhereARouteExistsTheHubsPersistedHalfIsTheServicesAnswer()
    {
        var routes = new V2VerbRoutes();
        var seen = new List<(string Verb, string Stdin)>();
        routes.Add(QcRoutes.Verbs, "qc-teardown", (stdin, _) => { seen.Add(("qc-teardown", stdin)); return Task.FromResult(new VerbAnswer(0, "{\"ok\":true,\"verb\":\"qc-teardown\",\"outcome\":\"already\",\"pr\":9,\"exit\":0}")); });
        routes.Add(QcRoutes.Verbs, "qc-reset", (stdin, _) => { seen.Add(("qc-reset", stdin)); return Task.FromResult(new VerbAnswer(12, "{\"ok\":false,\"verb\":\"qc-reset\",\"code\":\"no-environment\",\"reason\":\"none\",\"exit\":12}")); });
        var hub = Hub(new Runner("/nonexistent/factory.ps1") { Routes = routes });
        var (code, o) = await hub.DoTeardown(new JsonObject { ["item"] = 5, ["pr"] = 9, ["reason"] = "person" });
        Assert.Equal((0, "already"), (code, o["code"]!.ToString()));
        var (rc, ro) = await hub.DoReset(new JsonObject { ["item"] = 5, ["pr"] = 9 }, "operator");
        Assert.Equal((12, "no-environment"), (rc, ro["code"]!.ToString()));
        Assert.Equal(["qc-teardown", "qc-reset"], seen.Select(x => x.Verb));
        Assert.Equal(5, JsonNode.Parse(seen[0].Stdin)!["item"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheRoutesAreLookedUpAtEachCall_SoAHubBuiltBeforeThemFindsThem()
    {
        var runner = new Runner("/nonexistent/factory.ps1");
        var hub = Hub(runner);
        var routes = new V2VerbRoutes();
        runner.Routes = routes;
        routes.Add(QcRoutes.Verbs, "qc-teardown", (_, _) => Task.FromResult(new VerbAnswer(0, "{\"ok\":true,\"outcome\":\"already\",\"pr\":9,\"exit\":0}")));
        var (code, o) = await hub.DoTeardown(new JsonObject { ["item"] = 5, ["pr"] = 9 });
        Assert.Equal((0, "already"), (code, o["code"]!.ToString()));
    }

    // ---- the index v1's sweep reads -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheIndexIsTheJsonArrayOfQcItemsJsonInTheStateDirectory()
    {
        var index = new FileQcIndex(n => n == "FACTORY_STATE_DIR" ? dir : null);
        Assert.Empty(index.Items());
        index.Update(5, true); index.Update(7, true); index.Update(5, true);
        Assert.Equal("[7,5]", File.ReadAllText(Path.Combine(dir, "qc-items.json")));    // (an item is moved to the end, as Update-QcIndex does)
        Assert.Equal([7, 5], index.Items());
        index.Update(7, false); index.Update(99, false);
        Assert.Equal("[5]", File.ReadAllText(Path.Combine(dir, "qc-items.json")));
        index.Update(5, false);
        Assert.Equal("[]", File.ReadAllText(Path.Combine(dir, "qc-items.json")));
    }

    [Fact]
    public void TheIndexTheScriptWroteIsRead_AndAFileThatIsNoIndexIsNone()
    {
        File.WriteAllText(Path.Combine(dir, "qc-items.json"), "[3,12]");
        var index = new FileQcIndex(n => n == "FACTORY_STATE_DIR" ? dir : null);
        Assert.Equal([3, 12], index.Items());
        File.WriteAllText(Path.Combine(dir, "qc-items.json"), "{ nope");
        Assert.Empty(index.Items());
        File.WriteAllText(Path.Combine(dir, "qc-items.json"), "5");      // (a single number: the script reads it as a list of one)
        Assert.Equal([5], index.Items());
    }

    [Fact]
    public void TheStateDirectoryIsTheScriptsOwn_TheStateDirOverridesTheDataDirectoryWhichOverridesTheHome()
    {
        var data = Path.Combine(dir, "data");
        var viaData = new FileQcIndex(n => n == "FACTORY_DATA_DIR" ? data : null);
        viaData.Update(1, true);
        Assert.True(File.Exists(Path.Combine(data, "state", "qc-items.json")));
        var home = Path.Combine(dir, "home"); Directory.CreateDirectory(home);
        var viaHome = new FileQcIndex(n => n == "HOME" ? home : null);
        viaHome.Update(2, true);
        Assert.True(File.Exists(Path.Combine(home, ".local/share/factory-worker/state/qc-items.json")));
        var viaState = new FileQcIndex(n => n switch { "FACTORY_STATE_DIR" => Path.Combine(dir, "s"), "FACTORY_DATA_DIR" => data, _ => null });
        viaState.Update(3, true);
        Assert.True(File.Exists(Path.Combine(dir, "s", "qc-items.json")));
        Assert.Equal([1], viaData.Items());
    }

    // ---- the table waits for a good schema ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTableIsNotReadOrWrittenBeforeTheSchemaIsGood()
    {
        var schema = new FactorySchemaStatus(required: true);
        var inner = new InMemoryQcEnvironmentStore();
        IQcEnvironmentStore gated = new SchemaGatedQcStore(schema, inner);
        var record = new JsonObject { ["state"] = "requested", ["rev"] = 1 };
        await gated.Save("o/r!9", "o/r", "o/r#5", record, default);
        Assert.Empty(inner.Rows);
        Assert.Null(await gated.Load("o/r!9", default));
        Assert.Empty(await gated.Live("o/r", default));
        schema.Good(new SchemaReport { Verified = true });
        await gated.Save("o/r!9", "o/r", "o/r#5", record, default);
        Assert.Equal("requested", (await gated.Load("o/r!9", default))!.Status);
        Assert.Single(await gated.Live("o/r", default));
    }
}
