using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W6.6 (#335) in a gateway whose engine acts, over a real SQL Server and a fake GitHub on a local port: the docs plan from its start to the merge the saga asks the gateway for (the Wolverine host runs the generated
/// handlers, the merge command goes to GitHub, its answer cascades back to the saga through the outbox), and the places where none of it may exist: the kill switch, and the engines that do not act.
/// </summary>
public sealed class DocsPlanWiringTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string MergeSha = "bbb2222222222222222222222222222222222222";
    // composed at run time, so the secret scan has nothing to match (a token the gateway takes for a credential: "unset" is none)
    static readonly string Token = "ghp_" + new string('A', 36);

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

    /// <summary>The few endpoints of GitHub the merge needs, on a local port: the item (its labels), its comments, the pull request and its merge. Every request is kept.</summary>
    sealed class FakeApi : IDisposable
    {
        readonly HttpListener listener = new();
        readonly string repoPath;
        public readonly string Url;
        public readonly List<string> Requests = [];
        public readonly List<string> Bodies = [];
        public volatile bool MergeFails;
        public volatile string[] Labels = ["factory worker", "Auto Merge"];

        public FakeApi(string repoPath)
        {
            this.repoPath = repoPath;
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/"); listener.Start();
            _ = Task.Run(Loop);
        }

        async Task Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                lock (Requests) { Requests.Add($"{ctx.Request.HttpMethod} {path}"); Bodies.Add(body); }
                var (code, json) = Answer(ctx.Request.HttpMethod, path);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        }

        (int, string) Answer(string method, string path)
        {
            if (method == "GET" && path == $"/repos/{repoPath}/issues/701") return (200, new JsonObject { ["number"] = 701, ["body"] = "docs", ["labels"] = new JsonArray(Labels.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray()) }.ToJsonString());
            if (method == "GET" && path == $"/repos/{repoPath}/issues/701/comments") return (200, "[]");
            if (method == "GET" && path == $"/repos/{repoPath}/pulls/11")
                return (200, $$$"""{"state":"open","draft":false,"merged":false,"merge_commit_sha":null,"mergeable":true,"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{Head}}}","ref":"bot/701-x"}}""");
            if (method == "PUT" && path == $"/repos/{repoPath}/pulls/11/merge")
                return MergeFails ? (405, """{"message":"Required status check is expected."}""") : (200, $$$"""{"sha":"{{{MergeSha}}}","merged":true}""");
            return (404, """{"message":"Not Found"}""");
        }

        public int Count(string request) { lock (Requests) return Requests.Count(r => r == request); }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    static async Task<WorkItemState> Row(string cs, ItemId item) => (await new SqlWorkItemStore(() => WorkItemWiring.Open(cs)).Load(item.ToString(), default))!.State;

    [Fact]
    public async Task TheDocsPlan_RunsToItsMerge_TheSagaAsksTheGatewayToMerge_AndTheAnswerCompletesTheItem()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w66_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            env.Set("FACTORY_DB_CONNECTION", cs);
            env.Set("FACTORY_SWEEP_SECONDS", "3600");
            env.Apply();
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            using var api = new FakeApi(home.ToString());
            env.Set("GITHUB_API_URL", api.Url); env.Set("GH_TOKEN", Token); env.Apply();
            var item = new ItemId(home, 701);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");

            async Task Send(WorkItemInput m) => Assert.True(await wolverine.PublishAsync(m));
            async Task<WorkItemState> Settled(Func<WorkItemState, bool> reached, string what)
            {
                WorkItemState? s = null;
                Assert.True(await Until(async () => { try { s = await Row(cs, item); return reached(s); } catch { return false; } }, 60), what + ": " + (s is null ? "no row" : $"{s.Step} {s.ArmId} parked={s.Parked}"));
                return s!;
            }

            // the intake gave the item to the saga; the design is reviewed and left, the pull request exists, the development is reviewed and left
            await Send(new StartWorkItem(item, "intake", "docs"));
            var design = await Settled(s => s.Step == StepKind.Design, "the saga did not start");
            await Send(new ReviewRecorded(item, 1, design.ArmId, "review-d", true));
            await Settled(s => s.ReviewsComplete.Contains("Design"), "the review was not recorded");
            await Send(new StepCompleted(item, 1, design.ArmId, "done-d"));
            var develop = await Settled(s => s.Step == StepKind.Develop, "the design was not left");
            await Send(new PullRequestChanged(item, 1, "pr-1", 11, Head, ["docs/a.md"]));
            await Send(new ReviewRecorded(item, 1, develop.ArmId, "review-v", true));
            await Settled(s => s.ReviewsComplete.Contains("Develop") && s.PullRequest == 11, "the pull request was not recorded");
            await Send(new StepCompleted(item, 1, develop.ArmId, "done-v"));
            var verify = await Settled(s => s.Step == StepKind.Verify, "the development was not left");
            Assert.Equal(0, api.Count($"PUT /repos/{home}/pulls/11/merge"));

            // the PASS: the saga asks for the merge, the gateway merges the head the PASS covers, and the answer ends the item
            await Send(new VerdictRecorded(item, 1, verify.ArmId, "verdict-1", true, 11, Head, "reads well"));
            var done = await Settled(s => s.Status == WorkItemStatus.Completed, "the item was not merged");
            Assert.Equal((MergeSha, Head), (done.MergeCommit, done.VerdictHead));
            Assert.Equal(1, api.Count($"PUT /repos/{home}/pulls/11/merge"));
            lock (api.Requests) Assert.Equal(Head, JsonNode.Parse(api.Bodies[api.Requests.IndexOf($"PUT /repos/{home}/pulls/11/merge")])!["sha"]!.ToString());

            var actions = await Rows(cs, $"SELECT JSON_VALUE(Detail, '$.Action') FROM factory.ItemHistory WHERE ItemKey = '{item}' AND Type = 'workitem.decided' ORDER BY CAST(JSON_VALUE(Detail, '$.Sequence') AS int)");
            Assert.Contains("merge-requested", actions);
            Assert.Equal(["merged", "completed"], actions.TakeLast(2));
            // W7.2: the merge went through the merge train of the repository (direct strategy: let through at once), which recorded the merge and holds no line
            var train = Fworker.Workflows.Merge.MergeTrainState.FromJson((await Rows(cs, $"SELECT StateJson FROM factory.MergeTrains WHERE [Key] = '{home}'"))[0]);
            Assert.Empty(train.Line);
            Assert.Equal([(item.ToString(), MergeSha)], train.Merged.Select(m => (m.Item, m.Commit)));
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'merge.train.decided' AND Detail LIKE '%\"Action\":\"merged\"%'"))[0] == "1", 30), "the train's merge did not reach the history");
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }

    [Fact]
    public async Task AMergeThatIsBlocked_IsAskedAgainLater_NeverAFailure_AndTheMergeItselfWaitsForTheLabel()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w66_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.EditConfig = c => c["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            env.Set("FACTORY_DB_CONNECTION", cs); env.Set("FACTORY_SWEEP_SECONDS", "3600");
            env.Apply();
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            using var api = new FakeApi(home.ToString()) { Labels = ["factory worker"] };   // (no Auto Merge label)
            env.Set("GITHUB_API_URL", api.Url); env.Set("GH_TOKEN", Token); env.Apply();
            var item = new ItemId(home, 701);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");
            // the saga is put at its verify step directly, as a restored saga would be
            var store = new SqlWorkItemStore(() => WorkItemWiring.Open(cs));
            var state = new WorkItemState { Item = item.ToString(), Generation = 1, Plan = "docs", Step = StepKind.Verify, ArmId = "1.3", ArmSeq = 3, StartedAt = DateTimeOffset.UtcNow, PullRequest = 11, Head = Head, LeaseDue = DateTimeOffset.UtcNow.AddHours(3), Source = "test" };
            Assert.True(await store.Save(item.ToString(), state, 0, default));
            Assert.True(await wolverine.PublishAsync(new VerdictRecorded(item, 1, "1.3", "verdict-1", true, 11, Head, "ok")));
            Assert.True(await Until(async () => (await Row(cs, item)) is { Step: StepKind.Merge, MergeBlockedReason: "no-auto-merge-label" }, 30),
                "the merge was not blocked by the missing label: " + (await Row(cs, item)).ToJson() + " | dead letters: " + string.Join(";", await Rows(cs, "SELECT exception_type + ' ' + LEFT(exception_message, 300) FROM wolverine.wolverine_dead_letters"))
                + " | api: " + string.Join(",", api.Requests) + " | history: " + string.Join(",", await Rows(cs, $"SELECT JSON_VALUE(Detail, '$.Action') FROM factory.ItemHistory WHERE ItemKey = '{item}'")));
            var s = await Row(cs, item);
            Assert.False(s.Parked); Assert.Equal(WorkItemStatus.Open, s.Status);
            Assert.NotNull(s.MergeRetryAt);
            Assert.Equal(0, api.Count($"PUT /repos/{home}/pulls/11/merge"));
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
            // the next merge call is a durable scheduled message
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type LIKE '%MergeRetryDue'"))[0] == "1", 30),
                "the merge retry was not scheduled: " + string.Join(";", await Rows(cs, "SELECT message_type + ' ' + status FROM wolverine.wolverine_incoming_envelopes")));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}

/// <summary>The docs plan exists only where the engine acts: the kill switch (FACTORY_V2_OFF) puts the environment back on v1 whatever the engine lists say, and v1's verb router, sweep and ingress are what they were.</summary>
public sealed class DocsPlanInertTests
{
    static readonly string Token = "ghp_" + new string('A', 36);   // (composed at run time)

    [Fact]
    public void TheKillSwitch_LeavesNoSaga_NoFeed_AndNoOwnership_SoV1ReadsEveryItem()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
        env.Set("FACTORY_V2_OFF", "1");
        env.Apply();
        using var host = new GatewayHost();
        _ = host.Server;
        foreach (var t in new[] { typeof(V2Ownership), typeof(WorkItemService), typeof(WorkItemHistory), typeof(WorkItemParts), typeof(WolverineHostService) })
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType == t);
        Assert.IsType<V1VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        Assert.Null(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sweeper>().Single().V2);
    }

    [Fact]
    public void TheShadowRunAndV1_RouteVerbsToV1Alone()
    {
        if (!StubPwsh.Supported) return;
        using (var env = new GatewayEnvironment())
        {
            env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); env.Apply();
            using var host = new GatewayHost();
            _ = host.Server;
            Assert.IsType<V1VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        }
        using (var env = new GatewayEnvironment())
        {
            env.Apply();
            using var host = new GatewayHost();
            _ = host.Server;
            Assert.IsType<V1VerbRouter>(host.Services.GetRequiredService<IVerbRouter>());
        }
    }

    [Fact]
    public void WhereTheEngineActs_TheVerbsFeedTheSaga_AndTheIntakeJobIsOnlyThereWhenIntakeNamesTheDocsPlan()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
        env.Apply();
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
        Assert.IsType<V2VerbFeed>(host.Services.GetRequiredService<IVerbRouter>());
        Assert.NotNull(host.Services.GetRequiredService<WorkItemParts>());
        // intake is off in the source defaults, and names no v2 plan: the registry has no job for the docs intake
        Assert.False(host.Services.GetRequiredService<RecurringJobRegistry>().Has(RecurringJobSlots.DocsIntake));
    }

    [Fact]
    public void TheDocsIntakeJob_IsRegistered_WhenIntakeIsOnAndNamesTheDocsPlan()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("GH_TOKEN", Token);
        env.EditConfig = cfg =>
        {
            cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
            cfg["intake"] = new JsonObject { ["enabled"] = true, ["wipLimit"] = 6, ["fromColumn"] = "Todo", ["v2Plans"] = new JsonArray("docs") };
        };
        env.Apply();
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
        Assert.True(host.Services.GetRequiredService<RecurringJobRegistry>().Has(RecurringJobSlots.DocsIntake));
    }
}
