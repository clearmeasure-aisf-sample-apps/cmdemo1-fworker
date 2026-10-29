using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W7.0 and W7.1 in the real gateway host over a real SQL Server: where the engine acts, the Wolverine host runs the scheduler's handlers as generated code (the sequential durable queue, the history handlers), a
/// request is written to the ledger and an event reaches the item history; where it does not (the shadow run of tdd), nothing of the scheduler exists. Each test returns early where FACTORY_TEST_SQL is unset or
/// the platform has no stub pwsh.
/// </summary>
public sealed class CapacityWiringTests
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

    static RepoId Home(GatewayEnvironment env)
    {
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
        return RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
    }

    static RequestSlot Ask(ItemId item) => new(item, "ask-1", 1, "cursor-cloud", "design", default, false, false, 1, DateTimeOffset.UtcNow, [], Footprint.Empty);

    [Fact]
    public async Task WhereTheEngineActs_TheSchedulerRunsInTheWolverineHost_RequestToLedgerToHistory()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w71_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.Set("FACTORY_DB_CONNECTION", cs);
            env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            env.Apply();
            var item = new ItemId(Home(env), 701);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");

            // no runtime is on in the test configuration, so the scheduler says so: the request waits as runtime-off
            Assert.True(await wolverine.PublishAsync(Ask(item)));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.CapacityLedgers WHERE [Key] = 'factory'"))[0] == "1", 60), "the request did not reach the ledger");
            Assert.True(await Until(async () => (await Rows(cs, $"SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '{item}' AND Type = 'capacity.held' AND Detail LIKE '%runtime-off%'"))[0] == "1", 60), "the waiting code did not reach the history");
            var state = CapacityState.FromJson((await Rows(cs, "SELECT StateJson FROM factory.CapacityLedgers WHERE [Key] = 'factory'"))[0]);
            Assert.Equal("runtime-off", state.Queue[item.ToString()].Announced);
            Assert.Empty(state.Slots);

            // the item leaves the books
            Assert.True(await wolverine.PublishAsync(new ClearItem(item, "clear-1", "test")));
            Assert.True(await Until(async () => CapacityState.FromJson((await Rows(cs, "SELECT StateJson FROM factory.CapacityLedgers WHERE [Key] = 'factory'"))[0]).Queue.Count == 0, 60), "the item was not cleared");
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    [Fact]
    public async Task InTheShadowRunOfTdd_NothingOfTheSchedulerExists_ARequestIsNotHandled()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w71s_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.Set("FACTORY_DB_CONNECTION", cs);
            WolverineHostTestSupport.Shadow(env);
            env.Apply();
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");
            // the table exists (the migration is the host's), the scheduler does not: nothing handles the message and nothing is written
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = 'CapacityLedgers'"))[0]);
            try { await wolverine.PublishAsync(Ask(new ItemId(new RepoId("acme", "orders"), 701))); } catch (Exception) { /* no handler and no route: the bus may refuse */ }
            await Task.Delay(3000);
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM factory.CapacityLedgers"))[0]);
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type LIKE 'capacity.%'"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}

/// <summary>The scheduler's wiring pieces that need no host: what the plant's managers count, and the settings of the source configuration.</summary>
public sealed class CapacityWiringUnitTests
{
    [Fact]
    public void TheSourceConfiguration_GivesTheSchedulerNoRuntime_AndTheDefaultsOfTheDesign()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        var s = CapacitySettings.From(JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json"))), "tdd");
        Assert.Empty(s.Runtimes);
        Assert.False(s.Overlap.Enabled);
        Assert.Equal(new HeadroomSettings(2048, 4096, 30, 10), s.Headroom);
    }

    [Fact]
    public void TheRuntimeKinds_AreTheOnesTheRegistryKnowsByKind()
    {
        // the scheduler asks the registry for each kind it names; a kind the registry does not know is simply not on
        var registry = new RuntimeRegistry { CopilotCiRelease = null!, CopilotCodes = null! };
        foreach (var kind in RuntimeKinds.All) Assert.Null(registry.ByKind(kind));
        Assert.False(CapacityWiring.OnOf(registry)("octopus"));
        Assert.Equal(new RuntimeOutside(0, 0), CapacityWiring.OutsideOf(registry)("octopus"));
        Assert.Equal(["octopus", "cae-job", "cursor-cloud", "copilot-cloud"], RuntimeKinds.All);
    }
}

/// <summary>The read of the plant's manager that the scheduler shares the runtime's limits through (RuntimeManager.Load): what runs, what started today, and why nothing may start that is no shortage of slots.</summary>
public sealed class RuntimeManagerLoadTests
{
    sealed class PlainRuntime : IWorkerRuntime
    {
        public string Kind => "cursor-cloud";
        public int MaxConcurrent => 5;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) => Task.FromResult(new ProvisionResult(true, "x", ""));
        public Task Deprovision(string workerName, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>(Array.Empty<RuntimeInstance>());
    }

    static RuntimeManager Manager(Func<string?>? blocked = null)
    {
        var cfg = RuntimeSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
        var limits = RuntimeLimits.Of(cfg.CaeJob); limits.IsOn = () => true; limits.MaxConcurrent = 5; limits.MaxExecutionsPerDay = 30;
        if (blocked != null) limits.Blocked = blocked;
        return new RuntimeManager(cfg, new PlainRuntime(), new RuntimeNonces(), limits);
    }

    [Fact]
    public async Task TheLoad_IsWhatRunsAndWhatStartedToday_AndBlockedUntilTheExecutionsWereRead()
    {
        var m = Manager();
        Assert.Equal((0, 0, "runtime-relisting"), m.Load());   // a second execution could be started until the executions were read at start: the scheduler waits too
        await m.Poll(CancellationToken.None);
        Assert.Equal((0, 0, (string?)null), m.Load());
        m.Begin(5, "Development", ""); m.Begin(6, "Development", "");
        Assert.Equal((2, 2, (string?)null), m.Load());
    }

    [Fact]
    public async Task TheLoad_CarriesTheQuotaReasonOfTheManager_AsCanStartDoes()
    {
        string? quota = null;
        var m = Manager(() => quota);
        await m.Poll(CancellationToken.None);
        quota = "quota-exhausted";
        Assert.Equal("quota-exhausted", m.Load().Blocked);
        Assert.Equal((false, "quota-exhausted"), m.CanStart());   // the same reason the plant gives
        quota = null;
        Assert.Null(m.Load().Blocked);
    }
}
