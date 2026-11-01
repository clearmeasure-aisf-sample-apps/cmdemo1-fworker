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

    [Fact]
    public async Task SweeperWithIntakeOff_GivesTheScriptNoIntakeBlock_TheControlGivesIt()
    {
        foreach (var off in new[] { false, true })
        {
            var calls = new List<JsonObject>();
            var runner = new Runner("/nonexistent/factory.ps1");
            var routes = new V2VerbRoutes();
            routes.Add(BoardRoutes.Verbs, "sync-board", (stdin, _) =>
            {
                lock (calls) calls.Add((JsonObject)JsonNode.Parse(stdin)!);
                return Task.FromResult(new VerbAnswer(0, new JsonObject { ["ok"] = true, ["offers"] = new JsonArray(), ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["unconfirmed"] = new JsonArray() }.ToJsonString()));
            });
            runner.Routes = routes;
            var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30);
            var hub = new EventHub();
            var qc = new QcHub(runner, plant, hub, new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));
            var restored = Task.CompletedTask;
            var sweeper = new Sweeper(runner, hub, plant, qc, 3600, true, null, restored) { MayAct = () => true, IntakeOff = off };
            await sweeper.StartAsync(default);
            try
            {
                for (var i = 0; i < 300 && !Any(calls); i++) await Task.Delay(50);
                lock (calls)
                {
                    Assert.NotEmpty(calls);
                    Assert.Equal(!off, calls[0].ContainsKey("intake"));   // (the control proves the sweeper offers intake in this rig at all)
                }
            }
            finally { await sweeper.StopAsync(default); }
        }
        static bool Any(List<JsonObject> l) { lock (l) return l.Count > 0; }
    }

    [Theory]
    [InlineData("uat", "uat: the image has intake.enabled true and v1 acts")]
    [InlineData("prod", "prod: the same")]
    public async Task WhereTheEngineDoesNotAct_V1TakesNoTodoItem(string environment, string why)
    {
        _ = why;
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        Cutover(env, database: true, environment: environment);
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        _ = host.Server;
        var sweeper = host.Services.GetServices<IHostedService>().OfType<Sweeper>().Single();
        Assert.Null(sweeper.V2);          // (v1 acts here)
        Assert.True(sweeper.IntakeOff);
        var calls = await WaitForSweeps(env, 40);
        Assert.NotEmpty(calls);           // (v1 does sweep: only its intake is off)
        Assert.All(calls, c => Assert.False(c.ContainsKey("intake"), "the sweep told the script to take intake: " + c.ToJsonString()));
    }

    [Fact]
    public async Task APlainV1Gateway_WithNoPlansNamed_KeepsItsIntake()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_SWEEP_SECONDS", "1"); env.Set("FACTORY_SWEEP_MIN_SECONDS", "1");
        env.EditConfig = cfg => cfg["intake"] = new JsonObject { ["enabled"] = true };
        env.Apply(); WriteAnswer(env);
        using var host = new GatewayHost();
        _ = host.Server;
        Assert.False(host.Services.GetServices<IHostedService>().OfType<Sweeper>().Single().IntakeOff);
        var calls = await WaitForSweeps(env, 40);
        Assert.NotEmpty(calls);
        Assert.True(calls[0].ContainsKey("intake"));
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
        Assert.Contains(services, s => s is AutoPromoter);
        // nothing that acts on items acts (the lease is off in this test, so MayAct of the leader is true: only the inert rule says no), and a verb that changes anything is refused
        Assert.False(services.OfType<PlantTicker>().Single().MayAct!());
        Assert.False(services.OfType<BaseWatch>().Single().MayAct!());
        Assert.True(services.OfType<AutoPromoter>().Single().MayAct!());
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
    public void TheItemsTheAdoptionCouldNotFence_AreLeftOutOfTheSweepLikeTheSagasItems()
    {
        var own = new V2Ownership(new InMemoryWorkItemStore(), Home);
        own.Set([3]);
        var args = new JsonObject();
        Sweeper.AddV2Arguments(args, own, [], [8, 3]);
        Assert.Equal([3, 8], args["skip_items"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        var onlyLeft = new JsonObject();
        Sweeper.AddV2Arguments(onlyLeft, new V2Ownership(new InMemoryWorkItemStore(), Home), [], [8]);
        Assert.Equal([8], onlyLeft["skip_items"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        var gate = new AdoptionGate(() => true, _ => true, () => true, () => 1, excluded: () => new HashSet<int> { 8 });
        Assert.Equal([8], gate.Excluded().ToArray());
    }

    sealed class Below : IVerbRouter
    {
        public readonly List<string> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls.Add(verb); return Task.FromResult(new VerbAnswer(0, "{\"ok\":true}")); }
    }

    [Fact]
    public async Task TheInertRouter_RefusesEveryVerbThatChangesAnything_AndLetsTheReadingOnesThrough()
    {
        var below = new Below(); var router = new InertVerbRouter(below);
        foreach (var verb in InertVerbRouter.ReadOnly) Assert.Equal(0, (await router.RunAsync(verb, "{}", TimeSpan.FromSeconds(1))).Code);
        Assert.Equal(InertVerbRouter.ReadOnly.Count, below.Calls.Count);
        // (item-pr retargets, renames, closes and readies a pull request; wait-for-checks re-runs a job and saves the re-run: neither is read-only)
        foreach (var verb in new[] { "claim-item", "merge", "complete", "move-next", "approve-pr", "qc-request", "create-item", "park-item", "accept", "release", "item-pr", "wait-for-checks" })
        {
            var a = await router.RunAsync(verb, "{}", TimeSpan.FromSeconds(1));
            Assert.Equal(20, a.Code); Assert.Equal("database", JsonNode.Parse(a.Output)!["code"]!.ToString());
        }
        Assert.Equal(InertVerbRouter.ReadOnly.Count, below.Calls.Count);   // (none of the refused reached v1)
    }

    // The choke point: every call of the rule script, whoever makes it (QcHub.Ps, the item admin and demo endpoints, the webhook's ingest-event), goes through Runner. The runner here has no script at all: a call that reached v1 would throw.
    [Theory]
    [InlineData("qc-report")] [InlineData("unpark-item")] [InlineData("resettle-item")] [InlineData("publish-demo")] [InlineData("ingest-event")] [InlineData("item-pr")] [InlineData("wait-for-checks")] [InlineData("sync-board")] [InlineData("merge")]
    public async Task TheRunner_OfAnInertGateway_RefusesWhatChangesAnything_NeverStartingTheScript(string verb)
    {
        var runner = new Runner("/nonexistent/factory.ps1") { Inert = () => true };
        var (code, output) = await runner.RunAsync(verb, "{\"item\":7}", TimeSpan.FromSeconds(5));
        Assert.Equal(20, code); Assert.Equal("database", JsonNode.Parse(output)!["code"]!.ToString());
        (code, output) = await runner.RunScriptAsync(verb, "{\"item\":7}", TimeSpan.FromSeconds(5));
        Assert.Equal(20, code); Assert.Equal("database", JsonNode.Parse(output)!["code"]!.ToString());
    }

    [Theory]
    [InlineData("leader-lease")] [InlineData("plant-snapshot-save")] [InlineData("plant-snapshot-load")] [InlineData("revocations")] [InlineData("revoke-worker")] [InlineData("operator-release")] [InlineData("auto-promote")] [InlineData("operator-releases")] [InlineData("transcript-save")] [InlineData("get-item")]
    public async Task TheRunner_OfAnInertGateway_StillRunsTheHousekeepingAndTheReadingVerbs(string verb)
    {
        // (the script is not there: the call gets as far as starting it, and the refusal "database" is not what comes back)
        var runner = new Runner("/nonexistent/factory.ps1") { Inert = () => true };
        string? code = null;
        try { var (_, output) = await runner.RunScriptAsync(verb, "{}", TimeSpan.FromSeconds(5)); code = JsonNode.Parse(output)?["code"]?.ToString(); } catch (Exception) { code = "started"; }
        Assert.NotEqual("database", code);
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
        public readonly CountingStore Store = new();
        public long Epoch = 1;
        public readonly HashSet<long> DoneEpochs = [];
        public readonly Sweeper Sweeper;

        public Rig(Func<Rig, Func<CancellationToken, Task>>? poke = null)
        {
            var runner = new Runner("/nonexistent/factory.ps1");
            var routes = new V2VerbRoutes();
            routes.Add(BoardRoutes.Verbs, "sync-board", (stdin, _) =>
            {
                lock (Calls) Calls.Add(((JsonObject)JsonNode.Parse(stdin)!, Store.Reads));
                return Task.FromResult(new VerbAnswer(0, new JsonObject { ["ok"] = true, ["offers"] = new JsonArray(), ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["unconfirmed"] = new JsonArray() }.ToJsonString()));
            });
            runner.Routes = routes;
            var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30);
            var hub = new EventHub();
            var qc = new QcHub(runner, plant, hub, new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));
            var gate = new AdoptionGate(() => true, e => { lock (DoneEpochs) return DoneEpochs.Contains(e); }, () => true, () => Interlocked.Read(ref Epoch), poke: poke?.Invoke(this));
            Sweeper = new Sweeper(runner, hub, plant, qc, 3600, false, null, Task.CompletedTask) { MayAct = () => true, V2 = new V2Ownership(Store, Home), Adoption = gate };
        }

        public int Count() { lock (Calls) return Calls.Count; }
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
