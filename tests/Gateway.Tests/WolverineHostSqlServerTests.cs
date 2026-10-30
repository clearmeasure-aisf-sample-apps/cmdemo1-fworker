using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Database;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The host against a real SQL Server (the service container of tests.yml; FACTORY_TEST_SQL names it); each test returns early where it is not set.
/// Each test makes a database of its own and drops it.
/// </summary>
public sealed class WolverineHostSqlServerTests
{
    static string With(string cs, string database) => new SqlConnectionStringBuilder(cs) { InitialCatalog = database }.ConnectionString;

    static async Task<List<string>> Rows(string cs, string sql)
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c); await using var r = await cmd.ExecuteReaderAsync();
        var l = new List<string>(); while (await r.ReadAsync()) l.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
        return l;
    }

    static async Task<long> Count(string cs, string sql) => long.Parse((await Rows(cs, sql))[0]);

    // a gateway on a database of its own; mode is the "wolverine.mode" of the configuration (null: the default)
    static async Task WithGateway(string? mode, Func<string, GatewayHost, GatewayEnvironment, Task> body, Func<string, Task>? afterStop = null)
    {
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w22_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", cs);
        WolverineHostTestSupport.Shadow(env);
        env.Apply();
        if (mode != null)
        {
            var path = Path.Combine(env.Dir, "instance.test.json");
            var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            cfg["wolverine"] = new JsonObject { ["mode"] = mode };
            File.WriteAllText(path, cfg.ToJsonString());
        }
        var host = new GatewayHost(); var disposed = false;
        try
        {
            _ = host.Server;
            await body(cs, host, env);
            await host.DisposeAsync(); disposed = true;
            SqlConnection.ClearAllPools();
            if (afterStop != null) await afterStop(cs);
        }
        finally
        {
            if (!disposed) await host.DisposeAsync();
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static async Task<JsonNode?> WaitFor(GatewayHost host, Func<JsonNode?, bool> done, int seconds)
    {
        JsonNode? w = null; var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            w = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["wolverine"];
            if (done(w)) break;
            await Task.Delay(200);
        }
        return w;
    }

    // /ready waits for the schema where a database is configured: the first 200 (a 503 before it says what it waits for)
    static async Task WaitReady(GatewayHost host, int seconds = 90)
    {
        var sw = Stopwatch.StartNew(); var last = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort);
        while (last.Status != 200 && sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Assert.Equal(503, last.Status);
            Assert.Equal("schema", (string?)last.Json?["waitingFor"]);
            await Task.Delay(100);
            last = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort);
        }
        Assert.Equal(200, last.Status);
    }

    [Fact]
    public async Task Solo_Default_StartsInTheBackground_BuildsTheSchemaWithTheScripts_AndHoldsNoSessionOrLock()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithGateway(null, async (cs, host, env) =>
        {
            await WaitReady(host);   // 503 "schema" until the scripts ran and the catalog was checked, then 200
            var w = await WaitFor(host, n => (string?)n?["state"] == "running", 90);
            Assert.Equal("running", (string?)w?["state"]);
            Assert.Equal("solo", (string?)w?["mode"]);
            // the status says the schema is good, what this start applied, and that nothing differs
            var schema = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["schema"];
            Assert.Equal("good", (string?)schema?["state"]);
            Assert.Equal(FactorySchema.Scripts.Count, (int?)schema?["shipped"]);
            Assert.Equal(FactorySchema.Scripts.Count, schema?["applied"]?.AsArray().Count);
            Assert.Empty(schema?["pending"]?.AsArray() ?? []);
            Assert.Empty(schema?["differences"]?.AsArray() ?? []);
            Assert.Null(schema?["lastFailure"]);
            // the journal holds exactly the scripts of this build, and the EF migrations are gone
            Assert.Equal(FactorySchema.Scripts, await Rows(cs, "SELECT ScriptName FROM dbo.SchemaVersions ORDER BY ScriptName"));
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM sys.tables WHERE name='__EFMigrationsHistory'"));
            // the 19 tables of schema factory and Wolverine's storage (inbox, outbox, nodes, dead letters) in its own schema
            Assert.Equal(19, await Count(cs, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='factory'"));
            foreach (var t in new[] { "ItemHistory", "Correlations", "OpenInstances", "ProjectionVersions", "SessionMailbox", "CatalogCopies", "IdempotencyLedger", "IdempotencyClaims", "CopilotReservations", "IngressDedup", "WorkItems", "MergeTrains", "CapacityLedgers", "QcEnvironments", "Transcripts" })
                Assert.Equal(1, await Count(cs, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='factory' AND TABLE_NAME='{t}'"));
            foreach (var t in new[] { "wolverine_incoming_envelopes", "wolverine_outgoing_envelopes", "wolverine_dead_letters", "wolverine_nodes" })
                Assert.Equal(1, await Count(cs, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='wolverine' AND TABLE_NAME='{t}'"));
            // inert, and cheap: no leader, no application lock, nothing polling in the first seconds
            await Task.Delay(8000);
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION'"));
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM wolverine.wolverine_node_assignments"));
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes"));
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes"));
        });
    }

    [Fact]
    public async Task Balanced_ElectsItsNodeLeader_HoldsTheLock_AndTheStopReleasesIt()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithGateway("balanced", async (cs, host, env) =>
        {
            var w = await WaitFor(host, n => (string?)n?["state"] == "running", 90);
            Assert.Equal("balanced", (string?)w?["mode"]);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60) && !(await Rows(cs, "SELECT * FROM wolverine.wolverine_node_assignments")).Any(r => r.StartsWith("wolverine://leader/"))) await Task.Delay(500);
            var assignments = string.Join(" ; ", await Rows(cs, "SELECT * FROM wolverine.wolverine_node_assignments"));
            Assert.Contains("wolverine://leader/", assignments);
            Assert.Equal(1, await Count(cs, "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION'"));   // the leader's lock: a session that keeps a serverless database awake
            Assert.Equal(1, await Count(cs, "SELECT COUNT(*) FROM wolverine.wolverine_nodes"));
        }, async cs =>
        {
            // a second start applies nothing and finds the schema good (the library call the gateway makes)
            var second = await FactorySchema.UpgradeAndVerifyAsync(cs, new SchemaRunOptions());
            Assert.Empty(second.Applied);
            Assert.True(second.Good);
            // after the stop the node has left: no application lock is held (the server drops the session a moment after the connection closes)
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15) && await Count(cs, "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION'") > 0) await Task.Delay(250);
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION'"));
        });
    }
}
