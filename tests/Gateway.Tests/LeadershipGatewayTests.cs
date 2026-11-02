using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Persistence;
using Fworker.Workflows.PlantState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The leadership as the whole gateway does it, on a real SQL Server (FACTORY_TEST_SQL; the tests that need one return at once without it): it takes the SQL lock at its start, restores the plant from the database, acts,
/// writes the plant on every change, steps down when a newer process asks for the lock (the final plant first, the lock last) and hands over at its stop. Since V3.2 the lock is the only lease and the database the
/// only copy of the plant: the GitHub ref and the rule scripts that kept them are gone. The rules around the lock, on a fake server, are SqlLeadershipRulesTests'; the coordinator's restore and write, on a store in
/// memory, are PlantStateGatewayTests'.
/// </summary>
public sealed class LeadershipGatewayTests
{
    // the rule scripts for these tests: the revoked credentials read at start, anything else answers ok
    internal const string Stub = """
        #!/bin/sh
        verb="$6"
        cat > /dev/null
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    // a gateway with the leader lease and the plant on (the revocations are read, through the stub), quick snapshot cadence and lock checks
    internal static GatewayEnvironment Environment(Action<GatewayEnvironment>? change = null)
    {
        var env = new GatewayEnvironment();
        env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        env.Set("FACTORY_LEADER_LEASE", null);
        env.Set("FACTORY_PLANT_SNAPSHOT", null);
        env.Set("GH_TOKEN", "fake");
        env.Set("FACTORY_TRANSCRIPTS", "0");
        env.Set("FACTORY_TIMING_leaderSqlCheckSeconds", "1");
        env.Set("FACTORY_TIMING_plantSnapshotDebounceSeconds", "1");
        change?.Invoke(env);
        env.Apply();
        File.WriteAllText(Path.Combine(env.Dir, "bin", "pwsh"), Stub.Replace("\r\n", "\n") + "\n");
        return env;
    }

    static async Task<JsonNode?> Status(GatewayHost host) =>
        (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;

    internal static async Task<JsonNode?> StatusUntil(GatewayHost host, Func<JsonNode?, bool> cond, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        JsonNode? s = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            s = await Status(host);
            if (cond(s)) return s;
            await Task.Delay(100);
        }
        return s;
    }

    internal static string Role(JsonNode? s) => (string?)s?["leader"]?["role"] ?? "";
    static JsonNode? Restore(JsonNode? s) => s?["plantSnapshot"]?["restore"];
    static long Saves(JsonNode? s) => (long?)s?["plantSnapshot"]?["saves"] ?? 0;

    static void WithDatabase(GatewayEnvironment e, TestDatabase db) { e.Set("FACTORY_DB_CONNECTION", db.ConnectionString); WolverineHostTestSupport.Shadow(e); }

    [Fact]
    public async Task Start_TakesTheLockRestoresFromTheDatabaseAndActs_ThenWritesThePlantWithItsEpoch()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lead1_");
        using var env = Environment(e => WithDatabase(e, db));
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1, 60);
        Assert.Equal("acting", Role(s));
        Assert.Equal(1L, (long?)s?["leader"]?["epoch"]);   // the SQL term is the epoch
        Assert.Equal(true, (bool?)s?["leader"]?["sql"]?["held"]);
        Assert.Equal(1L, (long?)s?["leader"]?["sql"]?["epoch"]);
        Assert.Null(s?["leader"]?["predecessor"]);          // the ref's fields are gone
        var ps = s?["plantSnapshot"];
        Assert.Equal(true, (bool?)ps?["enabled"]);
        Assert.Equal(true, (bool?)ps?["leader"]);
        Assert.Equal(1L, (long?)ps?["saves"]);
        Assert.Equal(0L, (long?)ps?["saveFailures"]);
        Assert.Equal(1L, (long?)ps?["heldEpoch"]);
        var r = Restore(s);
        Assert.Equal("sql", (string?)r?["how"]);
        Assert.Equal("start", (string?)r?["via"]);
        Assert.Equal("none", (string?)r?["skipped"]);       // the first start of the database: nothing stored yet
        Assert.Equal(1L, await db.Count("SELECT Epoch FROM factory.PlantState"));
        var dash = (await host.SendAsync("GET", "/api/v1/dashboard", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json;
        Assert.Equal("sql", (string?)dash?["plantSnapshot"]?["restore"]?["how"]);
        Assert.Equal("acting", (string?)dash?["leader"]?["role"]);
    }

    [Fact]
    public async Task Start_RestoresThePlantTheTermBeforeWrote_AndSaysWhichTerm()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lead2_");
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(db.ConnectionString).Options;
        var seed = new JsonObject { ["v"] = 1, ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = new JsonArray(), ["owners"] = new JsonObject { ["12"] = "alice" }, ["epoch"] = 5, ["final"] = true };
        Assert.True(await new SqlPlantStateStore(() => new FactoryDbContext(options)).Save(seed, 5, "the-old-holder"));
        using var env = Environment(e => WithDatabase(e, db));
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Restore(x) != null, 60);
        var r = Restore(s);
        Assert.Equal("sql", (string?)r?["how"]);
        Assert.Equal(5L, (long?)r?["snapshotEpoch"]);
        Assert.Equal(true, (bool?)r?["final"]);
        Assert.NotNull(r?["gapSince"]);
    }

    [Fact]
    public async Task ANewerProcessAsksForTheLock_TheGatewayStandsDownWithItsFinalPlant_Answers503AndLetsTheLockGo()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lead3_");
        using var env = Environment(e => WithDatabase(e, db));
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1, 60);
        Assert.Equal("acting", Role(s));
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
        // another process on the same database: the newest asks, the holder makes room, the newcomer takes the lock with the next term
        var other = new SqlLease(db.ConnectionString, _ => Task.CompletedTask, 20);
        try
        {
            var sw = Stopwatch.StartNew(); SqlLeaseAcquire got = default;
            while (!got.Acquired && sw.Elapsed < TimeSpan.FromSeconds(60)) { got = await other.TryAcquireAsync("the-newer-process", true, default); if (!got.Acquired) await Task.Delay(200); }
            Assert.True(got.Acquired); Assert.Equal(2, got.Epoch);
            s = await StatusUntil(host, x => Role(x) == "superseded");
            Assert.Equal("superseded", Role(s));
            Assert.Equal(false, (bool?)s?["leader"]?["sql"]?["held"]);
            Assert.Equal(1L, (long?)s?["leader"]?["sql"]?["yielded"]);   // (it yielded: a takeover, not a loss)
            Assert.Equal("takeover", (string?)s?["leader"]?["supersededBy"]?["why"]);
            var ready = await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort);
            Assert.Equal(503, ready.Status);
            Assert.Equal(true, (bool?)ready.Json?["replaced"]);
            // its final plant was written before the lock went: the row is the old term's, marked final
            Assert.Equal(1L, await db.Count("SELECT Epoch FROM factory.PlantState"));
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.PlantState WHERE Document LIKE N'%\"final\":true%'"));
            // and it writes no more
            var saves = Saves(s);
            await Task.Delay(2500);
            Assert.Equal(saves, Saves(await Status(host)));
        }
        finally { await other.ReleaseAsync(); }
    }

    [Fact]
    public async Task StopWhileActing_WritesTheFinalPlantThenLetsTheLockGo_SoTheSuccessorTakesItAtOnce()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lead4_");
        using var env = Environment(e => WithDatabase(e, db));
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Role(x) == "acting" && Saves(x) >= 1, 60);
            Assert.Equal("acting", Role(s));
            await host.DisposeAsync();
            disposed = true;
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.PlantState WHERE Document LIKE N'%\"final\":true%'"));   // the stop wrote the plant as a hand-over
            var next = new SqlLease(db.ConnectionString, _ => Task.CompletedTask, 20);
            try { var got = await next.TryAcquireAsync("the-successor", false, default); Assert.True(got.Acquired); Assert.Equal(2, got.Epoch); }   // the lock was free at once
            finally { await next.ReleaseAsync(); }
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task LeaseOff_PlantOn_RestoresFromTheDatabaseOnce_AndWritesItAtTheStop()
    {
        if (!StubPwsh.Supported || !TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_lead5_");
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(db.ConnectionString).Options;
        var seed = new JsonObject { ["v"] = 1, ["at"] = DateTime.UtcNow.ToString("o"), ["assignments"] = new JsonArray(), ["owners"] = new JsonObject { ["12"] = "alice" } };
        Assert.True(await new SqlPlantStateStore(() => new FactoryDbContext(options)).Save(seed, 0, "before-the-lease"));
        using var env = Environment(e => { WithDatabase(e, db); e.Set("FACTORY_LEADER_LEASE", "0"); });
        var host = new GatewayHost();
        var disposed = false;
        try
        {
            _ = host.Server;
            var s = await StatusUntil(host, x => Restore(x) != null, 60);
            Assert.Equal("off", Role(s));                                   // no lease: it acts at once
            Assert.Null(s?["leader"]?["sql"]);
            Assert.Equal(true, (bool?)s?["plantSnapshot"]?["enabled"]);
            Assert.Null(s?["plantSnapshot"]?["leader"]);
            Assert.Equal("sql", (string?)Restore(s)?["how"]);
            Assert.Equal("", (string?)Restore(s)?["skipped"]);
            await host.DisposeAsync();
            disposed = true;
            Assert.Equal(1L, await db.Count("SELECT COUNT(*) FROM factory.PlantState WHERE Holder <> N'before-the-lease'"));   // the stop wrote the plant under this process's name
        }
        finally { if (!disposed) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task WithoutADatabase_ThereIsNoLease_TheGatewayActsAtOnce_AndKeepsNoPlant()
    {
        if (!StubPwsh.Supported) return;
        using var env = Environment();
        await using var host = new GatewayHost();
        _ = host.Server;
        var s = await StatusUntil(host, x => (string?)x?["leader"]?["role"] == "off");
        Assert.Equal("off", Role(s));
        Assert.Equal(false, (bool?)s?["leader"]?["enabled"]);
        Assert.Null(s?["leader"]?["sql"]);
        Assert.Equal(false, (bool?)s?["plantSnapshot"]?["enabled"]);
        Assert.Equal(200, (await host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
    }
}
