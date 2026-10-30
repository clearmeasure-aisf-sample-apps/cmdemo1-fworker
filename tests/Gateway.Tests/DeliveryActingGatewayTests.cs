using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.4 on the real gateway host: an environment where the engine acts (tdd in v2Environments, a Wolverine host on SQL Server FACTORY_TEST_SQL, stubs of GitHub and Octopus). The delivery train is attached and
/// takes the Octopus watcher's events in, PromoteRelease and AnswerSignOff are executed against Octopus from their own durable queue (once, whatever is published again), and nothing lands in the dead letters.
/// Each test returns early where FACTORY_TEST_SQL is unset or the platform has no stub pwsh.
/// </summary>
public sealed class DeliveryActingGatewayTests
{
    static readonly string A = new('a', 40), D = new('d', 40);

    sealed class Stub : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<(string Method, string Path, string Body)> seen = [];
        readonly CancellationTokenSource stop = new();
        public int Port { get; }
        public string Base => $"http://127.0.0.1:{Port}";

        public Stub()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Add(Base + "/"); listener.Start();
            _ = Task.Run(Loop);
        }

        public IReadOnlyList<(string Method, string Path, string Body)> Seen { get { lock (seen) return seen.ToList(); } }

        async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                string body; using (var rd = new StreamReader(c.Request.InputStream)) body = await rd.ReadToEndAsync();
                var path = c.Request.Url!.AbsolutePath;
                lock (seen) seen.Add((c.Request.HttpMethod, path + c.Request.Url.Query, body));
                var (status, text) = Answer(c.Request.HttpMethod, path, c.Request.Url.Query);
                var bytes = Encoding.UTF8.GetBytes(text);
                c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        }

        // Octopus, under /octopus: the project, a release, the environments, the deployments of the release (none to uat), the pending Sign-off
        (int, string) Answer(string method, string path, string query)
        {
            const string o = "/octopus/api/Spaces-355";
            if (method == "GET" && path == o + "/projects/cmdemo1-ui") return (200, """{"Id":"Projects-1"}""");
            if (method == "GET" && path == o + "/projects/Projects-1/releases/1.0.2") return (200, """{"Id":"Releases-2"}""");
            if (method == "GET" && path == o + "/environments") return (200, """{"Items":[{"Id":"E1","Name":"tdd"},{"Id":"E2","Name":"uat"}]}""");
            if (method == "GET" && path == o + "/releases/Releases-2/deployments") return (200, """{"Items":[{"EnvironmentId":"E1","TaskId":"T1"}]}""");
            if (method == "POST" && path == o + "/deployments/create/untenanted/v1") return (200, """{"DeploymentServerTasks":[{"ServerTaskId":"ServerTasks-9","DeploymentId":"Deployments-9"}]}""");
            if (method == "GET" && path == o + "/interruptions") return (200, """{"Items":[{"Id":"Interruptions-3","IsPending":true}]}""");
            if (method == "PUT" && path == o + "/interruptions/Interruptions-3/responsible") return (200, "{}");
            if (method == "POST" && path == o + "/interruptions/Interruptions-3/submit") return (200, "{}");
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

    static async Task WithActingGateway(Func<string, GatewayHost, Stub, Task> body)
    {
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w94_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        using var stub = new Stub();
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", cs);
        env.Set("GH_TOKEN", "ghp_" + new string('B', 26));   // composed at run time; the stub accepts anything
        env.Set("GITHUB_API_URL", stub.Base);
        env.Set("OCTOPUS_API_KEY", "API-" + new string('R', 20));
        env.EditConfig = cfg =>
        {
            cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            var oct = (JsonObject)cfg["octopus"]!;
            oct["url"] = stub.Base + "/octopus";
            cfg["delivery"] = new JsonObject { ["autoPromote"] = new JsonObject { ["enabled"] = true, ["environments"] = new JsonArray("uat", "prod"), ["gatewayEnvironment"] = "tdd" } };
        };
        env.Apply();
        GatewayHost? host = null;
        try
        {
            host = new GatewayHost(); _ = host.Server;
            await body(cs, host, stub);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static Task<Answer> Get(GatewayHost host, string path) => host.SendAsync("GET", path, GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);

    [Fact]
    public async Task TheTrainTakesTheWatchersEventsIn_AndItsCommandsAreExecutedOnce()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithActingGateway(async (cs, host, stub) =>
        {
            Assert.True(await Until(async () => (await Get(host, "/api/v1/status")).Json?["wolverine"]?["state"]?.ToString() == "running", 90), "the host did not start");
            Assert.Equal("v2", (await Get(host, "/api/v1/status")).Json!["engine"]?["mode"]?.ToString());
            var ingress = host.Services.GetRequiredService<IEventIngress>();
            // the train acts: the events of the Octopus watcher reach it (in the shadow run they reach the shadow's train; here it is the acting one)
            ingress.Ingest(new OctopusReleaseCreated("R-1", "1.0.1", D));
            ingress.Ingest(new OctopusDeploymentChanged("D-1-tdd", "T-1-tdd", "R-1", "1.0.1", "tdd", OctopusDeploymentPhase.Succeeded, D));
            Assert.True(await Until(async () => long.Parse((await Rows(cs, "SELECT COUNT(*) FROM factory.DeliveryTrains"))[0]) > 0, 60), "no train took an event in");
            var wolverine = host.Services.GetRequiredService<WolverineHostService>();
            // PromoteRelease: one deployment is created, however often the command is published (the key is the command's, and Octopus is read just before)
            var promote = new PromoteRelease("PromoteRelease|cmdemo1-ui|1.0.2|uat", "cmdemo1-ui", "cmdemo1-ui", "1.0.2", "uat");
            Assert.True(await wolverine.PublishAsync(promote));
            Assert.True(await Until(() => Task.FromResult(stub.Seen.Any(r => r.Method == "POST" && r.Path.EndsWith("/deployments/create/untenanted/v1"))), 60), "no deployment was created");
            var created = stub.Seen.Single(r => r.Method == "POST" && r.Path.EndsWith("/deployments/create/untenanted/v1"));
            var body = JsonNode.Parse(created.Body)!;
            Assert.Equal(("Spaces-355", "cmdemo1-ui", "1.0.2", "uat"), (body["SpaceIdOrName"]!.ToString(), body["ProjectName"]!.ToString(), body["ReleaseVersion"]!.ToString(), body["EnvironmentNames"]![0]!.ToString()));
            Assert.True(await wolverine.PublishAsync(promote));
            await Task.Delay(2500);
            Assert.Single(stub.Seen.Where(r => r.Method == "POST" && r.Path.EndsWith("/deployments/create/untenanted/v1")));
            // AnswerSignOff: responsibility by PUT, then the submit
            Assert.True(await wolverine.PublishAsync(new AnswerSignOff("AnswerSignOff|cmdemo1-ui|Interruptions-3", "cmdemo1-ui", "Interruptions-3", "ServerTasks-9", true, "Signed off by the factory worker gateway.")));
            Assert.True(await Until(() => Task.FromResult(stub.Seen.Any(r => r.Method == "POST" && r.Path.EndsWith("/interruptions/Interruptions-3/submit"))), 60), "the Sign-off was not answered");
            Assert.Contains(stub.Seen, r => r.Method == "PUT" && r.Path.EndsWith("/interruptions/Interruptions-3/responsible"));
            Assert.Equal("Proceed", JsonNode.Parse(stub.Seen.Single(r => r.Path.EndsWith("/submit")).Body)!["Result"]!.ToString());
            Assert.True(0 == long.Parse((await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]), string.Join(" // ", await Rows(cs, "SELECT message_type, exception_type, LEFT(exception_message, 400) FROM wolverine.wolverine_dead_letters")));
            // the engine acts for tdd; the shadow of the environments v1's AutoPromoter still promotes (uat and prod, W9.10p) is there, so the status shows its comparison
            Assert.NotNull((await Get(host, "/api/v1/status")).Json!["deliveryShadow"]);
        });
    }
}
