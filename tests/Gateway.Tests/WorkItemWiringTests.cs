using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>W6.1: what v1's sweep says about the items the v2 engine owns is dropped before the plant sees it (SweepV2Filter), and the ownership the filter asks (V2Ownership) follows the saga's table.</summary>
public sealed class SweepV2FilterTests
{
    static JsonObject Sweep() => (JsonObject)JsonNode.Parse("""
        {"ok":true,
         "offers":[{"item":7,"column":"Technical Design","reason":"intake"},{"item":8,"column":"Development","reason":"board-move"}],
         "triggers":[{"item":7,"event":"item.merged"},{"item":9,"event":"item.merged"}],
         "held":[{"item":7,"code":"depends-on","on":[3]},{"item":8,"code":"depends-on","on":[3]}],
         "parks":[{"item":7,"code":"x"}], "pass_unmerged":[{"item":7,"code":"grace"},{"item":8,"code":"grace"}], "flags":[{"item":7,"code":"release-queue-without-merge"}],
         "release_queue":[{"item":7,"release":"1.0.1"},{"item":8,"release":"1.0.2"}],
         "unconfirmed":[7,8,9], "unparked":[7], "holds":[7,8], "settling":[8,7],
         "owners":{"7":"alice","8":"bob"}, "owned":{"7":{"holder":"alice"},"8":{"holder":"bob"}}, "pins":{"7":"alice"}, "priorities":{"7":"urgent","8":"low"}, "runtimes":{"7":"cursor"}, "profiles":{"8":"testing"},
         "actions":[{"item":7,"action":"auto-unparked"}], "errors":[{"item":7,"code":"error"}], "skipped":[], "fenced":null}
        """)!;

    static string Items(JsonNode? list) => string.Join(",", ((JsonArray)list!).Select(x => x is JsonObject o ? o["item"]!.ToString() : x!.ToString()));

    [Fact]
    public void EverythingTheSweepSaysAboutAnOwnedItem_IsDropped_TheRestIsLeftAlone()
    {
        var s = Sweep();
        var removed = SweepV2Filter.Apply(s, n => n == 7);
        Assert.Equal("8", Items(s["offers"])); Assert.Equal("9", Items(s["triggers"])); Assert.Equal("8", Items(s["held"]));
        Assert.Equal("", Items(s["parks"])); Assert.Equal("8", Items(s["pass_unmerged"])); Assert.Equal("", Items(s["flags"])); Assert.Equal("8", Items(s["release_queue"]));
        Assert.Equal("8,9", Items(s["unconfirmed"])); Assert.Equal("", Items(s["unparked"])); Assert.Equal("8", Items(s["holds"])); Assert.Equal("8", Items(s["settling"]));
        Assert.Equal(["8"], ((JsonObject)s["owners"]!).Select(kv => kv.Key)); Assert.Equal(["8"], ((JsonObject)s["owned"]!).Select(kv => kv.Key));
        Assert.Empty((JsonObject)s["pins"]!); Assert.Equal(["8"], ((JsonObject)s["priorities"]!).Select(kv => kv.Key)); Assert.Empty((JsonObject)s["runtimes"]!); Assert.Equal(["8"], ((JsonObject)s["profiles"]!).Select(kv => kv.Key));
        Assert.Equal(16, removed);   // one entry of each of the seven lists of items, the four lists of numbers and the five maps that named 7
        // the script's own log of what it did is not rewritten
        Assert.Equal("7", Items(s["actions"])); Assert.Equal("7", Items(s["errors"]));
    }

    [Fact]
    public void NothingOwned_ChangesNothing_AndAnythingThatIsNoSweepIsLeftAsItIs()
    {
        var s = Sweep(); var before = s.ToJsonString();
        Assert.Equal(0, SweepV2Filter.Apply(s, _ => false));
        Assert.Equal(before, s.ToJsonString());
        Assert.Equal(0, SweepV2Filter.Apply(JsonNode.Parse("[1,2]")!, _ => true));
        var partial = JsonNode.Parse("""{"offers":null,"unconfirmed":"x","owners":[1],"held":[{"item":"7"},{"item":"abc"},{"nothing":1}]}""")!;
        Assert.Equal(1, SweepV2Filter.Apply(partial, _ => true));   // (the item given as the text "7" counts; the rest is not a list of items)
    }

    [Fact]
    public void ThePlansOfAnIncrementalOrPartialPass_AreFilteredLikeAFullOne()
    {
        var s = (JsonObject)JsonNode.Parse("""{"incremental":true,"items":[7],"offers":[{"item":7,"column":"Development","reason":"relaunch"}],"release_queue":null}""")!;
        Assert.Equal(1, SweepV2Filter.Apply(s, n => n == 7));
        Assert.Empty((JsonArray)s["offers"]!);
        Assert.Null(s["release_queue"]);
    }
}

/// <summary>D4 behind the saga's port, with the settings the gateway reads from config/instance.json.</summary>
public sealed class DocsOnlyPortTests
{
    static DocsOnlySettings Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return DocsOnlySettingsReader.From(JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json"))));
    }

    [Fact]
    public void TheSettings_AreTheOnesTheRuleScriptsRead()
    {
        var s = Source();
        Assert.Equal(4, s.Paths.Count);
        Assert.Equal("<!-- aisf:docs-only -->", s.Marker);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), s.LegacyBefore);
        Assert.Equal(["Technical Design"], s.DesignColumns);
        Assert.Equal("video", s.VideoLabel);
        Assert.Equal(["Integration Build (SQL container)"], s.MasterCodeChecks);
    }

    [Fact]
    public void ABrokenOrMissingConfiguration_GivesTheDefaultsAndNoPaths()
    {
        var s = DocsOnlySettingsReader.From(JsonNode.Parse("""{"docsOnlyPaths":"x","timing":{"docsOnlyLegacyBefore":5},"requiredBodySection":[1],"masterCodeChecks":{"a":1}}"""));
        Assert.Empty(s.Paths); Assert.Empty(s.DesignColumns); Assert.Empty(s.MasterCodeChecks);
        Assert.Null(s.LegacyBefore);
        Assert.Equal(("<!-- aisf:docs-only -->", "video"), (s.Marker, s.VideoLabel));
        Assert.Empty(DocsOnlySettingsReader.From(null).Paths);
    }

    [Fact]
    public void ThePort_AnswersWithD4_TheMarkerOfTheDesign_ThePathsOfTheConfiguration_TheBuildOfTheMerge()
    {
        var port = new DomainDocsOnlyPort(Source());
        Assert.True(port.ItemIsDocsOnly(true));
        Assert.False(port.ItemIsDocsOnly(false));
        Assert.True(port.FilesAreDocumentation(["docs/design/a.md", "README.md"]));
        Assert.False(port.FilesAreDocumentation(["docs/design/a.md", "src/App.cs"]));
        Assert.False(port.FilesAreDocumentation([]));   // (a pull request with no files proves nothing)
        // the code check skipped: no code changed (from the build); it ran: the merge is not docs-only; not completed yet: the files decide
        Assert.Equal(new MergeDocsOnlyVerdict(true, "build"), port.MergeIsDocsOnly([new BuildCheck("integration build (sql container)", "completed", "skipped")], null));
        Assert.Equal(new MergeDocsOnlyVerdict(false, "build"), port.MergeIsDocsOnly([new BuildCheck("Integration Build (SQL container)", "completed", "success")], ["docs/a.md"]));
        Assert.Equal(new MergeDocsOnlyVerdict(true, "pr-files"), port.MergeIsDocsOnly([new BuildCheck("Integration Build (SQL container)", "in_progress", null)], ["docs/a.md"]));
        Assert.Null(port.MergeIsDocsOnly([], null));
    }
}

public sealed class V2OwnershipTests
{
    static readonly RepoId Home = new("acme", "orders");

    sealed class FlakyStore(IWorkItemStore inner) : IWorkItemStore
    {
        public bool Down;
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => inner.Load(itemKey, ct);
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => Down ? throw new InvalidOperationException("the database does not answer") : inner.OwnedItems(ct);
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => Down ? throw new InvalidOperationException("the database does not answer") : inner.OwnedStates(ct);
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => inner.InFlightByRepository(ct);
    }

    static async Task Start(WorkItemService svc, ItemId item, string? plan = null) => await svc.Handle(new StartWorkItem(item, "intake", plan));

    [Fact]
    public async Task OwnsTheOpenItemsOfTheHomeRepository_NotTheOtherRepositories_NotTheCompletedOnes()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DomainDocsOnlyPort(DocsOnlySettingsReader.From(null))));
        await Start(svc, new ItemId(Home, 7)); await Start(svc, new ItemId(Home, 8), "docs"); await Start(svc, new ItemId("acme", "billing", 9));
        var own = new V2Ownership(store, Home);
        Assert.False(own.Owns(7));   // (nothing is read until the sweep asks)
        Assert.True(await own.RefreshAsync());
        Assert.True(own.Owns(7)); Assert.True(own.Owns(8)); Assert.False(own.Owns(9)); Assert.Equal(2, own.Count);
        await svc.Handle(new MergeSeen(new ItemId(Home, 8), "m", new string('a', 40), 3));   // the docs item completes
        await own.RefreshAsync();
        Assert.False(own.Owns(8)); Assert.True(own.Owns(7));
        Assert.Equal([7], own.Items);
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswer_KeepsTheLastAnswer_SoV1DoesNotTakeAnItemOverDuringAnOutage()
    {
        var store = new FlakyStore(new InMemoryWorkItemStore());
        var svc = new WorkItemService(store, new WorkItemSaga(new DomainDocsOnlyPort(DocsOnlySettingsReader.From(null))));
        await Start(svc, new ItemId(Home, 7));
        var own = new V2Ownership(store, Home);
        await own.RefreshAsync();
        store.Down = true;
        Assert.False(await own.RefreshAsync());
        Assert.True(own.Owns(7));
        store.Down = false;
        Assert.True(await own.RefreshAsync());
    }
}

/// <summary>Nothing of the saga exists outside the engine that acts: a gateway in the shadow run (tdd today) registers no ownership and no saga service, and its sweeper has no filter.</summary>
public sealed class WorkItemInertTests
{
    [Fact]
    public void AGatewayWithoutAnActingV2Engine_RegistersNoSaga_NoOwnership_AndFiltersNoSweep()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); env.Apply();
        using var host = new GatewayHost();
        _ = host.Server;
        foreach (var t in new[] { typeof(V2Ownership), typeof(WorkItemService), typeof(WorkItemHistory), typeof(WorkItemParts) })
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType == t);
        var sweeper = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sweeper>().Single();
        Assert.Null(sweeper.V2);
    }

    // The gateway of an environment in v2Environments never finished starting: building the Wolverine host resolves the leadership, which resolves the plant, which resolved the host for the journal sink
    // (a circle, so a start that ran until the stack was gone). Found by the first test that started one.
    [Fact]
    public void AGatewayWhoseEngineActs_Starts_RegistersTheSaga_AndHoldsTheOwnershipItsSweepAsks()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
        env.Apply();
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
        Assert.Single(host.Captured!, d => d.ServiceType == typeof(V2Ownership));
        var sweeper = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sweeper>().Single();
        Assert.Same(host.Services.GetRequiredService<V2Ownership>(), sweeper.V2);
        Assert.NotNull(host.Services.GetRequiredService<WolverineHostService>());
    }

    [Fact]
    public void TheV1Gateway_HasNoWolverineAtAll()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment(); env.Apply();
        using var host = new GatewayHost();
        _ = host.Server;
        Assert.DoesNotContain(host.Captured!, d => d.ServiceType == typeof(V2Ownership));
        Assert.Null(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sweeper>().Single().V2);
    }
}

/// <summary>
/// The hybrid composition end to end, in a gateway whose engine acts (v2Environments names the environment) over a real SQL Server: the Wolverine host runs the saga's handlers as generated code
/// (partitioned local queues, the history handler), a started item is written to the saga's table, and v1's sweep, which still offers it, finds the plant leaving it alone: the plan of the next sweep
/// holds one item fewer. When the item completes it is v1's again.
/// </summary>
public sealed class WorkItemWiringTests
{
    static string With(string cs, string database) => new SqlConnectionStringBuilder(cs) { InitialCatalog = database }.ConnectionString;

    static async Task<List<string>> Rows(string cs, string sql)
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c); await using var r = await cmd.ExecuteReaderAsync();
        var l = new List<string>(); while (await r.ReadAsync()) l.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
        return l;
    }

    static List<int?> WaitingOfSweeps(GatewayEnvironment env)
    {
        var log = env.StubLog + ".in";
        if (!File.Exists(log)) return [];
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2 && p[0] == "sync-board" && !p[1].Contains("\"items\""))
            .Select(p => (int?)(JsonNode.Parse(p[1]) as JsonObject)?["replenish_waiting"]).ToList();
    }

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds)) { if (await cond()) return true; await Task.Delay(200); }
        return false;
    }

    [SqlFact]
    public async Task AStartedItem_IsDecidedByTheSaga_AndV1sSweepLeavesItAlone_UntilItCompletes()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w61_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.Set("FACTORY_DB_CONNECTION", cs);
            env.Set("FACTORY_SWEEP_SECONDS", "1"); env.Set("FACTORY_SWEEP_MIN_SECONDS", "1");
            env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            env.Apply();
            // v1's sweep offers two items; no worker is connected, so the plant leaves whatever it is given waiting (replenish_waiting counts them)
            File.WriteAllText(Path.Combine(env.Dir, "answer-sync-board.json"), new JsonObject
            {
                ["ok"] = true, ["actions"] = new JsonArray(),
                ["offers"] = new JsonArray(new JsonObject { ["item"] = 701, ["column"] = "Development", ["reason"] = "x" }, new JsonObject { ["item"] = 702, ["column"] = "Technical Design", ["reason"] = "x" }),
            }.ToJsonString() + "\n");
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            var item = new ItemId(home, 701);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");
            Assert.True(await Until(() => Task.FromResult(WaitingOfSweeps(env).Count(w => w == 2) >= 1), 40), "the first plan, before any saga, should leave both items waiting");

            // the intake gives the docs item to v2: the saga starts through the host's partitioned queues, and its handlers are generated code
            Assert.True(await wolverine.PublishAsync(new StartWorkItem(item, "intake", "docs")));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = '" + item + "' AND Status = 'open'"))[0] == "1", 60), "the saga did not start");
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '" + item + "' AND Type = 'workitem.decided'"))[0] == "2", 60), "the decisions did not reach the history");
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = '" + item + "' AND ClosedAt IS NULL"))[0]);

            // v1's sweep still says 701 waits in Development, but the plant is offered the item by the saga, in the column of the step the saga entered (W6.6): v1's offer of it is dropped
            var plant = gateway.Services.GetRequiredService<Plant>();
            string? ColumnOf(int n) => plant.UnownedJson().OfType<JsonObject>().FirstOrDefault(r => r["item"]?.GetValue<int>() == n)?["column"]?.ToString();
            Assert.True(await Until(() => Task.FromResult(ColumnOf(701) == "Technical Design"), 60), "the plant was not offered the saga's item in its design column: " + plant.UnownedJson().ToJsonString());
            var before = WaitingOfSweeps(env).Count;
            Assert.True(await Until(() => Task.FromResult(WaitingOfSweeps(env).Skip(before).Count(w => w == 2) >= 2), 60), "the plan lost an offer: " + string.Join(",", WaitingOfSweeps(env)));
            Assert.Equal("Technical Design", ColumnOf(701));   // (v1's offer for the column Development never came back)
            Assert.Equal("Technical Design", ColumnOf(702));
            var owned = gateway.Services.GetRequiredService<V2Ownership>();
            Assert.True(owned.Owns(701)); Assert.False(owned.Owns(702));
            Assert.Equal([(701, "Technical Design")], owned.Offers);

            // the merge completes a docs item; it is v1's again, the plan holds both offers
            Assert.True(await wolverine.PublishAsync(new MergeSeen(item, "merged|1", new string('a', 40), 9)));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = '" + item + "' AND Status = 'completed'"))[0] == "1", 60), "the merge did not complete the item");
            Assert.True(await Until(() => Task.FromResult(ColumnOf(701) == "Development"), 60), "the completed item was not v1's again: " + plant.UnownedJson().ToJsonString());
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}
