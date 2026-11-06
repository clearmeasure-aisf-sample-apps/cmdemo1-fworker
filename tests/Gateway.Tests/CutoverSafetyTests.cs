using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The three safety problems the review of the tdd cutover (W9.9c) found:
/// 1. an environment outside engine.v2Environments must not turn v1's intake on (intake.enabled is true for the image so that the engine's intake runs, and v1's intake leaves nothing in Todo once it is not told the engine's plans);
/// 2. tdd with v2 configured and no database must stay up and inert, not crash and not fall back to v1;
/// 3. v1's sweep must not run beside the adoption of the term (no per-item lock between them).
/// </summary>
public sealed class CutoverSafetyTests
{
    static readonly RepoId Home = new("acme", "orders");

    static List<JsonObject> SyncBoardCalls(GatewayEnvironment env)
    {
        var log = env.StubLog + ".in";
        if (!File.Exists(log)) return [];
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2 && p[0] == "sync-board").Select(p => (JsonObject)JsonNode.Parse(p[1])!).ToList();
    }

    // The configuration of the cutover: v2 for tdd, v1's intake switched on for the image and told which plans the engine takes.
    static void Cutover(GatewayEnvironment env, bool database, string environment = "tdd", string? connection = null)
    {
        env.Set("FACTORY_ENVIRONMENT", environment);
        env.Set("FACTORY_SWEEP_SECONDS", "1"); env.Set("FACTORY_SWEEP_MIN_SECONDS", "1");
        if (database) env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        else if (connection is not null) env.Set("FACTORY_DB_CONNECTION", connection);
        else env.Set("FACTORY_DB_CONNECTION", null);
        env.EditConfig = cfg =>
        {
            cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd") };
            cfg["intake"] = new JsonObject { ["enabled"] = true, ["v2Plans"] = new JsonArray("docs", "feature") };
        };
    }

    static void WriteAnswer(GatewayEnvironment env) =>
        File.WriteAllText(Path.Combine(env.Dir, "answer-sync-board.json"), new JsonObject { ["ok"] = true, ["actions"] = new JsonArray(), ["offers"] = new JsonArray() }.ToJsonString() + "\n");

    static async Task<List<JsonObject>> WaitForSweeps(GatewayEnvironment env, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds) && SyncBoardCalls(env).Count == 0) await Task.Delay(200);
        return SyncBoardCalls(env);
    }

    // ---- 1. an environment outside v2 and v1's intake ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("uat", "uat: the image has intake.enabled true and no engine acts")]
    [InlineData("prod", "prod: the same")]
    public async Task WhereTheEngineDoesNotAct_NothingSweepsAndNothingIsPulled(string environment, string why)
    {
        _ = why;
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        Cutover(env, database: true, environment: environment);
        env.Set("FACTORY_TEST_STUB_SCRIPTS", null);   // (the real thing: an environment v2Environments does not name is inert)
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        _ = host.Server;
        var sweeper = host.Services.GetServices<IHostedService>().OfType<Sweeper>().Single();
        Assert.Null(sweeper.V2);          // (no engine acts here; v1's sweep is gone, V3.2)
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Empty(SyncBoardCalls(env));
    }

    // V3.3: what an inert environment answers. An environment v2Environments does not name (fworker's uat and prod) runs no rule script for a caller and acts on nothing: every verb is refused "inert"
    // (exit 20, HTTP 503), the loops that act on items may not act, /ready answers 503 "engine" for good, /health and /_build answer 200, the operator's status says inert. Only its housekeeping reaches a script.
    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    public async Task AnEnvironmentTheEngineDoesNotName_IsInert_VerbsRefused_Ready503Engine_Health200(string environment)
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        Cutover(env, database: false, environment: environment);
        env.Set("FACTORY_TEST_STUB_SCRIPTS", null);
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway did not start");
        var services = host.Services.GetServices<IHostedService>();
        Assert.False(services.OfType<PlantTicker>().Single().MayAct!());
        Assert.False(services.OfType<BaseWatch>().Single().MayAct!());
        Assert.IsType<InertVerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        foreach (var verb in new[] { "get-item", "claim-item", "merge", "complete", "qc-request", "sync-board", "my-items" })
        {
            var a = await host.SendAsync("POST", $"/api/v1/verbs/{verb}", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken, "{\"item\":7}");
            Assert.Equal((503, "inert", 20), (a.Status, a.Code, (int?)a.Json?["exit"] ?? -1));
        }
        Assert.Equal(200, (await host.SendAsync("GET", "/health", GatewayEnvironment.ApiPort)).Status);
        Assert.Equal(200, (await host.SendAsync("GET", "/_build", GatewayEnvironment.ApiPort)).Status);
        var ready = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort);
        Assert.Equal((503, "engine", environment), (ready.Status, ready.Json?["waitingFor"]?.ToString(), ready.Json?["environment"]?.ToString()));
        var status = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, status.Status);
        Assert.True(status.Json?["engine"]?["inert"]?.GetValue<bool>());
        Assert.Equal("engine", status.Json?["engine"]?["waitingFor"]?.ToString());
        await Task.Delay(TimeSpan.FromSeconds(3));
        // nothing reached the rule script but the gateway's own housekeeping
        Assert.Empty(StubPwsh.Read(env.StubLog).Where(c => !InertVerbs.Housekeeping.Contains(c.Verb)));
    }

    [Fact]
    public void TheTestStandIn_IsRefusedWithoutTheTestHooks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"instance-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"messaging\":{\"testHooks\":false}}");
        try
        {
            var cfg = InstanceConfig.Load(path, _ => null);
            Assert.Throws<InvalidOperationException>(() => EngineOptions.From(cfg, "uat", n => n == "FACTORY_TEST_STUB_SCRIPTS" ? "1" : null));
            Assert.True(EngineOptions.From(cfg, "uat", _ => null).Inert);
        }
        finally { File.Delete(path); }
    }

    // ---- 2. v2 configured, no database ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("this is not a connection string")]
    public async Task V2WithoutADatabase_Starts_StaysInert_Health200_Ready503Database_AndSweepsNothing(string? connection)
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        Cutover(env, database: false, connection: connection);
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway did not start");   // (it used to throw: V2Ownership was asked for and never registered)
        var services = host.Services.GetServices<IHostedService>();
        var sweeper = services.OfType<Sweeper>().Single();
        Assert.True(sweeper.V2!.Unavailable); Assert.False(sweeper.V2.Loaded);
        // nothing that acts on items acts (the lease is off in this test, so MayAct of the leader is true: only the inert rule says no), and a verb that changes anything is refused
        Assert.False(services.OfType<PlantTicker>().Single().MayAct!());
        Assert.False(services.OfType<BaseWatch>().Single().MayAct!());
        Assert.True(host.Services.GetRequiredService<ILeadership>().MayAct); Assert.False(new InertLeadership(host.Services.GetRequiredService<ILeadership>()).MayAct);
        var router = host.Services.GetRequiredService<IVerbRouter>();
        Assert.IsType<InertVerbRouter>(router);
        foreach (var verb in new[] { "claim-item", "merge", "move-next", "complete", "park-item", "qc-request", "sync-board" })
        {
            var refused = await router.RunAsync(verb, "{\"item\":7}", TimeSpan.FromSeconds(5));
            Assert.Equal(20, refused.Code);
            Assert.Equal("database", JsonNode.Parse(refused.Output)!["code"]!.ToString());
        }
        Assert.Empty(SyncBoardCalls(env));
        Assert.DoesNotContain(host.Captured!, d => d.ServiceType == typeof(WolverineHostService));

        Assert.Equal(200, (await host.SendAsync("GET", "/health", GatewayEnvironment.ApiPort)).Status);
        Answer ready = null!;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30)) { ready = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort); if (ready.Json?["waitingFor"]?.ToString() == "database") break; await Task.Delay(100); }
        Assert.Equal(503, ready.Status);
        Assert.Equal("database", ready.Json?["waitingFor"]?.ToString());
        Assert.DoesNotContain("Server=", ready.Body);

        var status = await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, status.Status);
        Assert.True(status.Json?["engine"]?["inert"]?.GetValue<bool>());
        Assert.Equal("v2", status.Json?["engine"]?["configured"]?.ToString());

        await Task.Delay(TimeSpan.FromSeconds(8));   // (the sweep interval is 1 s: a v1 that took over would have swept by now)
        Assert.Empty(SyncBoardCalls(env));
    }

    [Fact]
    public void TheItemsTheAdoptionCouldNotFence_AreNamedByTheGate()
    {
        var gate = new AdoptionGate(() => true, _ => true, () => true, () => 1, excluded: () => new HashSet<int> { 8 });
        Assert.Equal([8], gate.Excluded().ToArray());
    }

    static EngineOptions NoDatabaseEngine() => new() { Environment = "tdd", Configured = EngineMode.V2, NoDatabase = true };
    static EngineOptions UnnamedEngine() => new() { Environment = "uat", Configured = EngineMode.V1 };

    [Fact]
    public void AnEnvironmentTheEngineDoesNotAct_In_IsInert_AndSaysWhy()
    {
        Assert.True(NoDatabaseEngine().Inert); Assert.Equal("database", NoDatabaseEngine().InertReason);
        Assert.True(UnnamedEngine().Inert); Assert.Equal("engine", UnnamedEngine().InertReason);
        var acting = new EngineOptions { Environment = "tdd", Configured = EngineMode.V2 };
        Assert.False(acting.Inert); Assert.Null(acting.InertReason);
        Assert.Equal("engine", UnnamedEngine().Status()["waitingFor"]!.ToString());
        Assert.True(UnnamedEngine().Reported);
        Assert.False(new EngineOptions { Environment = "uat", Configured = EngineMode.V1, TestScripts = true }.Inert);   // (the unit tests' stand-in only)
    }

    // V3.3: an inert gateway refuses every verb, whatever it does: there is no v1 to run in v2's place
    [Theory]
    [InlineData("get-item")] [InlineData("my-items")] [InlineData("claim-item")] [InlineData("merge")] [InlineData("complete")] [InlineData("approve-pr")] [InlineData("qc-request")] [InlineData("revocations")] [InlineData("transcript-load")] [InlineData("sync-board")]
    public async Task TheInertRouter_RefusesEveryVerb_DatabaseWhereV2IsNamed_InertWhereItIsNot(string verb)
    {
        var a = await new InertVerbRouter(NoDatabaseEngine()).RunAsync(verb, "{}", TimeSpan.FromSeconds(1));
        Assert.Equal((20, "database"), (a.Code, JsonNode.Parse(a.Output)!["code"]!.ToString()));
        var u = await new InertVerbRouter(UnnamedEngine()).RunAsync(verb, "{}", TimeSpan.FromSeconds(1));
        var o = JsonNode.Parse(u.Output)!;
        Assert.Equal((20, "inert", false, verb), (u.Code, o["code"]!.ToString(), (bool)o["ok"]!, o["verb"]!.ToString()));
        Assert.Contains("'uat'", o["reason"]!.ToString());
    }

    // The choke point: every call of the rule script, whoever makes it (QcHub.Ps, the item admin and demo endpoints, the webhook's ingest-event), goes through Runner. The runner here has no script at all: a call that reached it would fail otherwise.
    [Theory]
    [InlineData("qc-report")] [InlineData("unpark-item")] [InlineData("resettle-item")] [InlineData("publish-demo")] [InlineData("ingest-event")] [InlineData("item-pr")] [InlineData("wait-for-checks")] [InlineData("sync-board")] [InlineData("merge")]
    [InlineData("get-item")] [InlineData("journal")] [InlineData("approve-pr")]
    public async Task TheRunner_OfAnInertGateway_RunsNoScriptButItsHousekeeping(string verb)
    {
        foreach (var (engine, exit, code) in new[] { (NoDatabaseEngine(), 20, "database"), (UnnamedEngine(), 20, "inert") })
        {
            var runner = new Runner("/nonexistent/rule.ps1") { Inert = () => engine };
            var (c, output) = await runner.RunAsync(verb, "{\"item\":7}", TimeSpan.FromSeconds(5));
            Assert.Equal((exit, code), (c, JsonNode.Parse(output)!["code"]!.ToString()));
            (c, output) = await runner.RunScriptAsync(verb, "{\"item\":7}", TimeSpan.FromSeconds(5));
            Assert.Equal((exit, code), (c, JsonNode.Parse(output)!["code"]!.ToString()));
        }
    }

    [Theory]
    [InlineData("revocations")] [InlineData("revoke-worker")] [InlineData("operator-release")] [InlineData("operator-releases")] [InlineData("transcript-save")] [InlineData("transcript-load")]
    public async Task TheRunner_OfAnInertGateway_StillRunsItsHousekeeping(string verb)
    {
        // (the script is not there: the call gets as far as starting it, and the inert refusal is not what comes back)
        var runner = new Runner("/nonexistent/rule.ps1") { Inert = () => UnnamedEngine() };
        string? code = null;
        try { var (_, output) = await runner.RunScriptAsync(verb, "{}", TimeSpan.FromSeconds(5)); code = JsonNode.Parse(output)?["code"]?.ToString(); } catch (Exception) { code = "started"; }
        Assert.NotEqual("inert", code);
    }

    [Fact]
    public async Task APipelineCall_OfQcReport_WithNoDatabase_IsRefused_AndNeverReachesV1()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        Cutover(env, database: false);
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        _ = host.Server;
        var a = await host.SendAsync("POST", "/api/v1/verbs/qc-report", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken, "{\"item\":7,\"environment\":\"qc-7\",\"verdict\":\"PASS\"}");
        Assert.NotInRange(a.Status, 200, 299);
        var log = File.Exists(env.StubLog + ".in") ? File.ReadAllText(env.StubLog + ".in") : "";
        Assert.DoesNotContain("qc-report", log);          // (the stub rule script was never asked)
        Assert.DoesNotContain("qc-", log);
    }

    // ---- 3. no sweep before the term's adoption ---------------------------------------------------------------------------------------------------

    sealed class CountingStore : IWorkItemStore
    {
        public readonly InMemoryWorkItemStore Inner = new();
        public int Reads;
        public bool FailReads;
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => Inner.Load(itemKey, ct);
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => Inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => Inner.OwnedItems(ct);
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (FailReads) throw new TimeoutException("the table");
            return Inner.OwnedStates(ct);
        }
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => Inner.InFlightByRepository(ct);
    }

    sealed class Rig
    {
        public readonly List<(JsonObject Args, int Reads)> Calls = [];
        public readonly EventHub Hub = new();
        public readonly CountingStore Store = new();
        public long Epoch = 1;
        public readonly HashSet<long> DoneEpochs = [];
        public readonly Sweeper Sweeper;

        public Rig(Func<Rig, Func<CancellationToken, Task>>? poke = null)
        {
            var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30);
            var hub = Hub;
            var gate = new AdoptionGate(() => true, e => { lock (DoneEpochs) return DoneEpochs.Contains(e); }, () => true, () => Interlocked.Read(ref Epoch), poke: poke?.Invoke(this));
            Sweeper = new Sweeper(hub, plant, 3600, false, Task.CompletedTask) { MayAct = () => true, V2 = new V2Ownership(Store, Home), Adoption = gate };
        }

        // a pass has run when the hub has a time for the last full one (the loop observes it within 50 ms; the reads are those of that moment)
        public int Count() { lock (Calls) { if (Calls.Count == 0 && Hub.LastSweep != null) Calls.Add((new JsonObject(), Store.Reads)); return Calls.Count; } }
        public void Adopted(long epoch) { lock (DoneEpochs) DoneEpochs.Add(epoch); }
        public async Task<bool> WaitForCall(int seconds) { for (var i = 0; i < seconds * 20 && Count() == 0; i++) await Task.Delay(50); return Count() > 0; }
    }

    [Fact]
    public async Task NoPassRunsUntilTheAdoptionOfTheTermIsOver_AndTheTableIsReadAgainFirst()
    {
        var r = new Rig();
        // (the table has been read, as it is at the first start after the cutover: empty, and "Loaded" says nothing about the adoption)
        await r.Sweeper.V2!.RefreshAsync();
        Assert.True(r.Sweeper.V2.Loaded);
        await r.Sweeper.StartAsync(default);
        try
        {
            Assert.False(await r.WaitForCall(8), "v1 swept while the adoption of the term was not over");
            var readsWhenAdopted = r.Store.Reads;
            r.Adopted(1);
            Assert.True(await r.WaitForCall(15));
            // the loop read the table after the adoption and before the pass (the pass reads it once more itself): at least two reads came after the flip
            Assert.True(r.Calls[0].Reads >= readsWhenAdopted + 2, $"reads at the pass {r.Calls[0].Reads}, when adopted {readsWhenAdopted}");
        }
        finally { await r.Sweeper.StopAsync(default); }
    }

    [Fact]
    public async Task TheWaitingSweep_AsksForTheTermsFirstLookOfTheAdoptionItself()
    {
        var pokes = 0;
        var r = new Rig(rig => ct => { Interlocked.Increment(ref pokes); rig.Adopted(1); return Task.CompletedTask; });
        await r.Sweeper.StartAsync(default);
        try
        {
            Assert.True(await r.WaitForCall(15));
            Assert.True(pokes >= 1);
        }
        finally { await r.Sweeper.StopAsync(default); }
    }

    [Fact]
    public async Task AdoptionOver_ButTheTableCannotBeRead_NoPassRuns()
    {
        var r = new Rig();
        await r.Sweeper.V2!.RefreshAsync();
        r.Store.FailReads = true;
        r.Adopted(1);
        await r.Sweeper.StartAsync(default);
        try
        {
            Assert.False(await r.WaitForCall(8), "v1 swept on an ownership the adoption's rows were not read into");
            r.Store.FailReads = false;
            Assert.True(await r.WaitForCall(15));
        }
        finally { await r.Sweeper.StopAsync(default); }
    }

    // ---- the gate -------------------------------------------------------------------------------------------------------------------------------------

    sealed class Clock { public DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero); }

    [Fact]
    public void TheGate_WaitsForTheTermsAdoption_ShowsAnOverdueWaitAfterTenMinutes_AndReadyAnswers503OnlyUntilTheFirstAdoption()
    {
        var clock = new Clock(); long epoch = 1; var done = new HashSet<long>(); var mayAct = true;
        var gate = new AdoptionGate(() => true, e => done.Contains(e), () => mayAct, () => epoch, () => clock.Now);
        Assert.Null(gate.Settled());
        Assert.Null(gate.ReadyWaiting());
        clock.Now += TimeSpan.FromMinutes(9);
        Assert.False(gate.Overdue); Assert.Null(gate.ReadyWaiting());
        Assert.True(gate.Block()["waiting"]!.GetValue<bool>()); Assert.False(gate.Block()["overdue"]!.GetValue<bool>());
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(gate.Overdue);
        Assert.NotNull(gate.ReadyWaiting());
        Assert.Equal("adoption", System.Text.Json.JsonSerializer.SerializeToNode(gate.ReadyWaiting())!["waitingFor"]!.ToString());
        Assert.True(gate.Block()["overdue"]!.GetValue<bool>());
        done.Add(1);
        Assert.Equal(1, gate.Settled());
        Assert.False(gate.Overdue); Assert.Null(gate.ReadyWaiting());
        Assert.True(gate.Block()["done"]!.GetValue<bool>()); Assert.False(gate.Block()["waiting"]!.GetValue<bool>());
        // a new term (the lease was lost and taken again): the sweep waits again, the wait is shown, but /ready does not flip back
        epoch = 2; Assert.Null(gate.Settled());
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.True(gate.Overdue); Assert.Null(gate.ReadyWaiting());
    }

    [Fact]
    public void TheGate_ForAProcessThatMayNotAct_OrThatHasNoAdoption()
    {
        var clock = new Clock();
        var standby = new AdoptionGate(() => true, _ => false, () => false, () => 1, () => clock.Now);
        Assert.Null(standby.Settled()); clock.Now += TimeSpan.FromHours(1);
        Assert.False(standby.Overdue); Assert.Null(standby.ReadyWaiting()); Assert.False(standby.Block()["waiting"]!.GetValue<bool>());
        var none = new AdoptionGate(() => false, _ => false, () => true, () => 1, () => clock.Now);
        Assert.NotNull(none.Settled()); Assert.False(none.Overdue);
    }
}
