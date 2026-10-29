using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Octopus;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W4.2: the Octopus watcher against Octopus-shaped answers: event mapping, no repeat across polls, the release's commit, the call budget, read only.</summary>
public sealed class OctopusWatcherTests
{
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    const string Sp = "api/Spaces-1";
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

    sealed class FakeOctopus : IOctopusReader
    {
        public Dictionary<string, JsonNode?> Answers = [];
        public List<string> Paths = [];
        public Task<JsonNode?> Get(string path, CancellationToken ct = default) { Paths.Add(path); return Task.FromResult(Answers.GetValueOrDefault(path)); }
        public void Set(string path, string json) => Answers[path] = JsonNode.Parse(json);
    }

    static FakeOctopus Server(string taskState = "Executing", string created = "2026-10-28T11:59:00Z")
    {
        var o = new FakeOctopus();
        o.Set($"{Sp}/projects/ui", """{"Id":"Projects-1"}""");
        o.Set($"{Sp}/environments?take=1000", """{"Items":[{"Id":"Environments-1","Name":"TDD"},{"Id":"Environments-2","Name":"prod"}]}""");
        o.Set($"{Sp}/projects/Projects-1/releases?take=30", $$"""{"Items":[{"Id":"Releases-5","Version":"1.0.5","Assembled":"{{created}}","ReleaseNotes":"built {{Sha.ToUpperInvariant()}}"}]}""");
        o.Set($"{Sp}/deployments?projects=Projects-1&take=30", $$"""{"Items":[{"Id":"Deployments-9","TaskId":"ServerTasks-9","ReleaseId":"Releases-5","EnvironmentId":"Environments-1","Created":"{{created}}"}]}""");
        o.Set($"{Sp}/tasks/ServerTasks-9", $$"""{"State":"{{taskState}}"}""");
        o.Set($"{Sp}/interruptions?regarding=ServerTasks-9&pendingOnly=true&take=20", """{"Items":[{"Id":"Interruptions-3","IsPending":true}]}""");
        return o;
    }

    static (OctopusWatcher w, List<IngressEvent> got) Make(FakeOctopus o, int budget = 60)
    {
        var got = new List<IngressEvent>();
        // (the replay markers are the subject of their own tests below)
        return (new OctopusWatcher(o, e => { if (e is not (OctopusReplayBegan or OctopusReplayComplete)) got.Add(e); }, new OctopusWatchSettings("Spaces-1", "ui", MaxCallsPerPoll: budget), () => T0), got);
    }

    static (OctopusWatcher w, List<IngressEvent> all) MakeAll(FakeOctopus o, int budget = 60, Func<IngressEvent, bool>? refuse = null)
    {
        var all = new List<IngressEvent>();
        return (new OctopusWatcher(o, e => { if (refuse?.Invoke(e) == true) throw new InvalidOperationException("the ingress is full"); all.Add(e); }, new OctopusWatchSettings("Spaces-1", "ui", MaxCallsPerPoll: budget), () => T0), all);
    }

    [Fact]
    public async Task TheFirstPollIsAReplay_BeganBeforeItsEvents_CompleteAfter_AndOnlyOnce()
    {
        var (w, all) = MakeAll(Server("Success"));
        Assert.True(await w.Poll());
        Assert.IsType<OctopusReplayBegan>(all[0]);
        Assert.IsType<OctopusReplayComplete>(all[^1]);
        Assert.Equal(1, all.OfType<OctopusReplayComplete>().Count());
        Assert.Contains(all, e => e is OctopusDeploymentChanged);
        await w.Poll();
        Assert.Equal(1, all.OfType<OctopusReplayBegan>().Count());
        Assert.Equal(1, all.OfType<OctopusReplayComplete>().Count());
    }

    [Fact]
    public async Task AReplayThatRanOutOfBudget_IsNotComplete_UntilAPollReadsEverything()
    {
        var (w, all) = MakeAll(Server("Success"), budget: 4);   // the project, the environments, the releases and the deployments: no call left for the tasks
        Assert.False(await w.Poll());
        Assert.Empty(all.OfType<OctopusReplayComplete>());
        Assert.Single(all.OfType<OctopusReplayBegan>());
        Assert.True(await MakeAllSamePoll(w, all));
    }

    static async Task<bool> MakeAllSamePoll(OctopusWatcher w, List<IngressEvent> all)
    {
        var ok = false;
        for (var i = 0; i < 5 && !ok; i++) ok = await w.Poll();
        return ok && all.OfType<OctopusReplayComplete>().Count() == 1 && all.OfType<OctopusReplayBegan>().Count() == 1;
    }

    [Fact]
    public async Task AMarkerThatTheIngressRefuses_IsPublishedAgainByTheNextPoll()
    {
        var refuse = true;
        var (w, all) = MakeAll(Server("Success"), refuse: e => refuse && e is OctopusReplayBegan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Poll());
        Assert.Empty(all);
        refuse = false;
        Assert.True(await w.Poll());
        Assert.IsType<OctopusReplayBegan>(all[0]);
        Assert.IsType<OctopusReplayComplete>(all[^1]);
    }

    [Fact]
    public async Task AnEventThatTheIngressRefuses_IsForgotten_AndOfferedAgain()
    {
        var refuse = true;
        var (w, all) = MakeAll(Server("Success"), refuse: e => refuse && e is OctopusDeploymentChanged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Poll());
        Assert.Empty(all.OfType<OctopusReplayComplete>());   // the replay is not complete: the deployment is missing
        refuse = false;
        await w.Poll();
        Assert.Contains(all, e => e is OctopusDeploymentChanged { Phase: OctopusDeploymentPhase.Started });
        Assert.Single(all.OfType<OctopusReplayComplete>());
    }

    static string KeyOf(IngressEvent e) => e switch { OctopusReleaseCreated r => r.Key, ReleaseCommitResolved c => c.Key, OctopusDeploymentChanged d => d.Key, SignOffRequested s => s.Key, _ => "" };

    [Fact]
    public async Task ARunningDeploymentGivesReleaseCommitStartedAndSignOff()
    {
        var (w, got) = Make(Server());
        Assert.True(await w.Poll());
        var rel = Assert.IsType<OctopusReleaseCreated>(got[0]); Assert.Equal(("Releases-5", "1.0.5", Sha), (rel.ReleaseId, rel.Version, rel.Commit));
        Assert.Equal(Sha, Assert.IsType<ReleaseCommitResolved>(got[1]).Commit);
        var dep = Assert.IsType<OctopusDeploymentChanged>(got[2]); Assert.Equal(("tdd", OctopusDeploymentPhase.Started, "Deployments-9"), (dep.Environment, dep.Phase, dep.DeploymentId));
        Assert.Equal(Sha, dep.Commit);
        var so = Assert.IsType<SignOffRequested>(got[3]); Assert.Equal(("Interruptions-3", "ServerTasks-9"), (so.InterruptionId, so.TaskId));
        Assert.Equal(4, got.Count);
    }

    [Fact]
    public async Task RepeatedPollsPublishNothingTwice()
    {
        var (w, got) = Make(Server());
        await w.Poll(); var first = got.Count; await w.Poll(); await w.Poll();
        Assert.Equal(first, got.Count);
        Assert.Equal(got.Count, got.Select(KeyOf).Distinct().Count());
    }

    [Theory]
    [InlineData("Success", OctopusDeploymentPhase.Succeeded)]
    [InlineData("Failed", OctopusDeploymentPhase.Failed)]
    [InlineData("TimedOut", OctopusDeploymentPhase.Failed)]
    [InlineData("Canceled", OctopusDeploymentPhase.Failed)]
    public async Task ATerminalTaskPublishesItsPhaseAfterStartedAndIsNotReadAgain(string state, OctopusDeploymentPhase expected)
    {
        var o = Server(); var (w, got) = Make(o);
        await w.Poll();
        o.Set($"{Sp}/tasks/ServerTasks-9", $$"""{"State":"{{state}}"}""");
        await w.Poll();
        Assert.Equal(expected, got.OfType<OctopusDeploymentChanged>().Last().Phase);
        Assert.Equal(2, got.OfType<OctopusDeploymentChanged>().Count());
        o.Paths.Clear(); await w.Poll();
        Assert.DoesNotContain(o.Paths, p => p.Contains("tasks/"));
    }

    [Fact]
    public async Task ADeploymentSeenFirstAsSucceededStillPublishesStartedThenSucceeded()
    {
        var (w, got) = Make(Server(taskState: "Success"));
        await w.Poll();
        Assert.Equal([OctopusDeploymentPhase.Started, OctopusDeploymentPhase.Succeeded], got.OfType<OctopusDeploymentChanged>().Select(d => d.Phase));
        Assert.DoesNotContain(got, e => e is SignOffRequested);
    }

    [Fact]
    public async Task HistoryOlderThanTheLookbackIsNotReplayed()
    {
        var (w, got) = Make(Server(taskState: "Success", created: "2026-10-28T08:00:00Z"));
        await w.Poll();
        Assert.Empty(got);
    }

    [Fact]
    public void TheCommitComesFromBuildInformationFirstThenTheNotes()
    {
        var other = new string('a', 40);
        Assert.Equal(other, OctopusWatcher.ReleaseCommit(JsonNode.Parse($$"""{"BuildInformation":[{"VcsCommitNumber":"short"},{"VcsCommitNumber":"{{other.ToUpperInvariant()}}"}],"ReleaseNotes":"{{Sha}}"}""")));
        Assert.Equal(Sha, OctopusWatcher.ReleaseCommit(JsonNode.Parse($$"""{"BuildInformation":[],"ReleaseNotes":"see {{Sha}} ok"}""")));
        Assert.Null(OctopusWatcher.ReleaseCommit(JsonNode.Parse("""{"ReleaseNotes":"no commit here, 123"}""")));
    }

    [Fact]
    public async Task AReleaseWithoutACommitPublishesNoCommitResolved()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", """{"Items":[{"Id":"Releases-5","Version":"1.0.5","Assembled":"2026-10-28T11:59:00Z","ReleaseNotes":"none"}]}""");
        var (w, got) = Make(o);
        await w.Poll();
        Assert.DoesNotContain(got, e => e is ReleaseCommitResolved);
        Assert.Null(Assert.IsType<OctopusReleaseCreated>(got[0]).Commit);
    }

    [Fact]
    public async Task ThePollStopsAtItsCallBudgetAndTheNextOneContinues()
    {
        var (w, got) = Make(Server(), budget: 5);
        Assert.False(await w.Poll()); Assert.Equal(5, w.LastCalls);
        for (var i = 0; i < 10 && !got.Any(e => e is SignOffRequested); i++) await w.Poll();
        Assert.Contains(got, e => e is SignOffRequested);
    }

    [Fact]
    public async Task OnlyGetsAreMade()
    {
        var o = Server(); var (w, _) = Make(o);
        await w.Poll();
        Assert.All(o.Paths, p => Assert.StartsWith(Sp + "/", p));
        Assert.Single(typeof(IOctopusReader).GetMethods());   // the reader's whole surface is Get
    }

    [Fact]
    public async Task AnUnknownProjectWatchesNothing()
    {
        var (w, got) = Make(new FakeOctopus());
        Assert.True(await w.Poll()); Assert.Empty(got);
    }

    // ---- #487: bounded memory, releases whose commit comes late ------------------------------------------------------------------------------------------

    static OctopusWatcher Windowed(FakeOctopus o, List<IngressEvent> got, int window) => new(o, e => { if (e is not (OctopusReplayBegan or OctopusReplayComplete)) got.Add(e); }, new OctopusWatchSettings("Spaces-1", "ui", ReleaseWindow: window, DeploymentWindow: window), () => T0);

    [Fact]
    public async Task TheMemoryIsBoundedByTheWindows_NotByTheAgeOfTheProcess_AndNothingIsPublishedTwice()
    {
        const int window = 5;
        var o = new FakeOctopus(); var got = new List<IngressEvent>(); var w = Windowed(o, got, window);
        o.Set($"{Sp}/projects/ui", """{"Id":"Projects-1"}""");
        o.Set($"{Sp}/environments?take=1000", """{"Items":[{"Id":"Environments-1","Name":"tdd"}]}""");
        var peak = 0;
        for (var i = 1; i <= 60; i++)
        {
            // the newest `window` releases and deployments; every deployment is still running and waits at its own interruption
            var ids = Enumerable.Range(Math.Max(1, i - window + 1), Math.Min(i, window)).ToList();
            var releaseItems = string.Join(",", ids.Select(n => $$"""{"Id":"Releases-{{n}}","Version":"1.0.{{n}}","Assembled":"2026-10-28T11:59:00Z","ReleaseNotes":"{{Sha}}"}"""));
            var deploymentItems = string.Join(",", ids.Select(n => $$"""{"Id":"Deployments-{{n}}","TaskId":"ServerTasks-{{n}}","ReleaseId":"Releases-{{n}}","EnvironmentId":"Environments-1","Created":"2026-10-28T11:59:00Z"}"""));
            o.Set($"{Sp}/projects/Projects-1/releases?take={window}", "{\"Items\":[" + releaseItems + "]}");
            o.Set($"{Sp}/deployments?projects=Projects-1&take={window}", "{\"Items\":[" + deploymentItems + "]}");
            foreach (var n in ids)
            {
                o.Set($"{Sp}/tasks/ServerTasks-{n}", """{"State":"Executing"}""");
                o.Set($"{Sp}/interruptions?regarding=ServerTasks-{n}&pendingOnly=true&take=20", $$"""{"Items":[{"Id":"Interruptions-{{n}}","IsPending":true}]}""");
            }
            Assert.True(await w.Poll());
            peak = Math.Max(peak, w.Remembered);
        }
        // 60 releases, each with its two events, and 60 deployments, each with Started and a sign-off: 240 events, every key once
        Assert.Equal(240, got.Count);
        Assert.Equal(got.Count, got.Select(KeyOf).Distinct().Count());
        Assert.InRange(peak, 1, 4 * window * 2);   // (keys of 5 releases and 5 deployments, the known releases)
        Assert.InRange(w.Remembered, 1, 4 * window * 2);
    }

    [Fact]
    public async Task AWindowThatCouldNotBeRead_ForgetsNothing_SoTheNextPollRepeatsNothing()
    {
        var o = Server(); var (w, got) = Make(o);
        await w.Poll(); var first = got.Count; var remembered = w.Remembered;
        var releases = o.Answers[$"{Sp}/projects/Projects-1/releases?take=30"]; var deployments = o.Answers[$"{Sp}/deployments?projects=Projects-1&take=30"];
        o.Answers.Remove($"{Sp}/projects/Projects-1/releases?take=30"); o.Answers.Remove($"{Sp}/deployments?projects=Projects-1&take=30");   // Octopus answers 404 to both: no list, not an empty one
        await w.Poll();
        Assert.Equal(remembered, w.Remembered);
        o.Answers[$"{Sp}/projects/Projects-1/releases?take=30"] = releases; o.Answers[$"{Sp}/deployments?projects=Projects-1&take=30"] = deployments;
        await w.Poll();
        Assert.Equal(first, got.Count);
    }

    [Fact]
    public async Task AnEmptyWindow_ForgetsWhatLeftIt()
    {
        var o = Server(); var (w, _) = Make(o);
        await w.Poll(); Assert.True(w.Remembered > 0);
        o.Set($"{Sp}/projects/Projects-1/releases?take=30", """{"Items":[]}"""); o.Set($"{Sp}/deployments?projects=Projects-1&take=30", """{"Items":[]}""");
        await w.Poll();
        Assert.Equal(0, w.Remembered);
    }

    static string ReleaseList(string commitField = "") => """{"Items":[{"Id":"Releases-5","Version":"1.0.5","Assembled":"2026-10-28T11:59:00Z","ReleaseNotes":"none"%FIELD%}]}""".Replace("%FIELD%", commitField);

    static string BuildInfo(string sha) => ",\"BuildInformation\":[{\"VcsCommitNumber\":\"" + sha + "\"}]";

    [Fact]
    public async Task AReleaseWhoseCommitComesLater_PublishesCommitResolvedOnce_AndNoSecondReleaseCreated()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList());
        var (w, got) = Make(o);
        await w.Poll();
        Assert.Null(Assert.IsType<OctopusReleaseCreated>(got[0]).Commit);
        Assert.DoesNotContain(got, e => e is ReleaseCommitResolved);
        Assert.Null(got.OfType<OctopusDeploymentChanged>().Single().Commit);

        await w.Poll();   // still nothing: nothing new is published, the commit is looked for again
        Assert.DoesNotContain(got, e => e is ReleaseCommitResolved);

        o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList(BuildInfo(Sha.ToUpperInvariant())));
        var calls = o.Paths.Count; await w.Poll();
        var resolved = Assert.Single(got.OfType<ReleaseCommitResolved>());
        Assert.Equal(("Releases-5", "1.0.5", Sha), (resolved.ReleaseId, resolved.Version, resolved.Commit));
        Assert.Single(got.OfType<OctopusReleaseCreated>());
        Assert.DoesNotContain(o.Paths.Skip(calls), p => p.Contains("/releases/Releases-5"));   // (the list had it: no extra call)

        await w.Poll(); await w.Poll();
        Assert.Single(got.OfType<ReleaseCommitResolved>());
        Assert.Equal(got.Count, got.Select(KeyOf).Distinct().Count());
    }

    [Fact]
    public async Task ACommitThatComesLater_IsOnTheEventsPublishedAfterIt()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList());
        var (w, got) = Make(o);
        await w.Poll();
        o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList(BuildInfo(Sha)));
        o.Set($"{Sp}/tasks/ServerTasks-9", """{"State":"Success"}""");
        await w.Poll();
        Assert.Equal(Sha, got.OfType<OctopusDeploymentChanged>().Single(d => d.Phase == OctopusDeploymentPhase.Succeeded).Commit);
    }

    [Fact]
    public async Task ACommitResolvedThatTheIngressRefuses_IsOfferedAgain()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList());
        var refuse = false;
        var (w, all) = MakeAll(o, refuse: e => refuse && e is ReleaseCommitResolved);
        await w.Poll();
        o.Set($"{Sp}/projects/Projects-1/releases?take=30", ReleaseList(BuildInfo(Sha)));
        refuse = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Poll());
        refuse = false;
        await w.Poll();
        Assert.Single(all.OfType<ReleaseCommitResolved>());
    }

    [Fact]
    public async Task AReleaseOutsideTheReleaseWindow_HasItsCommitLookedForAgainWhileItsDeploymentRuns_OncePerPoll()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", """{"Items":[]}""");
        o.Set($"{Sp}/releases/Releases-5", """{"Id":"Releases-5","Version":"1.0.5","ReleaseNotes":"none"}""");
        var (w, got) = Make(o);
        await w.Poll();
        Assert.Null(got.OfType<OctopusDeploymentChanged>().Single().Commit);
        o.Paths.Clear(); await w.Poll();
        Assert.Single(o.Paths, p => p == $"{Sp}/releases/Releases-5");   // looked at again, once
        o.Set($"{Sp}/releases/Releases-5", $$"""{"Id":"Releases-5","Version":"1.0.5","ReleaseNotes":"built {{Sha}}"}""");
        o.Set($"{Sp}/tasks/ServerTasks-9", """{"State":"Success"}""");
        await w.Poll();
        Assert.Equal(Sha, got.OfType<OctopusDeploymentChanged>().Single(d => d.Phase == OctopusDeploymentPhase.Succeeded).Commit);
        o.Paths.Clear(); await w.Poll();
        Assert.DoesNotContain(o.Paths, p => p.Contains("/releases/Releases-5"));   // finished: not read again
    }

    // ---- #496: the events carry Octopus's own time ------------------------------------------------------------------------------------------------------

    static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheReleaseIsAtItsAssembledTime_TheRunningDeploymentAtItsStartTime_TheSignOffAtTheInterruptionsCreatedTime()
    {
        var o = Server();
        o.Set($"{Sp}/tasks/ServerTasks-9", """{"State":"Executing","QueueTime":"2026-10-28T11:58:00+00:00","StartTime":"2026-10-28T11:59:30.5+00:00"}""");
        o.Set($"{Sp}/interruptions?regarding=ServerTasks-9&pendingOnly=true&take=20", """{"Items":[{"Id":"Interruptions-3","IsPending":true,"Created":"2026-10-28T12:00:00+02:00"}]}""");
        var (w, got) = Make(o);
        await w.Poll();
        Assert.Equal(Utc("2026-10-28T11:59:00Z"), Assert.IsType<OctopusReleaseCreated>(got[0]).At);
        Assert.Equal(Utc("2026-10-28T11:59:30.5Z"), Assert.IsType<OctopusDeploymentChanged>(got[2]).At);
        Assert.Equal(Utc("2026-10-28T10:00:00Z"), Assert.IsType<SignOffRequested>(got[3]).At);   // (12:00 at +02:00)
    }

    [Fact]
    public async Task ATerminalDeploymentIsAtItsCompletedTime_ItsStartedPhaseAtTheStartTime()
    {
        var o = Server("Success");
        o.Set($"{Sp}/tasks/ServerTasks-9", """{"State":"Success","QueueTime":"2026-10-28T11:57:00Z","StartTime":"2026-10-28T11:58:00Z","CompletedTime":"2026-10-28T11:59:10Z"}""");
        var (w, got) = Make(o);
        await w.Poll();
        var phases = got.OfType<OctopusDeploymentChanged>().ToList();
        Assert.Equal([(OctopusDeploymentPhase.Started, Utc("2026-10-28T11:58:00Z")), (OctopusDeploymentPhase.Succeeded, Utc("2026-10-28T11:59:10Z"))], phases.Select(d => (d.Phase, d.At!.Value)));
    }

    [Theory]
    [InlineData("""{"State":"Failed","StartTime":"2026-10-28T11:58:00Z"}""", "2026-10-28T11:58:00Z", "2026-10-28T11:58:00Z")]   // no CompletedTime: the StartTime
    [InlineData("""{"State":"Failed","QueueTime":"2026-10-28T11:57:00Z"}""", "2026-10-28T11:57:00Z", "2026-10-28T11:57:00Z")]   // never started: the QueueTime
    [InlineData("""{"State":"Failed"}""", "2026-10-28T11:59:00Z", "2026-10-28T11:59:00Z")]   // no task times at all: the deployment's Created
    [InlineData("""{"State":"Failed","StartTime":"not a time","CompletedTime":""}""", "2026-10-28T11:59:00Z", "2026-10-28T11:59:00Z")]   // times that do not parse are skipped
    public async Task AMissingTimeFallsBack_StartTime_QueueTime_ThenTheDeploymentsCreated(string task, string started, string finished)
    {
        var o = Server("Failed"); o.Set($"{Sp}/tasks/ServerTasks-9", task);
        var (w, got) = Make(o);
        await w.Poll();
        var phases = got.OfType<OctopusDeploymentChanged>().ToList();
        Assert.Equal([Utc(started), Utc(finished)], phases.Select(d => d.At!.Value));
    }

    [Fact]
    public async Task AQueuedDeploymentIsAtItsQueueTime()
    {
        var o = Server("Queued"); o.Set($"{Sp}/tasks/ServerTasks-9", """{"State":"Queued","QueueTime":"2026-10-28T11:58:30Z"}""");
        var (w, got) = Make(o);
        await w.Poll();
        Assert.Equal(Utc("2026-10-28T11:58:30Z"), Assert.Single(got.OfType<OctopusDeploymentChanged>()).At);
    }

    [Fact]
    public async Task AReleaseWithoutAnAssembledTimeHasNoTime_AndTheTrainStampsItItself()
    {
        var o = Server(); o.Set($"{Sp}/projects/Projects-1/releases?take=30", $$"""{"Items":[{"Id":"Releases-5","Version":"1.0.5","ReleaseNotes":"built {{Sha}}"}]}""");
        var (w, got) = Make(o);
        await w.Poll();
        Assert.Null(Assert.IsType<OctopusReleaseCreated>(got[0]).At);
    }

    // The events travel through the sequential octopus-events queue as JSON: a message written before At existed must still be read, and a new one must be readable by the old shape.
    static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void TheEventsRoundTripThroughJson_WithAndWithoutAt()
    {
        var at = Utc("2026-10-28T09:30:00.25+00:00");
        IngressEvent[] events =
        [
            new OctopusReleaseCreated("R-1", "1.0.1", Sha, at),
            new OctopusDeploymentChanged("D-1", "T-1", "R-1", "1.0.1", "tdd", OctopusDeploymentPhase.Succeeded, Sha, at),
            new SignOffRequested("I-1", "D-1", "T-1", "R-1", "1.0.1", "uat", Sha, at),
            new OctopusReleaseCreated("R-2", "1.0.2", null),
            new OctopusDeploymentChanged("D-2", "T-2", "R-2", "1.0.2", "tdd", OctopusDeploymentPhase.Started, null),
            new SignOffRequested("I-2", "D-2", "T-2", "R-2", "1.0.2", "uat", null),
        ];
        foreach (var e in events)
            Assert.Equal(e, System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(e, e.GetType(), Web), e.GetType(), Web));
    }

    [Fact]
    public void AMessageSerializedBeforeAtExisted_StillDeserializes_WithAtNull()
    {
        var release = System.Text.Json.JsonSerializer.Deserialize<OctopusReleaseCreated>("""{"releaseId":"R-1","version":"1.0.1","commit":null}""", Web);
        Assert.Equal(new OctopusReleaseCreated("R-1", "1.0.1", null), release);
        var dep = System.Text.Json.JsonSerializer.Deserialize<OctopusDeploymentChanged>("""{"deploymentId":"D-1","taskId":"T-1","releaseId":"R-1","version":"1.0.1","environment":"tdd","phase":1,"commit":null,"key":"octopus:deployment:D-1:Succeeded"}""", Web);
        Assert.Equal(new OctopusDeploymentChanged("D-1", "T-1", "R-1", "1.0.1", "tdd", OctopusDeploymentPhase.Succeeded, null), dep);
        var so = System.Text.Json.JsonSerializer.Deserialize<SignOffRequested>("""{"interruptionId":"I-1","deploymentId":"D-1","taskId":"T-1","releaseId":"R-1","version":"1.0.1","environment":"uat","commit":null}""", Web);
        Assert.Null(so!.At);
    }

    [Fact]
    public void AMessageWithAtIsReadByTheShapeThatHasNone()
    {
        // the previous shape of the record, as an older reader would have it: the unknown 'at' is ignored
        var json = System.Text.Json.JsonSerializer.Serialize(new OctopusReleaseCreated("R-1", "1.0.1", null, Utc("2026-10-28T09:30:00Z")), Web);
        Assert.Contains("\"at\":", json);
        Assert.Equal(new OldRelease("R-1", "1.0.1", null), System.Text.Json.JsonSerializer.Deserialize<OldRelease>(json, Web));
    }

    sealed record OldRelease(string ReleaseId, string Version, string? Commit);
}
