using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Delivery;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.7 on a Wolverine tracked session over SQL Server: a delivery of the dependency (in another repository) releases the held item through the router's real handler, which records the delivered-to
/// row itself before it wakes the train; the merged-items table answers "merged" for a dependency that settles without a release. They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class DeliveryDependenciesTrackedTests
{
    sealed class InputHandler<T>(DeliveryTrainService svc) : MessageHandler<T> where T : TrainInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            foreach (var o in r.Messages) await context.PublishAsync(o);
            if (r.WakeAt is { } due) await context.PublishAsync(new TrainWake(m.Key, due), new DeliveryOptions { ScheduledTime = due });
        }
    }

    /// <summary>The gateway's router handler for DeliveredTo (DeliveryTrainRouting): no other handler of the message records the row here.</summary>
    sealed class DeliveredRoute(DeliveryTrainRouter router, DeliveredToTable table) : MessageHandler<DeliveredTo>
    {
        protected override async Task HandleAsync(DeliveredTo m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var o in await DeliveryTrainRouting.Handle(m, router, table, cancellation)) await context.PublishAsync(o);
        }
    }

    sealed class CaptureHandler<T>(List<T> seen) : MessageHandler<T>
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) { lock (seen) seen.Add(m); return Task.CompletedTask; }
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static TrainFixture Fixture() => new(dependencies: true, web: true, window: new HealthWindow(0.01, 0.02), minReadGapSeconds: 0, pollSeconds: 1);

    static Func<string, Action<WolverineOptions>> Wire(TrainFixture fx, List<PromoteRelease> promotes, List<TrainDecided> decisions) => cs => w =>
    {
        fx.Reads.Clock = () => DateTimeOffset.UtcNow;
        var delivered = new DeliveredToTable(() => Open(cs));
        var svc = new DeliveryTrainService(new SqlTrainStore(() => Open(cs)), fx.Policies, fx.Reads, delivered);
        w.AddMessageHandler(new InputHandler<TrainCandidateAdded>(svc));
        w.AddMessageHandler(new InputHandler<TrainReleaseSeen>(svc));
        w.AddMessageHandler(new InputHandler<TrainDeploymentSeen>(svc));
        w.AddMessageHandler(new InputHandler<TrainWake>(svc));
        w.AddMessageHandler(new InputHandler<TrainDependencyDelivered>(svc));
        w.AddMessageHandler(new DeliveredRoute(new DeliveryTrainRouter(fx.Policies), delivered));
        w.AddMessageHandler(new CaptureHandler<PromoteRelease>(promotes));
        w.AddMessageHandler(new CaptureHandler<TrainDecided>(decisions));
        foreach (var t in new[] { typeof(TrainCandidateAdded), typeof(TrainReleaseSeen), typeof(TrainDeploymentSeen), typeof(TrainWake), typeof(TrainDependencyDelivered), typeof(DeliveredTo), typeof(PromoteRelease), typeof(TrainDecided) })
            w.PublishMessage(t).ToLocalQueue("train");
        w.LocalQueue("train").UseDurableInbox().Sequential();
        w.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
    };

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    [Fact]
    public async Task ADeliveryOfTheDependency_ReleasesTheHeldItem_ThroughTheRoutersHandler_WithoutWaitingForATimer()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); var promotes = new List<PromoteRelease>(); var decisions = new List<TrainDecided>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, promotes, decisions));
        var ago = DateTimeOffset.UtcNow.AddDays(-1);
        await h.TrackSend(new TrainReleaseSeen("orders/uat", "R-1", "1.0.1", Sha('d'), ago));
        foreach (var env in new[] { "Tdd", "Uat", "Prod" })
        {
            await h.TrackSend(new TrainDeploymentSeen("orders/uat", $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Started", Sha('d'), ago));
            await h.TrackSend(new TrainDeploymentSeen("orders/uat", $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Succeeded", Sha('d'), ago));
        }
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        var dependency = ItemId.Parse("acme/web#3");
        await h.TrackSend(new TrainCandidateAdded("orders/uat", new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [dependency], "webhook")));
        await h.TrackSend(new TrainReleaseSeen("orders/uat", "R-2", "1.0.2", Sha('a'), DateTimeOffset.UtcNow));
        await h.TrackSend(new TrainDeploymentSeen("orders/uat", "D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", "Started", Sha('a'), DateTimeOffset.UtcNow));
        await h.TrackSend(new TrainDeploymentSeen("orders/uat", "D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", "Succeeded", Sha('a'), DateTimeOffset.UtcNow));

        // held: the decision names the dependency; the window is long full, yet nothing is asked for
        Assert.True(await Until(() => Task.FromResult(decisions.ToArray().Any(d => d.Key == "orders/uat" && d.Action == "waiting" && d.Reasons.Any(r => r.Contains("depends on acme/web#3")))), 10));
        await Task.Delay(3000);
        Assert.Empty(promotes.ToArray());

        // the web train delivered #3 to uat (its output, DeliveredTo): the router's handler records the row and wakes the train, which promotes at once (the safety timer is ten minutes)
        await h.TrackSend(new DeliveredTo(dependency, "uat", "2.0.1"));
        Assert.True(await Until(() => Task.FromResult(promotes.ToArray().Length >= 1), 15), "the delivery did not release the item");
        Assert.Equal(("orders", "1.0.2", "uat"), (promotes[0].Deployable, promotes[0].Version, promotes[0].Environment));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo WHERE ItemKey = 'acme/web#3' AND Environment = 'uat'"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task ADeliveryNobodyWaitsFor_IsNotRecordedByTheRoutersHandler_WhereTheRuleIsOff()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = new TrainFixture(dependencies: false, web: true, window: new HealthWindow(0.01, 0.02), minReadGapSeconds: 0, pollSeconds: 1);
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, [], []));
        await h.TrackSend(new DeliveredTo(ItemId.Parse("acme/web#3"), "uat", "2.0.1"));
        // (the gateway's own DeliveredToHandler records the row; the router's handler does nothing where no train is woken)
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo"));
    }

    [Fact]
    public async Task TheMergedItemsTable_AnswersWhichDependenciesAreNotMerged()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync(Wire(Fixture(), [], []));
        var store = new SqlMergedItemStore(() => Open(h.ConnectionString));
        var row = new MergedItem { ItemKey = "acme/docs#4", Commit = Sha('b'), PullRequest = 40, Source = "webhook", MergedAt = DateTimeOffset.UtcNow, MessageId = Guid.NewGuid(), GatesJson = "{}", DependenciesJson = "[]" };
        await store.TryRecordAll([row], default);
        // a second merge commit of the same item (a reopened item that merged again) is still one merged item
        await store.TryRecordAll([new MergedItem { ItemKey = "acme/docs#4", Commit = Sha('c'), PullRequest = 41, Source = "poll", MergedAt = DateTimeOffset.UtcNow, MessageId = Guid.NewGuid(), GatesJson = "{}", DependenciesJson = "[]" }], default);
        var table = new DeliveredToTable(() => Open(h.ConnectionString));
        Assert.Equal([ItemId.Parse("acme/docs#5")], await table.NotYetMerged([ItemId.Parse("acme/docs#4"), ItemId.Parse("acme/docs#5")]));
        Assert.Empty(await table.NotYetMerged([ItemId.Parse("acme/docs#4")]));
        Assert.Empty(await table.NotYetMerged([]));
    }
}
