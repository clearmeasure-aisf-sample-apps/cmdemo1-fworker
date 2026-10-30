using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Delivery;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W5.2 on the real gateway host (the shadow environment tdd, a Wolverine host on SQL Server FACTORY_TEST_SQL, a stub of GitHub and of the apps): the merge webhook and the Octopus watcher's events
/// feed the delivery train, its decision is compared with v1's pass, the comparison is stored, and the report is on the status and on GET /api/v1/delivery-shadow. The stub records every request:
/// the shadow only reads. Each test returns early where FACTORY_TEST_SQL is unset or the platform has no stub pwsh.
/// </summary>
public sealed class DeliveryShadowGatewayTests
{
    const string Owner = "clearmeasure-aisf-sample-apps", Repo = "cmdemo1-workorders";
    static readonly string A = new('a', 40), D = new('d', 40);

    sealed class Stub : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<(string Method, string Path)> seen = [];
        readonly CancellationTokenSource stop = new();
        public int Port { get; }
        public volatile bool FailCompare;   // GitHub's compare answers 500: the read of a release's range fails
        public string Base => $"http://127.0.0.1:{Port}";

        public Stub()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Add(Base + "/"); listener.Start();
            _ = Task.Run(Loop);
        }

        public IReadOnlyList<(string Method, string Path)> Seen { get { lock (seen) return seen.ToList(); } }

        async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                var path = c.Request.Url!.AbsolutePath;
                lock (seen) seen.Add((c.Request.HttpMethod, path));
                var (status, body) = Answer(c.Request.HttpMethod, path);
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        }

        (int, string) Answer(string method, string path)
        {
            if (method != "GET") return (405, "{}");
            if (FailCompare && path.Contains("/compare/")) return (500, "{}");
            var repo = $"/repos/{Owner}/{Repo}";
            if (path == repo + "/pulls/12")
                return (200, new JsonObject { ["number"] = 12, ["merged"] = true, ["base"] = new JsonObject { ["ref"] = "master" }, ["head"] = new JsonObject { ["ref"] = "someone/7-thing" }, ["title"] = "Thing (#12)", ["body"] = "Closes #7", ["merge_commit_sha"] = A }.ToJsonString());
            if (path == repo + "/issues/7")
                return (200, new JsonObject { ["number"] = 7, ["labels"] = new JsonArray(new JsonObject { ["name"] = "Auto Merge" }), ["body"] = "A thing." }.ToJsonString());
            if (path == repo + "/issues/7/comments")
            {
                var state = new JsonObject { ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = 12 }, ["merge"] = new JsonObject { ["pr"] = 12, ["by"] = "gateway", ["sha"] = A, ["done_at"] = "2026-10-28T09:00:00Z" } };
                return (200, new JsonArray(new JsonObject { ["body"] = ItemStateComment.Body("factory-worker", 7, state) }).ToJsonString());
            }
            if (path == $"{repo}/compare/{D}...{A}")
                return (200, new JsonObject { ["status"] = "ahead", ["total_commits"] = 1, ["commits"] = new JsonArray(new JsonObject { ["sha"] = A, ["parents"] = new JsonArray(new JsonObject { ["sha"] = D }), ["commit"] = new JsonObject { ["message"] = "Thing (#12)" } }) }.ToJsonString());
            if (path == "/app/tdd/_build") return (200, new JsonObject { ["version"] = "1.0.2" }.ToJsonString());
            if (path == "/app/tdd/_healthcheck/detailed") return (200, new JsonObject { ["overallStatus"] = "Healthy" }.ToJsonString());
            return (404, "{}");
        }

        public void Dispose() { stop.Cancel(); try { listener.Close(); } catch { } }
    }

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
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    static Task WithShadowGateway(Func<string, GatewayHost, Stub, Task> body) => WithShadowDatabase(async (cs, stub, start) =>
    {
        var host = start();
        try { await body(cs, host, stub); }
        finally { await host.DisposeAsync(); }
    });

    /// <summary>The database, the stub and the environment of a shadow gateway; <c>start</c> makes a gateway on them, and a second one after the first is disposed is a restart on the same database.</summary>
    static async Task WithShadowDatabase(Func<string, Stub, Func<GatewayHost>, Task> body)
    {
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w52_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        using var stub = new Stub();
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", cs);
        env.Set("GH_TOKEN", "ghp_" + new string('B', 26));   // composed at run time; the stub accepts anything
        env.Set("GITHUB_API_URL", stub.Base);
        env.EditConfig = cfg =>
        {
            cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray(), ["shadowEnvironments"] = new JsonArray("tdd") };
            var oct = (JsonObject)cfg["octopus"]!;
            oct["tddUrl"] = stub.Base + "/app/tdd";
            oct["health"] = new JsonObject { ["spacingMinutes"] = 5, ["settleMinutes"] = 10 };
            cfg["delivery"] = new JsonObject { ["autoPromote"] = new JsonObject { ["enabled"] = true, ["environments"] = new JsonArray("uat", "prod"), ["gatewayEnvironment"] = "tdd" } };
        };
        env.Apply();
        try
        {
            await body(cs, stub, () => { var host = new GatewayHost(); _ = host.Server; return host; });
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static Task<Answer> Get(GatewayHost host, string path) => host.SendAsync("GET", path, GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);

    [SqlFact]
    public async Task TheMergeAndTheOctopusEvents_FeedTheTrain_V1sPassIsCompared_AndTheReportShowsIt()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithShadowGateway(async (cs, host, stub) =>
        {
            Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["wolverine"]?["state"]?.ToString() == "running", 90), "the host did not start");
            var status = (await Get(host, "/api/v1/status")).Json!;
            Assert.NotNull(status["deliveryShadow"]);
            Assert.Equal("shadow", status["engine"]?["mode"]?.ToString());
            var ingress = host.Services.GetRequiredService<IEventIngress>();
            // the merge webhook (v1's ingest-event answers base.merged) reaches the inbox, and ItemMerged is derived from it: the second handler of the inbox on the real host
            ingress.Ingest(new WebhookEventIngested(new JsonObject { ["provider"] = "github", ["event"] = "base.merged", ["ref"] = "delivery-1", ["pr"] = 12, ["sha"] = A }.ToJsonString()));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.MergedItems"))[0] == "1", 40), "ItemMerged was not derived");
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'delivery.item-merged'"))[0] == "1", 20));
            // the Octopus watcher's events: the baseline 1.0.1 everywhere, then 1.0.2 (the merge's commit) deployed to tdd
            ingress.Ingest(new OctopusReleaseCreated("R-1", "1.0.1", D));
            foreach (var e in new[] { "tdd", "uat", "prod" })
            {
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Started, D));
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Succeeded, D));
            }
            ingress.Ingest(new OctopusReleaseCreated("R-2", "1.0.2", A));
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Started, A));
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Succeeded, A));
            // the train judges 1.0.2 for uat: the tdd app has not been Healthy long enough yet, so it waits
            // (the uat train's own decision for 1.0.2, not any decision: the train says "following" while the tdd deployment still runs, and its other trains decide earlier)
            const string uatWaiting = "SELECT COUNT(*) FROM factory.DeliveryTrains WHERE [Key] LIKE '%/uat' AND StateJson LIKE '%waiting|1.0.2|%'";
            Assert.True(await Until(async () => (await Rows(cs, uatWaiting))[0] == "1", 60), "the uat train did not wait for 1.0.2: " + string.Join(" // ", await Rows(cs, "SELECT [Key] + ' ' + LEFT(StateJson, 200) + CAST(Revision AS varchar) FROM factory.DeliveryTrains")));
            // v1's pass (its AutoPromoter hands the actions of the verb auto-promote over) says the same
            var holder = host.Services.GetRequiredService<DeliveryShadowHolder>();
            holder.Shadow!.ObserveV1(new JsonArray(new JsonObject { ["item"] = 7, ["action"] = "promotion-waiting", ["release"] = "1.0.2", ["environment"] = "uat", ["items"] = new JsonArray(7), ["reasons"] = new JsonArray("the tdd app has not been Healthy long enough yet") }));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Release = '1.0.2' AND Outcome = 'match'"))[0] == "1", 60), "no comparison was stored: " + holder.Shadow.Status().ToJsonString() + " // " + string.Join(" // ", await Rows(cs, "SELECT Outcome + ' ' + Release + ' ' + V1Action + '/' + V2Action + ' ' + LEFT(Reason, 300) FROM factory.DeliveryShadowComparisons")));
            // the report: an authenticated read of the stored comparisons
            var report = await Get(host, "/api/v1/delivery-shadow?limit=5");
            Assert.Equal(200, report.Status);
            Assert.Equal(1, report.Json!["summary"]!["counts"]!["match"]!.GetValue<int>());
            Assert.Equal("1.0.2", report.Json["comparisons"]![0]!["release"]!.ToString());
            Assert.Equal("wait", report.Json["comparisons"]![0]!["v2Action"]!.ToString());
            Assert.Equal(200, (await Get(host, "/api/v1/delivery-shadow?outcome=differs")).Status);
            Assert.Equal(400, (await Get(host, "/api/v1/delivery-shadow?outcome=bogus")).Status);
            Assert.Equal(401, (await host.SendAsync("GET", "/api/v1/delivery-shadow", GatewayEnvironment.ApiPort)).Status);
            // the shadow only read: every request to GitHub and the apps was a GET
            Assert.NotEmpty(stub.Seen);
            Assert.All(stub.Seen, r => Assert.Equal("GET", r.Method));
            // and nothing was executed: no command of the train left the process
            var live = (await Get(host, "/api/v1/status")).Json!["deliveryShadow"]!;
            Assert.Equal(0, live["v2"]!["commandsExecuted"]!.GetValue<long>());
            Assert.True(0 == long.Parse((await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]), string.Join(" // ", await Rows(cs, "SELECT message_type, exception_type, LEFT(exception_message, 400) FROM wolverine.wolverine_dead_letters")));
        });
    }

    [SqlFact]
    public async Task TheWatchersReplay_IsAppliedWithoutADecision_AndJudgedOnceWhenItEnds()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithShadowGateway(async (cs, host, stub) =>
        {
            Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["wolverine"]?["state"]?.ToString() == "running", 90), "the host did not start");
            var ingress = host.Services.GetRequiredService<IEventIngress>();
            long Decisions() => host.Services.GetRequiredService<DeliveryShadowHolder>().Shadow!.Status()["v2"]!["decisions"]!.GetValue<long>();
            ingress.Ingest(new OctopusReplayBegan());
            ingress.Ingest(new OctopusReleaseCreated("R-1", "1.0.1", D));
            foreach (var e in new[] { "tdd", "uat", "prod" })
            {
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Started, D));
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Succeeded, D));
            }
            // the newest release is seen first, its deployment to tdd after it, as the watcher publishes a replay
            ingress.Ingest(new OctopusReleaseCreated("R-2", "1.0.2", A));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.DeliveryTrains WHERE Revision >= 8"))[0] != "0", 40), "the trains took no input in");
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Started, A));
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Succeeded, A));
            await Task.Delay(4000);
            Assert.Equal(0, Decisions());   // the replay is not over: nothing is decided on its partial state
            ingress.Ingest(new OctopusReplayComplete());
            Assert.True(await Until(() => Task.FromResult(Decisions() >= 1), 60), "the train did not judge its state at the end of the replay");
            Assert.True(0 == long.Parse((await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]), string.Join(" // ", await Rows(cs, "SELECT message_type, exception_type, LEFT(exception_message, 400) FROM wolverine.wolverine_dead_letters")));
        });
    }

    [SqlFact]
    public async Task ARestartOnTheSameDatabase_PublishesTheTrainsDecisionsAgain_AfterTheSecondReplay()
    {
        // tdd 1.0.838 and 1.0.839: the state of the first process held the fingerprint of every decision, so the second one replayed, judged, found nothing new and published nothing; its shadow stayed empty
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithShadowDatabase(async (cs, stub, start) =>
        {
            async Task Replay(GatewayHost host)
            {
                Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["wolverine"]?["state"]?.ToString() == "running", 90), "the host did not start");
                var ingress = host.Services.GetRequiredService<IEventIngress>();
                ingress.Ingest(new OctopusReplayBegan());
                ingress.Ingest(new OctopusReleaseCreated("R-1", "1.0.1", D));
                foreach (var e in new[] { "tdd", "uat", "prod" })
                {
                    ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Started, D));
                    ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Succeeded, D));
                }
                ingress.Ingest(new OctopusReleaseCreated("R-2", "1.0.2", A));
                ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Started, A));
                ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Succeeded, A));
                ingress.Ingest(new OctopusReplayComplete());
            }
            long V2Decisions(GatewayHost host) => host.Services.GetRequiredService<DeliveryShadowHolder>().Shadow!.Status()["v2"]!["decisions"]!.GetValue<long>();
            const string uatWaiting = "SELECT COUNT(*) FROM factory.DeliveryTrains WHERE [Key] LIKE '%/uat' AND StateJson LIKE '%waiting|1.0.2|%'";
            var first = start();
            try
            {
                await Replay(first);
                Assert.True(await Until(() => Task.FromResult(V2Decisions(first) >= 1), 60), "the first process published no decision");
                Assert.Equal("1", (await Rows(cs, uatWaiting))[0]);   // (and the train's state holds what it said)
            }
            finally { await first.DisposeAsync(); }
            // the second process: the database has the train's state with its decisions; the shadow in memory is new
            var second = start();
            try
            {
                Assert.Equal(0, V2Decisions(second));
                await Replay(second);
                Assert.True(await Until(() => Task.FromResult(V2Decisions(second) >= 1), 60), "the restarted train published no decision after its replay: " + second.Services.GetRequiredService<DeliveryShadowHolder>().Shadow!.Status().ToJsonString());
                Assert.True(0 == long.Parse((await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]), string.Join(" // ", await Rows(cs, "SELECT message_type, exception_type, LEFT(exception_message, 400) FROM wolverine.wolverine_dead_letters")));
                Assert.All(stub.Seen, r => Assert.Equal("GET", r.Method));   // still read only
            }
            finally { await second.DisposeAsync(); }
        });
    }

    [SqlFact]
    public async Task ARangeThatCannotBeRead_DoesNotSilenceTheTrain_TheOtherReleasesAreStillDecided()
    {
        // tdd 1.0.833: the replay ended with a release that succeeded in tdd whose range GitHub would not give (the shadow's reads are limited per minute); the whole look was thrown away and the train decided nothing
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithShadowGateway(async (cs, host, stub) =>
        {
            stub.FailCompare = true;
            Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["wolverine"]?["state"]?.ToString() == "running", 90), "the host did not start");
            var ingress = host.Services.GetRequiredService<IEventIngress>();
            ingress.Ingest(new OctopusReplayBegan());
            ingress.Ingest(new OctopusReleaseCreated("R-1", "1.0.1", D));
            foreach (var e in new[] { "tdd", "uat", "prod" })
            {
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Started, D));
                ingress.Ingest(new OctopusDeploymentChanged($"D-1-{e}", $"T-1-{e}", "R-1", "1.0.1", e, OctopusDeploymentPhase.Succeeded, D));
            }
            // 1.0.3 failed in tdd (a decision needs no read), 1.0.2 succeeded there (its range is unreadable)
            var c = new string('c', 40);
            ingress.Ingest(new OctopusReleaseCreated("R-3", "1.0.3", c));
            ingress.Ingest(new OctopusDeploymentChanged("D-3-tdd", "T-3-tdd", "R-3", "1.0.3", "tdd", OctopusDeploymentPhase.Started, c));
            ingress.Ingest(new OctopusDeploymentChanged("D-3-tdd", "T-3-tdd", "R-3", "1.0.3", "tdd", OctopusDeploymentPhase.Failed, c));
            ingress.Ingest(new OctopusReleaseCreated("R-2", "1.0.2", A));
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Started, A));
            ingress.Ingest(new OctopusDeploymentChanged("D-2-tdd", "T-2-tdd", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Succeeded, A));
            ingress.Ingest(new OctopusReplayComplete());
            // the uat train keeps the decision it published for each release in its state: 1.0.3's (a wait that needs no read) must be there although 1.0.2 could not be judged
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.DeliveryTrains WHERE [Key] LIKE '%/uat' AND StateJson LIKE '%\"Decisions\":{%\"1.0.3\":%'"))[0] != "0", 60), "the uat train decided nothing for 1.0.3 at the end of the replay");
        });
    }

    [Fact]
    public async Task WithoutTheShadow_TheReportRoute_Answers404()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Apply();
        var host = new GatewayHost();
        try
        {
            _ = host.Server;
            var a = await host.SendAsync("GET", "/api/v1/delivery-shadow", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
            Assert.Equal(404, a.Status);
            Assert.Equal("no-delivery-shadow", a.Code);
            Assert.Null((await Get(host, "/api/v1/status")).Json?["deliveryShadow"]);
        }
        finally { await host.DisposeAsync(); }
    }
}
