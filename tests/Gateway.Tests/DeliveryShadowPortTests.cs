using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Incident;
using Xunit;

namespace Gateway.Tests;

/// <summary>W5.2 without a database: the settings the train gets from v1's configuration, the reads (GET only, v1's budget rules), the Octopus events' way into the shadow host, and the pass hook.</summary>
public sealed class DeliveryShadowPortTests
{
    static readonly RepoId Home = RepoId.Parse("acme/work");
    static readonly string Sha1 = new('a', 40), Sha0 = new('d', 40);

    static InstanceConfig Config(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"dsp-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return InstanceConfig.Load(p, n => n == "FACTORY_ENVIRONMENT" ? "tdd" : null);
    }

    const string Full = """
        {"owner":"acme","repo":"work","octopus":{"project":"acme-ui","healthPath":"/_hc","tddUrl":"https://t.example/","uatUrl":"https://u.example","prodUrl":"https://p.example",
         "environments":{"tdd":"Tdd","uat":"Uat","prod":"Prod"},"health":{"spacingMinutes":3,"settleMinutes":7}},
         "delivery":{"autoPromote":{"enabled":true,"environments":["uat","prod"],"gatewayEnvironment":"tdd","signoffWaitHours":3}}}
        """;

    // ---- settings ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSettings_AreV1sConfiguration_ForTheGatewayEnvironmentItNames()
    {
        var s = DeliveryShadowSettings.From(Config(Full))!;
        Assert.Equal("acme-ui", s.Project);
        Assert.Equal(Promoter.Gateway, s.Promoter);
        Assert.Equal(["uat", "prod"], s.Environments);
        Assert.Equal("Uat", s.EnvironmentNames!["uat"]);
        Assert.Equal("https://u.example", s.EnvironmentUrls!["uat"]);
        Assert.Equal("/_hc", s.HealthPath);
        Assert.Equal(new HealthWindow(3, 7), s.Window);
        Assert.Equal(3, s.SignoffWaitHours);
        // another gateway environment than the one v1 names: v1 does not promote there, so the train only observes
        var elsewhere = Full.Replace("\"gatewayEnvironment\":\"tdd\"", "\"gatewayEnvironment\":\"uat\"");
        Assert.Empty(DeliveryShadowSettings.From(Config(elsewhere))!.Environments!);
    }

    [Fact]
    public void WhereV1DoesNotPromote_TheTrainObserves_AndWithoutAProjectThereIsNoTrain()
    {
        var off = Full.Replace("\"enabled\":true", "\"enabled\":false");
        Assert.Empty(DeliveryShadowSettings.From(Config(off))!.Environments!);
        Assert.Null(DeliveryShadowSettings.From(Config("""{"owner":"acme","repo":"work"}""")));
    }

    [Fact]
    public void OnlyTheHomeRepositorysDeployable_GetsTheSettings()
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null,
            [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)]), new RepositoryEntry(RepoId.Parse("acme/other"), [new DeployableEntry("api", RepoId.Parse("acme/other"), null, null, null, null, false)])], []);
        var f = DeliveryShadowSettings.For(Config(Full), () => snapshot);
        Assert.NotNull(f("ui"));
        Assert.Null(f("api"));
        Assert.Null(f("nothing"));
    }

    [Fact]
    public void TheDependenciesDeliveredRule_IsOnOnlyWhereTheEngineActs_AndTheFileTurnsItOn()
    {
        var on = Full.Replace("\"delivery\":{", "\"delivery\":{\"dependenciesDelivered\":true,");
        var off = Full.Replace("\"delivery\":{", "\"delivery\":{\"dependenciesDelivered\":false,");
        // the shadow run (tdd): whatever the file says, the train holds nothing for a dependency, so that it can be compared with v1's promotions
        Assert.False(DeliveryShadowSettings.From(Config(on))!.DependenciesDelivered);
        Assert.False(DeliveryShadowSettings.From(Config(on), engineActs: false)!.DependenciesDelivered);
        // where the engine acts: on when the file says so, off when it says no or says nothing
        Assert.True(DeliveryShadowSettings.From(Config(on), engineActs: true)!.DependenciesDelivered);
        Assert.False(DeliveryShadowSettings.From(Config(off), engineActs: true)!.DependenciesDelivered);
        Assert.False(DeliveryShadowSettings.From(Config(Full), engineActs: true)!.DependenciesDelivered);
        Assert.False(DeliveryShadowSettings.From(Config(Full.Replace("\"delivery\":{", "\"delivery\":{\"dependenciesDelivered\":\"yes\",")), engineActs: true)!.DependenciesDelivered);
        // and the policies of the home repository's deployable carry it
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        Assert.All(new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(Config(on), () => snapshot, engineActs: true)).All(), p => Assert.True(p.DependenciesDelivered));
        Assert.All(new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(Config(on), () => snapshot)).All(), p => Assert.False(p.DependenciesDelivered));
    }

    [Fact]
    public void TheShippedDefault_TurnsTheRuleOn_ForTheGatewayWhereTheEngineActs()
    {
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        var cfg = InstanceConfig.Load(Path.Combine(root, "config", "instance.json"), _ => null);
        Assert.True(cfg.DependenciesDelivered);
        // (the source defaults name no Octopus project, so there is no train to give the setting to; the settings of a deployed gateway do)
    }

    [Fact]
    public void TheDeployedImage_DoesNotPromoteUi_AndStillRollsBackTdd()
    {
        // owner, 2026-11-09: cmdemo1-system's ui-auto-promote is the only promoter of cmdemo1-ui to uat and prod; the tdd incident rollback stays here
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
        var merged = ApplyOverrides.Merge(File.ReadAllText(Path.Combine(root, "config", "instance.json")), File.ReadAllText(Path.Combine(root, "config", "deploy.overrides.json")));
        var cfg = Config(merged.ToJsonString());
        Assert.False(cfg.AutoPromoteHere);
        var settings = DeliveryShadowSettings.From(cfg, engineActs: true)!;
        Assert.Equal("cmdemo1-ui", settings.Project);
        Assert.Empty(settings.Environments!);
        Assert.Contains("scripts/Update/", settings.MigrationPaths!);
        Assert.Equal(RollbackMode.Automatic, IncidentSettings.From(merged).RollbackOf("tdd"));
        Assert.False(DocsPlanSettingsReader.From(merged, n => n == "FACTORY_ENVIRONMENT" ? "tdd" : null).Policy.HoldsForProd);

        var home = cfg.Home!.Value;
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, home.Owner, home, null, [new RepositoryEntry(home, [new DeployableEntry("ui", home, null, null, null, null, false)])], []);
        var trains = new CatalogDeliveryPolicies(() => snapshot, DeliveryShadowSettings.For(cfg, () => snapshot, engineActs: true)).All();
        var tdd = Assert.Single(trains, p => p.Environment == "tdd");
        Assert.Equal((Promoter.Pipeline, false, "cmdemo1-ui"), (tdd.Promoter, tdd.Acts, tdd.Project));
        Assert.True(RollbackRules.Automatic(tdd));
        Assert.Contains("scripts/Update/", tdd.Migrations);
        Assert.All(trains.Where(p => p.Environment is "uat" or "prod"), p => Assert.False(p.Enabled));
        Assert.All(trains.Where(p => p.Environment is "uat" or "prod"), p => Assert.False(p.Acts));
    }

    // ---- the reads -----------------------------------------------------------------------------------------------------------------------------------------

    sealed class Handler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url)> Seen = [];
        public Func<string, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.NotFound, "{}");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add((request.Method, request.RequestUri!.ToString()));
            var (status, body) = Answer(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    sealed class Facts : IMergeFactsReader
    {
        public int Pulls, Items;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) { Pulls++; return Task.FromResult<MergedPullRequest?>(new MergedPullRequest(repository, number, true, "master", "me/7-x", "x (#9)", "Closes #7", Sha1)); }
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) { Items++; return Task.FromResult<ItemMergeFacts?>(new ItemMergeFacts(item, true, ["Auto Merge"], "", true, 9, 9, "gateway", false)); }
    }

    static (HttpTrainReads Reads, Handler Gh, Handler App, Facts Facts, IDeliveryPolicies Policies) Reads(Func<DateTimeOffset>? clock = null)
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        var policies = new CatalogDeliveryPolicies(() => snapshot, _ => DeliveryShadowSettings.From(Config(Full)));
        var gh = new Handler(); var app = new Handler(); var facts = new Facts();
        var reads = new HttpTrainReads(new HttpClient(gh) { BaseAddress = new Uri("https://api.example/") }, new HttpClient(app), "factory-worker", facts, new ItemGateConfig("Auto Merge", "video"), policies, null, clock);
        return (reads, gh, app, facts, policies);
    }

    static string CompareBody() => new JsonObject { ["status"] = "ahead", ["total_commits"] = 1, ["commits"] = new JsonArray(new JsonObject { ["sha"] = Sha1.ToUpperInvariant(), ["parents"] = new JsonArray(new JsonObject { ["sha"] = Sha0 }), ["commit"] = new JsonObject { ["message"] = "x (#9)\n\nbody" } }) }.ToJsonString();

    [Fact]
    public async Task ACompare_IsOneGet_ParsedLikeV1_AndKeptForTwoMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _, _, _) = Reads(() => now);
        gh.Answer = _ => (HttpStatusCode.OK, CompareBody());
        // refs that are no commits (a branch name) can move: kept the two minutes (a compare of two fixed commits is kept for good, DeliveryShadowReadsTests)
        const string Moving = "master";
        var r = await reads.Compare(Home, Sha0, Moving, default);
        Assert.Equal("ahead", r!.Status);
        Assert.Equal(Sha1, r.Commits[0].Sha);   // lower case
        Assert.Equal([Sha0], r.Commits[0].ParentShas);
        Assert.StartsWith("x (#9)", r.Commits[0].Message);
        await reads.Compare(Home, Sha0, Moving, default);
        Assert.Single(gh.Seen);
        now += TimeSpan.FromMinutes(3);
        await reads.Compare(Home, Sha0, Moving, default);
        Assert.Equal(2, gh.Seen.Count);
        Assert.All(gh.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Fact]
    public async Task ACompareGitHubCannotDo_IsNull_AndAFailureThrows()
    {
        var (reads, gh, _, _, _) = Reads();
        gh.Answer = _ => (HttpStatusCode.UnprocessableEntity, "{}");
        Assert.Null(await reads.Compare(Home, Sha0, Sha1, default));
        gh.Answer = _ => (HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => reads.Compare(Home, Sha1, Sha0, default));
    }

    static string StateComment(string sha, string awaiting = "") =>
        Fworker.Integrations.Commands.ItemStateComment.Body("factory-worker", 7, new JsonObject { ["merge"] = new JsonObject { ["sha"] = sha, ["awaiting"] = awaiting, ["done_at"] = "2026-10-28T09:00:00Z" } });

    [Fact]
    public async Task ACommitOfAnItem_IsFound_ByItsPullRequest_AndASettledAnswerIsKept()
    {
        var (reads, gh, _, facts, _) = Reads();
        gh.Answer = p => p.Contains("/issues/7/comments") ? (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = StateComment(Sha1) }).ToJsonString()) : (HttpStatusCode.NotFound, "{}");
        var commit = new RangeCommit(Sha1, [Sha0], "Thing (#9)\n\nmore");
        Assert.Equal(new CommitItemLookup(7, ""), await reads.LookupCommit(Home, commit, default));
        Assert.Equal(new CommitItemLookup(7, ""), await reads.LookupCommit(Home, commit, default));
        Assert.Equal(1, facts.Pulls);   // Find-CommitItem's cache: the second ask reads nothing
        Assert.All(gh.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Fact]
    public async Task ACommitThatIsNoItemsMerge_IsRemembered_ForTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _, facts, _) = Reads(() => now);
        gh.Answer = _ => (HttpStatusCode.OK, "[]");
        var commit = new RangeCommit(Sha1, [Sha0], "Thing (#9)");
        Assert.Equal(new CommitItemLookup(0, ""), await reads.LookupCommit(Home, commit, default));
        Assert.Equal(new CommitItemLookup(0, ""), await reads.LookupCommit(Home, commit, default));
        Assert.Equal(1, facts.Pulls);
        now += TimeSpan.FromMinutes(11);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(2, facts.Pulls);
        // a commit without a pull request number in its message needs no read at all
        Assert.Equal(new CommitItemLookup(0, ""), await reads.LookupCommit(Home, new RangeCommit(new string('b', 40), [Sha0], "direct push"), default));
        Assert.Equal(2, facts.Pulls);
    }

    [Fact]
    public async Task AnItemNotYetSettled_SaysSo_AndIsKeptForAMinute()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _, facts, _) = Reads(() => now);
        gh.Answer = p => p.Contains("/issues/7/comments") ? (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = StateComment(Sha1, "ci") }).ToJsonString()) : (HttpStatusCode.NotFound, "{}");
        var commit = new RangeCommit(Sha1, [Sha0], "Thing (#9)");
        var r = await reads.LookupCommit(Home, commit, default);
        Assert.Equal(7, r.Item);
        Assert.Equal($"#7's merge ({Sha1[..7]}) is not settled yet (awaiting ci)", r.Gap);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(1, facts.Pulls);
        now += TimeSpan.FromSeconds(61);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(1, facts.Pulls);   // (the merged pull request is kept two minutes: a retry after a refused read does not spend it again)
        Assert.Equal(2, gh.Seen.Count);   // the item's state is read again after the minute
        now += TimeSpan.FromMinutes(3);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(2, facts.Pulls);
    }

    [Fact]
    public async Task TheGatesOfAnItem_AreRead_Once_PerMinute_AndAFreshReadSpendsARead()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, _, _, facts, _) = Reads(() => now);
        var g = await reads.ReadGates(new ItemId(Home, 7), default);
        Assert.True(g!.AutoMerge); Assert.Empty(g.Gaps);
        await reads.ReadGates(new ItemId(Home, 7), default);
        Assert.Equal(1, facts.Items);
        var evaluationBegan = now + TimeSpan.FromSeconds(1);
        now += TimeSpan.FromSeconds(2);
        await reads.ReadGates(new ItemId(Home, 7), default, freshSince: evaluationBegan);   // (the decision at a Sign-off is made on what the item carries now: a read before the evaluation began is not good enough)
        Assert.Equal(2, facts.Items);
        await reads.ReadGates(new ItemId(Home, 7), default, freshSince: evaluationBegan);   // (a read made after it began, by the evaluation's first judgment or an earlier look of the chain, counts)
        Assert.Equal(2, facts.Items);
        now += TimeSpan.FromMinutes(4);
        await reads.ReadGates(new ItemId(Home, 7), default);
        Assert.Equal(2, facts.Items);   // (kept for five minutes, longer than the minute of the cap)
        now += TimeSpan.FromMinutes(2);
        await reads.ReadGates(new ItemId(Home, 7), default);
        Assert.Equal(3, facts.Items);
    }

    [Fact]
    public async Task AProbe_ReadsBuildAndHealth_WithGetsOnly_AndAnAppThatDoesNotAnswerIsUnreachable()
    {
        var (reads, _, app, _, _) = Reads();
        app.Answer = p => p.EndsWith("/_build") ? (HttpStatusCode.OK, """{"version":"1.0.2"}""") : (HttpStatusCode.OK, """{"overallStatus":"Healthy"}""");
        var p = await reads.Probe("ui", "tdd", default);
        Assert.Equal(("https://t.example", "1.0.2", "Healthy"), (p.Url, p.RunningVersion, p.Status));
        Assert.Equal(["https://t.example/_build", "https://t.example/_hc"], app.Seen.Select(s => s.Url));
        Assert.All(app.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
        // a page that is no JSON is an answer, as in v1 (Read-EnvHealth: "status N without overallStatus"); it ends the tries
        app.Answer = _ => (HttpStatusCode.ServiceUnavailable, "not json");
        var odd = await reads.Probe("ui", "tdd", default);
        Assert.Equal("status 503 without overallStatus", odd.Status);
        Assert.Null(odd.RunningVersion);
        Assert.Null((await reads.Probe("ui", "nowhere", default)).Url);
    }

    sealed class Flaky(int failures, string body) : HttpMessageHandler
    {
        public int HealthCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/_build")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"version":"1.0.2"}""") });
            if (++HealthCalls <= failures) throw new HttpRequestException("connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    static HttpTrainReads ReadsWith(HttpMessageHandler app, TimeSpan budget = default)
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        var policies = new CatalogDeliveryPolicies(() => snapshot, _ => DeliveryShadowSettings.From(Config(Full)));
        return new HttpTrainReads(new HttpClient(new Handler()) { BaseAddress = new Uri("https://api.example/") }, new HttpClient(app) { Timeout = Timeout.InfiniteTimeSpan }, "factory-worker", new Facts(), new ItemGateConfig("Auto Merge", "video"), policies, null, null, null, budget);
    }

    [Fact]
    public async Task AProbeIsOneTryPerLook_ItNeverSleeps_AndTheTrainCountsTheLooksThatGotNoAnswer()
    {
        // v1 tries the health read 3 times, 20 s apart. The handler runs in the one queue of every train and must not sleep: one try, "unreachable", and the train (TrainEvidence.Unreachable) counts and looks again.
        var app = new Flaky(99, "");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var p = await ReadsWith(app).Probe("ui", "tdd", default);
        Assert.Equal("unreachable", p.Status);
        Assert.Equal(1, app.HealthCalls);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        var ok = new Flaky(0, """{"overallStatus":"Healthy"}""");
        Assert.Equal("Healthy", (await ReadsWith(ok).Probe("ui", "tdd", default)).Status);
        Assert.Equal(1, ok.HealthCalls);
    }

    sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task AProbe_IsBoundedInAll_AnAppThatNeverAnswersIsUnreachableWithinTheBudget_AndACallersCancelStillCancels()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var p = await ReadsWith(new Silent(), TimeSpan.FromMilliseconds(300)).Probe("ui", "tdd", default);
        Assert.Equal("unreachable", p.Status);
        Assert.Null(p.RunningVersion);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");   // (the build read and the health read share the 300 ms)
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadsWith(new Silent(), TimeSpan.FromMinutes(1)).Probe("ui", "tdd", cts.Token));
    }

    [Fact]
    public async Task WithTheIncidentSagaAttached_TheActingTrainsPortIsKept_SoTheSignOffsAndReleasesReachTheTrain()
    {
        var seen = new List<string>();
        var train = new ShadowOctopusPort(ev => { seen.Add("train:" + ev.GetType().Name); return Task.FromResult(true); }, true);
        var port = ShadowOctopusPort.WithIncidents(train, ev => { seen.Add("host:" + ev.GetType().Name); return Task.FromResult(true); });
        Assert.Same(train, port);
        await port.Publish(new SignOffRequested("I-1", "D-1", "T-1", "R-1", "1.0.2", "Uat", Sha1));
        await port.Publish(new OctopusReleaseCreated("R-1", "1.0.2", Sha1));
        Assert.Equal(["train:SignOffRequested", "train:OctopusReleaseCreated"], seen);
        // no train (or one that could not be built): the deployments reach the incident saga and the other events are accepted and dropped
        seen.Clear();
        var alone = ShadowOctopusPort.WithIncidents(new ShadowOctopusPort(_ => Task.FromResult(false), false), ev => { seen.Add("host:" + ev.GetType().Name); return Task.FromResult(true); });
        Assert.True(await alone.Publish(new SignOffRequested("I-1", "D-1", "T-1", "R-1", "1.0.2", "Uat", Sha1)));
        Assert.True(await alone.Publish(new OctopusDeploymentChanged("D-1", "T-1", "R-1", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha1)));
        Assert.Equal(["host:OctopusDeploymentChanged"], seen);
        Assert.Same(alone.GetType(), ShadowOctopusPort.WithIncidents(null, _ => Task.FromResult(true)).GetType());
    }

    // ---- the ingress, the pass hook -------------------------------------------------------------------------------------------------------------------------

    sealed class Rec : IEventIngress { public readonly List<IngressEvent> Got = []; public void Ingest(IngressEvent ev) => Got.Add(ev); }

    [Fact]
    public async Task TheOctopusEvents_GoToV1sIngressFirst_AndThenToTheShadowHost_ButASweepRequestDoesNot()
    {
        var inner = new Rec(); var published = new List<object>();
        var port = new ShadowOctopusPort(ev => { lock (published) published.Add(ev); return Task.FromResult(true); }, true);
        var ingress = new OctopusShadowIngress(inner, port);
        var events = new IngressEvent[] { new OctopusReleaseCreated("R", "1.0.2", Sha1), new ReleaseCommitResolved("R", "1.0.2", Sha1), new OctopusDeploymentChanged("D", "T", "R", "1.0.2", "tdd", OctopusDeploymentPhase.Succeeded, Sha1), new SignOffRequested("I", "D", "T", "R", "1.0.2", "uat", Sha1) };
        foreach (var e in events) ingress.Ingest(e);
        ingress.Ingest(new SweepRequested(3)); ingress.Ingest(new WebhookEventIngested("{}"));
        Assert.Equal(6, inner.Got.Count);   // v1 saw every one, first
        for (var i = 0; i < 200 && published.Count < 4; i++) await Task.Delay(10);
        lock (published) Assert.Equal(events.Cast<object>().ToHashSet(), published.ToHashSet());
    }

    [Fact]
    public async Task APortThatIsOff_OrThatFails_NeverReachesTheWatcher()
    {
        var inner = new Rec();
        using var failing = new OctopusShadowIngress(inner, new ShadowOctopusPort(_ => throw new InvalidOperationException("boom"), true));
        failing.Ingest(new OctopusReleaseCreated("R", "1", null));
        using var off = new OctopusShadowIngress(inner, new ShadowOctopusPort(_ => Task.FromResult(true), false));
        off.Ingest(new OctopusReleaseCreated("R", "1", null));
        await Task.Delay(100);
        Assert.Equal(2, inner.Got.Count);
        Assert.False(await new ShadowOctopusPort(_ => Task.FromResult(true), false).Publish(new object()));
    }

    [Fact]
    public async Task APublishThatFails_IsTriedAgain_AndTheEventsBehindItWaitForIt()
    {
        var attempts = 0; var got = new List<string>();
        var port = new ShadowOctopusPort(ev =>
        {
            if (ev is OctopusReleaseCreated && Interlocked.Increment(ref attempts) < 3) throw new HttpRequestException("the database does not answer");
            lock (got) got.Add(ev.GetType().Name);
            return Task.FromResult(true);
        }, true);
        using var ingress = new OctopusShadowIngress(new Rec(), port, retryFirst: TimeSpan.FromMilliseconds(5), retryMax: TimeSpan.FromMilliseconds(20));
        ingress.Ingest(new OctopusReleaseCreated("R", "1", null));
        ingress.Ingest(new OctopusDeploymentChanged("D", "T", "R", "1", "tdd", OctopusDeploymentPhase.Started, Sha1));
        for (var i = 0; i < 300 && got.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(3, attempts);   // two failures, then it went through
        Assert.Equal(["OctopusReleaseCreated", "OctopusDeploymentChanged"], got);   // in order
    }

    [Fact]
    public async Task AHostThatIsNotUpYet_IsWaitedFor_NotDroppedOn()
    {
        var up = false; var got = new List<object>();
        var port = new ShadowOctopusPort(ev => { if (!up) return Task.FromResult(false); lock (got) got.Add(ev); return Task.FromResult(true); }, true);
        using var ingress = new OctopusShadowIngress(new Rec(), port, retryFirst: TimeSpan.FromMilliseconds(5), retryMax: TimeSpan.FromMilliseconds(20));
        ingress.Ingest(new OctopusReplayBegan());
        await Task.Delay(100);
        Assert.Empty(got);
        up = true;
        for (var i = 0; i < 300 && got.Count < 1; i++) await Task.Delay(10);
        Assert.IsType<OctopusReplayBegan>(Assert.Single(got));
    }

    [Fact]
    public async Task AFullQueue_IsNoSilentDrop_TheWatcherIsTold_AndOffersTheEventAgain()
    {
        var gate = new TaskCompletionSource(); var got = new List<string>();
        var port = new ShadowOctopusPort(async ev => { await gate.Task; lock (got) got.Add(((OctopusDeploymentChanged)ev).DeploymentId); return true; }, true);
        using var ingress = new OctopusShadowIngress(new Rec(), port, capacity: 2);
        OctopusDeploymentChanged D(string id) => new(id, "T", "R", "1", "tdd", OctopusDeploymentPhase.Started, Sha1);
        ingress.Ingest(D("D1"));
        await Task.Delay(50);   // D1 is in the consumer's hands, the queue holds two more
        ingress.Ingest(D("D2")); ingress.Ingest(D("D3"));
        var full = Assert.Throws<InvalidOperationException>(() => ingress.Ingest(D("D4")));
        Assert.Contains("offered again", full.Message);
        gate.SetResult();
        for (var i = 0; i < 300 && got.Count < 3; i++) await Task.Delay(10);
        ingress.Ingest(D("D4"));   // what the watcher does at its next poll
        for (var i = 0; i < 300 && got.Count < 4; i++) await Task.Delay(10);
        Assert.Equal(["D1", "D2", "D3", "D4"], got);
    }

    [Fact]
    public async Task TheReplayMarkers_GoToTheHost_AndV1TakesThemWithoutComplaint()
    {
        var published = new List<object>();
        using var ingress = new OctopusShadowIngress(new V1EventIngress(new EventHub()), new ShadowOctopusPort(ev => { lock (published) published.Add(ev); return Task.FromResult(true); }, true));
        ingress.Ingest(new OctopusReplayBegan()); ingress.Ingest(new OctopusReplayComplete());
        for (var i = 0; i < 200 && published.Count < 2; i++) await Task.Delay(10);
        Assert.IsType<OctopusReplayBegan>(published[0]); Assert.IsType<OctopusReplayComplete>(published[1]);
    }

    [Fact]
    public void V1sIngress_TakesTheOctopusEvents_AndDoesNothingWithThem()
    {
        var hub = new EventHub();
        var v1 = new V1EventIngress(hub);
        v1.Ingest(new OctopusReleaseCreated("R", "1.0.2", null));
        v1.Ingest(new OctopusDeploymentChanged("D", "T", "R", "1.0.2", "tdd", OctopusDeploymentPhase.Started, null));
        Assert.Empty(hub.TakeSweepItems());
    }

    [Fact]
    public async Task TheReads_StopAtTheMinuteCap_AndWhileTheBudgetIsLow()
    {
        var now = DateTimeOffset.UtcNow; var low = false;
        var (_, gh0, _, facts, policies) = Reads(() => now);
        var reads = new HttpTrainReads(new HttpClient(gh0) { BaseAddress = new Uri("https://api.example/") }, new HttpClient(new Handler()), "factory-worker", facts, new ItemGateConfig("Auto Merge", "video"), policies, null, () => now, () => low);
        gh0.Answer = _ => (HttpStatusCode.OK, CompareBody());
        for (var i = 0; i < HttpTrainReads.MaxCallsPerMinute; i++) await reads.Compare(Home, Sha0, new string((char)('0' + i % 10), 39) + i.ToString("x")[^1], default);
        await Assert.ThrowsAsync<HttpRequestException>(() => reads.Compare(Home, Sha0, new string('e', 40), default));
        now += TimeSpan.FromMinutes(2);
        await reads.Compare(Home, Sha0, new string('e', 40), default);
        low = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => reads.Compare(Home, Sha0, new string('f', 40), default));
    }

    [Fact]
    public async Task TheWatchersEvents_ReachTheHostInTheOrderTheyWerePublished()
    {
        var got = new List<string>();
        var ingress = new OctopusShadowIngress(new Rec(), new ShadowOctopusPort(async ev => { await Task.Delay(ev is OctopusReleaseCreated ? 30 : 1); lock (got) got.Add(ev.GetType().Name); return true; }, true));
        ingress.Ingest(new OctopusReleaseCreated("R", "1", null));
        ingress.Ingest(new ReleaseCommitResolved("R", "1", Sha1));
        ingress.Ingest(new OctopusDeploymentChanged("D", "T", "R", "1", "tdd", OctopusDeploymentPhase.Started, Sha1));
        for (var i = 0; i < 300 && got.Count < 3; i++) await Task.Delay(10);
        Assert.Equal(["OctopusReleaseCreated", "ReleaseCommitResolved", "OctopusDeploymentChanged"], got);
    }

    // ---- the guard -----------------------------------------------------------------------------------------------------------------------------------------

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found");
    }

    [Fact]
    public void TheTrainsReadParts_HaveNoWayToWrite()
    {
        // The train's settings, reads and ingress may read GitHub, Octopus and the apps; they hold no command port, no Octopus or GitHub client with a write and send no HTTP verb but GET (the executor writes, DeliveryExecutorPort.cs).
        var files = new[] { "gateway/Ports/DeliveryShadowPort.cs", "gateway/Ports/DeliveryShadowReads.cs" };
        var forbidden = new[] { "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync", "HttpMethod.Post", "HttpMethod.Put", "HttpMethod.Delete", "HttpMethod.Patch", "SendAsJsonAsync", "PostAsJsonAsync",
            "ICommandPort", "SendAsync(", "Runner", "RunAsync(", "HttpOctopusClient", "OctopusRuntime" };
        foreach (var f in files)
        {
            var code = string.Join('\n', File.ReadAllLines(Path.Combine(Root(), f)).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
            foreach (var word in forbidden) Assert.False(code.Contains(word, StringComparison.Ordinal), $"{f} uses {word}: the train's reads must not be able to write");
        }
    }

    [Fact]
    public void TheOnlyHandlersOfTheTrainsCommands_AreTheExecutors()
    {
        // the workflows hold no handler of a PromoteRelease or an AnswerSignOff, and the gateway none but the executor's (W9.4: DeliveryExecutorHandlers), which only DeliveryActingWiring attaches, where the engine acts
        static bool Takes(System.Reflection.MethodInfo m) => m.Name == "Handle" && m.GetParameters().Any(p => p.ParameterType.FullName is "Fworker.Contracts.Commands.PromoteRelease" or "Fworker.Contracts.Commands.AnswerSignOff");
        Assert.DoesNotContain(typeof(DeliveryTrainHandlers).Assembly.GetTypes().SelectMany(t => t.GetMethods()), Takes);
        Assert.Equal(["DeliveryExecutorHandlers"], typeof(DeliveryShadowWiring).Assembly.GetTypes().Where(t => t.GetMethods().Any(Takes)).Select(t => t.Name).Distinct().ToList());
        var shadowWiring = File.ReadAllText(Path.Combine(Root(), "gateway", "Ports", "DeliveryShadowPort.cs"));
        Assert.DoesNotContain("DeliveryExecutor", shadowWiring);
        Assert.DoesNotContain("PromoteReleaseHandler", shadowWiring);
    }
}
