using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>What the gateway reads of config/instance.json for the merge train (W7.2): the strategy and the three pauses.</summary>
public sealed class MergeTrainSettingsReaderTests
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    [Fact]
    public void TheSourceDefaults_MergeDirectly_WithFiveMinutesOfExpiry_AndFifteenOfPatience()
    {
        var warnings = new List<string>();
        var s = MergeTrainSettingsReader.From(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "instance.json"))), warnings.Add);
        Assert.Equal((MergeStrategy.Direct, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), null), (s.Strategy, s.Expiry, s.Patience, s.WaitRetry));
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheDeployedOverrides_DoNotChooseAStrategy_SoTddMergesDirectlyUntilTheOwnerSaysOtherwise()
    {
        var overrides = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "config", "deploy.overrides.json")))!.AsObject();
        Assert.False(overrides.ContainsKey("mergeStrategy"));
        Assert.True(overrides["timing"] is not JsonObject t || !t.Any(kv => kv.Key.StartsWith("mergeTrain", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheConfiguration_ChoosesTheStrategy_AndThePauses()
    {
        var s = MergeTrainSettingsReader.From(JsonNode.Parse("""{"mergeStrategy":"Train","timing":{"mergeTrainEntryExpirySeconds":120,"mergeTrainHeadPatienceSeconds":600,"mergeTrainWaitSeconds":20}}"""));
        Assert.Equal((MergeStrategy.Train, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), (TimeSpan?)TimeSpan.FromSeconds(20)), (s.Strategy, s.Expiry, s.Patience, s.WaitRetry));
    }

    [Theory]
    [InlineData("""{"mergeStrategy":"queue"}""", "deferred")]
    [InlineData("""{"mergeStrategy":"nonsense"}""", "not one of")]
    public void TheMergeQueueIsDeferred_AndAnUnknownStrategyMergesDirectly_WithOneWarning(string json, string warning)
    {
        var warnings = new List<string>();
        var s = MergeTrainSettingsReader.From(JsonNode.Parse(json), warnings.Add);
        Assert.Equal(MergeStrategy.Direct, s.Strategy);
        Assert.Contains(warning, Assert.Single(warnings));
    }

    [Theory]
    [InlineData("""{"mergeStrategy":5,"timing":"x"}""")]
    [InlineData("""{"mergeStrategy":null,"timing":{"mergeTrainEntryExpirySeconds":"soon","mergeTrainHeadPatienceSeconds":-3,"mergeTrainWaitSeconds":0}}""")]
    [InlineData("""[]""")]
    public void ABrokenConfiguration_GivesTheDefaults_NeverAFault(string json)
    {
        var s = MergeTrainSettingsReader.From(JsonNode.Parse(json));
        Assert.Equal((MergeStrategy.Direct, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), null), (s.Strategy, s.Expiry, s.Patience, s.WaitRetry));
        Assert.Equal(MergeStrategy.Direct, MergeTrainSettingsReader.From(null).Strategy);
    }
}

/// <summary>
/// The merge train exists only where the engine acts (Engine.V2Acts): it is attached to the Wolverine host by WorkItemWiring.Attach, which the gateway calls only inside that branch (GatewayWolverineExtensions), and no other gateway
/// file refers to its handlers or its topology. The engines that do not act have no saga parts at all, so no train.
/// </summary>
public sealed class MergeTrainInertTests
{
    static readonly string Root = new Func<string>(() =>
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "gateway", "Program.cs"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    })();

    static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "gateway"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Internal{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}"))   // (the generated handler code, WolverineCodegen, names every handler)
            .Select(f => (Path.GetRelativePath(Root, f).Replace('\\', '/'), ArchitectureTests.Code(File.ReadAllText(f))));

    [Fact]
    public void OnlyTheWorkItemWiring_AttachesTheTrain_AndTheGatewayCallsThatOnlyWhereTheEngineActs()
    {
        var mentions = Sources().Where(s => Regex.IsMatch(s.Text, @"\bMergeTrain(Topology|Handlers|Routing|PortHandler|HistoryHandler)\b")).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();   // (Directory.EnumerateFiles has no order: the runner's file system gave WorkItemPort first)
        Assert.Equal(["gateway/Ports/DocsPlanPort.cs", "gateway/Ports/WorkItemPort.cs"], mentions);   // (the port's handler is declared beside the port; WorkItemPort attaches it)
        var callers = Sources().Where(s => s.Text.Contains("WorkItemWiring.Attach(")).ToList();
        var caller = Assert.Single(callers);
        Assert.Equal("gateway/Composition/GatewayWolverineExtensions.cs", caller.Path);
        var attach = caller.Text.IndexOf("WorkItemWiring.Attach(", StringComparison.Ordinal);
        var acts = caller.Text.LastIndexOf("if (c.Engine.V2Acts)", attach, StringComparison.Ordinal);
        Assert.True(acts >= 0, "WorkItemWiring.Attach is not under `if (c.Engine.V2Acts)`");
        // nothing closes that branch between the condition and the call
        Assert.DoesNotContain("\n        }\n", caller.Text[acts..attach].Replace("\r", ""));
    }

    [Fact]
    public void WhereTheEngineDoesNotAct_ThereAreNoSagaParts_SoNoTrain()
    {
        if (!StubPwsh.Supported) return;
        foreach (var kind in new[] { "v1" })
        {
            using var env = new GatewayEnvironment();
            env.Apply();
            using var host = new GatewayHost();
            _ = host.Server;
            Assert.DoesNotContain(host.Captured!, d => d.ServiceType == typeof(WorkItemParts));
            Assert.Null(host.Services.GetService<WorkItemParts>());
        }
    }
}

/// <summary>
/// W7.2 (#339) in a gateway whose engine acts, over a real SQL Server and a fake GitHub on a local port: two docs items of one repository ask for their merge together, with the train strategy. The Wolverine host runs the
/// generated handlers (the router, the train's inputs on the durable sequential queue, the merge port on its own queue, the history). The first item gets the turn and merges; the second is answered the wait of the line, which
/// the saga records as a blocked merge (never a failure), asks again after the pause the configuration sets, and merges after the first. The merges never overlap.
/// </summary>
public sealed class MergeTrainWiringTests
{
    const string Head = "aaa1111111111111111111111111111111111111";
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

    /// <summary>The endpoints of GitHub the merge needs for two items (701 with pull request 11, 702 with 12): the item and its labels, the pull request and its merge. The merge takes its time, so a second one that starts meanwhile shows.</summary>
    sealed class FakeApi : IDisposable
    {
        readonly HttpListener listener = new();
        readonly string repoPath;
        public readonly string Url;
        public readonly List<(string Path, DateTimeOffset Start, DateTimeOffset End)> Merges = [];
        public volatile int MergeMilliseconds = 1500;

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
                _ = Task.Run(() => Serve(ctx));
            }
        }

        async Task Serve(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var start = DateTimeOffset.UtcNow;
            var (code, json) = Answer(ctx.Request.HttpMethod, path);
            if (ctx.Request.HttpMethod == "PUT" && path.EndsWith("/merge")) { await Task.Delay(MergeMilliseconds); lock (Merges) Merges.Add((path, start, DateTimeOffset.UtcNow)); }
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
        }

        (int, string) Answer(string method, string path)
        {
            foreach (var (issue, pr) in new[] { (701, 11), (702, 12) })
            {
                if (method == "GET" && path == $"/repos/{repoPath}/issues/{issue}")
                    return (200, new JsonObject { ["number"] = issue, ["body"] = "docs", ["labels"] = new JsonArray(new JsonObject { ["name"] = "factory worker" }, new JsonObject { ["name"] = "Auto Merge" }) }.ToJsonString());
                if (method == "GET" && path == $"/repos/{repoPath}/issues/{issue}/comments") return (200, "[]");
                if (method == "GET" && path == $"/repos/{repoPath}/pulls/{pr}")
                    return (200, $$$"""{"state":"open","draft":false,"merged":false,"merge_commit_sha":null,"mergeable":true,"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{Head}}}","ref":"bot/{{{issue}}}-x"}}""");
                if (method == "PUT" && path == $"/repos/{repoPath}/pulls/{pr}/merge") return (200, $$$"""{"sha":"{{{new string((char)('a' + pr % 6), 40)}}}","merged":true}""");
            }
            return (404, """{"message":"Not Found"}""");
        }

        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    static async Task<WorkItemState> Row(string cs, ItemId item) => (await new SqlWorkItemStore(() => WorkItemWiring.Open(cs)).Load(item.ToString(), default))!.State;

    [SqlFact]
    public async Task Train_TwoItemsAskTogether_OneMerges_TheOtherWaitsItsTurn_TheMergesNeverOverlap()
    {
        if (!StubPwsh.Supported || WolverineHostTestSupport.Sql == null) return;
        var sql = WolverineHostTestSupport.Sql!;
        var db = "fw_w72_" + Guid.NewGuid().ToString("N")[..10];
        var master = With(sql, "master");
        await Rows(master, $"CREATE DATABASE [{db}]");
        var cs = With(sql, db);
        try
        {
            using var env = new GatewayEnvironment();
            env.EditConfig = cfg =>
            {
                cfg["engine"] = new JsonObject { ["v2Environments"] = new JsonArray("tdd") };
                cfg["mergeStrategy"] = "train";
                cfg["timing"]!["mergeTrainWaitSeconds"] = 2;
            };
            env.Set("FACTORY_DB_CONNECTION", cs); env.Set("FACTORY_SWEEP_SECONDS", "3600");
            env.Apply();
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(env.Dir, "instance.test.json")))!;
            var home = RepoId.Parse($"{cfg["owner"]}/{cfg["repo"]}");
            using var api = new FakeApi(home.ToString());
            env.Set("GITHUB_API_URL", api.Url); env.Set("GH_TOKEN", Token); env.Apply();
            var one = new ItemId(home, 701); var two = new ItemId(home, 702);
            // the gateway's durability agent looks for due scheduled messages every few hours by default (the database sleeps); this test needs the retry of the waiting item, so the agent polls twice a second
            await using var gateway = new GatewayHost { Extra = services => services.AddSingleton(WolverineHostTestSupport.Fast() with { ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500), ScheduledJobFirstExecution = TimeSpan.FromMilliseconds(500) }) };
            _ = gateway.Server;
            var wolverine = gateway.Services.GetRequiredService<WolverineHostService>();
            Assert.True(await Until(() => Task.FromResult(wolverine.State == WolverineHostService.Phase.Running), 60), "the Wolverine host did not start");

            // both sagas are put at their verify step, as restored sagas would be, and the PASS of both arrives together
            var store = new SqlWorkItemStore(() => WorkItemWiring.Open(cs));
            foreach (var (item, pr) in new[] { (one, 11), (two, 12) })
                Assert.True(await store.Save(item.ToString(), new WorkItemState { Item = item.ToString(), Generation = 1, Plan = "docs", Step = StepKind.Verify, ArmId = "1.3", ArmSeq = 3, StartedAt = DateTimeOffset.UtcNow, PullRequest = pr, Head = Head, LeaseDue = DateTimeOffset.UtcNow.AddHours(3), Source = "test" }, 0, default));
            Assert.True(await wolverine.PublishAsync(new VerdictRecorded(one, 1, "1.3", "verdict-1", true, 11, Head, "ok")));
            Assert.True(await wolverine.PublishAsync(new VerdictRecorded(two, 1, "1.3", "verdict-2", true, 12, Head, "ok")));

            Assert.True(await Until(async () => (await Row(cs, one)).Status == WorkItemStatus.Completed && (await Row(cs, two)).Status == WorkItemStatus.Completed, 90),
                "both items were not merged: " + (await Row(cs, one)).ToJson() + " / " + (await Row(cs, two)).ToJson() + " | train: " + string.Join(";", await Rows(cs, "SELECT StateJson FROM factory.MergeTrains"))
                + " | history: " + string.Join(",", await Rows(cs, "SELECT Type + ':' + JSON_VALUE(Detail, '$.Action') + ':' + ItemKey FROM factory.ItemHistory ORDER BY Id"))
                + " | envelopes: " + string.Join(",", await Rows(cs, "SELECT message_type + ' ' + status FROM wolverine.wolverine_incoming_envelopes"))
                + " | dead letters: " + string.Join(";", await Rows(cs, "SELECT exception_type + ' ' + LEFT(exception_message, 300) FROM wolverine.wolverine_dead_letters")));

            // two merges, one after the other
            (string Path, DateTimeOffset Start, DateTimeOffset End)[] merges;
            lock (api.Merges) merges = api.Merges.OrderBy(m => m.Start).ToArray();
            Assert.Equal(2, merges.Length);
            Assert.True(merges[1].Start >= merges[0].End, $"the merges overlapped: {merges[0].Start:O}-{merges[0].End:O} and {merges[1].Start:O}");

            // the loser of the first turn was told to wait: a blocked merge of the train, not a failure and not a park
            Assert.Equal("1", (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'workitem.decided' AND Detail LIKE '%merge-blocked%' AND Detail LIKE '%merge-train%'"))[0]);
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'workitem.decided' AND Detail LIKE '%\"Action\":\"parked\"%'"))[0]);

            // the train's table and decisions
            var state = Fworker.Workflows.Merge.MergeTrainState.FromJson((await Rows(cs, "SELECT StateJson FROM factory.MergeTrains WHERE [Key] = '" + home + "'"))[0]);
            Assert.Empty(state.Line);
            Assert.Equal(2, state.Merged.Count);
            Assert.Equal("2", (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'merge.train.decided' AND Detail LIKE '%\"Action\":\"enqueued\"%'"))[0]);
            Assert.True(await Until(async () => (await Rows(cs, "SELECT COUNT(*) FROM factory.ItemHistory WHERE Type = 'merge.train.decided' AND Detail LIKE '%\"Action\":\"merged\"%'"))[0] == "2", 30), "the train's merges did not reach the history");
            Assert.Equal("0", (await Rows(cs, "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"))[0]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try { await Rows(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"); } catch { }
        }
    }
}
