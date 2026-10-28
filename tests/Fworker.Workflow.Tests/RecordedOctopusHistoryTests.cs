using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W5 (issue 500): tdd's real Octopus history, recorded read only on 2026-10-28 (tests/fixtures/octopus-tdd-2026-10-28, record.sh), replayed through the C# watcher and the delivery train, and the train's
/// decisions checked against two things: what Octopus says (the fixture's tasks and deployments) and what v1's promotion pass (Verbs.Promote.ps1, Invoke-AutoPromoteInner) decides on the same data. The shadow
/// run's gate is event based (3 live app releases compared with no disagreements); this test carries most of that check in CI, on real data, before a gateway runs it.
///
/// The replay is the one of the gateway's shadow run: the watcher with its shadow settings (30 releases and 30 deployments per read, 30 calls per poll, a 30 day lookback, here 31 days; see OctopusWatchService) is polled
/// until its replay is complete, its events go through the router into the trains of one deployable (tdd, uat, prod), and the train judges its state once, at the replay's end. GitHub and the apps are fakes
/// that cannot answer, so a release that succeeded in the previous environment waits on a gate (it cannot be compared); everything that is decided by Octopus's own facts is exact.
/// </summary>
public class RecordedOctopusHistoryTests
{
    // ---- the fixture and the fake Octopus ---------------------------------------------------------------------------------------------------------------------

    static readonly Lazy<JsonNode> Fixture = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "octopus-tdd-2026-10-28", "octopus-history.json")))!);

    static JsonNode Project(string slug) => Fixture.Value["projects"]!.AsArray().First(p => p!["Slug"]!.ToString() == slug)!;
    static IEnumerable<string> Slugs() => Fixture.Value["projects"]!.AsArray().Select(p => p!["Slug"]!.ToString());
    public static IEnumerable<object[]> AllProjects() => Slugs().Select(s => new object[] { s });

    static DateTimeOffset Time(JsonNode? n) => DateTimeOffset.Parse(n!.ToString(), CultureInfo.InvariantCulture);

    /// <summary>Octopus as the fixture recorded it: the newest N releases and deployments of the project, a task by id, a release by id, the pending interruptions of a task.</summary>
    sealed class RecordedOctopus(JsonNode root, JsonNode project) : IOctopusReader
    {
        readonly string space = root["space"]!.ToString();
        public int Gets;
        /// <summary>A task's state at the moment of a read, other than the recorded one (null: as recorded): lets a test replay a deployment that was still running when it was first seen.</summary>
        public Func<string, string?>? StateOf;

        public Task<JsonNode?> Get(string path, CancellationToken ct = default)
        {
            Gets++;
            var prefix = $"api/{space}/";
            if (!path.StartsWith(prefix)) throw new InvalidOperationException($"unexpected path {path}");
            var p = path[prefix.Length..];
            var take = Regex.Match(p, @"take=(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 30;
            var pid = project["Id"]!.ToString();
            JsonNode? answer = null;
            if (p == $"projects/{project["Slug"]}") answer = new JsonObject { ["Id"] = pid };
            else if (p.StartsWith("environments?")) answer = new JsonObject { ["Items"] = root["environments"]!.DeepClone() };
            else if (p.StartsWith($"projects/{pid}/releases?"))
                answer = new JsonObject { ["Items"] = new JsonArray(project["Releases"]!.AsArray().OrderByDescending(r => Time(r!["Assembled"])).Take(take).Select(r => r!.DeepClone()).ToArray()) };
            else if (p.StartsWith($"deployments?projects={pid}&"))
                answer = new JsonObject { ["Items"] = new JsonArray(project["Deployments"]!.AsArray().OrderByDescending(d => Time(d!["Created"])).ThenByDescending(d => d!["Id"]!.ToString(), StringComparer.Ordinal).Take(take).Select(d => d!.DeepClone()).ToArray()) };
            else if (p.StartsWith("tasks/"))
            {
                answer = project["Tasks"]![p["tasks/".Length..]]?.DeepClone();
                if (answer is not null && StateOf?.Invoke(p["tasks/".Length..]) is { } over) answer["State"] = over;
            }
            else if (p.StartsWith("releases/")) answer = project["Releases"]!.AsArray().Concat(project["OlderReleases"]!.AsArray()).FirstOrDefault(r => r!["Id"]!.ToString() == p["releases/".Length..])?.DeepClone();
            else if (p.StartsWith("interruptions?regarding="))
                answer = new JsonObject { ["Items"] = project["Interruptions"]![Regex.Match(p, @"regarding=([^&]+)").Groups[1].Value]?.DeepClone() ?? new JsonArray() };
            else throw new InvalidOperationException($"unexpected path {path}");
            return Task.FromResult(answer);
        }
    }

    // ---- the replay -------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What the train published for one project's replay, and what it ended with.</summary>
    sealed record Replay(string Slug, string Deployable, IReadOnlyList<TrainDecided> Decisions, IReadOnlyDictionary<string, TrainState> States, int Polls, int Inputs, IReadOnlyList<object> Messages)
    {
        /// <summary>The decision the train holds for a release in an environment at the end (a "delivered" or "idle" retracts what was said before).</summary>
        public TrainDecided? Final(string env, string version) => FinalDecisions(env).GetValueOrDefault(version);

        public IReadOnlyDictionary<string, TrainDecided> FinalDecisions(string env)
        {
            var held = new Dictionary<string, TrainDecided>();
            foreach (var d in Decisions.Where(d => d.Key == TrainKey.Of(Deployable, env)))
            {
                if (d.Action is "delivered" or "idle") { held.Remove(d.Release); foreach (var s in d.Superseded ?? []) held.Remove(s); }
                else held[d.Release] = d;
            }
            return held;
        }
    }

    /// <summary>The shadow settings of the watcher in the gateway (OctopusWatchService.From with shadow true): 30 releases, 30 deployments, 30 calls per poll. The gateway's lookback is 30 days; here 31, because the fixture's 30 day window was cut from the start of the recording and recordedAt is its end, so no release of the fixture is "old".</summary>
    static OctopusWatchSettings ShadowSettings(string space, string project) => new(space, project, ReleaseWindow: 30, DeploymentWindow: 30, MaxCallsPerPoll: 30, Lookback: TimeSpan.FromDays(31));

    /// <summary>No window and no budget: the whole 30 days at once, for the checks that need every release.</summary>
    static OctopusWatchSettings WholeSettings(string space, string project) => new(space, project, ReleaseWindow: 100000, DeploymentWindow: 100000, MaxCallsPerPoll: 100000, Lookback: TimeSpan.FromDays(31));

    static readonly Dictionary<(string, bool), Replay> Replays = [];
    static readonly SemaphoreSlim ReplayLock = new(1, 1);

    static async Task<Replay> Run(string slug, bool shadowSettings)
    {
        await ReplayLock.WaitAsync();
        try
        {
            if (Replays.TryGetValue((slug, shadowSettings), out var cached)) return cached;
            return Replays[(slug, shadowSettings)] = await Execute(slug, shadowSettings);
        }
        finally { ReplayLock.Release(); }
    }

    /// <param name="stateOf">A task's state by poll number and task id, other than the recorded one (null: as recorded).</param>
    /// <param name="minPolls">Polls to make at least: the live polls that follow the replay's end.</param>
    static async Task<Replay> Execute(string slug, bool shadowSettings, Func<int, string, string?>? stateOf = null, int minPolls = 0)
    {
        var root = Fixture.Value; var project = Project(slug);
        var space = root["space"]!.ToString();
        var recordedAt = Time(root["recordedAt"]);
        var deployable = slug.StartsWith("cmdemo1-") ? slug["cmdemo1-".Length..] : slug;
        var repo = new RepoId("clearmeasure-aisf-sample-apps", slug);

        var now = recordedAt;   // the gateway's clock: every input takes a few milliseconds, as the real ones do, and the order of arrival is the order of the events
        var settings = new DeliveryPolicySettings(slug, Promoter.Gateway, ["uat", "prod"],
            new Dictionary<string, string> { ["tdd"] = "tdd", ["uat"] = "uat", ["prod"] = "prod" },
            new Dictionary<string, string> { ["tdd"] = "https://tdd.example", ["uat"] = "https://uat.example", ["prod"] = "https://prod.example" },
            null, new HealthWindow(5, 10), 2, false);
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, recordedAt, "clearmeasure-aisf-sample-apps", repo, null, [new RepositoryEntry(repo, [new DeployableEntry(deployable, repo, null, null, null, null, false)])], []);
        var policies = new DeliveryPolicies(new CatalogDeliveryPolicies(() => snapshot, n => n == deployable ? settings : null), 30, 120);
        var reads = new FakeTrainReads { Clock = () => now };
        var store = new InMemoryTrainStore();
        var svc = new DeliveryTrainService(store, policies, reads, new FakeDelivered(), () => now);
        var router = new DeliveryTrainRouter(policies, () => now);

        var published = new List<IngressEvent>();
        var pollNo = 0;
        var octopus = new RecordedOctopus(root, project) { StateOf = stateOf is null ? null : id => stateOf(pollNo, id) };
        var watcher = new OctopusWatcher(octopus, published.Add, shadowSettings ? ShadowSettings(space, slug) : WholeSettings(space, slug), () => recordedAt);

        var decisions = new List<TrainDecided>(); var messages = new List<object>();
        var polls = 0; var inputs = 0; var complete = false;
        while ((!complete || polls < minPolls) && polls < 100)
        {
            polls++; pollNo = polls;
            await watcher.Poll();
            var batch = published.ToList(); published.Clear();
            foreach (var ev in batch)
            {
                var routed = ev switch
                {
                    OctopusReleaseCreated e => router.Route(e),
                    ReleaseCommitResolved e => router.Route(e),
                    OctopusDeploymentChanged e => router.Route(e),
                    SignOffRequested e => router.Route(e),
                    OctopusReplayBegan e => router.Route(e),
                    OctopusReplayComplete e => router.Route(e),
                    _ => [],
                };
                if (ev is OctopusReplayComplete) complete = true;
                foreach (var input in routed)
                {
                    now += TimeSpan.FromMilliseconds(10); inputs++;
                    var result = await svc.Handle(input);
                    messages.AddRange(result.Messages);
                    decisions.AddRange(result.Messages.OfType<TrainDecided>());
                }
            }
        }
        Assert.True(complete, $"{slug}: the watcher's replay did not complete in {polls} polls");
        return new Replay(slug, deployable, decisions, store.States, polls, inputs, messages);
    }

    // ---- what Octopus says: the fixture as facts --------------------------------------------------------------------------------------------------------------

    sealed record Dep(string Id, string Version, string Env, string State, DateTimeOffset Created)
    {
        public bool Completed => State is "Success" or "Failed" or "TimedOut" or "Canceled";
        public bool Success => State == "Success";
        /// <summary>The phase text the train says (the watcher's mapping of a task state).</summary>
        public string Phase => OctopusWatcher.PhaseOf(State)?.ToString() ?? State;
    }

    /// <summary>The fixture as v1 reads Octopus: every deployment of a release, the newest per environment first (Get-ReleaseDeployments).</summary>
    sealed class Truth
    {
        public readonly Dictionary<string, (string Id, DateTimeOffset Assembled)> Releases = [];   // by version
        public readonly Dictionary<string, Dep> ById = [];
        readonly Dictionary<string, Dictionary<string, Dep>> newest = [];                            // version, environment name

        public Truth(JsonNode root, JsonNode project)
        {
            var envs = root["environments"]!.AsArray().ToDictionary(e => e!["Id"]!.ToString(), e => e!["Name"]!.ToString().ToLowerInvariant());
            var versions = new Dictionary<string, string>();   // release id to version
            foreach (var r in project["Releases"]!.AsArray().Concat(project["OlderReleases"]!.AsArray()))
            { versions[r!["Id"]!.ToString()] = r["Version"]!.ToString(); Releases[r["Version"]!.ToString()] = (r["Id"]!.ToString(), Time(r["Assembled"])); }
            foreach (var d in project["Deployments"]!.AsArray().OrderBy(d => Time(d!["Created"])).ThenBy(d => d!["Id"]!.ToString(), StringComparer.Ordinal))
            {
                var version = versions[d!["ReleaseId"]!.ToString()]; var env = envs[d["EnvironmentId"]!.ToString()];
                var dep = new Dep(d["Id"]!.ToString(), version, env, project["Tasks"]![d["TaskId"]!.ToString()]!["State"]!.ToString(), Time(d["Created"]));
                ById[dep.Id] = dep;
                if (!newest.TryGetValue(version, out var byEnv)) newest[version] = byEnv = [];
                byEnv[env] = dep;   // (in order of creation: the newest wins)
            }
        }

        public Dep? Of(string version, string env) => newest.TryGetValue(version, out var m) && m.TryGetValue(env, out var d) ? d : null;
        public bool HasAny(string version) => newest.ContainsKey(version);
    }

    // ---- v1's decision, from the rules of Invoke-AutoPromoteInner ----------------------------------------------------------------------------------------------

    /// <summary>
    /// What v1 decides for a release, restricted to what Octopus alone settles. v1 picks the target as the first of uat and prod without a successful deployment of the release, then: a deployment of
    /// the release to the target that ended is a wait for "failed", one that runs is followed; no deployment to the previous environment is a wait for "none", one that is not successful a wait for
    /// "failed" (ended) or "started" (still running). Null: the previous environment succeeded, and the decision is made by gates GitHub and the apps answer (the fake cannot).
    /// </summary>
    static (string Target, ShadowDecision? Decision)? V1(string deployable, Truth t, string version)
    {
        string[] chain = ["tdd", "uat", "prod"];
        string? target = null;
        foreach (var e in new[] { "uat", "prod" }) if (t.Of(version, e) is not { Success: true }) { target = e; break; }
        if (target is null) return null;   // promoted all the way: v1 does not judge it
        var prev = chain[Array.IndexOf(chain, target) - 1];
        var have = t.Of(version, target); var pd = t.Of(version, prev);
        ShadowDecision D(string action, string native, string reason) => new(ShadowSide.V1, deployable, target, version, action, native, [reason], [], DateTimeOffset.MinValue);
        if (have is { Completed: true }) return (target, D(ShadowActions.Wait, "promotion-waiting", $"its deployment to {target} ended {have.State} ({have.Id}); the operator deploys it again, or the item goes back to Development"));
        if (have is { Completed: false }) return (target, new ShadowDecision(ShadowSide.V1, deployable, target, version, ShadowActions.Follow, "promotion-following", [], [], DateTimeOffset.MinValue));
        if (pd is null) return (target, D(ShadowActions.Wait, "promotion-waiting", $"it has no deployment to {prev}"));
        if (!pd.Success) return (target, D(ShadowActions.Wait, "promotion-waiting", $"its deployment to {prev} is {(pd.Completed ? pd.State : "still " + pd.State)} ({pd.Id})"));
        return (target, null);
    }

    static ShadowDecision Train(TrainDecided d) => new(ShadowSide.V2, TrainKey.Split(d.Key).Deployable, TrainKey.Split(d.Key).Environment, d.Release, ShadowActions.OfTrain(d.Action) ?? d.Action, d.Action, d.Reasons, d.Items, d.At, d.Replay);

    // ---- the checks -------------------------------------------------------------------------------------------------------------------------------------------

    static readonly Regex DeploymentReason = new(@"^its deployment to (?<env>\w+) (?:is (?<still>still )?(?<phase>\w+)|ended (?<ended>\w+)) \((?<id>Deployments-\d+)\)", RegexOptions.Compiled);
    static readonly Regex NoDeploymentReason = new(@"^it has no deployment to (?<env>\w+)$", RegexOptions.Compiled);

    /// <summary>The reasons of one decision against Octopus: a deployment a reason names is the fixture's, with the state and the environment the reason says; "no deployment" is said only where there is none.</summary>
    static IEnumerable<string> Disagreements(Truth t, TrainDecided d)
    {
        foreach (var reason in d.Reasons)
        {
            if (DeploymentReason.Match(reason) is { Success: true } m)
            {
                if (!t.ById.TryGetValue(m.Groups["id"].Value, out var dep)) { yield return $"{d.Key} {d.Release}: {m.Groups["id"].Value} is no deployment of the fixture ({reason})"; continue; }
                var phase = m.Groups["ended"].Success ? m.Groups["ended"].Value : m.Groups["phase"].Value;
                if (dep.Version != d.Release) yield return $"{d.Key} {d.Release}: {dep.Id} is a deployment of {dep.Version} ({reason})";
                if (!string.Equals(dep.Env, m.Groups["env"].Value, StringComparison.OrdinalIgnoreCase)) yield return $"{d.Key} {d.Release}: {dep.Id} went to {dep.Env} ({reason})";
                if (phase != dep.Phase) yield return $"{d.Key} {d.Release}: {dep.Id} is {dep.Phase} ({dep.State}) in Octopus, the decision says {phase} ({reason})";
                if (m.Groups["still"].Success == dep.Completed) yield return $"{d.Key} {d.Release}: {dep.Id} {(dep.Completed ? "finished" : "runs")} in Octopus, the decision says it {(m.Groups["still"].Success ? "is still running" : "ended")} ({reason})";
            }
            else if (NoDeploymentReason.Match(reason) is { Success: true } n)
            {
                if (t.Of(d.Release, n.Groups["env"].Value.ToLowerInvariant()) is { } dep) yield return $"{d.Key} {d.Release}: no deployment to {n.Groups["env"].Value}, but Octopus has {dep.Id} ({dep.State})";
            }
        }
    }

    static string Describe(TrainDecided? d) => d is null ? "no decision" : $"{d.Action} [{string.Join("; ", d.Reasons)}]";

    static (Truth Truth, Replay Replay) Both(Replay r) => (new Truth(Fixture.Value, Project(r.Slug)), r);

    // The fixture is what it claims to be: the history the cases below are about is in it.
    [Fact]
    public void TheFixtureHoldsTheRecordedHistory()
    {
        Assert.Equal(30, (int)Fixture.Value["days"]!);
        Assert.Equal(["tdd", "uat", "prod"], Fixture.Value["environments"]!.AsArray().Select(e => e!["Name"]!.ToString()));
        var ui = new Truth(Fixture.Value, Project("cmdemo1-ui"));
        Assert.Equal("Releases-51167", ui.Releases["2.4.1298"].Id);
        Assert.Equal("Success", ui.ById["Deployments-57454"].State);
        Assert.Equal("tdd", ui.ById["Deployments-57454"].Env);
        foreach (var v in new[] { "2.4.1228", "2.4.1257", "2.4.1261", "2.4.1264", "2.4.1277", "2.4.1283", "2.4.1288" })
            Assert.Equal("Failed", ui.Of(v, "tdd")!.State);
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public async Task TheReplayCompletes_AndTheTrainDecidesOnlyAfterIt(string slug)
    {
        var r = await Run(slug, shadowSettings: true);
        Assert.True(r.Polls >= 1);
        Assert.NotEmpty(r.Decisions);
        // the train judges its state once, at the replay's end: every decision of the replay is flagged so
        Assert.All(r.Decisions, d => Assert.True(d.Replay, $"{d.Key} {d.Release}: {d.Action} was decided outside the replay"));
        Assert.All(r.States.Values, s => Assert.Null(s.ReplayingSince));
        // nothing is asked of Octopus or of a person: the commands of the train would be recorded, never sent (the shadow host has no handler), and a Sign-off is never asked in finished history
        Assert.DoesNotContain(r.Messages, m => m is Fworker.Contracts.Commands.AnswerSignOff);
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public async Task EveryReasonOfEveryDecision_AgreesWithOctopus(string slug)
    {
        foreach (var shadow in new[] { true, false })
        {
            var (t, r) = Both(await Run(slug, shadow));
            var wrong = r.Decisions.SelectMany(d => Disagreements(t, d)).Distinct().ToList();
            Assert.True(wrong.Count == 0, $"{slug} (shadow settings: {shadow}), {wrong.Count} reasons disagree with Octopus:\n" + string.Join("\n", wrong.Take(40)));
            // (the check looked at something: ui has nine releases with a failed tdd deployment and ten with no deployment to uat)
            if (slug == "cmdemo1-ui") Assert.True(r.Decisions.SelectMany(d => d.Reasons).Count(x => DeploymentReason.IsMatch(x) || NoDeploymentReason.IsMatch(x)) >= 19, "the reasons of ui were not checked");
        }
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public async Task WhereOctopusAloneDecidesTheRelease_TheTrainDecidesAsV1Does(string slug)
    {
        foreach (var shadow in new[] { true, false })
        {
            var (t, r) = Both(await Run(slug, shadow));
            var problems = new List<string>(); var compared = 0;
            foreach (var env in new[] { "uat", "prod" })
                foreach (var (version, d) in r.FinalDecisions(env))
                {
                    if (V1(r.Deployable, t, version) is not { } v1 || v1.Target != env) continue;   // (v1 judges a release only for its target: the first environment it has not succeeded in)
                    if (v1.Decision is null)
                    {
                        // the previous environment succeeded: v1 decides on gates that Octopus does not hold; the train must not say anything of the previous deployment
                        if (Train(d).Action == ShadowActions.Wait && Train(d).ReasonClass is ShadowReasons.None or ShadowReasons.Failed or ShadowReasons.Started)
                            problems.Add($"{env} {version}: the previous environment succeeded in Octopus, the train says {Describe(d)}");
                        continue;
                    }
                    compared++;
                    if (!ShadowClassifier.Agree(v1.Decision, Train(d)))
                        problems.Add($"{env} {version}: v1 decides {v1.Decision.Action}/{v1.Decision.ReasonClass} [{string.Join("; ", v1.Decision.Reasons)}], the train {Describe(d)}");
                }
            Assert.True(problems.Count == 0, $"{slug} (shadow settings: {shadow}), {problems.Count} releases differ from v1 (of {compared} compared):\n" + string.Join("\n", problems.Take(40)));
            if (slug == "cmdemo1-ui") Assert.True(compared >= FailedInTdd.Length, $"only {compared} releases were compared with v1");
        }
    }

    // ---- the cases of 2026-10-28 on cmdemo1-ui --------------------------------------------------------------------------------------------------------------

    /// <summary>The releases whose tdd deployment failed on 2026-10-26/27: the seven that v1 and the train both waited on for "failed" in the shadow run (1228, 1257, 1261, 1264, 1277, 1283, 1288), and 1268 and 1273, which failed the same way.</summary>
    static readonly string[] FailedInTdd = ["2.4.1228", "2.4.1257", "2.4.1261", "2.4.1264", "2.4.1268", "2.4.1273", "2.4.1277", "2.4.1283", "2.4.1288"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ui_TheReleasesThatFailedInTdd_WaitInUatForFailed_AsV1Does(bool shadowSettings)
    {
        var (t, r) = Both(await Run("cmdemo1-ui", shadowSettings));
        foreach (var version in FailedInTdd)
        {
            var tdd = t.Of(version, "tdd")!;
            Assert.Equal("Failed", tdd.State);
            var d = r.Final("uat", version);
            Assert.True(d is { Action: "waiting" }, $"uat {version}: {Describe(d)}");
            // the very words of v1 (Invoke-AutoPromoteInner), with the deployment of Octopus
            var v1 = V1("ui", t, version)!.Value;
            Assert.Equal("uat", v1.Target);
            Assert.Equal([$"its deployment to tdd is Failed ({tdd.Id})"], d!.Reasons);
            Assert.Equal(v1.Decision!.Reasons, d.Reasons);
            Assert.Equal(ShadowReasons.Failed, Train(d).ReasonClass);
            Assert.True(ShadowClassifier.Agree(v1.Decision, Train(d)));
            Assert.True(d.Replay);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ui_Prod_WaitsForNoDeploymentToUat_WhereThereIsNone(bool shadowSettings)
    {
        var (t, r) = Both(await Run("cmdemo1-ui", shadowSettings));
        var open = r.FinalDecisions("prod");
        // the ten releases after 2.4.1120 (the last in prod) were never deployed to uat
        string[] expected = [.. FailedInTdd, "2.4.1298"];
        Assert.Equal(expected.Order(), open.Keys.Order());
        foreach (var (version, d) in open)
        {
            Assert.Null(t.Of(version, "uat"));
            Assert.Equal("waiting", d.Action);
            Assert.Equal(["it has no deployment to uat"], d.Reasons);
            Assert.Equal(ShadowReasons.None, Train(d).ReasonClass);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ui_Uat_2_4_1298_DoesNotWaitForAFinishedTddDeployment(bool shadowSettings)
    {
        // Deployments-57454 (tdd, 2.4.1298) succeeded on 2026-10-27 12:05Z. The shadow run on tdd read "wait for started" for it (issue 500; the fix is on cm-ai-ops-bot/w5-stale-started).
        var (t, r) = Both(await Run("cmdemo1-ui", shadowSettings));
        Assert.Equal("Success", t.ById["Deployments-57454"].State);
        var d = r.Final("uat", "2.4.1298");
        Assert.NotNull(d);
        Assert.DoesNotContain(d!.Reasons, x => x.Contains("still"));
        Assert.NotEqual(ShadowReasons.Started, Train(d).ReasonClass);
        Assert.Equal("Succeeded", r.States["ui/uat"].DeploymentOf("2.4.1298", "tdd")!.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ui_ADeploymentFirstSeenRunning_IsSettledByTheNextPoll(bool shadowSettings)
    {
        // The live order of events on 2026-10-27: the watcher saw Deployments-57454 execute, the replay ended, and a later poll read the task finished.
        var firstRead = true;
        var r = await Execute("cmdemo1-ui", shadowSettings, (_, task) => { if (task != "ServerTasks-12126952" || !firstRead) return null; firstRead = false; return "Executing"; }, minPolls: 6);
        // while it ran, the train said so ...
        var running = r.Decisions.Where(d => d.Key == "ui/uat" && d.Release == "2.4.1298" && d.Reasons.Any(x => x.Contains("still Started (Deployments-57454)"))).ToList();
        Assert.NotEmpty(running);
        // ... and once it finished, the last word is not "started"
        var final = r.Final("uat", "2.4.1298");
        Assert.NotNull(final);
        Assert.DoesNotContain(final!.Reasons, x => x.Contains("still"));
        Assert.Equal("Succeeded", r.States["ui/uat"].DeploymentOf("2.4.1298", "tdd")!.Phase);
    }
}
