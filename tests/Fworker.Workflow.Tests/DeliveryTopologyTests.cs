using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using JasperFx.CodeGeneration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// tdd 1.0.831: the train decided uat 2.4.1298 "wait for started" for good, although Deployments-57454 was Success in Octopus. The watcher published, in this order, Started and Succeeded of 57454 and the
/// end of the replay; Wolverine gave each message type its own parallel queue, so the end of the replay (a cold, quick handler) reached the train before Succeeded, which was behind a backlog of
/// deployment events. These tests run the router's queue topology on a plain host (no database) and the train on the order it delivers.
/// </summary>
public class DeliveryTopologyTests
{
    sealed class Probe
    {
        public readonly List<string> Handled = [];
        public void Add(string s) { lock (Handled) Handled.Add(s); }
    }

    // a handler that takes its time as a router with a backlog does (the deployment events are many, the end marker is one)
    sealed class SlowOrQuick<T>(Probe probe, string name, int ms) : MessageHandler<T>
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            if (ms > 0) await Task.Delay(ms, cancellation);
            probe.Add(name);
        }
    }

    static IEnumerable<IngressEvent> Poll() =>
    [
        new OctopusReplayBegan(),
        new OctopusDeploymentChanged("Deployments-57450", "T-1", "R-1", "2.4.1288", "tdd", OctopusDeploymentPhase.Started, null),
        new OctopusDeploymentChanged("Deployments-57450", "T-1", "R-1", "2.4.1288", "tdd", OctopusDeploymentPhase.Failed, null),
        new OctopusDeploymentChanged("Deployments-57454", "T-2", "R-2", "2.4.1298", "tdd", OctopusDeploymentPhase.Started, null),
        new OctopusDeploymentChanged("Deployments-57454", "T-2", "R-2", "2.4.1298", "tdd", OctopusDeploymentPhase.Succeeded, null),
        new OctopusReplayComplete(),
    ];

    [Fact]
    public async Task TheWatchersEvents_AreHandledInTheOrderTheWatcherPublishedThem()
    {
        var probe = new Probe();
        using var host = await new HostBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical))
            .UseWolverine(w =>
            {
                w.ServiceName = "fworker-topology-tests";
                w.Discovery.DisableConventionalDiscovery();
                w.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                w.AddMessageHandler(new SlowOrQuick<OctopusReplayBegan>(probe, "began", 0));
                w.AddMessageHandler(new DeploymentHandler(probe));
                w.AddMessageHandler(new SlowOrQuick<OctopusReplayComplete>(probe, "complete", 0));
                DeliveryTopology.RouteOctopusEvents(w);
            }).StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        foreach (var ev in Poll()) await bus.PublishAsync(ev);
        for (var i = 0; i < 100 && probe.Handled.Count < 6; i++) await Task.Delay(100);
        Assert.Equal(["began", "Deployments-57450:Started", "Deployments-57450:Failed", "Deployments-57454:Started", "Deployments-57454:Succeeded", "complete"], probe.Handled);
    }

    sealed class DeploymentHandler(Probe probe) : MessageHandler<OctopusDeploymentChanged>
    {
        protected override async Task HandleAsync(OctopusDeploymentChanged m, MessageContext context, CancellationToken cancellation)
        {
            await Task.Delay(200, cancellation);
            probe.Add($"{m.DeploymentId}:{m.Phase}");
        }
    }

    // and the train, whatever order it hears the end of the replay in, ends up with the deployment finished
    [Theory]
    [InlineData("SDC")] [InlineData("SCD")] [InlineData("CSD")] [InlineData("CDS")] [InlineData("DCS")] [InlineData("DSC")]
    public async Task AFinishedDeployment_IsNeverStarted_InAnyOrderOfStartedSucceededAndTheEndOfTheReplay(string order)
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a');
        await f.Send(new TrainReplay(f.Key, true));
        await f.Release("1.0.2", 'a');
        foreach (var c in order)
        {
            if (c == 'S') await f.Deploy("1.0.2", "tdd", "Started");
            if (c == 'D') await f.Deploy("1.0.2", "tdd", "Succeeded");
            if (c == 'C') await f.Send(new TrainReplay(f.Key, false));
        }
        var dep = f.State.Deployments["1.0.2"]["tdd"];
        Assert.Equal("Succeeded", dep.Phase);
        Assert.True(dep.Completed);
        // the last decision about the release no longer says it waits for a deployment that has finished
        Assert.DoesNotContain(f.Of<TrainDecided>().Last(d => d.Release == "1.0.2").Reasons, r => r.Contains("still Started"));
    }
}
