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
}
