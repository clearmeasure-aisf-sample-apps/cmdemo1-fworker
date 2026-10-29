using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// #496: the delivery train hears an Octopus fact at the time Octopus gave it, not at the time the router received it. After a gateway restart the watcher replays Octopus history; a tdd deployment that
/// finished two hours ago must not open a fresh health window (the uat train would wait it out, where v1 promotes: a spurious 'differs').
/// </summary>
public class DeliveryTrainTimestampTests
{
    /// <summary>A router on the fixture's clock; the inputs of the fixture's train go to the fixture, as the outbox would deliver them.</summary>
    sealed class Routed(TrainFixture f)
    {
        readonly DeliveryTrainRouter router = new(f.Policies, () => f.Now);
        public Task Release(OctopusReleaseCreated e) => Feed(router.Route(e));
        public Task Deploy(OctopusDeploymentChanged e) => Feed(router.Route(e));
        public Task SignOff(SignOffRequested e) => Feed(router.Route(e));
        async Task Feed(IReadOnlyList<TrainInput> inputs) { foreach (var i in inputs.Where(i => i.Key == f.Key)) await f.Send(i); }
    }

    /// <summary>Baseline 1.0.1 everywhere, item #7 merged as commit a; the app of tdd runs 1.0.2. Nothing of 1.0.2 is seen yet.</summary>
    static async Task<TrainFixture> Merged()
    {
        var f = new TrainFixture();
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        return f;
    }

    static OctopusDeploymentChanged TddDeployment(OctopusDeploymentPhase phase, DateTimeOffset? at)
        => new("Deployments-2", "ServerTasks-2", "Releases-2", "1.0.2", "Tdd", phase, Sha('a'), at);

    [Fact]
    public async Task AReplayedTddDeploymentOfTwoHoursAgo_IsSeenAtItsOwnTime_AndTheUatTrainDoesNotWaitOutAFreshHealthWindow()
    {
        var f = await Merged(); var r = new Routed(f);
        var finished = T0.AddHours(-2);
        await r.Release(new OctopusReleaseCreated("Releases-2", "1.0.2", Sha('a'), finished.AddMinutes(-10)));
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Started, finished.AddMinutes(-5)));
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Succeeded, finished));

        var deployment = f.State.DeploymentOf("1.0.2", "tdd")!;
        Assert.Equal(finished, deployment.FinishedAt);
        Assert.Equal(finished.AddMinutes(-5), deployment.StartedAt);
        Assert.Equal(finished.AddMinutes(-10), f.State.Releases["1.0.2"].Assembled);

        // the first Healthy read is not enough (two reads 5 min apart are), but the deployment is old: the second read, 5 minutes later, completes the evidence
        await f.Wake();
        Assert.Equal("waiting", f.LastDecision!.Action);
        Assert.Empty(f.Of<PromoteRelease>());
        await f.After(TimeSpan.FromMinutes(5));
        Assert.Single(f.Of<PromoteRelease>());
        Assert.Equal("promote", f.LastDecision!.Action);
    }

    [Fact]
    public async Task TheSameDeploymentStampedWithTheArrivalTime_WaitsTenMinutes_WhichIsWhatAtFixes()
    {
        var f = await Merged(); var r = new Routed(f);
        await r.Release(new OctopusReleaseCreated("Releases-2", "1.0.2", Sha('a')));   // (no At: the router's clock)
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Started, null));
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Succeeded, null));
        Assert.Equal(f.Now, f.State.DeploymentOf("1.0.2", "tdd")!.FinishedAt);

        await f.Wake();
        await f.After(TimeSpan.FromMinutes(5));
        Assert.Empty(f.Of<PromoteRelease>());   // the window is fresh: the later read must be 10 min after the deployment finished
        await f.After(TimeSpan.FromMinutes(5));
        Assert.Single(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task AMissingAt_FallsBackToTheClock_ForEveryKindOfEvent()
    {
        var f = await Merged(); var r = new Routed(f);
        var now = f.Now;
        await r.Release(new OctopusReleaseCreated("Releases-2", "1.0.2", Sha('a')));
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Started, null));
        Assert.Equal(now, f.State.Releases["1.0.2"].Assembled);
        Assert.Equal(now, f.State.DeploymentOf("1.0.2", "tdd")!.StartedAt);

        f.Now = now.AddMinutes(3);
        await r.Deploy(TddDeployment(OctopusDeploymentPhase.Succeeded, null));
        Assert.Equal(now.AddMinutes(3), f.State.DeploymentOf("1.0.2", "tdd")!.FinishedAt);

        var inputs = new DeliveryTrainRouter(f.Policies, () => f.Now).Route(new SignOffRequested("I-2", "D", "T", "Releases-2", "1.0.2", "Uat", Sha('a')));
        Assert.All(inputs, i => Assert.Equal(f.Now, Assert.IsType<TrainSignOffAsked>(i).At));
    }

    [Fact]
    public void AFutureAt_IsClampedToTheClock_AndAPastAtIsKept()
    {
        var f = new TrainFixture();
        var router = new DeliveryTrainRouter(f.Policies, () => T0);

        var future = T0.AddMinutes(7);
        Assert.All(router.Route(new OctopusReleaseCreated("R", "1.0.2", null, future)), i => Assert.Equal(T0, Assert.IsType<TrainReleaseSeen>(i).At));
        Assert.All(router.Route(TddDeployment(OctopusDeploymentPhase.Succeeded, future)), i => Assert.Equal(T0, Assert.IsType<TrainDeploymentSeen>(i).At));
        Assert.All(router.Route(new SignOffRequested("I", "D", "T", "R", "1.0.2", "Uat", null, future)), i => Assert.Equal(T0, Assert.IsType<TrainSignOffAsked>(i).At));

        var past = T0.AddHours(-2);
        Assert.All(router.Route(new OctopusReleaseCreated("R", "1.0.2", null, past)), i => Assert.Equal(past, Assert.IsType<TrainReleaseSeen>(i).At));
        Assert.All(router.Route(TddDeployment(OctopusDeploymentPhase.Succeeded, past)), i => Assert.Equal(past, Assert.IsType<TrainDeploymentSeen>(i).At));
        Assert.All(router.Route(new SignOffRequested("I", "D", "T", "R", "1.0.2", "Uat", null, past)), i => Assert.Equal(past, Assert.IsType<TrainSignOffAsked>(i).At));

        // an At equal to the clock is itself
        Assert.All(router.Route(TddDeployment(OctopusDeploymentPhase.Started, T0)), i => Assert.Equal(T0, Assert.IsType<TrainDeploymentSeen>(i).At));
    }
}
