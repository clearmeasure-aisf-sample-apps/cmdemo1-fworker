using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W7.3 (#340) in a gateway whose engine acts and carries the feature plan, over a real SQL Server and a fake GitHub on a local port: the Wolverine host runs the generated handlers of the new messages (the slot request,
/// the scheduler's word to the saga, the checks, the merge of a code change), and the places where none of it may exist: an engine that names no feature plan registers no watcher.
/// </summary>
public sealed class FeaturePlanWiringTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
    const string MergeSha = "bbb2222222222222222222222222222222222222";
    static readonly string Token = "ghp_" + new string('A', 36);   // composed at run time, so the secret scan has nothing to match

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

    /// <summary>The endpoints of GitHub a feature item's slot request and merge need, on a local port: the item with its labels, the pull request and its merge.</summary>
    sealed class FakeApi : IDisposable
    {
        readonly HttpListener listener = new();
        readonly string repoPath;
        public readonly string Url;
        public readonly List<string> Requests = [];

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
                _ = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                lock (Requests) Requests.Add($"{ctx.Request.HttpMethod} {path}");
                var (code, json) = Answer(ctx.Request.HttpMethod, path);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        }

        (int, string) Answer(string method, string path)
        {
            if (method == "GET" && path == $"/repos/{repoPath}/issues/702")
                return (200, new JsonObject { ["number"] = 702, ["body"] = "a feature", ["labels"] = new JsonArray(new JsonObject { ["name"] = "factory worker" }, new JsonObject { ["name"] = "Auto Merge" }, new JsonObject { ["name"] = "priority:high" }) }.ToJsonString());
            if (method == "GET" && path == $"/repos/{repoPath}/issues/702/comments") return (200, "[]");
            if (method == "GET" && path == $"/repos/{repoPath}/pulls/12")
                return (200, $$$"""{"state":"open","draft":false,"merged":false,"merge_commit_sha":null,"mergeable":true,"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{Head}}}","ref":"bot/702-x"}}""");
            if (method == "PUT" && path == $"/repos/{repoPath}/pulls/12/merge") return (200, $$$"""{"sha":"{{{MergeSha}}}","merged":true}""");
            return (404, """{"message":"Not Found"}""");
        }

        public int Count(string request) { lock (Requests) return Requests.Count(r => r == request); }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    static async Task<WorkItemState> Row(string cs, ItemId item) => (await new SqlWorkItemStore(() => WorkItemWiring.Open(cs)).Load(item.ToString(), default))!.State;

    [SqlFact]
    public async Task AFeatureItem_AsksTheSchedulerForASlot_TheWaitReachesTheSaga_AndItsMergeIsACodeChangesMerge()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w73_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.EditConfig = cfg =>
            {
                cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
                cfg["intake"] = new JsonObject { ["enabled"] = false, ["wipLimit"] = 6, ["fromColumn"] = "Todo", ["v2Plans"] = new JsonArray("docs", "feature") };
                cfg["priorityLabels"] = new JsonObject { ["top"] = "priority:top", ["high"] = "priority:high" };
            };
            env.Set("FACTORY_DB_CONNECTION", cs);
            env.Set("FACTORY_SWEEP_SECONDS", "3600");
            env.Apply();
            var cfgNow = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfgNow["owner"]}/{cfgNow["repo"]}");
            using var api = new FakeApi(home.ToString());
            env.Set("GITHUB_API_URL", api.Url); env.Set("GH_TOKEN", Token); env.Apply();
            var item = new ItemId(home, 702);
            await using var gateway = new GatewayHost();
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");
            async Task Send(WorkItemInput m) => Assert.True(await wolverine.PublishAsync(m));
            async Task<WorkItemState> Settled(Func<WorkItemState, bool> reached, string what)
            {
                WorkItemState? s = null;
                Assert.True(await Until(async () => { try { s = await Row(cs, item); return reached(s); } catch { return false; } }, 60), what + ": " + (s is null ? "no row" : s.ToJson()));
                return s!;
            }

            // the intake gave the item to the saga on the feature plan: it asks for a slot; the gateway reads the item's labels and sends the scheduler's request; no runtime is on in this gateway, so the
            // scheduler says why the item waits and the word travels back to the saga (the generated handlers of four new messages and a cascade through the outbox)
            await Send(new StartWorkItem(item, "intake", "feature"));
            var waiting = await Settled(s => s.SlotWait.Length > 0, "the wait of the scheduler did not reach the saga");
            Assert.Equal(("feature", StepKind.Design, true, ""), (waiting.Plan, waiting.Step, waiting.SlotAsked, waiting.SlotId));
            Assert.StartsWith("runtime-off", waiting.SlotWait);
            Assert.Null(waiting.LeaseDue);   // (no session is offered and no lease runs while the item waits)
            Assert.True(api.Count($"GET /repos/{home}/issues/702") >= 1, "the labels of the item were not read");
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'capacity.held'"))[0] == "1", 30), "the wait is not in the history");
            Assert.True(await Until(async () => (await Rows(cs, $"SELECT COUNT(*) FROM factory.ItemHistory WHERE ItemKey = '{item}' AND Type = 'workitem.decided' AND Detail LIKE '%\"Action\":\"held\"%'"))[0] == "1", 30), "the saga did not decide the wait");
            Assert.Contains(await Rows(cs, "SELECT [Key] FROM factory.CapacityLedgers"), k => k == "factory");
            var ledger = Fworker.Workflows.Capacity.CapacityState.FromJson((await Rows(cs, "SELECT StateJson FROM factory.CapacityLedgers WHERE [Key] = 'factory'"))[0]);
            Assert.Equal("high", ledger.Queue[item.ToString()].Rank.Priority);   // (the request carried the rank of the item's labels)
            Assert.True(ledger.Queue[item.ToString()].Rank.InIntake);

            // the item is not offered to the plant while it waits for its slot
            Assert.DoesNotContain(gateway.Services.GetRequiredService<V2Ownership>().Offers, o => o.Item == 702);

            // the saga is put at its merge directly, as a restored saga would be, with the checks of the head green: the PASS asks for the merge and the gateway merges as for a code change
            var store = new SqlWorkItemStore(() => WorkItemWiring.Open(cs));
            var current = await store.Load(item.ToString(), default);
            var at = current!.State;
            at.Step = StepKind.Verify; at.ArmId = "1.4"; at.ArmSeq = 4; at.PullRequest = 12; at.Head = Head; at.SlotAsked = false; at.SlotWait = ""; at.LeaseDue = DateTimeOffset.UtcNow.AddHours(3);
            Assert.True(await store.Save(item.ToString(), at, current.Revision, default));
            await Send(new ChecksObserved(item, 1, "checks-1", Head, ChecksVerdict.Green, []));
            await Settled(s => s.ChecksState == "green", "the checks were not recorded");
            await Send(new VerdictRecorded(item, 1, "1.4", "verdict-1", true, 12, Head, "reads well"));
            var done = await Settled(s => s.Step == StepKind.Deliver, "the item was not merged");
            Assert.Equal((MergeSha, Head), (done.MergeCommit, done.VerdictHead));
            Assert.Equal(1, api.Count($"PUT /repos/{home}/pulls/12/merge"));
            // the merge cleared the item from the scheduler and the train recorded it (the tail of the cascade, so the database is quiet before it is dropped)
            Assert.True(await Until(async () => !Fworker.Workflows.Capacity.CapacityState.FromJson((await Rows(cs, "SELECT StateJson FROM factory.CapacityLedgers WHERE [Key] = 'factory'"))[0]).Queue.ContainsKey(item.ToString()), 30), "the merged item stayed in the scheduler");
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
    public void TheWatcherOfCI_IsRegisteredOnlyWhereTheEngineCarriesTheFeaturePlan()
    {
        if (!StubPwsh.Supported) return;
        foreach (var (plans, expected) in new[] { (new[] { "docs", "feature" }, true), (new[] { "docs" }, false), (Array.Empty<string>(), false) })
        {
            using var env = new GatewayEnvironment();
            env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("GH_TOKEN", Token);
            env.EditConfig = cfg =>
            {
                cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
                cfg["intake"] = new JsonObject { ["enabled"] = false, ["wipLimit"] = 6, ["fromColumn"] = "Todo", ["v2Plans"] = new JsonArray(plans.Select(p => (JsonNode)p).ToArray()) };
            };
            env.Apply();
            using var host = new GatewayHost();
            var started = Task.Run(() => host.Server);
            Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
            Assert.Equal(expected, host.Services.GetRequiredService<RecurringJobRegistry>().Has(RecurringJobSlots.FeatureChecks));
        }
    }

    [Fact]
    public void WhereTheEngineActs_TheRearmOfTheSagasAndThePurgeOfTheLedger_AreFilled_AndTheHealthBlockExists()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); env.Set("GH_TOKEN", Token);
        env.EditConfig = cfg => cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd"), ["shadowEnvironments"] = new JsonArray() };
        env.Apply();
        using var host = new GatewayHost();
        var started = Task.Run(() => host.Server);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)), "the gateway of an acting v2 environment did not finish starting");
        var jobs = host.Services.GetRequiredService<RecurringJobRegistry>();
        Assert.True(jobs.Has(RecurringJobSlots.SagaRearm));   // W9.10o: the repair of a save whose timer or command was never sent
        Assert.True(jobs.Has(RecurringJobSlots.LedgerPurge));   // W9.10o: the successes of the idempotency ledger do not live for ever
        Assert.NotNull(host.Services.GetService<V2Health>());
        // W9.10g: the plant-wide steps of the sweep, the retention and the faster reconciler are jobs of the engine, and the sweep knows it leaves the steps to them
        foreach (var slot in new[] { RecurringJobSlots.ClaimRefReap, RecurringJobSlots.GapRenew, RecurringJobSlots.BoardGuard, RecurringJobSlots.QcSync, RecurringJobSlots.Replenish, RecurringJobSlots.BacklogLow, RecurringJobSlots.RetentionPrune })
            Assert.True(jobs.Has(slot), slot);
        Assert.NotNull(host.Services.GetService<PlantStepJobs>());
        Assert.Contains(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), h => h is Sweeper { PlantJobs: true });
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_NoFeaturePartsExist()
    {
        if (!StubPwsh.Supported) return;
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable); WolverineHostTestSupport.Shadow(env); env.Apply();
        using var host = new GatewayHost();
        _ = host.Server;
        foreach (var t in new[] { typeof(WorkItemParts), typeof(FeaturePlanParts), typeof(SlotRequestPort), typeof(FeatureNotice), typeof(V2Ownership), typeof(PlantStepJobs) })
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType == t);
    }
}
