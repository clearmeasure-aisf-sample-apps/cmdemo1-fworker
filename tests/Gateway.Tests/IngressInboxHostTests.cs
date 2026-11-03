using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W4.1 and W5.0 on the real gateway host (#494, #485): an engine that acts (v2Environments names the environment) over a real SQL Server, with a stub of GitHub. One webhook event reaches BOTH
/// handlers of IngressInboxMessage, the inbox (deduplication, correlations, history) and the ItemMerged derivation (merged-items table, ItemMerged, its history), as generated code on the
/// durable queue. Each test returns early where FACTORY_TEST_SQL is unset or the platform has no stub pwsh.
/// </summary>
public sealed class IngressInboxHostTests
{
    const string Owner = "clearmeasure-aisf-sample-apps", Repo = "cmdemo1-workorders";
    static readonly string Merge = new('a', 40), Head = new('c', 40);

    sealed class Stub : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<string> seen = [];
        readonly CancellationTokenSource stop = new();
        public string Base { get; }
        public int FailPullRequestReads;   // the next reads of pull request 12 answer 502

        public Stub()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Base = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Base + "/"); listener.Start();
            _ = Task.Run(Loop);
        }

        public int Count(string path) { lock (seen) return seen.Count(p => p == path); }

        async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                var path = c.Request.Url!.AbsolutePath;
                lock (seen) seen.Add(path);
                var (status, body) = path.EndsWith("/pulls/12") && Interlocked.Decrement(ref FailPullRequestReads) >= 0 ? (502, "{}") : Answer(path);
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        }

        static (int, string) Answer(string path)
        {
            var repo = $"/repos/{Owner}/{Repo}";
            if (path == repo + "/pulls/12")
                return (200, new JsonObject { ["number"] = 12, ["merged"] = true, ["base"] = new JsonObject { ["ref"] = "master" }, ["head"] = new JsonObject { ["ref"] = "someone/7-thing" }, ["title"] = "Thing (#12)", ["body"] = "Closes #7", ["merge_commit_sha"] = Merge }.ToJsonString());
            if (path == repo + "/issues/7")
                return (200, new JsonObject { ["number"] = 7, ["labels"] = new JsonArray(new JsonObject { ["name"] = "Auto Merge" }), ["body"] = "A thing." }.ToJsonString());
            if (path == repo + "/issues/7/comments")
                return (200, new JsonArray(new JsonObject { ["body"] = ItemStateComment.Body("factory-worker", 7, new JsonObject { ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = 12 } }) }).ToJsonString());
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

    static async Task<bool> RowCount(string cs, string sql, string expected, int seconds) => await Until(async () => (await Rows(cs, sql))[0] == expected, seconds);

    static string Event(string name, string delivery, int pr, string sha, int item = 0)
    {
        var o = new JsonObject { ["provider"] = "github", ["event"] = name, ["ref"] = delivery, ["pr"] = pr, ["sha"] = sha };
        if (item > 0) o["item"] = item;
        return o.ToJsonString();
    }

    static async Task WithV2Gateway(Func<string, GatewayHost, Stub, Task> body)
    {
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_inbox_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        using var stub = new Stub();
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", cs);
        env.Set("GH_TOKEN", "ghp_" + new string('B', 26));   // composed at run time; the stub accepts anything
        env.Set("GITHUB_API_URL", stub.Base);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd") };
        env.Apply();
        try
        {
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            await body(cs, gateway, stub);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    static async Task WaitRunning(GatewayHost gateway)
    {
        var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
        Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 90), "the Wolverine host did not start");
    }

    [SqlFact]
    public async Task OneWebhookEvent_ReachesBothHandlers_TheCorrelationCarriesTheGeneration_AndALaterDuplicateIsAnsweredWithoutAGitHubRead()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithV2Gateway(async (cs, gateway, stub) =>
        {
            await WaitRunning(gateway);
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            var item = new ItemId(RepoId.Parse($"{Owner}/{Repo}"), 7);
            var ingress = gateway.Services.GetRequiredService<IEventIngress>();

            // the saga owns the item (generation 1): the pull request opened for it is mapped with that generation, by the inbox handler
            Assert.True(await wolverine.PublishAsync(new StartWorkItem(item, "intake", "docs")));
            Assert.True(await RowCount(cs, $"SELECT COUNT(*) FROM factory.OpenInstances WHERE ItemKey = '{item}' AND Generation = 1 AND ClosedAt IS NULL", "1", 60), "the saga did not open an instance");
            ingress.Ingest(new WebhookEventIngested(Event("pr.opened", "delivery-1", 12, Head, 7)));
            Assert.True(await RowCount(cs, $"SELECT COUNT(*) FROM factory.Correlations WHERE Kind = 'pr' AND [Key] = '{Owner}/{Repo}!12' AND ItemKey = '{item}' AND Generation = 1", "1", 60),
                "the pull request was not mapped with the generation: " + string.Join(" // ", await Rows(cs, "SELECT Kind + ' ' + [Key] + ' ' + ItemKey + ' ' + CAST(Generation AS varchar) FROM factory.Correlations")));
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'ingress.inbox.webhook.pr.opened'", "1", 30));

            // the merge webhook: handler one stores the accepted event in the history, handler two derives ItemMerged from the pull request and the item
            ingress.Ingest(new WebhookEventIngested(Event("base.merged", "delivery-2", 12, Merge)));
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'ingress.inbox.webhook.base.merged'", "1", 60), "the inbox handler did not run for the merge");
            Assert.True(await RowCount(cs, $"SELECT COUNT(*) FROM factory.MergedItems WHERE ItemKey = '{item}' AND [Commit] = '{Merge}'", "1", 60), "the derivation handler did not run for the merge");
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'delivery.item-merged'", "1", 60), "ItemMerged did not reach its history");

            // the pr.merged webhook of the same merge: the inbox takes it in (another delivery), and the derivation answers from the table before it reads GitHub
            var reads = stub.Count($"/repos/{Owner}/{Repo}/pulls/12");
            ingress.Ingest(new WebhookEventIngested(Event("pr.merged", "delivery-3", 12, Head, 7)));
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'ingress.inbox.webhook.pr.merged'", "1", 60), "the inbox handler did not run for pr.merged");
            await Task.Delay(2000);
            Assert.Equal(reads, stub.Count($"/repos/{Owner}/{Repo}/pulls/12"));
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM factory.MergedItems"))[0]);
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'delivery.item-merged'"))[0]);
            Assert.True(0 == long.Parse((await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]), string.Join(" // ", await Rows(cs, "SELECT message_type, exception_type, LEFT(exception_message, 400) FROM wolverine.wolverine_dead_letters")));
        });
    }

    [SqlFact]
    public async Task AGitHubFaultOnTheMergeRead_IsTriedAgain_AndNothingGoesToTheDeadLetters()
    {
        // the merge is derived from this message or from nothing; a 502 of GitHub must not end it after a few quick attempts (#494)
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithV2Gateway(async (cs, gateway, stub) =>
        {
            await WaitRunning(gateway);
            stub.FailPullRequestReads = 1;
            gateway.Services.GetRequiredService<IEventIngress>().Ingest(new WebhookEventIngested(Event("base.merged", "delivery-502", 12, Merge)));
            Assert.True(await RowCount(cs, $"SELECT COUNT(*) FROM factory.MergedItems WHERE [Commit] = '{Merge}'", "1", 90), "the derivation was not tried again after the 502");
            Assert.True(stub.Count($"/repos/{Owner}/{Repo}/pulls/12") >= 2);
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'delivery.item-merged'", "1", 30));
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        });
    }

    [SqlFact]
    public async Task AWebhookThatArrivesBeforeTheHostIsUp_IsStoredWhenItIs_NotLost()
    {
        // the publish happens after the webhook was answered; the host (database migrations, the leader) is not always up yet, and a publish that said "not up" used to be the end of the event (#485)
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        await WithV2Gateway(async (cs, gateway, stub) =>
        {
            gateway.Services.GetRequiredService<IEventIngress>().Ingest(new WebhookEventIngested(Event("base.merged", "delivery-early", 12, Merge)));
            await WaitRunning(gateway);
            Assert.True(await RowCount(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'ingress.inbox.webhook.base.merged'", "1", 90), "the event that came before the host was up was lost");
            Assert.True(await RowCount(cs, $"SELECT COUNT(*) FROM factory.MergedItems WHERE [Commit] = '{Merge}'", "1", 60), "ItemMerged was not derived from it");
        });
    }
}
