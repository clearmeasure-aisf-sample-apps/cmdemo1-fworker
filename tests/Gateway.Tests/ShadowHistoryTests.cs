using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// Shadow history (task W2.3, gateway/Ports/ShadowHistory.cs): in a shadow environment the ingress events and the journal lines are recorded into ItemHistory,
/// best-effort, after v1 has acted. These tests have no database: the recorder with a stand-in publisher, the ingress decorator, and the registration.
/// </summary>
public sealed class ShadowHistoryRecorderTests
{
    static readonly RepoId Home = RepoId.Parse("acme/work");

    sealed class Sink
    {
        public readonly List<ShadowHistoryRecord> Got = new();
        public Task<bool> Publish(ShadowHistoryRecord r) { lock (Got) Got.Add(r); return Task.FromResult(true); }
        public List<ShadowHistoryRecord> Wait(int n)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until) { lock (Got) if (Got.Count >= n) break; Thread.Sleep(10); }
            lock (Got) return Got.ToList();
        }
    }

    [Fact]
    public void AWebhookEventIsRecordedWithItsItemAndTypeAndTheEventJson()
    {
        var sink = new Sink(); var h = new ShadowHistory(sink.Publish, Home);
        h.Event(new WebhookEventIngested("{\"event\":\"pr.opened\",\"item\":5}"));
        var r = Assert.Single(sink.Wait(1));
        Assert.Equal("acme/work#5", r.ItemKey); Assert.Equal("ingress.webhook.pr.opened", r.Type); Assert.Equal("{\"event\":\"pr.opened\",\"item\":5}", r.Detail);
    }

    [Fact]
    public void ASweepRequestIsRecordedForItsItem_AndForTheBoardWithoutOne()
    {
        var sink = new Sink(); var h = new ShadowHistory(sink.Publish, Home);
        h.Event(new SweepRequested(7)); h.Event(new SweepRequested());
        var got = sink.Wait(2);
        Assert.Equal(new[] { "acme/work#7", "board" }, got.Select(r => r.ItemKey).OrderBy(x => x).ToArray());
        Assert.All(got, r => Assert.Equal("ingress.sweep-requested", r.Type));
    }

    [Fact]
    public void AJournalLineIsRecordedWithItsEventKeyFieldsAndWhetherItWasPosted()
    {
        var sink = new Sink(); var h = new ShadowHistory(sink.Publish, Home);
        var fields = new JsonObject { ["worker"] = "w1" };
        h.Journal(12, "matched", fields, "m|1", true);
        fields["worker"] = "changed-after";   // the record is a copy
        var r = Assert.Single(sink.Wait(1));
        Assert.Equal("acme/work#12", r.ItemKey); Assert.Equal("v1.journal.matched", r.Type);
        var d = JsonNode.Parse(r.Detail!)!;
        Assert.Equal("matched", (string?)d["event"]); Assert.Equal("m|1", (string?)d["key"]); Assert.True((bool)d["posted"]!); Assert.Equal("w1", (string?)d["fields"]!["worker"]);
    }

    [Fact]
    public void ALongDetailIsCut()
    {
        var sink = new Sink(); var h = new ShadowHistory(sink.Publish, Home);
        h.Journal(1, "x", new JsonObject { ["t"] = new string('a', 50000) }, "k", true);
        Assert.Equal(ShadowHistory.MaxDetailChars, Assert.Single(sink.Wait(1)).Detail!.Length);
    }

    [Fact]
    public void APublishThatThrowsOrRefuses_NeverReachesTheCaller_AndIsCounted()
    {
        var calls = 0;
        var h = new ShadowHistory(r => { calls++; if (calls == 1) throw new InvalidOperationException("db down"); return Task.FromResult(false); }, Home);
        h.Event(new SweepRequested(1)); h.Event(new SweepRequested(2));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && ((long)h.Status()["failed"]! + (long)h.Status()["dropped"]!) < 2) Thread.Sleep(10);
        Assert.Equal(1L, (long)h.Status()["failed"]!); Assert.Equal(1L, (long)h.Status()["dropped"]!); Assert.Equal(0L, (long)h.Status()["recorded"]!);
    }

    [Fact]
    public void ABacklogBeyondTheLimitDropsTheNewest_AndNeverBlocks()
    {
        var gate = new TaskCompletionSource<bool>();
        var h = new ShadowHistory(r => gate.Task, Home, maxInFlight: 3);
        for (var i = 1; i <= 10; i++) h.Event(new SweepRequested(i));
        Assert.Equal(7L, (long)h.Status()["dropped"]!); Assert.Equal(3, (int)h.Status()["inFlight"]!);
        gate.SetResult(true);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && (long)h.Status()["recorded"]! < 3) Thread.Sleep(10);
        Assert.Equal(3L, (long)h.Status()["recorded"]!);
    }

    [Fact]
    public void TheIngressDecorator_RunsV1First_ThenRecords_AndARecordingFailureDoesNotChangeV1()
    {
        var hub = new EventHub();
        var recording = new Sink(); var good = new ShadowEventIngress(new V1EventIngress(hub), new ShadowHistory(recording.Publish, Home));
        good.Ingest(new SweepRequested(7)); good.Ingest(new WebhookEventIngested("{\"event\":\"pr.opened\",\"item\":5}"));
        Assert.Equal(new List<int> { 7 }, hub.TakeSweepItems());
        Assert.Equal("{\"event\":\"pr.opened\",\"item\":5}", Assert.Single(hub.Subscribe(0).Item2).Item2);
        Assert.Equal(2, recording.Wait(2).Count);

        // a recorder that throws on every call: v1 still acts
        var hub2 = new EventHub();
        var bad = new ShadowEventIngress(new V1EventIngress(hub2), new ThrowingShadow());
        bad.Ingest(new SweepRequested(9)); bad.Ingest(new WebhookEventIngested("{\"event\":\"x\"}"));
        Assert.Equal(new List<int> { 9 }, hub2.TakeSweepItems());
        Assert.Single(hub2.Subscribe(0).Item2);
    }

    sealed class ThrowingShadow : IShadowHistory
    {
        public void Event(IngressEvent ev) => throw new InvalidOperationException("boom");
        public void Journal(int item, string ev, JsonObject fields, string key, bool posted) => throw new InvalidOperationException("boom");
        public JsonObject Status() => new();
    }
}

/// <summary>Shadow off (every gateway of the other tests, and a gateway with a connection string whose environment is not listed): nothing is recorded, nothing is registered.</summary>
public sealed class ShadowHistoryOffTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void NoShadowRegistered_AndTheIngressIsV1Alone()
    {
        if (!StubPwsh.Supported) return;
        Assert.DoesNotContain(gw.Host.Captured!, d => d.ServiceType == typeof(IShadowHistory));
        Assert.IsType<V1EventIngress>(gw.Host.Services.GetRequiredService<IEventIngress>());
    }
}

public sealed class ShadowHistoryNotListedFixture : GatewayFixture
{
    // a connection string and an environment (tdd) that no list names: v1 alone, no host, no shadow, no database call
    protected override void Configure(GatewayEnvironment env) => env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
}

public sealed class ShadowHistoryNotListedTests(ShadowHistoryNotListedFixture gw) : IClassFixture<ShadowHistoryNotListedFixture>
{
    [Fact]
    public async Task AConnectionStringWithoutTheEnvironmentInTheList_RegistersNoHostAndNoShadow()
    {
        if (!StubPwsh.Supported) return;
        foreach (var t in WolverineHostTestSupport.HostTypes.Append(typeof(IShadowHistory))) Assert.DoesNotContain(gw.Host.Captured!, d => d.ServiceType == t);
        Assert.IsType<V1EventIngress>(gw.Host.Services.GetRequiredService<IEventIngress>());
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Null(a.Json?["wolverine"]);
    }
}

public sealed class ShadowHistoryKilledFixture : GatewayFixture
{
    // listed, and FACTORY_V2_OFF: the kill switch puts the environment back on v1 with no shadow, so no host
    protected override void Configure(GatewayEnvironment env) { env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("FACTORY_V2_OFF", "1"); WolverineHostTestSupport.Shadow(env); }
}

public sealed class ShadowHistoryKilledTests(ShadowHistoryKilledFixture gw) : IClassFixture<ShadowHistoryKilledFixture>
{
    [Fact]
    public void TheKillSwitch_RegistersNoHostAndNoShadow()
    {
        if (!StubPwsh.Supported) return;
        foreach (var t in WolverineHostTestSupport.HostTypes.Append(typeof(IShadowHistory))) Assert.DoesNotContain(gw.Host.Captured!, d => d.ServiceType == t);
        Assert.IsType<V1EventIngress>(gw.Host.Services.GetRequiredService<IEventIngress>());
    }
}

/// <summary>Shadow on, the database unreachable: the gateway is v1 as before, the events are dropped (counted), and the answers do not change.</summary>
public sealed class ShadowHistoryUnreachableFixture : GatewayFixture
{
    protected override void Configure(GatewayEnvironment env) { env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); }
}

public sealed class ShadowHistoryUnreachableTests(ShadowHistoryUnreachableFixture gw) : IClassFixture<ShadowHistoryUnreachableFixture>
{
    [Fact]
    public async Task ARecordingThatCannotBeStored_DoesNotChangeV1()
    {
        if (!StubPwsh.Supported) return;
        var shadow = gw.Host.Services.GetRequiredService<IShadowHistory>();
        var ingress = gw.Host.Services.GetRequiredService<IEventIngress>();
        Assert.IsType<ShadowEventIngress>(ingress);
        var hub = gw.Host.Services.GetRequiredService<EventHub>();
        ingress.Ingest(new SweepRequested(41));
        Assert.Contains(41, hub.TakeSweepItems());   // v1 acted
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && (long)shadow.Status()["dropped"]! + (long)shadow.Status()["failed"]! < 1) await Task.Delay(20);
        Assert.Equal(0L, (long)shadow.Status()["recorded"]!);
        Assert.True((long)shadow.Status()["dropped"]! + (long)shadow.Status()["failed"]! >= 1);
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        Assert.NotNull(a.Json?["wolverine"]?["shadowHistory"]);
        Assert.Equal(200, (await gw.Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status);
    }
}

/// <summary>Against SQL Server (FACTORY_TEST_SQL): the events and journal lines reach ItemHistory through the durable queue.</summary>
public sealed class ShadowHistorySqlServerTests
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

    static async Task Run(bool shadow, Func<string, GatewayHost, Task> body)
    {
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w23_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", cs);
        if (shadow) WolverineHostTestSupport.Shadow(env);
        env.Apply();
        var host = new GatewayHost();
        try
        {
            _ = host.Server;
            await body(cs, host);
        }
        finally
        {
            await host.DisposeAsync();
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static async Task WaitRunning(GatewayHost host)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            var w = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["wolverine"];
            if ((string?)w?["state"] == "running") return;
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException("the Wolverine host did not start within 90 seconds");
    }

    static async Task<long> WaitCount(string cs, string sql, long atLeast, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds); long n = 0;
        while (DateTime.UtcNow < until) { n = await Count(cs, sql); if (n >= atLeast) break; await Task.Delay(250); }
        return n;
    }

    [Fact]
    public async Task ShadowOn_RecordsEventsAndJournalLines_AndV1StillActs()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await Run(true, async (cs, host) =>
        {
            await WaitRunning(host);
            var ingress = host.Services.GetRequiredService<IEventIngress>();
            var hub = host.Services.GetRequiredService<EventHub>();
            ingress.Ingest(new WebhookEventIngested("{\"event\":\"pr.opened\",\"item\":5}"));
            ingress.Ingest(new SweepRequested(7));
            Assert.Contains(7, hub.TakeSweepItems());   // v1 acted
            host.Services.GetRequiredService<Plant>().JournalManagerAction(9, new JsonObject { ["action"] = "restart" }, "shadow-test|1");

            Assert.Equal(3, await WaitCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory", 3));
            var rows = await Rows(cs, "SELECT ItemKey, Kind, Type, CASE WHEN MessageId IS NULL THEN 0 ELSE 1 END FROM factory.ItemHistory ORDER BY Type");
            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, r => r.EndsWith("#5|event|ingress.webhook.pr.opened|1"));
            Assert.Contains(rows, r => r == "board|event|ingress.sweep-requested|1" || r.EndsWith("#7|event|ingress.sweep-requested|1"));
            Assert.Contains(rows, r => r.EndsWith("#9|event|v1.journal.manager-action|1"));
            var detail = (await Rows(cs, "SELECT Detail FROM factory.ItemHistory WHERE Type='v1.journal.manager-action'"))[0];
            Assert.Equal("restart", (string?)JsonNode.Parse(detail)!["fields"]!["action"]);
            // the durable queue's storage is the wolverine schema (the envelope is removed once handled)
            Assert.Equal(1, await Count(cs, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='wolverine' AND TABLE_NAME='wolverine_incoming_envelopes'"));
            var st = (await host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken)).Json?["wolverine"]?["shadowHistory"];
            Assert.Equal(3L, (long)st!["recorded"]!); Assert.Equal(0L, (long)st["failed"]!);
        });
    }

    [Fact]
    public async Task ShadowOff_ARegisteredConnectionString_RecordsNothing_AndCreatesNoTable()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await Run(false, async (cs, host) =>
        {
            host.Services.GetRequiredService<IEventIngress>().Ingest(new SweepRequested(7));
            host.Services.GetRequiredService<Plant>().JournalManagerAction(9, new JsonObject { ["action"] = "restart" }, "shadow-test|2");
            await Task.Delay(3000);
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('ItemHistory','wolverine_incoming_envelopes')"));
        });
    }

    [Fact]
    public async Task ARedeliveredRecord_WritesItsRowOnce()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await Run(true, async (cs, host) =>
        {
            await WaitRunning(host);
            var store = new ShadowHistoryStore(cs);
            var rec = new ShadowHistoryRecord("acme/work#1", "ingress.sweep-requested", null, DateTimeOffset.UtcNow);
            var id = Guid.NewGuid();
            await store.WriteAsync(rec, id, CancellationToken.None); await store.WriteAsync(rec, id, CancellationToken.None);
            Assert.Equal(1, await Count(cs, "SELECT COUNT(*) FROM factory.ItemHistory"));
        });
    }
}
