using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Intake;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>W7.3 (#340): the gateway's side of the feature plan: its settings, the merge in the queue, the card, the slot request, the watcher of CI alongside, the notice of a fix-forward round, the feed of the verbs and the offers.</summary>
public sealed class FeaturePlanSettingsTests
{
    static JsonNode Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "instance.json")))!;
    }

    [Fact]
    public void TheSourceDefaults_CarryNoFeaturePlan_AndKeepTheFinalEnvironmentProd()
    {
        var s = DocsPlanSettingsReader.From(Source());
        Assert.False(s.CarriesFeature);
        Assert.Null(s.Policy.FeatureRules);
        Assert.False(s.Policy.HasFeatureSteps);
        Assert.Equal(("prod", "Release Queue", true, ""), (s.FinalEnvironment, s.ReleaseColumn, s.FeatureSlots, s.SlotRuntime));
        Assert.Equal("prod", s.Policy.FinalEnvironment);
    }

    [Fact]
    public void TheFeaturePlanNamedByIntake_GivesThePolicyItsRules_AndTheSlotsFollowTheirSwitch()
    {
        var cfg = Source();
        cfg["intake"]!["v2Plans"] = new JsonArray("docs", "Feature");
        cfg["delivery"]!["finalEnvironment"] = "tdd";
        var s = DocsPlanSettingsReader.From(cfg);
        Assert.True(s.CarriesFeature);
        Assert.True(s.Policy.HasFeatureSteps);
        Assert.True(s.Policy.Schedules("feature")); Assert.False(s.Policy.Schedules("docs"));
        Assert.True(s.Policy.Carries("feature") && s.Policy.Carries("docs"));
        Assert.Equal("tdd", s.Policy.FinalEnvironment);
        cfg["scheduler"]!["featureSlots"] = false;
        Assert.False(DocsPlanSettingsReader.From(cfg).Policy.Schedules("feature"));
        Assert.True(DocsPlanSettingsReader.From(cfg).Policy.HasFeatureSteps);   // (the steps stay; only the slots are off)
    }

    [Fact]
    public void AMalformedConfiguration_FallsBack()
    {
        var s = DocsPlanSettingsReader.From(JsonNode.Parse("""{"delivery":[1],"scheduler":"x","columns":{"hold":"x"}}"""));
        Assert.Equal(("prod", "", true), (s.FinalEnvironment, s.ReleaseColumn, s.FeatureSlots));
        var odd = DocsPlanSettingsReader.From(JsonNode.Parse("""{"scheduler":{"featureSlots":"no","runtime":5},"delivery":{"finalEnvironment":3}}"""));
        Assert.Equal(("prod", true, ""), (odd.FinalEnvironment, odd.FeatureSlots, odd.SlotRuntime));
    }

    [Fact]
    public void TheSourceDefaults_AreWhatTheDocsDescribe_InTheirSettings()
    {
        var cfg = Source();
        Assert.Equal("prod", cfg["delivery"]!["finalEnvironment"]!.ToString());
        Assert.True(cfg["scheduler"]!["featureSlots"]!.GetValue<bool>());
        Assert.Equal("", cfg["scheduler"]!["runtime"]!.ToString());
        Assert.Empty(cfg["intake"]!["v2Plans"]!.AsArray());   // (nothing starts a saga until the owner names a plan)
    }
}

public sealed class FeatureMergeAndCardTests
{
    static readonly RepoId Repo = new("acme", "orders");
    static readonly ItemId Item = new(Repo, 7);
    const string Head = "1111111111111111111111111111111111111111";
    const string PrPath = "repos/acme/orders/pulls/11";

    static DocsPlanSettings Settings(Action<JsonNode>? edit = null)
    {
        var cfg = JsonNode.Parse("""{"autoMergeLabel":"Auto Merge","holdLabels":["hold:owner"],"mergeQueueDepth":2,"mergePolicy":"auto","baseUpdate":{"autoUpdateBranch":true},"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","hold":["Release Queue"],"done":"Done"},"intake":{"v2Plans":["docs","feature"]},"requiredBodySection":{"Technical Design":"## Technical Design"}}""")!;
        edit?.Invoke(cfg);
        return DocsPlanSettingsReader.From(cfg);
    }

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public readonly Dictionary<string, string> Routes = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.Method.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}";
            Requests.Add(key);
            return Task.FromResult(Routes.TryGetValue(key, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") });
        }
    }

    sealed class Labels(params string[] labels) : IMergeFactsReader
    {
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) => Task.FromResult<ItemMergeFacts?>(new ItemMergeFacts(item, true, labels, "body", true, 11, null, null, false));
    }

    static string Pr() => $$$"""{"state":"open","draft":false,"merged":false,"merge_commit_sha":null,"mergeable":true,"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{Head}}}","ref":"bot/7-x"}}""";

    static (DocsMergePort, Fake) Port(Func<MergeRequested, CancellationToken, Task<int>>? busy)
    {
        var http = new Fake(); http.Routes["GET " + PrPath] = Pr();
        var factory = new GitHubClientFactory(new HttpClient(http) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new Fworker.Integrations.GitHub.GitHubBudget());
        var merge = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), new MergeDirect(factory, null, (_, _) => Task.CompletedTask));
        return (new DocsMergePort(merge, new Labels("factory worker", "Auto Merge"), Settings(), null, busy), http);
    }

    static MergeRequested Request(string plan) => new(Item, 1, "1.4", 1, 11, Head, 0, "", plan);

    [Fact]
    public async Task ACodeChange_WaitsInTheMergeQueue_WhileDepthMergedItemsWaitForTheirRelease()
    {
        var asked = new List<MergeRequested>();
        var (port, http) = Port((m, _) => { asked.Add(m); return Task.FromResult(2); });
        var r = (await port.Run(Request(WorkPlans.Feature), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "merge-queue", true), (r.Kind, r.Reason, r.Transient));
        Assert.Equal(["acme/orders#7"], asked.Select(m => m.Item.ToString()));
        Assert.DoesNotContain(http.Requests, x => x.StartsWith("PUT"));

        // fewer than the depth wait: the merge goes on to GitHub (here it answers nothing for the merge, which is not the point)
        var (open, http2) = Port((_, _) => Task.FromResult(1));
        await open.Run(Request(WorkPlans.Feature), default);
        Assert.Contains(http2.Requests, x => x.StartsWith("PUT"));
    }

    [Fact]
    public async Task ADocumentationChange_NeverWaitsInTheQueue_AndIsNotCounted()
    {
        var calls = 0;
        var (port, http) = Port((_, _) => { calls++; return Task.FromResult(99); });
        await port.Run(Request(WorkPlans.Docs), default);
        Assert.Equal(0, calls);   // (the count is not even read)
        Assert.Contains(http.Requests, x => x.StartsWith("PUT"));
        // and a request that names no plan (one sent before the feature plan, still in a merge line) is a documentation change as it always was
        Assert.Equal("docs", new MergeRequested(Item, 1, "1.4", 1, 11, Head, 0, "").Plan);
    }

    [Fact]
    public async Task AQueueCountThatCannotBeRead_IsATransientWait_NeverARefusalForGood()
    {
        var (port, _) = Port((_, _) => throw new TimeoutException("the database does not answer"));
        var r = (await port.Run(Request(WorkPlans.Feature), default)).Result;
        Assert.Equal((MergeResultKind.Blocked, "merge-error", true), (r.Kind, r.Reason, r.Transient));
    }

    [Fact]
    public async Task TheQueueCount_IsTheSagasMergedFeatureItemsThatNoEnvironmentHasSeen()
    {
        var store = new InMemoryWorkItemStore();
        async Task Put(int n, string plan, StepKind step, string commit, params string[] delivered)
        {
            var s = new WorkItemState { Item = new ItemId(Repo, n).ToString(), Generation = 1, Plan = plan, Step = step, MergeCommit = commit };
            foreach (var d in delivered) s.Delivered[d] = "1.0.1";
            await store.Save(s.Item, s, 0, default);
        }
        await Put(1, "feature", StepKind.Deliver, "aaa");                  // waits for its release candidate
        await Put(2, "feature", StepKind.Deliver, "bbb");                  // too
        await Put(3, "feature", StepKind.Deliver, "ccc", "tdd");           // delivered to the first environment: no longer waiting
        await Put(4, "docs", StepKind.Deliver, "ddd");                     // a documentation change never counts
        await Put(5, "feature", StepKind.Merge, "");                       // not merged yet
        await Put(7, "feature", StepKind.Deliver, "eee");                  // the item asking
        var other = new WorkItemState { Item = new ItemId("acme", "billing", 8).ToString(), Generation = 1, Plan = "feature", Step = StepKind.Deliver, MergeCommit = "fff" };
        await store.Save(other.Item, other, 0, default);                   // another repository
        Assert.Equal(2, await new MergeQueueCount(store).Busy(Request("feature"), default));
    }

    static WorkItemDecided Decided(string action, string? step = null, string plan = "feature") => new(Item, 1, 5, action, plan, step, step is null ? null : "1.1", [], DateTimeOffset.UtcNow);

    [Fact]
    public void TheCardOfAFeatureItem_FollowsItsSteps_AndWaitsForTheDeliveryInTheReleaseQueue()
    {
        var p = new DocsProjection(() => null, Settings());
        Assert.Equal("Technical Design", p.ColumnFor(Decided(WorkItemActions.StepEntered, "Design")));
        Assert.Equal("Development", p.ColumnFor(Decided(WorkItemActions.StepEntered, "Develop")));
        Assert.Equal("Functional Testing", p.ColumnFor(Decided(WorkItemActions.StepEntered, "Verify")));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.StepEntered, "Merge")));
        Assert.Equal("Release Queue", p.ColumnFor(Decided(WorkItemActions.StepEntered, "Deliver")));
        Assert.Equal("Done", p.ColumnFor(Decided(WorkItemActions.Completed)));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.HandedBack)));
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.FixForward, "Develop")));
        // the docs plan has nothing to deliver: its decisions are what they were
        Assert.Null(p.ColumnFor(Decided(WorkItemActions.StepEntered, "Deliver", "docs")));
        // an engine that does not carry the feature plan moves no card of it
        var off = new DocsProjection(() => null, Settings(cfg => cfg["intake"]!["v2Plans"] = new JsonArray("docs")));
        Assert.Null(off.ColumnFor(Decided(WorkItemActions.StepEntered, "Design")));
        Assert.Null(off.ColumnFor(Decided(WorkItemActions.Completed)));
        // and without a hold column the delivery step moves nothing
        var none = new DocsProjection(() => null, Settings(cfg => cfg["columns"]!["hold"] = new JsonArray()));
        Assert.Null(none.ColumnFor(Decided(WorkItemActions.StepEntered, "Deliver")));
    }
}

public sealed class SlotRequestPortTests
{
    static readonly ItemId Item = new("acme", "orders", 7);
    static readonly DateTimeOffset At = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    sealed class Reader(Func<int, string[]> labels) : IMergeFactsReader
    {
        public int Reads;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<ItemMergeFacts?>(new ItemMergeFacts(item, true, labels(Reads), "body", false, null, null, null, false));
        }
    }

    static SlotRequestSettings Settings(string configured = "", string json = """{"runtimes":{"octopus":{"enabled":true,"maxConcurrent":4}},"priorityLabels":{"top":"priority:top","high":"priority:high"}}""")
    {
        var root = JsonNode.Parse(json);
        return SlotRequestSettings.From(root, CapacitySettings.From(root, "tdd"), configured);
    }

    static SlotWanted Wanted(string phase = "develop", bool resume = false, string[]? files = null, bool fromPr = false) => new(Item, 1, "1.2", "ask|acme/orders#7|1|3", phase, resume, 2, files ?? [], fromPr, At);

    [Fact]
    public async Task TheRequest_CarriesWhatTheSagaAsked_AndTheRankOfTheItemsLabels()
    {
        var port = new SlotRequestPort(new Reader(_ => ["factory worker", "Priority:Top", "worker:w9"]), Settings());
        var r = await port.Run(Wanted(files: ["src/a.cs"], fromPr: true, resume: true), default);
        Assert.Equal((Item, "ask|acme/orders#7|1|3", 1, "octopus", "develop"), (r.Item, r.Key, r.Generation, r.Runtime, r.Phase));
        Assert.Equal((true, "top", false), (r.Rank.Pinned, r.Rank.Priority, r.Rank.InIntake));
        Assert.Equal((true, 2, At), (r.Resume, r.ColumnDepth, r.RequestedAt));
        Assert.Equal(["src/a.cs"], r.Footprint.Entries);
        Assert.Equal(FootprintSource.PullRequest, r.Footprint.Source);
        Assert.False(r.Generated);
        Assert.Empty(r.RequiredToolchains);
    }

    [Fact]
    public async Task ADesignRequest_IsInTheIntake_AndAnItemWithoutLabelsRanksPlain()
    {
        var port = new SlotRequestPort(new Reader(_ => ["factory worker"]), Settings());
        var r = await port.Run(Wanted("design"), default);
        Assert.Equal((false, null, true), (r.Rank.Pinned, r.Rank.Priority, r.Rank.InIntake));
        Assert.True(r.Footprint.IsEmpty);
        var high = await new SlotRequestPort(new Reader(_ => ["priority:high"]), Settings()).Run(Wanted(), default);
        Assert.Equal("high", high.Rank.Priority);
    }

    // V1.3: the runtime of the request is the runtime: label's, else the routing rule of the step's column, else scheduler.runtime
    const string Routed = """
        {"runtimes":{"environments":{"tdd":{"octopus":{"enabled":true,"maxConcurrent":4},"routing":[{"phase":"Functional Testing","runtime":"cursor-cloud"},{"phase":"Development","runtime":"local"},{"phase":"Technical Design","runtime":"copilot-cloud"}]},
        "uat":{"routing":[{"runtime":"cae-job"}]}}},"api":{"cursor":{"enabled":true,"maxConcurrent":1,"maxExecutionsPerDay":9},"copilot":{"enabled":true,"maxConcurrent":1,"maxExecutionsPerDay":9,"environments":{"tdd":{"enabled":true}}}}}
        """;

    static SlotRequestSettings RoutedSettings(string environment = "tdd")
    {
        var root = JsonNode.Parse(Routed);
        return SlotRequestSettings.From(root, CapacitySettings.From(root, environment), "", environment,
            phase => phase switch { "design" => "Technical Design", "develop" => "Development", "verify" => "Functional Testing", _ => null });
    }

    [Theory]
    [InlineData("develop", "factory worker", "octopus")]                              // a rule that says local is passed over: the default
    [InlineData("verify", "factory worker", "cursor-cloud")]                          // the rule of the column
    [InlineData("design", "factory worker", "copilot-cloud")]
    [InlineData("verify", "runtime:copilot-cloud", "copilot-cloud")]                  // the label wins over the routing
    [InlineData("develop", "Runtime:Cursor-Cloud", "cursor-cloud")]
    [InlineData("develop", "runtime:nothing-we-know", "octopus")]                     // a kind the scheduler does not know is passed over
    [InlineData("develop", "runtime:local", "octopus")]
    public async Task TheRuntimeOfTheRequest_IsTheLabelsThenTheRoutingOfTheColumnThenTheDefault(string phase, string label, string expected)
    {
        var port = new SlotRequestPort(new Reader(_ => ["factory worker", label]), RoutedSettings());
        Assert.Equal(expected, (await port.Run(Wanted(phase), default)).Runtime);
    }

    [Fact]
    public async Task AnEnvironmentWithoutAnEntry_HasNoRoutingAndTheDefaultRuntime_AndAnEnvironmentOfItsOwnUsesItsRules()
    {
        var prod = RoutedSettings("prod");   // (tdd routes the design step to copilot-cloud; prod has no entry, so no rule: the default runtime)
        Assert.Equal(prod.Runtime, (await new SlotRequestPort(new Reader(_ => []), prod).Run(Wanted("design"), default)).Runtime);
        Assert.Equal("cae-job", (await new SlotRequestPort(new Reader(_ => []), RoutedSettings("uat")).Run(Wanted("verify"), default)).Runtime);
        // without environments the block applies as it is
        var flat = JsonNode.Parse("""{"runtimes":{"routing":[{"phase":"*","runtime":"cursor-cloud"}]},"api":{"cursor":{"enabled":true,"maxConcurrent":1}}}""");
        var settings = SlotRequestSettings.From(flat, CapacitySettings.From(flat, "tdd"), "", "tdd");
        Assert.Equal("cursor-cloud", (await new SlotRequestPort(new Reader(_ => []), settings).Run(Wanted("develop"), default)).Runtime);
        // no reader, no labels: the routing and the default still decide
        Assert.Equal("copilot-cloud", (await new SlotRequestPort(null, RoutedSettings()).Run(Wanted("design"), default)).Runtime);
    }

    [Fact]
    public async Task ALabelForARuntimeThatIsOff_StillAsksForIt_SoTheSchedulerHoldsTheSlotAsRuntimeOff()
    {
        // (cae-job has no job in this configuration: the scheduler has no such runtime and holds the request with runtime-off)
        var port = new SlotRequestPort(new Reader(_ => ["runtime:cae-job"]), RoutedSettings());
        var request = await port.Run(Wanted("develop"), default);
        Assert.Equal("cae-job", request.Runtime);
        var fx = new Fworker.Workflows.Capacity.CapacityScheduler(CapacitySettings.From(JsonNode.Parse(Routed), "tdd"));
        var t = fx.Apply(null, request, At);
        Assert.Contains(t.Result.Messages, m => m is Held { Code: "runtime-off" });
    }

    [Fact]
    public async Task ALabelReadThatFails_IsReadAgain_ThenTheRequestGoesWithoutIt_AndNeverFailsTheRequest()
    {
        var pauses = new List<TimeSpan>();
        var flaky = new FlakyReader(2);
        var port = new SlotRequestPort(flaky, Settings(), (d, _) => { pauses.Add(d); return Task.CompletedTask; });
        var r = await port.Run(Wanted(), default);
        Assert.Equal("top", r.Rank.Priority);   // (the third try read them)
        Assert.Equal(2, pauses.Count);

        var broken = new FlakyReader(100);
        var degraded = await new SlotRequestPort(broken, Settings(), (_, _) => Task.CompletedTask).Run(Wanted(), default);
        Assert.Equal((false, null), (degraded.Rank.Pinned, degraded.Rank.Priority));
        Assert.Equal(3, broken.Calls);
        // no reader at all (no credential): the same
        Assert.Null((await new SlotRequestPort(null, Settings()).Run(Wanted(), default)).Rank.Priority);
        // a cancellation is not a fault to ride out
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SlotRequestPort(new CancelReader(), Settings()).Run(Wanted(), cts.Token));
    }

    sealed class FlakyReader(int failures) : IMergeFactsReader
    {
        public int Calls;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default)
        {
            if (++Calls <= failures) throw new HttpRequestException("GitHub does not answer");
            return Task.FromResult<ItemMergeFacts?>(new ItemMergeFacts(item, true, ["priority:top"], "body", false, null, null, null, false));
        }
    }

    sealed class CancelReader : IMergeFactsReader
    {
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) => throw new OperationCanceledException(ct);
    }

    [Fact]
    public void TheRuntime_IsTheConfiguredOne_ElseOctopus_ElseTheFirstTheSchedulerKnows()
    {
        Assert.Equal("octopus", Settings().Runtime);
        Assert.Equal("cursor-cloud", Settings("cursor-cloud").Runtime);
        Assert.Equal("octopus", Settings("nonsense").Runtime);
        var cursor = Settings(json: """{"api":{"cursor":{"enabled":true,"maxConcurrent":1}}}""");
        Assert.Equal("cursor-cloud", cursor.Runtime);
        Assert.Equal("octopus", Settings(json: "{}").Runtime);   // (nothing configured: the request waits as runtime-off, visibly)
        Assert.Equal(("priority:top", "priority:high"), (Settings(json: "{}").TopLabel, Settings(json: "{}").HighLabel));
        Assert.True(Settings().IsPinned(["worker:w1"]) && !Settings().IsPinned(["worker:"]) && !Settings().IsPinned(["factory worker"]));
    }
}

public sealed class PullRequestChecksTests
{
    static readonly ItemId Item = new("acme", "orders", 7);
    const string Sha = "aaa1111111111111111111111111111111111111";

    sealed class Verbs
    {
        public readonly List<(string Verb, string Stdin)> Calls = [];
        readonly Queue<(int, string)> answers;
        public Verbs(params (int, string)[] answers) { this.answers = new(answers); }
        public Task<(int, string)> Run(string verb, string stdin, TimeSpan timeout) { Calls.Add((verb, stdin)); return Task.FromResult(answers.Dequeue()); }
    }

    static string Brief(string status, params string[] failed) => new JsonObject { ["ok"] = true, ["status"] = status, ["sha"] = Sha.ToUpperInvariant(), ["failed_checks"] = new JsonArray(failed.Select(f => (JsonNode)f).ToArray()) }.ToJsonString();

    [Fact]
    public async Task GreenAndPendingChecks_AreReadWithOneBriefCall()
    {
        var v = new Verbs((0, Brief("success")), (0, Brief("pending")));
        var checks = new PullRequestChecks(v.Run);
        var green = await checks.Read(Item, default);
        Assert.Equal((Sha, ChecksVerdict.Green), (green.Head, green.Verdict));
        Assert.Equal(("pr-checks", """{"item":7,"brief":true}"""), v.Calls[0]);
        var pending = await checks.Read(Item, default);
        Assert.Equal(ChecksVerdict.Pending, pending.Verdict);
        Assert.Equal(2, v.Calls.Count);
    }

    [Fact]
    public async Task AFailedHead_IsRuledByTheScriptsOwnWait_SoItsOneRerunStaysTheScripts()
    {
        var red = new Verbs((0, Brief("failure", "Build result", "Qodana")), (10, """{"ok":false,"code":"checks-red","checks":{"sha":"AAA1111111111111111111111111111111111111","failed_checks":["Build result"]}}"""));
        var r = await new PullRequestChecks(red.Run).Read(Item, default);
        Assert.Equal((ChecksVerdict.Red, Sha), (r.Verdict, r.Head));
        Assert.Equal(["Build result"], r.Failed);
        Assert.Equal("wait-for-checks", red.Calls[1].Verb);
        Assert.Equal("""{"item":7,"timeoutSeconds":1}""", red.Calls[1].Stdin);

        // the script started its one re-run, or the head turned green meanwhile, or a run is still going: not red
        foreach (var (code, answer, expected) in new[]
        {
            (21, """{"ok":false,"code":"rerun-started"}""", ChecksVerdict.Pending),
            (21, """{"ok":false,"code":"still-pending"}""", ChecksVerdict.Pending),
            (0, """{"ok":true,"status":"success","sha":"aaa1111111111111111111111111111111111111"}""", ChecksVerdict.Green),
        })
        {
            var v = new Verbs((0, Brief("failure", "Build result")), (code, answer));
            Assert.Equal(expected, (await new PullRequestChecks(v.Run).Read(Item, default)).Verdict);
        }
        // a red with no names in the wait keeps the names of the first look
        var bare = new Verbs((0, Brief("failure", "Qodana")), (10, """{"ok":false,"code":"checks-red"}"""));
        Assert.Equal(["Qodana"], (await new PullRequestChecks(bare.Run).Read(Item, default)).Failed);
    }

    [Fact]
    public async Task AnAnswerThatIsNoState_IsAFault_NeverARedOrGreenHead()
    {
        foreach (var answers in new[]
        {
            new[] { (20, """{"ok":false,"code":"internal-error"}""") },
            new[] { (0, "not json") },
            new[] { (0, Brief("failure")), (20, """{"ok":false,"code":"internal-error"}""") },
            new[] { (0, Brief("failure")), (12, """{"ok":false,"code":"not-found"}""") },
            new[] { (0, Brief("failure")), (0, "") },
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PullRequestChecks(new Verbs(answers).Run).Read(Item, default));
    }
}

public sealed class FeatureChecksJobTests
{
    static readonly RepoId Home = new("acme", "orders");
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Head2 = "ccc3333333333333333333333333333333333333";

    sealed class Checks : IPullRequestChecks
    {
        public readonly Dictionary<int, CheckRead> Reads = [];
        public readonly List<int> Asked = [];
        public readonly HashSet<int> Fail = [];
        public Task<CheckRead> Read(ItemId item, CancellationToken ct)
        {
            Asked.Add(item.Number);
            if (Fail.Contains(item.Number)) throw new InvalidOperationException("the script answered 20");
            return Task.FromResult(Reads[item.Number]);
        }
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly Checks Reader = new();
        public readonly List<WorkItemInput> Published = [];
        public bool Leader = true, Low, HostUp = true;
        public DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        public readonly FeatureChecksJob Job;

        public Rig()
        {
            var policy = WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false) };
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort(), policy), () => Now);
            Job = new FeatureChecksJob(Store, Home, Reader, m => { if (!HostUp) return Task.FromResult(false); Published.Add(m); return Task.FromResult(true); }, () => Leader, () => Low, () => Now);
        }

        /// <summary>A feature item in the step, with a pull request at <paramref name="head"/>.</summary>
        public async Task Item(int n, StepKind step, string head = Head, string? plan = null)
        {
            var id = new ItemId(Home, n);
            await Svc.Handle(new StartWorkItem(id, "intake", plan));
            for (var i = 0; i < 6 && (await Svc.Store.Load(id.ToString(), default))!.State.Step != step; i++)
            {
                var s = (await Svc.Store.Load(id.ToString(), default))!.State;
                if (s.Step == StepKind.Develop) await Svc.Handle(new PullRequestChanged(id, 0, $"pr-{n}-{i}", 100 + n, head, ["src/a.cs"]));
                await Svc.Handle(new ReviewRecorded(id, s.Generation, s.ArmId, $"r-{n}-{i}", true));
                await Svc.Handle(new StepCompleted(id, s.Generation, s.ArmId, $"d-{n}-{i}"));
            }
            if (step == StepKind.Merge) { var s = (await Svc.Store.Load(id.ToString(), default))!.State; await Svc.Handle(new VerdictRecorded(id, s.Generation, s.ArmId, $"v-{n}", true, 100 + n, head, "ok")); }
        }
    }

    [Fact]
    public async Task AFeatureItemInFunctionalTesting_IsReadAndToldGreenOnce_ThePendingStateEveryMinute()
    {
        var rig = new Rig();
        await rig.Item(7, StepKind.Verify);
        rig.Reader.Reads[7] = new CheckRead(Head, ChecksVerdict.Pending, []);
        Assert.Equal("1 read, 1 told", await rig.Job.Run(default));
        var pending = Assert.IsType<ChecksObserved>(Assert.Single(rig.Published));
        Assert.Equal((new ItemId(Home, 7), 1, Head, ChecksVerdict.Pending), (pending.Item, pending.Generation, pending.Head, pending.Verdict));
        rig.Published.Clear();
        await rig.Job.Run(default);   // the same minute: the same key, the saga answers a duplicate
        var same = Assert.IsType<ChecksObserved>(Assert.Single(rig.Published));
        Assert.Equal(pending.Key, same.Key);
        rig.Published.Clear(); rig.Now = rig.Now.AddMinutes(1);
        await rig.Job.Run(default);
        Assert.NotEqual(pending.Key, Assert.IsType<ChecksObserved>(Assert.Single(rig.Published)).Key);   // (a new minute is a new sign of life)

        rig.Published.Clear();
        rig.Reader.Reads[7] = new CheckRead(Head, ChecksVerdict.Green, []);
        await rig.Job.Run(default);
        Assert.Equal(ChecksVerdict.Green, Assert.IsType<ChecksObserved>(Assert.Single(rig.Published)).Verdict);
        rig.Published.Clear();
        Assert.Equal("1 read, 0 told", await rig.Job.Run(default));   // green is told once for a head
        Assert.Empty(rig.Published);
    }

    [Fact]
    public async Task AHeadTheSagaDoesNotKnow_IsToldFirst_ThenItsChecks()
    {
        var rig = new Rig();
        await rig.Item(7, StepKind.Verify);
        rig.Reader.Reads[7] = new CheckRead(Head2, ChecksVerdict.Red, ["Build result"]);
        await rig.Job.Run(default);
        Assert.Equal(["PullRequestChanged", "ChecksObserved"], rig.Published.Select(m => m.GetType().Name));
        var pr = Assert.IsType<PullRequestChanged>(rig.Published[0]);
        Assert.Equal((107, Head2), (pr.PullRequest, pr.Head));
        Assert.Equal("pr|watch|" + Head2, pr.Key);
        var red = Assert.IsType<ChecksObserved>(rig.Published[1]);
        Assert.Equal(ChecksVerdict.Red, red.Verdict);
        Assert.Equal(["Build result"], red.Failed);
        // and the saga does what the rules say with them: a fix-forward round
        foreach (var m in rig.Published) await rig.Svc.Handle(m);
        var state = rig.Store.States["acme/orders#7"];
        Assert.Equal((StepKind.Develop, 1), (state.Step, state.FixForwardRounds));
    }

    [Fact]
    public async Task OnlyFeatureItemsInVerifyOrMerge_WithAPullRequest_OfTheHomeRepository_AreWatched()
    {
        var rig = new Rig();
        await rig.Item(1, StepKind.Verify);
        await rig.Item(2, StepKind.Merge);
        await rig.Item(3, StepKind.Develop);                 // the owner is still at work
        await rig.Item(4, StepKind.Verify, plan: "docs");    // another plan
        await rig.Item(5, StepKind.Verify);
        await rig.Svc.Handle(new ParkWorkItem(new ItemId(Home, 5), 0, "park-1", "person"));   // parked
        await rig.Svc.Handle(new StartWorkItem(new ItemId("acme", "billing", 6), "intake"));    // another repository
        foreach (var n in new[] { 1, 2 }) rig.Reader.Reads[n] = new CheckRead(Head, ChecksVerdict.Green, []);
        Assert.Equal("2 read, 2 told", await rig.Job.Run(default));
        Assert.Equal([1, 2], rig.Reader.Asked.Order());
    }

    [Fact]
    public async Task ARead_ThatFails_LeavesThatItemForTheNextRun_AndTheOthersAreReadAll_TheSame()
    {
        var rig = new Rig();
        await rig.Item(1, StepKind.Verify); await rig.Item(2, StepKind.Verify);
        rig.Reader.Reads[2] = new CheckRead(Head, ChecksVerdict.Green, []);
        rig.Reader.Fail.Add(1);
        Assert.Equal("2 read, 1 told", await rig.Job.Run(default));
        Assert.Equal(2, Assert.IsType<ChecksObserved>(Assert.Single(rig.Published)).Item.Number);
        rig.Reader.Fail.Clear(); rig.Reader.Reads[1] = new CheckRead(Head, ChecksVerdict.Green, []);
        rig.Published.Clear();
        await rig.Job.Run(default);
        Assert.Equal(1, Assert.IsType<ChecksObserved>(Assert.Single(rig.Published)).Item.Number);   // (nothing was remembered as failed or as told)
    }

    [Fact]
    public async Task AHostThatDoesNotTakeTheMessage_IsAFault_ThatIsTriedAgain_NotATold()
    {
        var rig = new Rig();
        await rig.Item(7, StepKind.Verify);
        rig.Reader.Reads[7] = new CheckRead(Head, ChecksVerdict.Green, []);
        rig.HostUp = false;
        Assert.Equal("1 read, 0 told", await rig.Job.Run(default));
        rig.HostUp = true;
        Assert.Equal("1 read, 1 told", await rig.Job.Run(default));
        Assert.Equal(ChecksVerdict.Green, Assert.IsType<ChecksObserved>(Assert.Single(rig.Published)).Verdict);
    }

    [Fact]
    public async Task OnlyTheLeaderReads_NotOnALowBudget_AndNoMoreThanTheShareOfARun_OldestLookFirst()
    {
        var rig = new Rig();
        for (var n = 1; n <= 10; n++) { await rig.Item(n, StepKind.Verify); rig.Reader.Reads[n] = new CheckRead(Head, ChecksVerdict.Pending, []); }
        rig.Leader = false; Assert.Equal("not-leader", await rig.Job.Run(default));
        rig.Leader = true; rig.Low = true; Assert.Equal("budget-low", await rig.Job.Run(default));
        Assert.Empty(rig.Reader.Asked);
        rig.Low = false;
        await rig.Job.Run(default);
        Assert.Equal(FeatureChecksJob.PerRun, rig.Reader.Asked.Count);
        var first = rig.Reader.Asked.ToList();
        rig.Reader.Asked.Clear();
        await rig.Job.Run(default);
        var notYet = Enumerable.Range(1, 10).Except(first).ToList();
        Assert.Equal(2, notYet.Count);
        Assert.All(notYet, n => Assert.Contains(n, rig.Reader.Asked));   // (the two that were not read come first)
    }

    [Fact]
    public async Task ACancellation_IsNotSwallowed_AndALostLeaseMidRunStopsTheRun()
    {
        var rig = new Rig();
        await rig.Item(1, StepKind.Verify); await rig.Item(2, StepKind.Verify);
        rig.Reader.Reads[1] = rig.Reader.Reads[2] = new CheckRead(Head, ChecksVerdict.Pending, []);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Job.Run(cts.Token));
        var calls = 0;
        var job = new FeatureChecksJob(rig.Store, Home, rig.Reader, m => { rig.Published.Add(m); return Task.FromResult(true); }, () => ++calls < 3, () => false, () => rig.Now);
        Assert.Equal("not-leader", await job.Run(default));
        Assert.Single(rig.Published);
    }
}

public sealed class FeatureNoticeTests
{
    static readonly ItemId Item = new("acme", "orders", 7);
    static WorkItemDecided Decided(string action, string plan = "feature") => new(Item, 1, 4, action, plan, "Develop", "1.2", ["round 1 of 4"], DateTimeOffset.UtcNow);

    [Fact]
    public void AFixForwardRound_SendsTheOwnerWhatTheSweepSendsForARedHead()
    {
        var sent = new List<(int, JsonObject)>();
        var notice = new FeatureNotice((i, t) => { sent.Add((i, t)); return true; }, "Development");
        Assert.True(notice.Notify(Decided(WorkItemActions.FixForward)));
        var (item, trigger) = Assert.Single(sent);
        Assert.Equal(7, item);
        Assert.Equal(("gateway", "checks.failed", 7, "Development", "fix-forward"), (trigger["provider"]!.ToString(), trigger["event"]!.ToString(), trigger["item"]!.GetValue<int>(), trigger["column"]!.ToString(), trigger["reason"]!.ToString()));
        foreach (var other in new[] { Decided(WorkItemActions.ChecksRed), Decided(WorkItemActions.Parked), Decided(WorkItemActions.FixForward, "docs") })
            Assert.False(notice.Notify(other));
        Assert.Single(sent);
    }

    [Fact]
    public void ARouteThatFails_IsLogged_NotThrown()
    {
        var notice = new FeatureNotice((_, _) => throw new InvalidOperationException("no plant"), "Development");
        Assert.False(notice.Notify(Decided(WorkItemActions.FixForward)));
        FeatureNoticeHandler.Handle(Decided(WorkItemActions.FixForward), notice);
    }
}

public sealed class FeatureFeedTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    const string Head = "aaa1111111111111111111111111111111111111";
    const string Section = "## Technical Design\nthe design";

    [Theory]
    [InlineData(true, """["uat","prod"]""", "tdd-gw", true)]
    [InlineData(false, """["uat","prod"]""", "tdd-gw", false)]   // (the promotion is off)
    [InlineData(true, """["uat"]""", "tdd-gw", false)]            // (prod is not among its environments)
    [InlineData(true, """["uat","prod"]""", "other-gw", false)]   // (it promotes from another gateway)
    [InlineData(true, """["uat","prod"]""", "", false)]
    public void TheSettleHoldForProdNeedsV1ToPromoteProdFromThisGateway(bool enabled, string environments, string gatewayEnvironment, bool holds)
    {
        var cfg = JsonNode.Parse("{\"delivery\":{\"finalEnvironment\":\"tdd\",\"autoPromote\":{\"enabled\":" + (enabled ? "true" : "false") + ",\"environments\":" + environments + ",\"gatewayEnvironment\":\"" + gatewayEnvironment + "\"}}}");
        var policy = DocsPlanSettingsReader.From(cfg, n => n == "FACTORY_ENVIRONMENT" ? "tdd-gw" : null).Policy;
        Assert.Equal(holds, policy.HoldsForProd);
    }

    static DocsPlanSettings Settings(bool feature = true) =>DocsPlanSettingsReader.From(JsonNode.Parse(
        $$$"""{"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"},"intake":{"v2Plans":[{{{(feature ? "\"docs\",\"feature\"" : "\"docs\"")}}}]}}"""));

    sealed class Inner(int code, string output) : IVerbRouter
    {
        public readonly List<(string Verb, string Stdin)> Calls = [];
        public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) { Calls.Add((verb, stdin)); return Task.FromResult(new VerbAnswer(code, output)); }
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly V2Ownership Own;
        public readonly List<WorkItemInput> Published = [];
        public IEnumerable<WorkItemInput> Inputs => Published.Where(m => m is not LeaseRenewed);

        public Rig(string plan)
        {
            var policy = Settings().Policy;
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort(), policy));
            Own = new V2Ownership(Store, Home);
            Svc.Handle(new StartWorkItem(Item, "intake", plan)).GetAwaiter().GetResult();
            Own.RefreshAsync().GetAwaiter().GetResult();
        }

        public WorkItemState State => Store.States[Item.ToString()];
        public V2VerbFeed Feed(IVerbRouter inner, bool feature = true, PullRequestHeads? heads = null) => new(inner, Own, Store, Home, Settings(feature), m => { Published.Add(m); return Task.FromResult(true); }, heads, _ => Task.CompletedTask);
    }

    static string Body(object? extra = null)
    {
        var o = new JsonObject { ["item"] = 7 };
        if (extra is not null) foreach (var kv in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o.ToJsonString();
    }

    sealed class PullHandler(string head, string[] files) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Paths.Add(path);
            var json = path.Contains("/files") ? new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["filename"] = f }).ToArray()).ToJsonString() : $$$"""{"head":{"sha":"{{{head}}}"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    static PullRequestHeads Heads(PullHandler h) => new(new GitHubClientFactory(new HttpClient(h) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), new Fworker.Integrations.GitHub.GitHubBudget()));

    [Fact]
    public async Task TheDesignsFilesLine_GoesToTheSagaAsTheFootprint_ForAFeatureItem()
    {
        var rig = new Rig("feature");
        await rig.Feed(new Inner(0, """{"ok":true,"appended":true}""")).RunAsync("append-body", Body(new { content = Section + "\nFiles: src/a.cs, `src/b.cs` (new)\n" }), TimeSpan.FromSeconds(1));
        var design = Assert.IsType<DesignRecorded>(Assert.Single(rig.Inputs));
        Assert.Equal(["src/a.cs", "src/b.cs"], design.Files);
        // the docs plan brings none
        var docs = new Rig("docs");
        await docs.Feed(new Inner(0, """{"ok":true,"appended":true}""")).RunAsync("append-body", Body(new { content = Section + "\nFiles: src/a.cs\n" }), TimeSpan.FromSeconds(1));
        Assert.Null(Assert.IsType<DesignRecorded>(Assert.Single(docs.Inputs)).Files);
    }

    [Fact]
    public async Task ADevelopmentThatCompletes_TellsTheSagaThePullRequestsFiles_AndFunctionalTestingStartsAlongsideCI()
    {
        var rig = new Rig("feature");
        await rig.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r1", true)); await rig.Svc.Handle(new StepCompleted(Item, 1, "1.1", "d1"));
        var http = new PullHandler(Head, ["src/a.cs", "src/b.cs"]);
        var inner = new Inner(0, """{"ok":true,"completed":true,"prNumber":11,"checks":"pending","ci":"alongside","continue":true,"next_column":"Functional Testing"}""");
        var answer = await rig.Feed(inner, heads: Heads(http)).RunAsync("complete", Body(new { column = "Development" }), TimeSpan.FromSeconds(1));
        Assert.Equal(0, answer.Code);
        Assert.Contains("\"ci\":\"alongside\"", answer.Output);   // (the script's answer is the caller's, unchanged)
        // the gateway asked the script to let CI run alongside
        var call = JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!;
        Assert.True(call["ftWithPendingChecks"]!.GetValue<bool>());
        Assert.Equal(["PullRequestChanged", "StepCompleted"], rig.Inputs.Select(m => m.GetType().Name));
        var pr = Assert.IsType<PullRequestChanged>(rig.Inputs.First());
        Assert.Equal((11, Head), (pr.PullRequest, pr.Head));
        Assert.Equal(["src/a.cs", "src/b.cs"], pr.Files);
        Assert.Contains(http.Paths, p => p.StartsWith("repos/acme/orders/pulls/11/files"));
    }

    [Fact]
    public async Task OtherCallsAreNotAsked_ADocsItemsCompletion_AnotherStep_AnotherVerb_OrAnEngineWithoutTheFeaturePlan()
    {
        var feature = new Rig("feature");   // in Design: nothing to ask
        var design = new Inner(0, """{"ok":true,"completed":true}""");
        await feature.Feed(design).RunAsync("complete", Body(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(design.Calls[0].Stdin)!.AsObject().ContainsKey("ftWithPendingChecks"));

        var docs = new Rig("docs");
        await docs.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r1", true)); await docs.Svc.Handle(new StepCompleted(Item, 1, "1.1", "d1"));
        var docsCall = new Inner(0, """{"ok":true,"completed":true}""");
        await docs.Feed(docsCall).RunAsync("complete", Body(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(docsCall.Calls[0].Stdin)!.AsObject().ContainsKey("ftWithPendingChecks"));

        var dev = new Rig("feature");
        await dev.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r1", true)); await dev.Svc.Handle(new StepCompleted(Item, 1, "1.1", "d1"));
        var comment = new Inner(0, """{"ok":true}""");
        await dev.Feed(comment).RunAsync("comment", Body(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(comment.Calls[0].Stdin)!.AsObject().ContainsKey("ftWithPendingChecks"));
        var off = new Inner(0, """{"ok":true,"completed":true,"prNumber":11}""");
        await dev.Feed(off, feature: false).RunAsync("complete", Body(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(off.Calls[0].Stdin)!.AsObject().ContainsKey("ftWithPendingChecks"));
        Assert.Empty(dev.Inputs);   // (an engine that does not carry the plan tells its saga nothing about a feature item)
    }

    [Fact]
    public async Task TheFlagIsTheGatewaysToGive_OneACallerSent_IsTakenOutOfEveryCompletion()
    {
        var sent = Body(new { ftWithPendingChecks = true, column = "Development" });
        // a docs item, an item of another number, a feature item in Design: none is asked for it, so a caller's own flag goes
        foreach (var (plan, item) in new[] { ("docs", 7), ("feature", 99), ("feature", 7) })
        {
            var rig = new Rig(plan);
            var inner = new Inner(0, """{"ok":true,"completed":true}""");
            var body = JsonNode.Parse(sent)!.AsObject(); body["item"] = item;
            await rig.Feed(inner).RunAsync("complete", body.ToJsonString(), TimeSpan.FromSeconds(1));
            Assert.False(JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject().ContainsKey("ftWithPendingChecks"), $"{plan} #{item}");
        }
        // and the rest of what the caller sent is kept
        var keep = new Rig("docs"); var seen = new Inner(0, """{"ok":true}""");
        await keep.Feed(seen).RunAsync("complete", sent, TimeSpan.FromSeconds(1));
        Assert.Equal("Development", JsonNode.Parse(seen.Calls[0].Stdin)!["column"]!.ToString());
        // a flag on any other verb is none of the feed's business
        var other = new Inner(0, """{"ok":true}""");
        await keep.Feed(other).RunAsync("comment", sent, TimeSpan.FromSeconds(1));
        Assert.Equal(sent, other.Calls[0].Stdin);
    }

    // v2: a feature item in Development is given the flag by the feed, once, whatever the spelling of the one the caller sent (the script reads the name without regard to case)
    [Fact]
    public async Task TheFeedGivesTheFlagOnce_ToAFeatureItemInDevelopment_WhateverTheCaseOfOneACallerSent()
    {
        var rig = new Rig("feature");
        await rig.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r1", true)); await rig.Svc.Handle(new StepCompleted(Item, 1, "1.1", "d1"));
        var inner = new Inner(0, """{"ok":true,"completed":true}""");
        var body = JsonNode.Parse(Body(new { column = "Development" }))!.AsObject(); body["FTWITHPENDINGCHECKS"] = false;
        await rig.Feed(inner).RunAsync("complete", body.ToJsonString(), TimeSpan.FromSeconds(1));
        var call = JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject();
        Assert.Equal(["ftWithPendingChecks"], call.Select(p => p.Key).Where(k => k.Equals("ftWithPendingChecks", StringComparison.OrdinalIgnoreCase)).ToArray());
        Assert.True(call["ftWithPendingChecks"]!.GetValue<bool>());
    }

    [Fact]
    public void TakingTheFlagOut_RemovesEverySpellingOfIt_AndNothingElse()
    {
        var body = new JsonObject { ["item"] = 7, ["ftWithPendingChecks"] = true, ["FtWithPendingChecks"] = "true", ["ftwithpendingchecks"] = 1, ["comment"] = "x" };
        Assert.True(V2VerbFeed.TakeOutFlag(body));
        Assert.Equal(["item", "comment"], body.Select(p => p.Key).ToArray());
        Assert.False(V2VerbFeed.TakeOutFlag(body));
    }

    // W9.10e: no inline merge for an item whose saga merges it. The verdict of an owned item carries the gateway's flag, once, whatever the plan and whatever a caller sent; the script makes no merge on seeing it (Verbs.Pr.ps1).
    [Theory]
    [InlineData("docs")]
    [InlineData("feature")]
    public async Task AVerdictForAnOwnedItem_IsGivenSagaMerges_OnceAndWhateverTheCaseOfOneACallerSent(string plan)
    {
        var rig = new Rig(plan);
        var inner = new Inner(0, """{"ok":true,"recorded":true,"passed":true,"next":"merge"}""");
        var body = JsonNode.Parse(Body(new { passed = true, pr = 11 }))!.AsObject(); body["SAGAMERGES"] = false; body["sagaMerges"] = "no";
        var answer = await rig.Feed(inner).RunAsync("validation-verdict", body.ToJsonString(), TimeSpan.FromSeconds(1));
        Assert.Equal(0, answer.Code);
        var call = JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject();
        Assert.Equal([V2VerbFeed.SagaMergesFlagName], call.Select(p => p.Key).Where(k => k.Equals(V2VerbFeed.SagaMergesFlagName, StringComparison.OrdinalIgnoreCase)).ToArray());
        Assert.True(call["sagaMerges"]!.GetValue<bool>());
        Assert.Equal(11, call["pr"]!.GetValue<int>());   // (the rest of the call is kept)
        Assert.False(call.ContainsKey(V2VerbFeed.FlagName));
    }

    [Fact]
    public async Task AVerdictForAnItemThatHasNoSaga_KeepsV1sInlineMerge_AndACallersFlagIsTakenOut()
    {
        var rig = new Rig("docs");
        var other = JsonNode.Parse(Body(new { passed = true, pr = 11 }))!.AsObject(); other["item"] = 99; other["sagaMerges"] = true;
        var inner = new Inner(0, """{"ok":true,"recorded":true}""");
        await rig.Feed(inner).RunAsync("validation-verdict", other.ToJsonString(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(Assert.Single(inner.Calls).Stdin)!.AsObject().ContainsKey(V2VerbFeed.SagaMergesFlagName));
        // and the flag is the verdict's: no other verb is given it
        var comment = new Inner(0, """{"ok":true}""");
        await rig.Feed(comment).RunAsync("comment", Body(), TimeSpan.FromSeconds(1));
        Assert.False(JsonNode.Parse(Assert.Single(comment.Calls).Stdin)!.AsObject().ContainsKey(V2VerbFeed.SagaMergesFlagName));
    }

    [Fact]
    public void TakingTheFlagsOut_AlsoRemovesSagaMerges_InEverySpelling()
    {
        var body = new JsonObject { ["item"] = 7, ["sagaMerges"] = true, ["SagaMerges"] = "true", ["SAGAMERGES"] = 1, ["ftWithPendingChecks"] = true, ["pr"] = 3 };
        Assert.True(V2VerbFeed.TakeOutFlag(body));
        Assert.Equal(["item", "pr"], body.Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task AFaultWhileAskingForTheFlag_LeavesTheCallAsItCame()
    {
        var rig = new Rig("feature");
        await rig.Svc.Handle(new ReviewRecorded(Item, 1, "1.1", "r1", true)); await rig.Svc.Handle(new StepCompleted(Item, 1, "1.1", "d1"));
        var inner = new Inner(0, """{"ok":true,"completed":true}""");
        var feed = new V2VerbFeed(inner, rig.Own, new ThrowingStore(rig.Store), Home, Settings(), m => Task.FromResult(true));
        var stdin = Body();
        await feed.RunAsync("complete", stdin, TimeSpan.FromSeconds(1));
        Assert.Equal(stdin, Assert.Single(inner.Calls).Stdin);
    }

    sealed class ThrowingStore(IWorkItemStore inner) : IWorkItemStore
    {
        public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct) => throw new TimeoutException();
        public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct) => inner.Save(itemKey, state, expectedRevision, ct);
        public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct) => inner.OwnedItems(ct);
        public Task<IReadOnlyList<WorkItemState>> OwnedStates(CancellationToken ct) => inner.OwnedStates(ct);
        public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct) => inner.InFlightByRepository(ct);
    }
}

public sealed class FeatureOfferAndSessionTests
{
    static readonly RepoId Home = new("acme", "orders");
    static string? Column(StepKind k) => k switch { StepKind.Design => "Technical Design", StepKind.Develop => "Development", StepKind.Verify => "Functional Testing", _ => null };

    [Fact]
    public async Task AnItemThatWaitsForItsSlot_IsNotOffered_UntilTheSchedulerGrantsOne()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(true) }));
        var item = new ItemId(Home, 7);
        await svc.Handle(new StartWorkItem(item, "intake"));
        var own = new V2Ownership(store, Home, Column);
        await own.RefreshAsync();
        Assert.True(own.Owns(7));
        Assert.Empty(own.Offers);   // (it asked for a slot and has none: a hold, a full runtime or a runtime that is off keeps the session from being offered)
        await svc.Handle(new SlotAssigned(item, 1, "slot|s1", "s1", "octopus", "host-a", "design", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4)));
        await own.RefreshAsync();
        Assert.Equal([(7, "Technical Design")], own.Offers);
        await svc.Handle(new SlotHeld(item, 1, "held|1", "file-overlap", "acme/orders#3", "acme/orders#3", DateTimeOffset.UtcNow.AddSeconds(5)));
        await own.RefreshAsync();
        Assert.Empty(own.Offers);
    }

    [Fact]
    public async Task AFeatureItemOfAnEngineWithoutSlots_IsOfferedAsAlways()
    {
        var store = new InMemoryWorkItemStore();
        var svc = new WorkItemService(store, new WorkItemSaga(new DocsPort(), WorkItemPolicy.Default with { FeatureRules = new FeaturePlanRules(false) }));
        await svc.Handle(new StartWorkItem(new ItemId(Home, 7), "intake"));
        var own = new V2Ownership(store, Home, Column);
        await own.RefreshAsync();
        Assert.Equal([(7, "Technical Design")], own.Offers);
    }

    [Fact]
    public async Task ASessionThatIsGone_EndsItsSlotAndTellsTheSaga_OnlyForAnItemTheSagaOwns()
    {
        var sink = new JournalCommandSink(_ => Task.FromResult(true), 30);
        var told = new List<(int, string)>();
        sink.OnSessionGone = (item, key) => { told.Add((item, key)); return Task.CompletedTask; };
        // (the plant takes the code off the line and tells the sink beside it: Plant.JournalCoded, tested through the plant below)
        await sink.Coded(7, "reassigned", "session-gone", "gone|a-1");
        await sink.Coded(7, "reassigned", "offer-lapsed", "ack|a-1|w1");
        await sink.Coded(7, "matched", "session-gone", "m|a-1");
        await sink.Send(7, "reassigned", new JsonObject { ["reason"] = "the session is gone", ["opCode"] = "session-gone" }, "gone|a-3");   // (a line carrying the code tells nothing: it never reaches the sink with one)
        Assert.Equal([(7, "gone|a-1")], told);
        // a fault of the callback does not stop the line from being written: the plant logs it and writes the line (see the next tests)
        var lines = new List<WriteJournalLine>();
        var tough = new JournalCommandSink(c => { lines.Add(c); return Task.FromResult(true); }, 30) { OnSessionGone = (_, _) => throw new InvalidOperationException("host down") };
        var plant = new Plant("secret", tough.Send, 30) { JournalCoded = tough.Coded };
        plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        plant.Plan(new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" }), null, null);
        await Until(() => { lock (lines) return lines.Any(l => l.Event == "offered"); });
        string assignment; lock (lines) assignment = lines.Single(l => l.Event == "offered").Key.Split('|')[1];
        plant.OnVerb("accept", new JsonObject { ["item"] = 7 }, new Principal("item", "w1", 7, "owner", assignment), new JsonObject());
        plant.Plan(new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" }), null, null, sweepStarted: DateTime.UtcNow.AddSeconds(5));
        await Until(() => { lock (lines) return lines.Any(l => l.Event == "reassigned"); });
        lock (lines) Assert.Single(lines, l => l.Event == "reassigned");
    }

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
    }

    // The sink's hook is reached through the plant, as production reaches it: Plant.Journal takes the code off the line's fields before it queues the write, so the delegate (sink.Send) never sees
    // "opCode"; the plant tells the sink in the chain, before the line, and the line is the one v1 writes (no code in its fields).
    [Fact]
    public async Task ASessionThatIsGone_ThroughThePlant_EndsItsSlot_AndTheLineHasNoCode()
    {
        var sent = new List<WriteJournalLine>(); var gone = new List<(int Item, string Key)>(); var order = new List<string>();
        var sink = new JournalCommandSink(c => { lock (order) { sent.Add(c); order.Add("line:" + c.Event); } return Task.FromResult(true); }, 30);
        sink.OnSessionGone = (item, key) => { lock (order) { gone.Add((item, key)); order.Add("gone"); } return Task.CompletedTask; };
        var plant = new Plant("secret", (item, ev, fields, key) => sink.Send(item, ev, fields, key), 30) { JournalCoded = sink.Coded };
        plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        var offers = new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" });
        plant.Plan(offers, null, null);
        await Until(() => { lock (order) return sent.Any(s => s.Event == "offered"); });
        var assignment = sent.Single(s => s.Event == "offered").Key.Split('|')[1];
        plant.OnVerb("accept", new JsonObject { ["item"] = 7 }, new Principal("item", "w1", 7, "owner", assignment), new JsonObject());
        Assert.Empty(gone);
        // the sweep after the acceptance finds the item in need of an owner again: the session is gone
        plant.Plan(new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" }), null, null, sweepStarted: DateTime.UtcNow.AddSeconds(5));
        await Until(() => { lock (order) return gone.Count > 0 && sent.Any(s => s.Event == "reassigned"); });
        Assert.Equal([(7, "gone|" + assignment)], gone);
        var line = sent.Single(s => s.Event == "reassigned");
        Assert.False(line.Fields.ContainsKey("opCode"));
        Assert.Contains("is gone", line.Fields["reason"]!.ToString());
        lock (order) Assert.True(order.IndexOf("gone") < order.IndexOf("line:reassigned"));
    }

    [Fact]
    public async Task APlantWithoutTheHook_JournalsAGoneSessionAsBefore_AndAFaultOfTheHookDoesNotStopTheLine()
    {
        foreach (var hook in new Func<int, string, string, string, Task>?[] { null, (_, _, _, _) => throw new InvalidOperationException("host down") })
        {
            var lines = new List<(string Ev, JsonObject Fields, string Key)>();
            var plant = new Plant("secret", (item, ev, fields, key) => { lock (lines) lines.Add((ev, fields, key)); return Task.CompletedTask; }, 30) { JournalCoded = hook };
            plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
            plant.Plan(new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" }), null, null);
            await Until(() => { lock (lines) return lines.Any(l => l.Ev == "offered"); });
            string assignment; lock (lines) assignment = lines.Single(l => l.Ev == "offered").Key.Split('|')[1];
            plant.OnVerb("accept", new JsonObject { ["item"] = 7 }, new Principal("item", "w1", 7, "owner", assignment), new JsonObject());
            plant.Plan(new JsonArray(new JsonObject { ["item"] = 7, ["column"] = "Development", ["kind"] = "own-item" }), null, null, sweepStarted: DateTime.UtcNow.AddSeconds(5));
            await Until(() => { lock (lines) return lines.Any(l => l.Ev == "reassigned"); });
            lock (lines)
            {
                var line = lines.Single(l => l.Ev == "reassigned");
                Assert.Equal("gone|" + assignment, line.Key);
                Assert.Equal(["reason"], line.Fields.Select(f => f.Key).ToArray());   // v1's text of the line: its reason only, never the code
            }
        }
    }
}

/// <summary>
/// v1 (the engine of tdd today, no V2VerbFeed): the flag that lets Functional Testing start beside pending checks is not a worker's to send. The verb route takes it out of every call before the
/// rule script runs, so the item parks at awaiting-checks as it always did.
/// </summary>
public sealed class FtWithPendingChecksV1Tests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public async Task AWorkersFlag_NeverReachesTheRuleScript_InV1()
    {
        if (!StubPwsh.Supported) return;
        var token = Auth.MintItemToken(gw.Env.TokenSecret, 7, "owner", "a7", "ft-owner", TimeSpan.FromHours(1));
        var body = """{"item":7,"column":"Development","comment":"PR open. REVIEW: COMPLETE","ftWithPendingChecks":true,"FTWITHPENDINGCHECKS":"true","ftwithpendingchecks":1}""";
        var r = await gw.Host.SendAsync("POST", "/api/v1/verbs/complete", GatewayEnvironment.ApiPort, token, body, 60);
        Assert.Equal(200, r.Status);
        var seen = StubPwsh.Inputs(gw.Env.StubLog, "complete").Select(i => JsonNode.Parse(i.Text)!.AsObject()).Where(o => o["item"]?.ToString() == "7").ToList();
        var call = Assert.Single(seen);
        Assert.DoesNotContain(call, p => p.Key.Equals("ftWithPendingChecks", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Development", call["column"]!.ToString());   // (the rest of the call is kept)
        Assert.Contains("REVIEW: COMPLETE", call["comment"]!.ToString());
    }
}

public sealed class FeatureIntakeTests
{
    static readonly RepoId Home = new("acme", "orders");
    const string Marker = "<!-- aisf:docs-only -->";

    sealed class Boards(params BoardCard[] cards) : IBoardReader
    {
        public Task<IReadOnlyList<BoardCard>> Read(BoardRef board, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BoardCard>>(cards);
    }

    sealed class Items(Dictionary<int, string> bodies) : IMergeFactsReader
    {
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) => Task.FromResult<ItemMergeFacts?>(new ItemMergeFacts(item, false, ["factory worker"], bodies[item.Number], false, null, null, null, false));
    }

    static async Task<(string, List<StartWorkItem>)> Run(string[] plans, Dictionary<int, string> bodies, params int[] todo)
    {
        var started = new List<StartWorkItem>();
        var settings = new IntakeSettings(true, 6, "Todo", [], new Dictionary<string, int>(), plans, "engine:v2", "factory worker", "", "");
        var store = new InMemoryWorkItemStore();
        var intake = new WorkItemIntake(store, settings, (m, _) => { started.Add(m); return Task.CompletedTask; });
        var docsSettings = DocsPlanSettingsReader.From(JsonNode.Parse($$$"""{"intake":{"v2Plans":[{{{string.Join(",", plans.Select(p => $"\"{p}\""))}}}]}}"""));
        var job = new DocsIntakeJob(new Boards(todo.Select(n => new BoardCard(Home, n, "Todo", true, "item " + n)).ToArray()), new Items(bodies), intake, () => new BoardRef("acme", 3), Home, docsSettings,
            ["Technical Design", "Development", "Functional Testing"], () => 1, () => false, () => true, () => false, null, (_, _, _) => Task.FromResult(1));   // (the runtime has room: a code item needs it)
        return (await job.Run(default), started);
    }

    [Fact]
    public async Task WhereTheEngineCarriesTheFeaturePlan_ACodeItemStartsOnIt_AndADocsItemOnTheDocsPlan()
    {
        var (result, started) = await Run(["docs", "feature"], new() { [4] = "a feature", [5] = "docs\n" + Marker }, 4, 5);
        Assert.Equal("started acme/orders#4", result);
        var start = Assert.Single(started);
        Assert.Equal((new ItemId(Home, 4), "feature"), (start.Item, start.Plan));
        var (_, docs) = await Run(["docs", "feature"], new() { [5] = "docs\n" + Marker }, 5);
        Assert.Equal("docs", Assert.Single(docs).Plan);
    }

    [Fact]
    public async Task WhereItDoesNot_ACodeItemIsV1sToTake_AsBefore()
    {
        var (result, started) = await Run(["docs"], new() { [4] = "a feature" }, 4);
        Assert.Equal("pulled", result);
        Assert.Empty(started);
        var (featureOnly, onlyStarted) = await Run(["feature"], new() { [4] = "a feature", [5] = "docs\n" + Marker }, 5);
        Assert.Equal("pulled", featureOnly);   // (a docs item is v1's when the engine carries only the feature plan)
        Assert.Empty(onlyStarted);
    }
}
