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

    // W9.10e: an empty set that only means "not read yet" must not look like "v1 owns everything"; the sweeper waits for Loaded
    [Fact]
    public async Task TheOwnershipIsNotLoaded_UntilTheFirstSuccessfulRead_AndStaysLoadedWhenALaterReadFails()
    {
        var store = new FlakyStore(new InMemoryWorkItemStore()) { Down = true };
        var own = new V2Ownership(store, Home);
        Assert.False(own.Loaded);
        Assert.False(await own.RefreshAsync());   // (the first read fails: v1 still knows nothing of the saga's items)
        Assert.False(own.Loaded);
        store.Down = false;
        Assert.True(await own.RefreshAsync());
        Assert.True(own.Loaded);
        store.Down = true;
        Assert.False(await own.RefreshAsync());
        Assert.True(own.Loaded);   // (the last answer stands)
        var given = new V2Ownership(store, Home);
        given.Set([7]);
        Assert.True(given.Loaded);
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

/// <summary>
/// W9.10e: where the engine acts the plant is fed from nothing until the saga's table has been read once (an empty set that only means "not read yet" would make the plant drop the owners of items in flight).
/// </summary>
public sealed class SweeperV2ArgumentsTests
{
    // The v2 gateway's store throws on every read (no database answers), so no pass runs and the plant is fed nothing until the table has been read.
    [Fact]
    public async Task WhereTheEngineActs_NoPassRunsWhileTheSagaTableHasNeverBeenRead()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_SWEEP_SECONDS", "1"); env.Set("FACTORY_SWEEP_MIN_SECONDS", "1");
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd") };
        env.Apply();
        using var host = new GatewayHost();
        _ = host.Server;
        var sweeper = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sweeper>().Single();
        await Task.Delay(TimeSpan.FromSeconds(15));
        Assert.False(sweeper.V2!.Loaded);
        Assert.Null(host.Services.GetRequiredService<EventHub>().LastSweep);
    }
}

/// <summary>
/// The hybrid composition end to end, in a gateway whose engine acts (v2Environments names the environment) over a real SQL Server: the Wolverine host runs the saga's handlers as generated code
/// (partitioned local queues, the history handler), a started item is written to the saga's table, and the plant is fed from the saga alone (V3.2): the item is offered in the column of its step, and no more once it completes.
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

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds)) { if (await cond()) return true; await Task.Delay(200); }
        return false;
    }

    [SqlFact]
    public async Task AStartedItem_IsDecidedByTheSaga_AndOfferedByIt_UntilItCompletes()
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
            env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd") };
            env.Apply();
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            var item = new ItemId(home, 701);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");

            // the intake gives the docs item to v2: the saga starts through the host's partitioned queues, and its handlers are generated code
            Assert.True(await wolverine.PublishAsync(new StartWorkItem(item, "intake", "docs")));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = '" + item + "' AND Status = 'open'"))[0] == "1", 60), "the saga did not start");
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '" + item + "' AND Type = 'workitem.decided'"))[0] == "2", 60), "the decisions did not reach the history");
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = '" + item + "' AND ClosedAt IS NULL"))[0]);

            // the plant is offered the item by the saga, in the column of the step the saga entered (W6.6)
            var plant = gateway.Services.GetRequiredService<Plant>();
            string? ColumnOf(int n) => plant.UnownedJson().OfType<JsonObject>().FirstOrDefault(r => r["item"]?.GetValue<int>() == n)?["column"]?.ToString();
            Assert.True(await Until(() => Task.FromResult(ColumnOf(701) == "Technical Design"), 60), "the plant was not offered the saga's item in its design column: " + plant.UnownedJson().ToJsonString());
            Assert.Null(ColumnOf(702));   // (no saga, no offer: the plant is fed from the saga alone)
            var owned = gateway.Services.GetRequiredService<V2Ownership>();
            Assert.True(owned.Owns(701)); Assert.False(owned.Owns(702));
            Assert.Equal([(701, "Technical Design")], owned.Offers);

            // the merge completes a docs item; the saga offers it no more
            Assert.True(await wolverine.PublishAsync(new MergeSeen(item, "merged|1", new string('a', 40), 9)));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.WorkItems WHERE ItemKey = '" + item + "' AND Status = 'completed'"))[0] == "1", 60), "the merge did not complete the item");
            Assert.True(await Until(() => Task.FromResult(ColumnOf(701) == null), 60), "the completed item is still offered: " + plant.UnownedJson().ToJsonString());
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}
