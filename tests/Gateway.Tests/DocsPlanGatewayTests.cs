using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Intake;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>The docs-only test of these tests: the design's marker decides, every file is documentation, the merge says nothing.</summary>
sealed class DocsPort : IDocsOnlyPort
{
    public bool ItemIsDocsOnly(bool designMarker) => designMarker;
    public bool? FilesAreDocumentation(IReadOnlyList<string> files) => true;
    public MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles) => null;
}

/// <summary>W6.6 (#335): the gateway's side of the docs plan: its settings, the merge it makes for the saga, the card that follows the saga, the saga's offers, the feed of the verbs and the intake of docs items.</summary>
public sealed class DocsPlanSettingsTests
{
    static JsonNode Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json")))!;
    }

    [Fact]
    public void TheSettingsAreWhatInstanceJsonSays()
    {
        var s = DocsPlanSettingsReader.From(Source());
        Assert.Equal(("Auto Merge", 4), (s.AutoMergeLabel, s.MaxUpdates));
        Assert.Equal("Technical Design", s.ColumnOf(StepKind.Design));
        Assert.Equal("Development", s.ColumnOf(StepKind.Develop));
        Assert.Equal("Functional Testing", s.ColumnOf(StepKind.Verify));
        Assert.Null(s.ColumnOf(StepKind.Merge));   // (the merge happens in the validation column)
        Assert.Equal("Done", s.DoneColumn);
        Assert.Equal("<!-- aisf:docs-only -->", s.DocsMarker);
        Assert.Equal("## Technical Design", s.DesignSection);
        Assert.Equal((3, 2), (s.Rules.MaxReviewRounds, s.Rules.DocsOnlyMaxReviewRounds));
        Assert.Equal([StepKind.Design, StepKind.Develop], s.Rules.ReviewedSteps);   // (reviewVerdictColumns: the design and the development column)
        Assert.True(s.IsHold("hold:owner")); Assert.True(s.IsHold("hold:anything")); Assert.False(s.IsHold("Auto Merge"));
        // the source defaults name no v2 plan, so the policy carries every plan and nothing starts a saga
        Assert.Empty(s.CarriedPlans);
        Assert.Null(s.Policy.CarriedPlans);
    }

    [Fact]
    public void TheDocsPlanNamedByIntake_IsTheOnlyPlanTheEngineCarries()
    {
        var cfg = Source();
        cfg["intake"]!["v2Plans"] = new JsonArray("Docs");
        var s = DocsPlanSettingsReader.From(cfg);
        Assert.Equal(["docs"], s.CarriedPlans);
        Assert.True(s.Policy.Carries("docs")); Assert.False(s.Policy.Carries("feature"));
    }

    [Fact]
    public void TheMergeAndAutoUpdateSettings_FollowThePolicyAndTheConfiguration()
    {
        var cfg = Source();
        Assert.True(DocsPlanSettingsReader.From(cfg).UpdateBranchWhenBehind);   // mergePolicy auto and baseUpdate.autoUpdateBranch
        cfg["mergePolicy"] = "peer-approved";
        Assert.False(DocsPlanSettingsReader.From(cfg).UpdateBranchWhenBehind);
        cfg["mergePolicy"] = "auto"; cfg["baseUpdate"]!["autoUpdateBranch"] = false; cfg["mergeQueueDepth"] = 2;
        var s = DocsPlanSettingsReader.From(cfg);
        Assert.Equal((false, 2), (s.UpdateBranchWhenBehind, s.MergeQueueDepth));
    }

    [Fact]
    public void AMalformedConfiguration_FallsBackFieldByField()
    {
        var s = DocsPlanSettingsReader.From(JsonNode.Parse("""{"timing":[1],"columns":"x","intake":5,"holdLabels":"x","requiredBodySection":[1]}"""));
        Assert.Equal((3, 2, null, "Done", "Auto Merge", 4), (s.Rules.MaxReviewRounds, s.Rules.DocsOnlyMaxReviewRounds, s.Rules.ReviewedSteps, s.DoneColumn, s.AutoMergeLabel, s.MaxUpdates));
        Assert.Empty(s.Columns); Assert.Empty(s.CarriedPlans);
        Assert.Equal(DocsPlanSettingsReader.From(null).DocsMarker, s.DocsMarker);
    }
}

public sealed class DocsMergePortTests
{
    static readonly RepoId Repo = new("acme", "orders");
    static readonly ItemId Item = new(Repo, 7);
    const string Head = "1111111111111111111111111111111111111111";
    const string MergeSha = "4444444444444444444444444444444444444444";

    static DocsPlanSettings Settings(Action<JsonNode>? edit = null)
    {
        var cfg = JsonNode.Parse("""{"autoMergeLabel":"Auto Merge","holdLabels":["hold:owner"],"mergeQueueDepth":2,"mergePolicy":"auto","baseUpdate":{"autoUpdateBranch":true},"timing":{"outOfDateMergeAttempts":3}}""")!;
        edit?.Invoke(cfg);
        return DocsPlanSettingsReader.From(cfg);
    }

    sealed record Sent(string Method, string Path, string Body);

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<Sent> Requests = [];
        public readonly Dictionary<string, Queue<(HttpStatusCode, string)>> Routes = [];
        public Fake On(string route, HttpStatusCode code, string json) { if (!Routes.TryGetValue(route, out var q)) Routes[route] = q = new(); q.Enqueue((code, json)); return this; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add(new(request.Method.Method, path, body));
            if (!Routes.TryGetValue($"{request.Method.Method} {path}", out var q) || q.Count == 0) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            var (code, json) = q.Count > 1 ? q.Dequeue() : q.Peek();
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    sealed class Facts(IReadOnlyList<string> labels, bool exists = true) : IMergeFactsReader
    {
        public Exception? Throw;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult<ItemMergeFacts?>(exists ? new ItemMergeFacts(item, true, labels, "body", true, 11, null, null, false) : null);
        }
    }

    static string Pr(string head = Head, string mergeable = "true") =>
        $$$"""{"state":"open","draft":false,"merged":false,"merge_commit_sha":null,"mergeable":{{{mergeable}}},"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{head}}}","ref":"bot/7-x"}}""";

    const string PrPath = "repos/acme/orders/pulls/11";

    static (DocsMergePort, Fake, InMemoryIdempotencyLedger) Port(Facts facts, Fake? http = null, DocsPlanSettings? settings = null)
    {
        http ??= new Fake();
        var factory = new GitHubClientFactory(new HttpClient(http) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new Fworker.Integrations.GitHub.GitHubBudget());
        var ledger = new InMemoryIdempotencyLedger();
        var merge = new MergePullRequestHandler(ledger, new MergeDirect(factory, null, (_, _) => Task.CompletedTask));
        return (new DocsMergePort(merge, facts, settings ?? Settings()), http, ledger);
    }

    static MergeRequested Request(int attempt = 1, int updates = 0, string updatedFrom = "") => new(Item, 1, "1.4", attempt, 11, Head, updates, updatedFrom);

    [Fact]
    public async Task AMerge_SendsThePassHead_AndAnswersWithTheCommit_UnderTheKeyOfTheAttempt()
    {
        var http = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("PUT " + PrPath + "/merge", HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var (port, _, _) = Port(new Facts(["factory worker", "auto merge"]), http);
        var answer = await port.Run(Request(), default);
        Assert.Equal((Item, 1, "1.4", "merge-attempt|1.4|1", 1), (answer.Item, answer.Generation, answer.ArmId, answer.Key, answer.Attempt));
        Assert.Equal((MergeResultKind.Merged, MergeSha), (answer.Result.Kind, answer.Result.Commit));
        var put = Assert.Single(http.Requests, r => r.Method == "PUT");
        Assert.Equal(Head, JsonNode.Parse(put.Body)!["sha"]!.ToString());
    }

    [Fact]
    public async Task WithoutTheAutoMergeLabel_OrUnderAHold_NothingIsMerged_AndTheAnswerIsAWait()
    {
        foreach (var (labels, reason) in new[] { (new[] { "factory worker" }, "no-auto-merge-label"), (new[] { "Auto Merge", "hold:owner" }, "hold"), (new[] { "Auto Merge", "hold:release" }, "hold") })
        {
            var http = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr());
            var (port, _, _) = Port(new Facts(labels), http);
            var r = (await port.Run(Request(), default)).Result;
            Assert.Equal((MergeResultKind.Blocked, reason, true), (r.Kind, r.Reason, r.Transient));
            Assert.Empty(http.Requests);   // (not even a read of the pull request)
        }
    }

    [Fact]
    public async Task BelowTheReserveOfTheGitHubBudget_TheMergeWaitsForTheReset_WithoutAnyCall()
    {
        var http = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr());
        var factory = new GitHubClientFactory(new HttpClient(http) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), new Fworker.Integrations.GitHub.GitHubBudget());
        var low = true;
        var port = new DocsMergePort(new MergePullRequestHandler(new InMemoryIdempotencyLedger(), new MergeDirect(factory, null, (_, _) => Task.CompletedTask)), new Facts(["Auto Merge"]), Settings(), () => low);
        var r = (await port.Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "github-budget", true), (r.Kind, r.Reason, r.Transient));
        Assert.Empty(http.Requests);
        low = false;
        Assert.NotEqual("github-budget", (await port.Run(Request(2), default)).Result.Reason);
    }

    [Fact]
    public async Task AnItemThatDoesNotExist_IsARefusalForGood_AndWithoutACredentialTheMergeIsUnavailable()
    {
        var (port, _, _) = Port(new Facts(["Auto Merge"], exists: false));
        var r = (await port.Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Failed, "item-not-found", false), (r.Kind, r.Reason, r.Transient));
        var none = (await new DocsMergePort(null, null, Settings()).Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "merge-unavailable", true), (none.Kind, none.Reason, none.Transient));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task AFaultOfTheCallOrOfGitHub_IsATransientWait_NeverAnException_AndNeverAPermanentFailure(Type fault)
    {
        var facts = new Facts(["Auto Merge"]) { Throw = (Exception)Activator.CreateInstance(fault)! };
        var (port, _, _) = Port(facts);
        var r = (await port.Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "merge-error", true), (r.Kind, r.Reason, r.Transient));
    }

    [Fact]
    public async Task AnOwnerWithoutAnInstallation_IsAFailureForGood()
    {
        var facts = new Facts(["Auto Merge"]) { Throw = new NoInstallationException("acme") };
        var r = (await Port(facts).Item1.Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Failed, "no-installation", false), (r.Kind, r.Reason, r.Transient));
    }

    [Fact]
    public async Task AServerErrorOfGitHub_IsTransient_AndTheLedgerKeepsNothingOfIt_SoTheNextAttemptMerges()
    {
        var http = new Fake().On("GET " + PrPath, HttpStatusCode.InternalServerError, """{"message":"boom"}""");
        var (port, _, ledger) = Port(new Facts(["Auto Merge"]), http);
        var first = (await port.Run(Request(1), default)).Result;
        Assert.True(first.Transient);
        Assert.NotEqual(MergeResultKind.Merged, first.Kind);
        Assert.Null(ledger.TryGet(MergePullRequest.KeyFor(Repo, 11, Head, 0, "1.4")));
        http.Routes.Clear();
        http.On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("PUT " + PrPath + "/merge", HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        Assert.Equal(MergeResultKind.Merged, (await port.Run(Request(2), default)).Result.Kind);
    }

    [Fact]
    public async Task AResumeAfterAFailedMerge_IsANewArm_SoAStoredAnswerOfTheParkedArmNeverComesBack()
    {
        var http = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(mergeable: "false")).On("GET " + PrPath, HttpStatusCode.OK, Pr())
            .On("PUT " + PrPath + "/merge", HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var (port, _, ledger) = Port(new Facts(["Auto Merge"]), http);
        var failed = (await port.Run(Request(1), default)).Result;
        Assert.Equal((MergeResultKind.Failed, "not-mergeable"), (failed.Kind, failed.Reason));
        Assert.NotNull(ledger.TryGet(MergePullRequest.KeyFor(Repo, 11, Head, 0, "1.4")));
        var gets = http.Requests.Count;
        Assert.Equal("not-mergeable", (await port.Run(Request(1), default)).Result.Reason);   // the same arm: the ledger answers, no call
        Assert.Equal(gets, http.Requests.Count);
        // the operator resumes: the step is entered anew (ArmId 1.5) and the merge is asked of GitHub again
        var resumed = (await port.Run(new MergeRequested(Item, 1, "1.5", 1, 11, Head, 0, ""), default)).Result;
        Assert.Equal(MergeResultKind.Merged, resumed.Kind);
    }

    [Fact]
    public async Task ARefusalOfGitHub_IsPermanent_AndAHeadThatMovedIsReportedAsSuch()
    {
        var gone = new Fake().On("GET " + PrPath, HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var r = (await Port(new Facts(["Auto Merge"]), gone).Item1.Run(Request(), default)).Result;
        Assert.Equal((MergeResultKind.Failed, "pr-not-found", false), (r.Kind, r.Reason, r.Transient));
        var moved = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: "9999999999999999999999999999999999999999"));
        var m = (await Port(new Facts(["Auto Merge"]), moved).Item1.Run(Request(), default)).Result;
        Assert.NotEqual(MergeResultKind.Merged, m.Kind);
        Assert.DoesNotContain(moved.Requests, x => x.Method == "PUT");
    }

    [Fact]
    public void TheOutcomesOfTheCommand_MapToTheAnswersOfTheSaga()
    {
        Assert.Equal(new MergeResult(MergeResultKind.Merged, "merged", "", MergeSha), DocsMergePort.Map(MergeOutcome.MergedAs(MergeSha)));
        Assert.Equal(new MergeResult(MergeResultKind.HeadMoved, "stale-head", "x"), DocsMergePort.Map(MergeOutcome.HeadMoved("x")));
        var blocked = DocsMergePort.Map(MergeOutcome.Blocked("branch-updated", "d", "abc") with { Updated = true, UpdatesAdded = 1, RetryAfter = TimeSpan.FromSeconds(5) });
        Assert.Equal((MergeResultKind.Blocked, true, "abc", 1, true, TimeSpan.FromSeconds(5)), (blocked.Kind, blocked.Transient, blocked.NewHead, blocked.UpdatesAdded, blocked.Updated, blocked.RetryAfter));
        Assert.False(DocsMergePort.Map(MergeOutcome.Failed("not-mergeable", "d")).Transient);
        Assert.True(DocsMergePort.Map(MergeOutcome.Failed("merge-refused", "d", FailureKind.Transient)).Transient);
        Assert.True(DocsMergePort.Map(MergeOutcome.Failed("rate-limited", "d", FailureKind.RateLimited, TimeSpan.FromMinutes(1))).Transient);
        Assert.False(DocsMergePort.Map(MergeOutcome.Failed("x", "d", FailureKind.NotSupported)).Transient);
    }

    [Fact]
    public async Task TheBranchIsUpdatedOnlyOnTheFirstCallOfAnArm_AndOnlyWhenTheConfigurationAsks()
    {
        string Behind() => """{"ahead_by":0,"behind_by":1,"status":"behind"}""";
        Fake Http() => new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On($"GET repos/acme/orders/compare/master...{Head}", HttpStatusCode.OK, Behind())
            .On("PUT " + PrPath + "/update-branch", HttpStatusCode.Accepted, "{}").On("PUT " + PrPath + "/merge", HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        // the first call finds the branch behind and asks GitHub to update it, with the head the PASS covers as the expected one
        var first = Http();
        var o = (await Port(new Facts(["Auto Merge"]), first).Item1.Run(Request(attempt: 1), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "branch-updated"), (o.Kind, o.Reason));
        var update = Assert.Single(first.Requests, r => r.Path.EndsWith("/update-branch"));
        Assert.Equal(Head, JsonNode.Parse(update.Body)!["expected_head_sha"]!.ToString());
        // a retry does not look for an update before the merge (the script updates once per verdict), nor does a configuration that turns it off
        var retry = Http();
        Assert.Equal(MergeResultKind.Merged, (await Port(new Facts(["Auto Merge"]), retry).Item1.Run(Request(attempt: 2), default)).Result.Kind);
        Assert.DoesNotContain(retry.Requests, r => r.Path.Contains("compare") || r.Path.EndsWith("/update-branch"));
        var off = Http();
        Assert.Equal(MergeResultKind.Merged, (await Port(new Facts(["Auto Merge"]), off, Settings(c => c["baseUpdate"]!["autoUpdateBranch"] = false)).Item1.Run(Request(), default)).Result.Kind);
        Assert.DoesNotContain(off.Requests, r => r.Path.EndsWith("/update-branch"));
    }

    [Fact]
    public async Task ATurnOfTheMergeTrain_IsAnsweredToTheTrain_AndItsFirstTurnDecidesTheUpdateOfABehindBranch()
    {
        string Behind() => """{"ahead_by":0,"behind_by":1,"status":"behind"}""";
        Fake Http() => new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On($"GET repos/acme/orders/compare/master...{Head}", HttpStatusCode.OK, Behind())
            .On("PUT " + PrPath + "/update-branch", HttpStatusCode.Accepted, "{}").On("PUT " + PrPath + "/merge", HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        // the first turn the train grants updates the branch, although it is the saga's second attempt (the first was a wait of the line)
        var first = Http();
        var request = Request(attempt: 2);
        var answer = await Port(new Facts(["Auto Merge"]), first).Item1.Run(new MergeGranted("acme/orders", request, FirstTurn: true), default);
        Assert.Equal(("acme/orders", request), (answer.Key, answer.Request));   // (the answer goes back to the train of the repository, with the request it answers)
        Assert.Equal((MergeResultKind.Blocked, "branch-updated"), (answer.Result.Kind, answer.Result.Reason));
        Assert.Single(first.Requests, r => r.Path.EndsWith("/update-branch"));
        // a later turn of the same arm does not look for an update, and merges
        var later = Http();
        var merged = await Port(new Facts(["Auto Merge"]), later).Item1.Run(new MergeGranted("acme/orders", Request(attempt: 1), FirstTurn: false), default);
        Assert.Equal(MergeResultKind.Merged, merged.Result.Kind);
        Assert.DoesNotContain(later.Requests, r => r.Path.Contains("compare") || r.Path.EndsWith("/update-branch"));
        // the answer to the saga stays what it was: the first attempt updates, with no train in the way
        var plain = Http();
        var direct = await Port(new Facts(["Auto Merge"]), plain).Item1.Run(Request(attempt: 1), default);
        Assert.Equal((MergeResultKind.Blocked, "branch-updated", "merge-attempt|1.4|1"), (direct.Result.Kind, direct.Result.Reason, direct.Key));
    }

    [Fact]
    public async Task ACancellation_IsNotAnAnswer()
    {
        var facts = new Facts(["Auto Merge"]) { Throw = new OperationCanceledException() };
        var (port, _, _) = Port(facts);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => port.Run(Request(), cts.Token));
    }
}

public sealed class DocsProjectionTests
{
    static readonly ItemId Item = new("acme", "orders", 7);
    static DocsPlanSettings Settings() => DocsPlanSettingsReader.From(JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"}}"""));

    sealed class Board : HttpMessageHandler
    {
        public string Column = "Todo";
        public bool OnBoard = true;
        public int Mutations, FailMutations; public HttpStatusCode FailWith = HttpStatusCode.InternalServerError;
        public readonly string[] Columns = ["Todo", "Technical Design", "Development", "Functional Testing", "Done"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = JsonNode.Parse(req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult())!.AsObject();
            var query = body["query"]!.ToString(); var v = body["variables"]!.AsObject();
            if (query.StartsWith("query($o:String!,$n:Int!)"))
                return Task.FromResult(Ok(new JsonObject { ["data"] = new JsonObject { ["organization"] = new JsonObject { ["projectV2"] = new JsonObject
                {
                    ["id"] = "PVT_1",
                    ["fields"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject(), new JsonObject
                    {
                        ["id"] = "FIELD_status", ["name"] = "Status",
                        ["options"] = new JsonArray(Columns.Select(c => (JsonNode)new JsonObject { ["id"] = "opt_" + c.Replace(' ', '_'), ["name"] = c }).ToArray()),
                    }) },
                } } } }));
            if (query.StartsWith("query($o:String!,$r:String!"))
                return Task.FromResult(Ok(new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issue"] = new JsonObject
                {
                    ["id"] = "I_7",
                    ["projectItems"] = new JsonObject { ["nodes"] = new JsonArray(OnBoard ? [(JsonNode)new JsonObject
                    {
                        ["id"] = "card_7", ["project"] = new JsonObject { ["id"] = "PVT_1" }, ["fieldValueByName"] = new JsonObject { ["name"] = Column },
                    }] : [(JsonNode)new JsonObject { ["id"] = "card_other", ["project"] = new JsonObject { ["id"] = "PVT_other" } }]) },
                } } } }));
            Mutations++;
            if (FailMutations > 0) { FailMutations--; return Task.FromResult(new HttpResponseMessage(FailWith)); }
            Column = Columns.First(c => "opt_" + c.Replace(' ', '_') == v["o"]!.ToString());
            return Task.FromResult(Ok(new JsonObject { ["data"] = new JsonObject() }));
        }

        static HttpResponseMessage Ok(JsonNode n) => new(HttpStatusCode.OK) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    sealed class Rig
    {
        public readonly Board Api = new();
        public readonly InMemoryProjectionVersions Versions = new();
        public readonly List<TimeSpan> Pauses = [];
        public readonly DocsProjection Projection;
        public Rig(bool noBoard = false)
        {
            var http = new HttpClient(Api) { BaseAddress = new Uri("http://fake/") };
            var card = new BoardColumnProjection(http, new StaticCredentialSource([], new GitHubCredential("t", "token")), new BoardRef("acme", 3), Versions, new ProjectionEcho(Versions));
            Projection = new DocsProjection(() => noBoard ? null : card, Settings(), (d, _) => { Pauses.Add(d); return Task.CompletedTask; });
        }
    }

    static WorkItemDecided Decided(string action, long sequence, string? step = null, string plan = "docs") =>
        new(Item, 1, sequence, action, plan, step, step is null ? null : "1.1", [], DateTimeOffset.UtcNow);

    [Fact]
    public void TheColumnsOfTheDecisions()
    {
        var p = new Rig().Projection;
        Assert.Equal("Technical Design", p.ColumnFor(Decided(WorkItemActions.StepEntered, 2, "Design")));
        Assert.Equal("Development", p.ColumnFor(Decided(WorkItemActions.StepEntered, 3, "Develop")));
        Assert.Equal("Functional Testing", p.ColumnFor(Decided(WorkItemActions.StepEntered, 4, "Verify")));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.StepEntered, 5, "Merge")));   // (the merge happens in the validation column)
        Assert.Equal("Done", p.ColumnFor(Decided(WorkItemActions.Completed, 6)));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.HandedBack, 6)));   // (v1 takes the item up where it is)
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.Started, 1)));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.Parked, 7, "Verify")));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.StepEntered, 2, "Design", plan: "feature")));
    }

    [Fact]
    public async Task AStartedItemLeavesTodo_AndTheMergedOneGoesToDone_ByTheSequenceOfTheDecision()
    {
        var rig = new Rig();
        Assert.True(await rig.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Equal("Technical Design", rig.Api.Column);
        Assert.True(await rig.Projection.Project(Decided(WorkItemActions.Completed, 9), default));
        Assert.Equal("Done", rig.Api.Column);
        Assert.Equal(9, await rig.Versions.Applied(Item.ToString(), ProjectionTargets.Card));
        // a late decision of an earlier step never moves the card back
        Assert.True(await rig.Projection.Project(Decided(WorkItemActions.StepEntered, 4, "Verify"), default));
        Assert.Equal("Done", rig.Api.Column);
        Assert.Equal(2, rig.Api.Mutations);
    }

    [Fact]
    public async Task AFault_IsTriedAgainAFewTimes_AndTheCardMovesOnceGitHubAnswers()
    {
        var rig = new Rig(); rig.Api.FailMutations = 2;
        Assert.True(await rig.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Equal(3, rig.Api.Mutations);
        Assert.Equal(2, rig.Pauses.Count);
        Assert.Equal("Technical Design", rig.Api.Column);
    }

    [Fact]
    public async Task AFaultThatStays_IsGivenUp_WithoutRecordingTheVersion_SoTheNextDecisionProjectsTheCardAgain()
    {
        var rig = new Rig(); rig.Api.FailMutations = 100;
        Assert.False(await rig.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Equal(4, rig.Api.Mutations);   // the first try and three more
        Assert.Equal(0, await rig.Versions.Applied(Item.ToString(), ProjectionTargets.Card));
        rig.Api.FailMutations = 0;
        Assert.True(await rig.Projection.Project(Decided(WorkItemActions.StepEntered, 3, "Develop"), default));
        Assert.Equal("Development", rig.Api.Column);
    }

    [Fact]
    public async Task ARefusalForGood_IsLoggedNotRetried_AndNoBoardMeansNothingToDo()
    {
        var off = new Rig(); off.Api.OnBoard = false;
        Assert.True(await off.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Empty(off.Pauses); Assert.Equal(0, off.Api.Mutations);
        var rejected = new Rig(); rejected.Api.FailMutations = 100; rejected.Api.FailWith = HttpStatusCode.UnprocessableEntity;
        Assert.True(await rejected.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Equal(1, rejected.Api.Mutations); Assert.Empty(rejected.Pauses);
        var none = new Rig(noBoard: true);
        Assert.True(await none.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), default));
        Assert.Equal(0, none.Api.Mutations);
    }

    [Fact]
    public async Task ACancellation_IsNotRetried()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Projection.Project(Decided(WorkItemActions.StepEntered, 2, "Design"), cts.Token));
    }
}

public sealed class V2OfferTests
{
    static readonly RepoId Home = new("acme", "orders");
    static string? Column(StepKind k) => k switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null };

    static async Task<(InMemoryWorkItemStore, WorkItemService)> Store()
    {
        var store = new InMemoryWorkItemStore();
        return (store, new WorkItemService(store, new WorkItemSaga(new DocsPort())));
    }

    [Fact]
    public async Task AnOpenItemOnAStepASessionWorks_IsOfferedInTheColumnOfTheStep_UntilASessionHoldsIt()
    {
        var (store, svc) = await Store();
        await svc.Handle(new StartWorkItem(new ItemId(Home, 7), "intake", "docs"));
        var own = new V2Ownership(store, Home, Column);
        Assert.Empty(own.Offers);   // (nothing is read until the sweep asks)
        await own.RefreshAsync();
        Assert.Equal([(7, "Technical Design")], own.Offers);
        var node = Assert.IsType<JsonObject>(Assert.Single(own.OfferNodes(_ => false)));
        Assert.Equal((7, "Technical Design", "handoff"), (node["item"]!.GetValue<int>(), node["column"]!.ToString(), node["reason"]!.ToString()));
        // a session holds it: it goes on with its owner, and is offered again only when the owner is gone
        Assert.Empty(own.OfferNodes(item => item == 7));
        // an incremental pass over other items never offers it
        Assert.Empty(own.OfferNodes(_ => false, [8, 9]));
        Assert.Single(own.OfferNodes(_ => false, [7]));
    }

    [Fact]
    public async Task AParkedItem_AnItemOnTheMerge_AnotherRepositorysItem_AndACompletedOne_AreNotOffered()
    {
        var (store, svc) = await Store();
        var parked = new ItemId(Home, 7); var merging = new ItemId(Home, 8); var other = new ItemId("acme", "billing", 9); var done = new ItemId(Home, 10); var verifying = new ItemId(Home, 11);
        foreach (var i in new[] { parked, merging, other, done, verifying }) await svc.Handle(new StartWorkItem(i, "intake", "docs"));
        await svc.Handle(new ParkWorkItem(parked, 0, "park-1", "a person looks"));
        // the merge step: the saga merges, no session is needed
        await WalkToMerge(svc, merging);
        await svc.Handle(new MergeSeen(done, "m", new string('a', 40), 3));
        await WalkTo(svc, verifying, StepKind.Verify);
        var own = new V2Ownership(store, Home, Column);
        await own.RefreshAsync();
        Assert.Equal([(11, "Functional Testing")], own.Offers);
        Assert.True(own.Owns(7) && own.Owns(8) && own.Owns(11)); Assert.False(own.Owns(9) || own.Owns(10));
    }

    [Fact]
    public async Task AfterABreak_AnUnadoptedOpenRow_IsNotOffered_UntilTheAdoptionHasExaminedIt_OrTheGateIsReleased()
    {
        // W9.9b (review): the row is still generation 1 (Open, Design, a session to place) and v1 may have moved or merged the item since; the plant would start a session on it
        var (store, svc) = await Store();
        var a = new ItemId(Home, 7); var b = new ItemId(Home, 8);
        await svc.Handle(new StartWorkItem(a, "intake", "docs")); await svc.Handle(new StartWorkItem(b, "intake", "docs"));
        var gate = new Fworker.Workflows.Adoption.ReadoptionGate();
        var own = new V2Ownership(store, Home, Column, gate);
        await own.RefreshAsync();
        Assert.Equal(2, own.OfferNodes(_ => false).Count);   // (no break: nothing is held)

        gate.Hold("v1 ran with the kill switch", newTerm: true);
        Assert.Empty(own.Offers); Assert.Empty(own.OfferNodes(_ => false)); Assert.Empty(own.OfferNodes(_ => false, [7]));
        await own.RefreshAsync();   // (a refresh in between changes nothing)
        Assert.Empty(own.OfferNodes(_ => false));
        Assert.True(own.Owns(7) && own.Owns(8));   // (v1's sweep still leaves both alone)

        gate.Adopted(a.ToString());   // the adoption examined item 7 only
        Assert.Equal([7], own.OfferNodes(_ => false).Select(n => n["item"]!.GetValue<int>()));
        Assert.Equal([(7, "Technical Design")], own.Offers);

        gate.Release();   // the term's run is done
        Assert.Equal(2, own.OfferNodes(_ => false).Count);

        // a new start of the host is a new term: what the adoption examined before says nothing about this break
        gate.Hold("v1 ran again", newTerm: true);
        Assert.Empty(own.OfferNodes(_ => false));
    }

    static async Task WalkTo(WorkItemService svc, ItemId item, StepKind until)
    {
        for (var i = 0; (await svc.Store.Load(item.ToString(), default))!.State.Step != until && i < 6; i++)
        {
            var s = (await svc.Store.Load(item.ToString(), default))!.State;
            switch (s.Step)
            {
                case StepKind.Design: case StepKind.Develop:
                    if (s.Step == StepKind.Develop) await svc.Handle(new PullRequestChanged(item, 0, "pr-1", 11, "aaa", ["docs/a.md"]));
                    await svc.Handle(new ReviewRecorded(item, s.Generation, s.ArmId, "review-" + s.ArmId, true));
                    await svc.Handle(new StepCompleted(item, s.Generation, s.ArmId, "done-" + s.ArmId));
                    break;
                case StepKind.Verify: await svc.Handle(new VerdictRecorded(item, s.Generation, s.ArmId, "verdict-" + s.ArmId, true, 11, "aaa", "ok")); break;
            }
        }
    }

    static Task WalkToMerge(WorkItemService svc, ItemId item) => WalkTo(svc, item, StepKind.Merge);

    [Fact]
    public async Task WithoutTheColumnsOfTheDocsPlan_OrAfterAFailedRead_NothingIsOffered_AndTheLastAnswerStands()
    {
        var (store, svc) = await Store();
        await svc.Handle(new StartWorkItem(new ItemId(Home, 7), "intake", "docs"));
        var bare = new V2Ownership(store, Home);
        await bare.RefreshAsync();
        Assert.Empty(bare.Offers); Assert.True(bare.Owns(7));
        var own = new V2Ownership(store, Home, Column);
        await own.RefreshAsync();
        var down = new V2Ownership(new DownStore(), Home, Column);
        Assert.False(await down.RefreshAsync());
        Assert.Empty(down.Offers);
        Assert.Single(own.Offers);
    }

    sealed class DownStore : IWorkItemStore
    {
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => throw new TimeoutException();
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => throw new TimeoutException();
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => throw new TimeoutException();
    }
}

public sealed class V2VerbFeedTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    const string Head = "aaa1111111111111111111111111111111111111";
    static DocsPlanSettings Settings() => DocsPlanSettingsReader.From(JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"}}"""));

    sealed class Inner(int code, string output) : IVerbRouter
    {
        public int Calls;
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls++; return Task.FromResult(new VerbAnswer(code, output)); }
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly V2Ownership Own;
        public readonly List<WorkItemInput> Published = [];
        /// <summary>What was published, without the signs of life.</summary>
        public IEnumerable<WorkItemInput> Inputs => Published.Where(m => m is not LeaseRenewed);
        public DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        public Func<WorkItemInput, bool> Accepts = _ => true;
        public Exception? PublishFault;
        public readonly List<TimeSpan> Pauses = [];

        public Rig()
        {
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort()));
            Own = new V2Ownership(Store, Home);
        }

        public async Task Start() { await Svc.Handle(new StartWorkItem(Item, "intake", "docs")); await Own.RefreshAsync(); }
        public WorkItemState State => Store.States[Item.ToString()];

        public V2VerbFeed Feed(IVerbRouter inner, PullRequestHeads? heads = null) =>
            new(inner, Own, Store, Home, Settings(), m => { if (PublishFault is not null) throw PublishFault; if (!Accepts(m)) return Task.FromResult(false); Published.Add(m); return Task.FromResult(true); }, heads, d => { Pauses.Add(d); return Task.CompletedTask; }, () => Now);

        public async Task Walk(params StepKind[] steps)
        {
            foreach (var step in steps)
            {
                var s = State;
                if (step == StepKind.Develop) await Svc.Handle(new PullRequestChanged(Item, 0, "pr-" + s.ArmId, 11, Head, ["docs/a.md"]));
                if (step == StepKind.Verify) await Svc.Handle(new VerdictRecorded(Item, s.Generation, s.ArmId, "v-" + s.ArmId, true, 11, Head, "ok"));
                else { await Svc.Handle(new ReviewRecorded(Item, s.Generation, s.ArmId, "r-" + s.ArmId, true)); await Svc.Handle(new StepCompleted(Item, s.Generation, s.ArmId, "d-" + s.ArmId)); }
            }
        }
    }

    static string Body(string verb, object? extra = null)
    {
        var o = new JsonObject { ["item"] = 7 };
        if (extra is not null) foreach (var kv in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o.ToJsonString();
    }

    const string Section = "## Technical Design\nthe design";

    [Fact]
    public async Task TheScriptsAnswerIsTheCallers_AndTheDesignItWroteGoesToTheSaga_WithTheMarkerItCarries()
    {
        var rig = new Rig(); await rig.Start();
        var inner = new Inner(0, """{"ok":true,"appended":true}""");
        var answer = await rig.Feed(inner).RunAsync("append-body", Body("append-body", new { content = Section + "\n<!-- aisf:docs-only -->" }), TimeSpan.FromSeconds(1));
        Assert.Equal((0, """{"ok":true,"appended":true}"""), (answer.Code, answer.Output));
        var design = Assert.IsType<DesignRecorded>(Assert.Single(rig.Inputs));
        Assert.Equal((Item, 1, "1.1", true), (design.Item, design.Generation, design.ArmId, design.DocsOnlyMarker));
        rig.Published.Clear();
        await rig.Feed(inner).RunAsync("append-body", Body("append-body", new { content = Section }), TimeSpan.FromSeconds(1));
        Assert.False(Assert.IsType<DesignRecorded>(Assert.Single(rig.Inputs)).DocsOnlyMarker);
        // an append that is not the design section tells the saga nothing
        rig.Published.Clear();
        await rig.Feed(inner).RunAsync("append-body", Body("append-body", new { content = "some notes" }), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Inputs);
    }

    // The sweep reads the set of the saga's items before the board; the intake can start a saga between the two, the card then moves, the sweep offers the item, and a session that is quick sends its design before the next read.
    // The feed used to skip every call while it knew no saga (Count 0) or not that item, and the saga never heard the design: its completion was refused, and the review of the next column was taken for the design's.
    [Fact]
    public async Task ASagaThatStartedAfterTheOwnershipWasRead_StillHearsTheDesignAndTheCompletion()
    {
        var rig = new Rig();
        await rig.Svc.Handle(new StartWorkItem(Item, "intake", "docs"));   // (no Own.RefreshAsync: the set is the one read before the start)
        Assert.False(rig.Own.Owns(7));
        var feed = rig.Feed(new Inner(0, """{"ok":true,"appended":true}"""));
        await feed.RunAsync("append-body", Body("append-body", new { content = Section + "\n<!-- aisf:docs-only -->" }), TimeSpan.FromSeconds(1));
        Assert.IsType<DesignRecorded>(Assert.Single(rig.Inputs));
        Assert.True(rig.Own.Owns(7));
        rig.Published.Clear();
        await rig.Feed(new Inner(0, """{"ok":true,"completed":true,"moved_to":"Development"}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.IsType<StepCompleted>(Assert.Single(rig.Inputs));
    }

    [Fact]
    public async Task AReviewAndTheCompletionsOfTheColumns_GoToTheSaga_InTheOrderTheyHappened()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Feed(new Inner(0, """{"ok":true,"verdict":"COMPLETE","round":1}""")).RunAsync("record-review", Body("record-review"), TimeSpan.FromSeconds(1));
        var review = Assert.IsType<ReviewRecorded>(Assert.Single(rig.Inputs));
        Assert.True(review.Complete);
        rig.Published.Clear();
        await rig.Feed(new Inner(0, """{"ok":true,"verdict":"FINDINGS","round":2}""")).RunAsync("record-review", Body("record-review"), TimeSpan.FromSeconds(1));
        Assert.False(Assert.IsType<ReviewRecorded>(Assert.Single(rig.Inputs)).Complete);
        rig.Published.Clear();
        await rig.Feed(new Inner(0, """{"ok":true,"completed":true,"moved_to":"Development"}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        var done = Assert.IsType<StepCompleted>(Assert.Single(rig.Inputs));
        Assert.Equal(("1.1", $"done|1.1"), (done.ArmId, done.Key));
    }

    [Fact]
    public async Task ADevelopmentThatCompletes_TellsTheSagaThePullRequestFirst_WithItsHead()
    {
        var rig = new Rig(); await rig.Start(); await rig.Walk(StepKind.Design);
        var http = new HeadHandler(Head);
        var heads = new PullRequestHeads(new GitHubClientFactory(new HttpClient(http) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), new Fworker.Integrations.GitHub.GitHubBudget()));
        await rig.Feed(new Inner(0, """{"ok":true,"completed":true,"prNumber":11}"""), heads).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(["PullRequestChanged", "StepCompleted"], rig.Inputs.Select(m => m.GetType().Name));
        var pr = Assert.IsType<PullRequestChanged>(rig.Inputs.First());
        Assert.Equal((11, Head), (pr.PullRequest, pr.Head));
        Assert.Equal("repos/acme/orders/pulls/11", http.Paths.Single());
    }

    sealed class HeadHandler(string head) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""{"head":{"sha":"{{{head.ToUpperInvariant()}}}"}}""") });
        }
    }

    [Fact]
    public async Task AFailedWorkerReport_ParksTheItemInTheSagaToo()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Feed(new Inner(0, """{"ok":true,"completed":false,"parked":true,"reason":"worker-failure"}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        var park = Assert.IsType<ParkWorkItem>(Assert.Single(rig.Inputs));
        Assert.Equal("worker-failure", park.Reason);
    }

    [Fact]
    public async Task AVerdict_GoesToTheSagaWithTheHeadItWasGivenFor_AndAPassTheScriptParkedParksTheSaga()
    {
        var rig = new Rig(); await rig.Start(); await rig.Walk(StepKind.Design, StepKind.Develop);
        await rig.Feed(new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"merge"}""")).RunAsync("validation-verdict", Body("validation-verdict", new { pr = 11, head_sha = Head, summary = "reads well", passed = true }), TimeSpan.FromSeconds(1));
        var pass = Assert.IsType<VerdictRecorded>(Assert.Single(rig.Inputs));
        Assert.Equal((true, 11, Head, "reads well", "1.3"), (pass.Passed, pass.PullRequest, pass.Head, pass.Summary, pass.ArmId));
        rig.Published.Clear();
        await rig.Feed(new Inner(0, """{"ok":true,"recorded":true,"passed":false,"next":"demoted"}""")).RunAsync("validation-verdict", Body("validation-verdict", new { pr = 11, head_sha = Head, summary = "no", passed = false }), TimeSpan.FromSeconds(1));
        Assert.False(Assert.IsType<VerdictRecorded>(Assert.Single(rig.Inputs)).Passed);
        rig.Published.Clear();
        await rig.Feed(new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"parked"}""")).RunAsync("validation-verdict", Body("validation-verdict", new { pr = 11, head_sha = Head, summary = "ok", passed = true }), TimeSpan.FromSeconds(1));
        Assert.Equal("pass-without-auto-merge", Assert.IsType<ParkWorkItem>(Assert.Single(rig.Inputs)).Reason);
    }

    [Fact]
    public async Task AVerdictThatNamesNoHead_TakesTheHeadOfThePullRequest()
    {
        var rig = new Rig(); await rig.Start(); await rig.Walk(StepKind.Design, StepKind.Develop);
        var heads = new PullRequestHeads(new GitHubClientFactory(new HttpClient(new HeadHandler(Head)) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), new Fworker.Integrations.GitHub.GitHubBudget()));
        await rig.Feed(new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"merge"}"""), heads).RunAsync("validation-verdict", Body("validation-verdict", new { pr = 11, summary = "ok", passed = true }), TimeSpan.FromSeconds(1));
        Assert.Equal(Head, Assert.IsType<VerdictRecorded>(Assert.Single(rig.Inputs)).Head);
    }

    [Fact]
    public async Task NothingIsFed_ForAnItemThatIsNotOwned_ACallThatFailed_AVerbOutsideTheSet_OrAFeatureItem()
    {
        var rig = new Rig(); await rig.Start();
        var ok = new Inner(0, """{"ok":true,"completed":true}""");
        // not owned: another item
        await rig.Feed(ok).RunAsync("complete", new JsonObject { ["item"] = 99 }.ToJsonString(), TimeSpan.FromSeconds(1));
        // the script failed
        var failed = await rig.Feed(new Inner(10, """{"ok":false,"code":"missing-required-section"}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(10, failed.Code);
        // a verb the saga has no input for
        await rig.Feed(ok).RunAsync("comment", Body("comment"), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Inputs);
        // a feature item, and an ownership that is empty (an item the set lacks is looked up by its key, twice for this call: before the script, for the flags, and after it; nothing is fed)
        var feature = new Rig(); await feature.Svc.Handle(new StartWorkItem(Item, "intake")); await feature.Own.RefreshAsync();
        await feature.Feed(ok).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Empty(feature.Inputs);
        var empty = new Rig();
        var counting = new CountingStore(empty.Store);
        var told = new List<WorkItemInput>();
        var feed = new V2VerbFeed(ok, new V2Ownership(counting, Home), counting, Home, Settings(), m => { told.Add(m); return Task.FromResult(true); });
        await feed.RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(2, counting.Loads);
        Assert.Empty(told);
    }

    sealed class CountingStore(IWorkItemStore inner) : IWorkItemStore
    {
        public int Loads;
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) { Loads++; return inner.Load(itemKey, ct); }
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => inner.OwnedItems(ct);
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => inner.OwnedStates(ct);
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => inner.InFlightByRepository(ct);
    }

    [Fact]
    public async Task ACompletedSaga_AndAStepThatIsNotTheOneTheVerbIsFor_TellNothing()
    {
        var rig = new Rig(); await rig.Start();
        // a verdict while the saga is still in the design: not for this step
        await rig.Feed(new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"merge"}""")).RunAsync("validation-verdict", Body("validation-verdict", new { pr = 11, head_sha = Head, passed = true }), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Inputs);
        await rig.Svc.Handle(new MergeSeen(Item, "m", new string('b', 40), 11));
        await rig.Own.RefreshAsync();
        await rig.Feed(new Inner(0, """{"ok":true,"completed":true}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Inputs);
    }

    [Fact]
    public async Task AHostThatDoesNotTakeTheEvent_IsAskedAgain_ThenTheEventIsDropped_AndTheCallersAnswerIsUntouched()
    {
        var rig = new Rig(); await rig.Start();
        var attempts = 0; rig.Accepts = m => { if (m is not LeaseRenewed) attempts++; return false; };
        var answer = await rig.Feed(new Inner(0, """{"ok":true,"completed":true}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(0, answer.Code);
        Assert.Equal(3, attempts);
        Assert.Equal(4, rig.Pauses.Count);   // (two pauses for the sign of life, two for the completion)
        // a host that throws is asked again too; and past the last try the call still answers
        var thrower = new Rig(); await thrower.Start();
        thrower.PublishFault = new TimeoutException();
        Assert.Equal(0, (await thrower.Feed(new Inner(0, """{"ok":true,"completed":true}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1))).Code);
        // a host that comes back in time gets the event
        var later = new Rig(); await later.Start();
        var n = 0; later.Accepts = m => m is LeaseRenewed || ++n >= 2;
        await later.Feed(new Inner(0, """{"ok":true,"completed":true}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Single(later.Inputs);
    }

    [Fact]
    public async Task TheCallsOfAWorkingSession_RenewTheLeaseOfTheStep_OncePerMinute_AndOnlyForTheItemsOfTheSaga()
    {
        var rig = new Rig(); await rig.Start();
        var inner = new Inner(0, """{"ok":true}""");
        var feed = rig.Feed(inner);
        await feed.RunAsync("comment", Body("comment"), TimeSpan.FromSeconds(1));
        var renewal = Assert.IsType<LeaseRenewed>(Assert.Single(rig.Published));
        Assert.Equal((Item, 1, "1.1"), (renewal.Item, renewal.Generation, renewal.ArmId));
        // the next calls within the minute add nothing; a minute later the next one renews again, under a key of its own
        await feed.RunAsync("claim-item", Body("claim-item"), TimeSpan.FromSeconds(1));
        rig.Now = rig.Now.AddSeconds(30);
        await feed.RunAsync("open-pr", Body("open-pr"), TimeSpan.FromSeconds(1));
        Assert.Single(rig.Published);
        rig.Now = rig.Now.AddSeconds(31);
        await feed.RunAsync("update-pr", Body("update-pr"), TimeSpan.FromSeconds(1));
        Assert.Equal(2, rig.Published.Count);
        Assert.NotEqual(((LeaseRenewed)rig.Published[0]).Key, ((LeaseRenewed)rig.Published[1]).Key);
        // a verb of no interest, a failed call, another item and a parked saga renew nothing
        rig.Now = rig.Now.AddMinutes(5); rig.Published.Clear();
        await feed.RunAsync("get-item", Body("get-item"), TimeSpan.FromSeconds(1));   // (a read, by anyone: no sign of life)
        await rig.Feed(new Inner(10, """{"ok":false}""")).RunAsync("comment", Body("comment"), TimeSpan.FromSeconds(1));
        await feed.RunAsync("comment", new JsonObject { ["item"] = 99 }.ToJsonString(), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Published);
        await rig.Svc.Handle(new ParkWorkItem(Item, 0, "park-1", "a person looks"));
        await feed.RunAsync("comment", Body("comment"), TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Published);
        // a verb that ends the step renews first, so the renewal carries the arm that ends
        var fresh = new Rig(); await fresh.Start();
        await fresh.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r-1", true));
        await fresh.Feed(new Inner(0, """{"ok":true,"completed":true}""")).RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(["LeaseRenewed", "StepCompleted"], fresh.Published.Select(m => m.GetType().Name));
        Assert.All(fresh.Published, m => Assert.Equal("1.1", ((IArmedInput)m).ArmId));
    }

    [Fact]
    public async Task AStoreThatDoesNotAnswer_DoesNotFailTheCall()
    {
        var rig = new Rig(); await rig.Start();
        var feed = new V2VerbFeed(new Inner(0, """{"ok":true,"completed":true}"""), rig.Own, new ThrowingLoad(rig.Store), Home, Settings(), m => Task.FromResult(true));
        var answer = await feed.RunAsync("complete", Body("complete"), TimeSpan.FromSeconds(1));
        Assert.Equal(0, answer.Code);
    }

    sealed class ThrowingLoad(IWorkItemStore inner) : IWorkItemStore
    {
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => throw new TimeoutException();
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => inner.OwnedItems(ct);
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => inner.OwnedStates(ct);
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => inner.InFlightByRepository(ct);
    }
}

public sealed class DocsIntakeJobTests
{
    static readonly RepoId Home = new("acme", "orders");
    const string Marker = "<!-- aisf:docs-only -->";
    static readonly string[] Ai = ["Technical Design", "Development", "Functional Testing"];
    static readonly JsonNode DocsSource = JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"hold":["Release Queue"],"done":"Done"}}""")!;

    sealed class Boards(params BoardCard[] cards) : IBoardReader
    {
        public Exception? Throw;
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef board, CancellationToken ct = default) => Throw is null ? Task.FromResult<IReadOnlyList<BoardCard>>(cards) : throw Throw;
    }

    sealed class Items(Dictionary<int, (string Body, string[] Labels)> items) : IMergeFactsReader
    {
        public readonly List<int> Read = [];
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default)
        {
            Read.Add(item.Number);
            return Task.FromResult(items.TryGetValue(item.Number, out var i) ? new ItemMergeFacts(item, false, i.Labels, i.Body, false, null, null, null, false) : null);
        }
    }

    static BoardCard Card(int n, string column, RepoId? repo = null, bool open = true) => new(repo ?? Home, n, column, open, "item " + n);

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly List<StartWorkItem> Started = [];
        public bool Leader = true, Low; public int Slots = 1; public bool DesignWaiting; public BoardRef? Board = new("acme", 3);
        public DocsIntakeJob Job;
        public Items Facts;

        public Rig(Boards boards, Items facts, IntakeSettings? settings = null, Func<BoardRef?>? board = null, IItemIssueReader? issues = null, Func<CancellationToken, Task<int>>? capacity = null, JsonNode? source = null)
        {
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort()));
            Facts = facts;
            settings ??= new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), ["docs"], "engine:v2", "factory worker", "", "");
            var intake = new WorkItemIntake(Store, settings, async (m, ct) => { Started.Add(m); await Svc.Handle(m, ct); });
            Job = new DocsIntakeJob(boards, facts, intake, board ?? (() => Board), Home, DocsPlanSettingsReader.From(source ?? DocsSource), Ai, () => Slots, () => DesignWaiting, () => Leader, () => Low, issues, capacity);
        }
    }

    // V1.2: where no owner session exists (tdd) the free slots are the scheduler's free capacity of the runtime
    [Fact]
    public async Task WithNoOwnerSession_TheSchedulersFreeCapacity_IsTheIntakesSlots()
    {
        var cards = new Boards(Card(5, "Todo")); var items = Todo((5, Marker));
        var noCapacity = new Rig(cards, new Items(items)) { Slots = 0 };
        Assert.Equal("no-free-slot", await noCapacity.Job.Run(default));
        var zero = new Rig(cards, new Items(items), capacity: _ => Task.FromResult(0)) { Slots = 0 };
        Assert.Equal("no-free-slot", await zero.Job.Run(default));
        var room = new Rig(cards, new Items(items), capacity: _ => Task.FromResult(2)) { Slots = 0 };
        Assert.Equal("started acme/orders#5", await room.Job.Run(default));
        // the owner sessions' slots and the capacity add up
        var both = new Rig(cards, new Items(items), capacity: _ => Task.FromResult(1)) { Slots = 1 };

        Assert.Equal("started acme/orders#5", await both.Job.Run(default));
    }

    [Fact]
    public async Task ALedgerThatCannotBeRead_CountsNoCapacity_AndLeavesTheOwnerSessionsSlots()
    {
        var cards = new Boards(Card(5, "Todo")); var items = Todo((5, Marker));
        var down = new Rig(cards, new Items(items), capacity: _ => throw new TimeoutException("the database does not answer")) { Slots = 0 };
        Assert.Equal("no-free-slot", await down.Job.Run(default));
        var owner = new Rig(cards, new Items(items), capacity: _ => throw new TimeoutException("the database does not answer")) { Slots = 1 };
        Assert.Equal("started acme/orders#5", await owner.Job.Run(default));
    }

    // V1.3 (D3): a person's hotfix label starts the hotfix plan where the engine carries it
    static readonly JsonNode HotfixSource = JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","hold":["Release Queue"],"done":"Done"},"intake":{"v2Plans":["docs","feature","hotfix"]}}""")!;

    [Fact]
    public async Task AHotfixLabel_StartsTheHotfixPlan_AtTheTopRankWithoutADesignStep()
    {
        var hotfixSettings = new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), ["docs", "feature", "hotfix"], "engine:v2", "factory worker", "", "");
        var facts = new Items(new Dictionary<int, (string, string[])> { [5] = ("fix it", new[] { "factory worker", "Hotfix" }), [6] = ("plain", new[] { "factory worker" }) });
        var rig = new Rig(new Boards(Card(5, "Todo"), Card(6, "Todo")), facts, hotfixSettings, source: HotfixSource);
        Assert.Equal("started acme/orders#5", await rig.Job.Run(default));
        var start = Assert.Single(rig.Started);
        Assert.Equal((new ItemId(Home, 5), "intake", "hotfix"), (start.Item, start.Source, start.Plan));
        var state = rig.Store.States["acme/orders#5"];
        Assert.Equal(("hotfix", StepKind.Develop), (state.Plan, state.Step));
        // an item without the label goes the feature way
        var plain = new Rig(new Boards(Card(6, "Todo")), facts, hotfixSettings, source: HotfixSource);
        Assert.Equal("started acme/orders#6", await plain.Job.Run(default));
        Assert.Equal("feature", plain.Started.Single().Plan);
    }

    [Fact]
    public async Task AHotfixLabel_IsNothingSpecial_WhereTheEngineDoesNotCarryTheHotfixPlan()
    {
        var featureOnly = new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), ["docs", "feature"], "engine:v2", "factory worker", "", "");
        var source = JsonNode.Parse("""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"intake":{"v2Plans":["docs","feature"]}}""")!;
        var facts = new Items(new Dictionary<int, (string, string[])> { [5] = ("fix it", new[] { "factory worker", "hotfix" }) });
        var rig = new Rig(new Boards(Card(5, "Todo")), facts, featureOnly, source: source);
        Assert.Equal("started acme/orders#5", await rig.Job.Run(default));
        Assert.Equal("feature", rig.Started.Single().Plan);
    }

    static Dictionary<int, (string, string[])> Todo(params (int, string)[] items) => items.ToDictionary(i => i.Item1, i => (i.Item2, new[] { "factory worker" }));

    [Fact]
    public async Task TheFirstTodoItemWhoseBodyCarriesTheMarker_StartsItsSagaOnTheDocsPlan()
    {
        var rig = new Rig(new Boards(Card(4, "Todo"), Card(5, "Todo"), Card(6, "Todo")), new Items(Todo((4, "a feature"), (5, "Docs only.\n" + Marker), (6, "More docs " + Marker))));
        Assert.Equal("pulled", await rig.Job.Run(default));   // (item 4 is v1's: the rule pulls it, v1's own intake moves it)
        Assert.Empty(rig.Started);
        var docsFirst = new Rig(new Boards(Card(5, "Todo"), Card(4, "Todo")), new Items(Todo((5, "Docs only.\n" + Marker), (4, "a feature"))));
        Assert.Equal("started acme/orders#5", await docsFirst.Job.Run(default));
        var start = Assert.Single(docsFirst.Started);
        Assert.Equal((new ItemId(Home, 5), "intake", "docs"), (start.Item, start.Source, start.Plan));
        Assert.Equal(("docs", StepKind.Design), (docsFirst.Store.States["acme/orders#5"].Plan, docsFirst.Store.States["acme/orders#5"].Step));
    }

    [Fact]
    public async Task OffUnlessIntakeIsOnAndNamesTheDocsPlan_AndNotWhileThisProcessDoesNotActOrTheBudgetIsLow()
    {
        var cards = new Boards(Card(5, "Todo")); var items = Todo((5, Marker));
        var noPlan = new Rig(cards, new Items(items), new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), [], "engine:v2", "", "", ""));
        Assert.Equal("off", await noPlan.Job.Run(default));
        var disabled = new Rig(cards, new Items(items), new IntakeSettings(false, 6, "Todo", [], new Dictionary<string, int>(), ["docs"], "engine:v2", "", "", ""));
        Assert.Equal("off", await disabled.Job.Run(default));
        var standby = new Rig(cards, new Items(items)) { Leader = false };
        Assert.Equal("not-leader", await standby.Job.Run(default));
        var low = new Rig(cards, new Items(items)) { Low = true };
        Assert.Equal("budget-low", await low.Job.Run(default));
        var none = new Rig(cards, new Items(items)) { Board = null };
        Assert.Equal("no-board", await none.Job.Run(default));
        Assert.All(new[] { noPlan, disabled, standby, low, none }, r => { Assert.Empty(r.Started); Assert.Empty(r.Facts.Read); });
    }

    [Fact]
    public async Task ALostLeaseDuringTheBoardRead_StartsNothing()
    {
        var rig = new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, Marker))));
        var calls = 0;
        var intake = new WorkItemIntake(rig.Store, new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), ["docs"], "engine:v2", "", "", ""), (m, ct) => { rig.Started.Add(m); return Task.CompletedTask; });
        var job = new DocsIntakeJob(new Boards(Card(5, "Todo")), rig.Facts, intake, () => new BoardRef("acme", 3), Home, DocsPlanSettingsReader.From(null), Ai, () => 1, () => false, () => ++calls < 2, () => false);
        Assert.Equal("not-leader", await job.Run(default));
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task ItemsOfOtherRepositories_ClosedOnes_AndOtherColumns_AreNotCandidates_AndOnlyTheFirstTenAreRead()
    {
        var cards = new List<BoardCard> { Card(1, "Todo", new RepoId("acme", "billing")), Card(2, "Todo", open: false), Card(3, "Development") };
        for (var n = 10; n < 30; n++) cards.Add(Card(n, "Todo"));
        var facts = new Items(Todo(Enumerable.Range(10, 20).Select(n => (n, "plain")).ToArray()));
        var rig = new Rig(new Boards(cards.ToArray()), facts);
        await rig.Job.Run(default);
        Assert.Equal(Enumerable.Range(10, 10), facts.Read);
    }

    /// <summary>The issues of the tests: closed ones by number (the rest are open, or unknown).</summary>
    sealed class Issues(int[] closed, params int[] open) : IItemIssueReader
    {
        public readonly List<int> Read = [];
        public Exception? Throw;
        public Task<ItemIssue?> ReadIssue(ItemId item, CancellationToken ct = default)
        {
            Read.Add(item.Number);
            if (Throw is not null) throw Throw;
            return Task.FromResult<ItemIssue?>(closed.Contains(item.Number) || open.Contains(item.Number) ? new ItemIssue(item, closed.Contains(item.Number), []) : null);
        }
        public Task<IReadOnlyList<CommitCheck>?> ReadCheckRuns(RepoId repository, string commit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CommitCheck>?>(null);
        public Task<IReadOnlyList<string>?> ReadPullRequestFiles(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>?>(null);
    }

    // W9.10h (D1): the engine's intake checks "Depends on" itself (v1's intake no longer takes the items that name one); the rule is Test-DependencyMet of Verbs.Sweep.ps1
    [Theory]
    [InlineData("Done", true, true)]            // merged and delivered: the card is in the done column
    [InlineData("Release Queue", true, true)]   // merged, waiting for production: a hold column
    [InlineData("Development", false, true)]    // the dependency is closed
    [InlineData("Development", true, false)]    // still being worked on
    [InlineData("Todo", true, false)]
    public async Task AnItemThatDependsOnAnItemOnTheBoard_StartsOnlyOnceTheDependencyIsClosedOrInTheDoneOrAHoldColumn(string column, bool open, bool starts)
    {
        var rig = new Rig(new Boards(Card(5, "Todo"), Card(3, column, open: open)), new Items(Todo((5, "Depends on #3\n" + Marker))));
        var result = await rig.Job.Run(default);
        Assert.Equal(starts ? "started acme/orders#5" : "no-candidate", result);
        Assert.Equal(starts ? 1 : 0, rig.Started.Count);
    }

    [Fact]
    public async Task ADependencyThatIsNotOnTheBoard_IsReadFromItsIssue_AndOneThatCannotBeReadIsNotMet()
    {
        var closed = new Issues([3]);
        var done = new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on #3\n" + Marker))), issues: closed);
        Assert.Equal("started acme/orders#5", await done.Job.Run(default));
        Assert.Equal([3], closed.Read);
        var open = new Issues([], 3);
        Assert.Equal("no-candidate", await new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on #3\n" + Marker))), issues: open).Job.Run(default));
        var unknown = new Issues([]);   // (no such issue)
        Assert.Equal("no-candidate", await new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on #3\n" + Marker))), issues: unknown).Job.Run(default));
        var broken = new Issues([3]) { Throw = new HttpRequestException("boom") };
        Assert.Equal("no-candidate", await new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on #3\n" + Marker))), issues: broken).Job.Run(default));
        Assert.Equal("no-candidate", await new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on #3\n" + Marker)))).Job.Run(default));   // (nothing to read it with)
    }

    [Fact]
    public async Task EveryDependencyMustBeMet_AForeignOneBlocksNothing_AndTheItemsOwnNumberIsNoDependency()
    {
        var both = new Rig(new Boards(Card(5, "Todo"), Card(3, "Done"), Card(4, "Development")), new Items(Todo((5, "Depends on #3 and #4\n" + Marker))));
        Assert.Equal("no-candidate", await both.Job.Run(default));
        var allMet = new Rig(new Boards(Card(5, "Todo"), Card(3, "Done"), Card(4, "Done", open: false)), new Items(Todo((5, "Depends on #3 and #4\n" + Marker))));
        Assert.Equal("started acme/orders#5", await allMet.Job.Run(default));
        var foreign = new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, "Depends on acme/billing#9 and #5\n" + Marker))));
        Assert.Equal("started acme/orders#5", await foreign.Job.Run(default));   // (a reference to another repository is recorded, as in v1, and blocks nothing; #5 is the item itself)
        var qualifiedHome = new Rig(new Boards(Card(5, "Todo"), Card(3, "Todo")), new Items(Todo((5, "Depends on acme/orders#3\n" + Marker))));
        Assert.Equal("no-candidate", await qualifiedHome.Job.Run(default));   // (a qualified reference that names the home repository is a home item)
    }

    [Fact]
    public async Task ADependentThatIsPassedOver_DoesNotKeepAnotherItemFromStarting()
    {
        var rig = new Rig(new Boards(Card(5, "Todo"), Card(6, "Todo"), Card(3, "Development")), new Items(Todo((5, "Depends on #3\n" + Marker), (6, "No dependency\n" + Marker))));
        Assert.Equal("started acme/orders#6", await rig.Job.Run(default));
    }

    [Fact]
    public async Task TheLimitsOfTheIntakeRuleHold()
    {
        var noSlot = new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, Marker)))) { Slots = 0 };
        Assert.Equal("no-free-slot", await noSlot.Job.Run(default));
        var waiting = new Rig(new Boards(Card(5, "Todo")), new Items(Todo((5, Marker)))) { DesignWaiting = true };
        Assert.Equal("design-waiting", await waiting.Job.Run(default));
        // the work in flight is counted from the board's cards of the AI columns, both engines' items alike
        var busy = new Rig(new Boards(Card(5, "Todo"), Card(21, "Technical Design"), Card(22, "Development"), Card(23, "Development"), Card(24, "Functional Testing"), Card(25, "Functional Testing"), Card(26, "Technical Design", open: false), Card(27, "Done")),
            new Items(Todo((5, Marker))), new IntakeSettings(true, 5, "Todo", [], new Dictionary<string, int>(), ["docs"], "engine:v2", "factory worker", "", ""));
        Assert.Equal("wip-limit", await busy.Job.Run(default));
    }

    [Fact]
    public async Task AFaultOfTheBoardOrOfTheItems_ThrowsToTheJobsRegistry_AndStartsNothing()
    {
        var boards = new Boards(Card(5, "Todo")) { Throw = new HttpRequestException("boom") };
        var rig = new Rig(boards, new Items(Todo((5, Marker))));
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Job.Run(default));
        Assert.Empty(rig.Started);
        // an item the reader cannot find is skipped, not a failure
        var gone = new Rig(new Boards(Card(5, "Todo")), new Items([]));
        Assert.Equal("no-candidate", await gone.Job.Run(default));
    }
}
