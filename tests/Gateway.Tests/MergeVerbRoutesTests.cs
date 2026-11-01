using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.3 (#355): the merge train verbs in the gateway (gateway/Verbs/Routes/Merge.cs, MergeVerbs.cs, Ports/MergeVerbPort.cs). The route file registers the five verbs where the engine acts; each verb is v2's for an item the
/// saga owns and the rule script's for every other item (the hybrid composition); the GitHub adapters ask for the paths of BaseUpdate.ps1 and read the answers the script reads.
/// </summary>
public sealed class MergeVerbRoutesTests
{
    static readonly RepoId Home = new("acme", "orders");
    const string Head = "aaa1111111111111111111111111111111111111";

    static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    // ---- the route file -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRouteFileRegistersTheFiveVerbsOfItsGroup_AndResolvesNothingAtStart()
    {
        var routes = new V2VerbRoutes();
        MergeRoutes.Register(routes, new ServiceCollection().BuildServiceProvider());   // (an empty container: nothing is resolved before a call)
        Assert.Equal(MergeRoutes.Verbs.OrderBy(v => v), routes.Verbs.OrderBy(v => v));
        Assert.Equal(["approve-pr", "base-update-branch", "base-watch", "clear-hold", "merge"], routes.Verbs.OrderBy(v => v));
    }

    // ---- the handlers: v2 for the saga's items, the script for the rest -----------------------------------------------------------------

    sealed class Labels(params string[] names) : IItemLabels
    {
        public Task<IReadOnlyList<string>?> LabelsOf(ItemId item, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(names);
    }

    sealed class Github : IBaseUpdateGitHub
    {
        public List<BasePull> Open = [];
        public readonly List<string> Calls = [];
        public Task<string> BaseHead(CancellationToken ct) => Task.FromResult("m1");
        public Task<IReadOnlyList<string>?> ChangedFiles(string from, string to, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public Task<IReadOnlyList<BasePull>> OpenPullRequests(CancellationToken ct) => Task.FromResult<IReadOnlyList<BasePull>>(Open);
        public Task<BasePull> PullRequest(int number, CancellationToken ct) => Task.FromResult(Open.Single(p => p.Number == number));
        public Task<IReadOnlyList<string>> PullRequestFiles(int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<int> BehindBy(string branch, CancellationToken ct) { Calls.Add("behind " + branch); return Task.FromResult(2); }
        public Task<int> UpdateBranch(int pullRequest, string expectedHead, CancellationToken ct) { Calls.Add($"update {pullRequest}"); return Task.FromResult(202); }
    }

    sealed class Items(InMemoryWorkItemStore store) : IBaseUpdateItems
    {
        public Task<BaseItemFacts?> FactsOf(int item, CancellationToken ct) =>
            Task.FromResult(store.States.TryGetValue(new ItemId(Home, item).ToString(), out var s) ? BaseItemFacts.From(s) : null);
        public Task<string?> QcRecord(int pullRequest, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly Github Gh = new();
        public readonly List<(string Verb, string Stdin)> Script = [];   // (no rule script is behind the verbs any more: it stays empty)
        public readonly List<MergeRetryDue> Published = [];
        public MergeVerbHandlers Handlers;

        public Rig(string policy = "auto", bool parts = true, Fworker.Workflows.WorkItem.Verbs.IItemRefusals? refusals = null, IMergeMigrations? migrations = null, params string[] labels)
        {
            var settings = new MergeVerbSettings(policy, "Auto Merge", ["hold:owner"], "Functional Testing", _ => null);
            var verbs = new MergeVerbService(Home, Store, new Labels(labels), m => { Published.Add(m); return Task.FromResult(true); }, settings, new MergeVerbWait(TimeSpan.Zero, 1), _ => Task.CompletedTask, migrations: migrations, refusals: refusals);
            var update = new BaseUpdateService(Gh, new Items(Store), new BackmergeSettings("factory-worker"), _ => Task.CompletedTask);
            MergeVerbParts? p = parts ? new MergeVerbParts(verbs, update, Store, Home) : null;
            Handlers = new MergeVerbHandlers(() => p);
        }

        public async Task Own(int item, StepKind step = StepKind.Merge, string verdictHead = Head, string mergeCommit = "", int pr = 11)
        {
            var key = new ItemId(Home, item).ToString();
            var state = new WorkItemState { Item = key, Generation = 1, Plan = WorkPlans.Docs, Step = step, ArmId = "1.3", VerdictHead = verdictHead, PullRequest = pr, MergeCommit = mergeCommit };
            Assert.True(await Store.Save(key, state, 0, CancellationToken.None));
        }
    }

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WithoutTheSagasParts_EveryVerbIsRefusedToBeTriedAgain()
    {
        var rig = new Rig(parts: false);
        foreach (var (verb, call) in new (string, Func<string, Task<VerbAnswer>>)[] { ("merge", s => rig.Handlers.Merge(s, Timeout)), ("approve-pr", s => rig.Handlers.ApprovePr(s, Timeout)), ("clear-hold", s => rig.Handlers.ClearHold(s, Timeout)),
            ("base-watch", s => rig.Handlers.BaseWatch(s, Timeout)), ("base-update-branch", s => rig.Handlers.BaseUpdateBranch(s, Timeout)) })
        {
            var a = await call("{\"item\":7}");
            Assert.Equal((20, "v2-unavailable"), (a.Code, J(a.Output)["code"]!.ToString()));
        }
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task AnItemTheSagaDoesNotOwn_IsRefused_NotAnsweredByAnotherEngine()
    {
        var rig = new Rig();
        const string body = "{\"item\":9,\"pr\":3}";
        foreach (var a in new[] { await rig.Handlers.Merge(body, Timeout), await rig.Handlers.BaseUpdateBranch("{\"item\":9,\"pr\":3,\"expected\":\"x\"}", Timeout) })
            Assert.Equal((10, "not-answered"), (a.Code, J(a.Output)["code"]!.ToString()));
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task MergePolicyPeerApproved_IsNotAnsweredByV2_EvenForAnItemTheSagaOwns()
    {
        var rig = new Rig(policy: "peer-approved", labels: "Auto Merge");
        await rig.Own(7);
        var m = await rig.Handlers.Merge("{\"item\":7}", Timeout);
        Assert.Equal((10, "not-answered"), (m.Code, J(m.Output)["code"]!.ToString()));
        var a = await rig.Handlers.ApprovePr("{\"item\":7,\"text\":\"x\"}", Timeout);
        Assert.Equal((10, "not-answered"), (a.Code, J(a.Output)["code"]!.ToString()));
    }

    [Fact]
    public async Task AnItemTheSagaOwns_IsAnsweredHere_WithTheScriptsShape()
    {
        var rig = new Rig(labels: "Auto Merge");
        await rig.Own(7, mergeCommit: "ccc3333333333333333333333333333333333333");
        var a = await rig.Handlers.Merge("{\"item\":7,\"pr\":11}", Timeout);
        Assert.Equal(0, a.Code);
        Assert.Equal(("already-merged", "ccc3333333333333333333333333333333333333"), (J(a.Output)["outcome"]!.ToString(), J(a.Output)["sha"]!.ToString()));
        Assert.Empty(rig.Script);
        // approve-pr needs no item: under auto it is the script's refusal whatever the item
        a = await rig.Handlers.ApprovePr("{\"item\":7,\"text\":\"fine\"}", Timeout);
        Assert.Equal((10, "not-required"), (a.Code, J(a.Output)["code"]!.ToString()));
        a = await rig.Handlers.ClearHold("{\"item\":7}", Timeout);
        Assert.Equal((10, "not-held"), (a.Code, J(a.Output)["code"]!.ToString()));
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task ARefusalOfTheMergeCheck_IsTheAnswer_AndNothingIsAskedOfTheSaga()
    {
        var rig = new Rig(labels: "hold:owner");
        await rig.Own(7);
        var a = await rig.Handlers.Merge("{\"item\":7}", Timeout);
        Assert.Equal((10, "held-by-label"), (a.Code, J(a.Output)["code"]!.ToString()));
        Assert.Empty(rig.Published);
    }

    sealed class Counted : Fworker.Workflows.WorkItem.Verbs.IItemRefusals
    {
        public readonly List<int> Added = [];
        public Task Add(int item, CancellationToken ct) { Added.Add(item); return Task.CompletedTask; }
        public Task Reset(int item, CancellationToken ct) => Task.CompletedTask;
        public Task<int> Count(int item, CancellationToken ct) => Task.FromResult(Added.Count(x => x == item));
    }

    sealed class Collides(MigrationCollision? c) : IMergeMigrations
    {
        public Task<MigrationCollision?> CollisionOf(int pullRequest, CancellationToken ct) => Task.FromResult(c);
    }

    [Fact]
    public async Task ARefusal10_ThatV2Answers_IsOneDataPointOfTheModelLadder_AsTheScriptCountsItsOwn()
    {
        var counted = new Counted();
        var rig = new Rig(labels: "hold:owner", refusals: counted);
        await rig.Own(7);
        Assert.Equal(10, (await rig.Handlers.Merge("{\"item\":7}", Timeout)).Code);                       // held-by-label
        Assert.Equal(10, (await rig.Handlers.ApprovePr("{\"item\":7,\"text\":\"fine\"}", Timeout)).Code);   // not-required under auto
        Assert.Equal([7, 7], counted.Added);
        Assert.Equal(10, (await rig.Handlers.ClearHold("{\"item\":7}", Timeout)).Code);                   // not-held: clear-hold is no verb of the ladder
        Assert.Equal([7, 7], counted.Added);
        // an item the saga does not own is refused without a count
        await rig.Handlers.Merge("{\"item\":9}", Timeout);
        Assert.Equal([7, 7], counted.Added);
    }

    [Fact]
    public async Task AMigrationNumberCollision_IsTheMergeVerbsRefusal_AndCountsLikeAnyRefusal()
    {
        var counted = new Counted();
        var rig = new Rig(labels: "Auto Merge", refusals: counted, migrations: new Collides(new MigrationCollision("src/Database/scripts/Update/040_A.sql", "src/Database/scripts/Update/040_B.sql", "041")));
        await rig.Own(7);
        var a = await rig.Handlers.Merge("{\"item\":7,\"pr\":11}", Timeout);
        Assert.Equal((10, "migration-number-taken", "041"), (a.Code, J(a.Output)["code"]!.ToString(), J(a.Output)["next"]!.ToString()));
        Assert.Equal([7], counted.Added); Assert.Empty(rig.Published); Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task AFaultOfTheCall_IsTheScriptsInternalError_NotAnExceptionAndNotTheScript()
    {
        var rig = new Rig();
        var store = new ThrowingStore();
        var verbs = new MergeVerbService(Home, store, new Labels(), _ => Task.FromResult(true), new MergeVerbSettings("auto", "Auto Merge", [], "Functional Testing", _ => null));
        var handlers = new MergeVerbHandlers(() => new MergeVerbParts(verbs, null, store, Home));
        var a = await handlers.Merge("{\"item\":7}", Timeout);
        Assert.Equal(20, a.Code);
        Assert.Equal(("internal-error", 30), (J(a.Output)["code"]!.ToString(), J(a.Output)["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Empty(rig.Script);
    }

    sealed class ThrowingStore : IWorkItemStore
    {
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => throw new InvalidOperationException("the database is down");
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => throw new InvalidOperationException("the database is down");
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => throw new NotSupportedException();
    }

    // ---- base-watch: the saga's items here, the others by the script ----------------------------------------------------------------------

    [Fact]
    public async Task BaseWatchWithNoItemOfTheSaga_HasNoRow()
    {
        var rig = new Rig();
        const string body = "{\"known\":\"m0\",\"items\":[8,9],\"mergedPrs\":[]}";
        var a = await rig.Handlers.BaseWatch(body, Timeout);
        Assert.Equal(0, a.Code);
        Assert.Empty(J(a.Output)["rows"]!.AsArray());
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task BaseWatchWithTheSagasItemsAndOthers_HasARowForTheSagasItemsOnly_InTheOrderOfTheCall()
    {
        var rig = new Rig();
        await rig.Own(7, StepKind.Develop, verdictHead: "", pr: 11);
        await rig.Own(9, StepKind.Develop, verdictHead: "", pr: 13);
        rig.Gh.Open = [new BasePull(11, "open", "factory-worker/7-x", "h7", true, "clean"), new BasePull(13, "open", "factory-worker/9-x", "h9", true, "clean")];
        var a = await rig.Handlers.BaseWatch("{\"known\":\"\",\"items\":[7,8,9]}", Timeout);
        Assert.Equal(0, a.Code);
        var rows = J(a.Output)["rows"]!.AsArray();
        Assert.Equal([7, 9], rows.Select(r => r!["item"]!.GetValue<int>()));
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task BaseWatchWithNoItems_ReadsTheHeadHere()
    {
        var rig = new Rig();
        var a = await rig.Handlers.BaseWatch("{\"known\":\"\",\"items\":[]}", Timeout);
        Assert.Equal("m1", J(a.Output)["head"]!.ToString());
        Assert.Empty(rig.Script);
    }

    [Fact]
    public async Task AFailedWatch_IsAnInternalErrorTheGatewayLogsAsWatchFailed()
    {
        var rig = new Rig();
        var failing = new MergeVerbHandlers(() => new MergeVerbParts(new MergeVerbService(Home, new ThrowingStore(), new Labels(), _ => Task.FromResult(true), new MergeVerbSettings("auto", "Auto Merge", [], "x", _ => null)),
            new BaseUpdateService(rig.Gh, new Items(rig.Store), new BackmergeSettings("factory-worker")), new ThrowingStore(), Home));
        var a = await failing.BaseWatch("{\"items\":[7]}", Timeout);
        Assert.Equal(20, a.Code);
        Assert.Equal("internal-error", J(a.Output)["code"]!.ToString());
    }

    // ---- what the Runner does before it starts the script, done for the calls v2 answers --------------------------------------------------

    [Fact]
    public async Task TheCallsThatCanWait_AreNotMadeWhileTheGitHubBudgetIsLow_AsTheRunnerDoesNotMakeThem()
    {
        var budget = new GitHubBudget(300);
        budget.Observe(new JsonObject { ["core"] = new JsonObject { ["remaining"] = "10", ["reset"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(), ["limit"] = "5000" } });
        var handlers = MergeVerbHandlers.For(new ServiceCollection().AddSingleton(budget).BuildServiceProvider());   // (no Runner and no saga: the answer is given before either is looked for)
        foreach (var (verb, call) in new (string, Func<Task<VerbAnswer>>)[] { ("base-watch", () => handlers.BaseWatch("{\"items\":[7]}", Timeout)), ("base-update-branch", () => handlers.BaseUpdateBranch("{\"item\":7,\"pr\":11,\"expected\":\"h\"}", Timeout)) })
        {
            var a = await call();
            var o = J(a.Output);
            Assert.Equal((20, false, verb, "github-budget"), (a.Code, o["ok"]!.GetValue<bool>(), o["verb"]!.ToString(), o["code"]!.ToString()));
            Assert.True(o["retryAfterSeconds"]!.GetValue<int>() > 0);
            Assert.Equal(20, o["exit"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TheBranchIsNotMoved_ByAGatewayThatDoesNotHoldTheLeaderLease()
    {
        var leader = new GatewayLeader(new Runner(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "factory.ps1")), new LeaderSettings { On = true }, "0.0.0");   // (starting: it has not taken the lease)
        Assert.False(leader.MayAct);
        var handlers = MergeVerbHandlers.For(new ServiceCollection().AddSingleton(leader).BuildServiceProvider());
        var a = await handlers.BaseUpdateBranch("{\"item\":7,\"pr\":11,\"expected\":\"h\"}", Timeout);
        Assert.Equal((20, "lease-lost"), (a.Code, J(a.Output)["code"]!.ToString()));
        // (the watch only reads, and a gateway without the lease leaves its trigger to the loop that checks MayAct: the guard does not refuse it)
    }

    [Fact]
    public async Task AGuardThatRefuses_IsTheAnswer_AndMergeIsNeverGuarded()
    {
        var handlers = new MergeVerbHandlers(() => null, verb => verb is "base-watch" ? new VerbAnswer(20, "{\"code\":\"guarded\"}") : null);
        Assert.Equal(new VerbAnswer(20, "{\"code\":\"guarded\"}"), await handlers.BaseWatch("{}", Timeout));
        Assert.Equal("v2-unavailable", J((await handlers.Merge("{\"item\":7}", Timeout)).Output)["code"]!.ToString());   // (merge is never guarded: the parts are looked for)
    }

    // ---- the QC record, on a real SQL Server (FACTORY_TEST_SQL; returns at once without one) -------------------------------------------

    [SqlFact]
    public async Task TheQcRecordOfAPullRequest_IsReadByItsKey_AndTheSagasFactsByItsItem()
    {
        if (!TestDatabase.Available) return;
        await using var db = await TestDatabase.CreateAsync("fw_w93_");
        Fworker.Persistence.FactoryDbContext Open() => WorkItemWiring.Open(db.ConnectionString);
        await using (var write = Open())
        {
            write.QcEnvironments.Add(new Fworker.Persistence.QcEnvironmentRow { PullRequestKey = "acme/orders!11", Repository = "acme/orders", ItemKey = "acme/orders#7", Revision = 1, Status = "ready", StateJson = "{\"sha\":\"h7\"}", UpdatedAt = DateTimeOffset.UtcNow });
            await write.SaveChangesAsync();
        }
        var store = new InMemoryWorkItemStore();
        var items = new SagaBaseUpdateItems(store, Home, Open);
        Assert.Equal("{\"sha\":\"h7\"}", await items.QcRecord(11, default));
        Assert.Null(await items.QcRecord(12, default));
        Assert.Null(await new SagaBaseUpdateItems(store, Home, null).QcRecord(11, default));   // (no database: no record)
        Assert.Null(await items.FactsOf(7, default));   // (the saga does not know the item: the script's)
        await store.Save("acme/orders#7", new WorkItemState { Item = "acme/orders#7", Generation = 1, Step = StepKind.Merge, VerdictHead = "h7", PullRequest = 11 }, 0, default);
        Assert.Equal(new BaseItemFacts(true, true, false, true, 11, ""), await items.FactsOf(7, default));
    }

    // ---- the settings ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSettingsAreWhatInstanceJsonSays_WithTheScriptsDefaults()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json")))!;
        var s = MergeVerbSettingsReader.From(cfg, DocsPlanSettingsReader.From(cfg).ColumnOf);
        Assert.Equal(("auto", "Auto Merge", "Functional Testing"), (s.MergePolicy, s.AutoMergeLabel, s.ValidationColumn));
        Assert.Equal(["hold:owner"], s.HoldLabels);
        Assert.Equal("Technical Design", s.ColumnName(StepKind.Design));
        Assert.Equal("Functional Testing", s.ColumnName(StepKind.Merge));   // (no column of its own: the validation column)
        var empty = MergeVerbSettingsReader.From(JsonNode.Parse("{}"), _ => null);
        Assert.Equal(("auto", "Auto Merge", "hold:owner"), (empty.MergePolicy, empty.AutoMergeLabel, empty.HoldLabels.Single()));
        Assert.Empty(MergeVerbSettingsReader.From(JsonNode.Parse("{\"holdLabels\":[]}"), _ => null).HoldLabels);
        Assert.Equal("hold:owner", MergeVerbSettingsReader.From(cfg, _ => null).HoldLabelOn(["Auto Merge", "HOLD:OWNER"]));   // (case does not matter)
    }

    // ---- the GitHub adapters ----------------------------------------------------------------------------------------------------------

    sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}{(request.Content is null ? "" : " " + await request.Content.ReadAsStringAsync(ct))}");
            var (status, body) = answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    static GitHubClientFactory Clients(Fake fake) => new(new HttpClient(fake) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("test", "t")), new Fworker.Integrations.GitHub.GitHubBudget());

    [Fact]
    public async Task TheLabelsOfAnItem_AreReadFromItsIssue_AndAMissingOneIsNull()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath.EndsWith("/issues/7") ? (HttpStatusCode.OK, "{\"labels\":[{\"name\":\"Auto Merge\"},{\"name\":\"hold:owner\"}]}") : (HttpStatusCode.NotFound, "{}"));
        var labels = new GitHubItemLabels(Clients(fake));
        Assert.Equal(["Auto Merge", "hold:owner"], await labels.LabelsOf(new ItemId(Home, 7), default));
        Assert.Null(await labels.LabelsOf(new ItemId(Home, 8), default));
        Assert.Equal(["GET /repos/acme/orders/issues/7", "GET /repos/acme/orders/issues/8"], fake.Requests);
        var broken = new GitHubItemLabels(Clients(new Fake(_ => (HttpStatusCode.InternalServerError, "{}"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.LabelsOf(new ItemId(Home, 7), default));
    }

    [Fact]
    public async Task TheMigrationCheck_ReadsTheFilesOfThePullRequest_AndTheScriptsDirectoryOfTheBaseBranchOnlyWhenItAddsOne()
    {
        const string Dir = "src/Database/scripts/Update/";
        var fake = new Fake(r =>
        {
            var p = r.RequestUri!.PathAndQuery;
            if (p.StartsWith("/repos/acme/orders/pulls/11/files")) return (HttpStatusCode.OK, "[{\"filename\":\"" + Dir + "040_AddOrders.sql\",\"status\":\"added\"},{\"filename\":\"src/App.cs\",\"status\":\"modified\"}]");
            if (p.StartsWith("/repos/acme/orders/pulls/12/files")) return (HttpStatusCode.OK, "[{\"filename\":\"src/App.cs\",\"status\":\"modified\"}]");
            if (p.StartsWith("/repos/acme/orders/pulls/13/files")) return (HttpStatusCode.OK, "[{\"filename\":\"" + Dir + "041_New.sql\",\"status\":\"added\"}]");
            if (p.StartsWith("/repos/acme/orders/pulls/14/files")) return (HttpStatusCode.InternalServerError, "{}");
            if (p.StartsWith("/repos/acme/orders/contents/" + Dir.TrimEnd('/') + "?ref=master")) return (HttpStatusCode.OK, "[{\"name\":\"039_A.sql\"},{\"name\":\"040_TakenFirst.sql\"},{\"name\":\"notes.txt\"}]");
            return (HttpStatusCode.NotFound, "{}");
        });
        var check = new GitHubMergeMigrations(Clients(fake), Home, "master");
        Assert.Equal(new MigrationCollision(Dir + "040_AddOrders.sql", Dir + "040_TakenFirst.sql", "041"), await check.CollisionOf(11, default));
        Assert.Null(await check.CollisionOf(12, default));
        Assert.Null(await check.CollisionOf(13, default));   // 041 is free on the base branch
        Assert.Equal(2, fake.Requests.Count(x => x.StartsWith("GET /repos/acme/orders/contents/src/Database/scripts/Update?ref=master")));   // pull requests 11 and 13 only
        await Assert.ThrowsAsync<HttpRequestException>(() => check.CollisionOf(14, default));
        // a base branch without the directory: an empty one
        var none = new GitHubMergeMigrations(Clients(new Fake(r => r.RequestUri!.PathAndQuery.Contains("/files") ? (HttpStatusCode.OK, "[{\"filename\":\"" + Dir + "001_A.sql\",\"status\":\"added\"}]") : (HttpStatusCode.NotFound, "{}"))), Home, "master");
        Assert.Null(await none.CollisionOf(11, default));
    }

    [Fact]
    public async Task TheBackMergeCalls_AskForThePathsOfTheScript_AndReadItsAnswers()
    {
        var fake = new Fake(r =>
        {
            var p = r.RequestUri!.PathAndQuery;
            if (p.EndsWith("/branches/master")) return (HttpStatusCode.OK, "{\"commit\":{\"sha\":\"m2\"}}");
            if (p.Contains("/compare/m1...m2")) return (HttpStatusCode.OK, "{\"files\":[{\"filename\":\"a.md\"},{\"filename\":\"b.cs\"}]}");
            if (p.Contains("/compare/gone...m2")) return (HttpStatusCode.NotFound, "{}");
            if (p.Contains("/compare/master...factory-worker/7-x")) return (HttpStatusCode.OK, "{\"behind_by\":5}");
            if (p.Contains("/compare/master...nowhere")) return (HttpStatusCode.UnprocessableEntity, "{}");
            if (p.StartsWith("/repos/acme/orders/pulls?state=open")) return (HttpStatusCode.OK, "[{\"number\":11,\"state\":\"open\",\"head\":{\"ref\":\"factory-worker/7-x\",\"sha\":\"h7\"},\"mergeable\":null,\"mergeable_state\":\"unknown\"}]");
            if (p.EndsWith("/pulls/11")) return (HttpStatusCode.OK, "{\"number\":11,\"state\":\"open\",\"head\":{\"ref\":\"factory-worker/7-x\",\"sha\":\"h7\"},\"mergeable\":true,\"mergeable_state\":\"clean\"}");
            if (p.Contains("/pulls/11/files?per_page=100&page=1")) return (HttpStatusCode.OK, "[{\"filename\":\"a.md\"}]");
            if (r.Method == HttpMethod.Put && p.EndsWith("/pulls/11/update-branch")) return (HttpStatusCode.Accepted, "{}");
            if (r.Method == HttpMethod.Put && p.EndsWith("/pulls/12/update-branch")) return (HttpStatusCode.Conflict, "{}");
            if (r.Method == HttpMethod.Put && p.EndsWith("/pulls/13/update-branch")) return (HttpStatusCode.BadGateway, "{}");
            return (HttpStatusCode.NotFound, "{}");
        });
        var gh = new GitHubBaseUpdate(Clients(fake), Home, "master");
        Assert.Equal("m2", await gh.BaseHead(default));
        Assert.Equal(["a.md", "b.cs"], await gh.ChangedFiles("m1", "m2", default));
        Assert.Null(await gh.ChangedFiles("gone", "m2", default));
        var open = Assert.Single(await gh.OpenPullRequests(default));
        Assert.Equal(new BasePull(11, "open", "factory-worker/7-x", "h7", null, "unknown"), open);   // (mergeable null until GitHub has computed it)
        Assert.Equal(new BasePull(11, "open", "factory-worker/7-x", "h7", true, "clean"), await gh.PullRequest(11, default));
        Assert.Equal(["a.md"], await gh.PullRequestFiles(11, default));
        Assert.Equal(5, await gh.BehindBy("factory-worker/7-x", default));
        Assert.Equal(-1, await gh.BehindBy("nowhere", default));
        Assert.Equal((202, 409), (await gh.UpdateBranch(11, "h7", default), await gh.UpdateBranch(12, "h7", default)));
        await Assert.ThrowsAsync<HttpRequestException>(() => gh.UpdateBranch(13, "h7", default));
        Assert.Contains("PUT /repos/acme/orders/pulls/11/update-branch {\"expected_head_sha\":\"h7\"}", fake.Requests);
        Assert.Contains("GET /repos/acme/orders/compare/master...factory-worker/7-x?per_page=1", fake.Requests);
    }
}
