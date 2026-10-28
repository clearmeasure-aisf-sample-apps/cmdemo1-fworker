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

        static (int, string) Answer(string method, string path)
        {
            if (method != "GET") return (405, "{}");
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

    static async Task WithShadowGateway(Func<string, GatewayHost, Stub, Task> body)
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
        var host = new GatewayHost(); var disposed = false;
        try
        {
            _ = host.Server;
            await body(cs, host, stub);
            await host.DisposeAsync(); disposed = true;
        }
        finally
        {
            if (!disposed) await host.DisposeAsync();
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static Task<Answer> Get(GatewayHost host, string path) => host.SendAsync("GET", path, GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);

    [Fact]
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
            Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["deliveryShadow"]?["v2"]?["decisions"]?.GetValue<long>() >= 1, 60), "the train decided nothing");
            // v1's pass (its AutoPromoter hands the actions of the verb auto-promote over) says the same
            var holder = host.Services.GetRequiredService<DeliveryShadowHolder>();
            holder.Shadow!.ObserveV1(new JsonArray(new JsonObject { ["item"] = 7, ["action"] = "promotion-waiting", ["release"] = "1.0.2", ["environment"] = "uat", ["items"] = new JsonArray(7), ["reasons"] = new JsonArray("the tdd app has not been Healthy long enough yet") }));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Release = '1.0.2' AND Outcome = 'match'"))[0] == "1", 60), "no comparison was stored");
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

    [Fact]
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
