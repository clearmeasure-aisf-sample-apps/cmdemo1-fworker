using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
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
/// W5.1 on a Wolverine tracked session over SQL Server: the train's state in the DeliveryTrains table, its timers as durable scheduled messages, the router, the delivered-to table, a
/// restart in the middle. The handlers are the pre-built ones of the harness (no runtime compiler in this project), calling the same DeliveryTrainService as the gateway's static handlers.
/// They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class DeliveryTrainTrackedTests
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

    sealed class RouteHandler<T>(Func<T, IReadOnlyList<TrainInput>> route) : MessageHandler<T>
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var i in route(m)) await context.PublishAsync(i);
        }
    }

    sealed class CaptureHandler<T>(List<T> seen) : MessageHandler<T>
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) { lock (seen) seen.Add(m); return Task.CompletedTask; }
    }

    sealed class DeliveredHandler(DeliveredToTable table) : MessageHandler<DeliveredTo>
    {
        protected override Task HandleAsync(DeliveredTo m, MessageContext context, CancellationToken cancellation) => table.Record(m, DateTimeOffset.UtcNow, cancellation);
    }

    sealed class Captured
    {
        public readonly List<PromoteRelease> Promotes = [];
        public readonly List<AnswerSignOff> Answers = [];
        public readonly List<TrainDecided> Decisions = [];
        public int Count<T>(List<T> l) { lock (l) return l.Count; }
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    /// <summary>The fixture supplies the catalog policies and the fake reads; the clock is the real one here, with a health window of about a second and a timer every second.</summary>
    static TrainFixture Fixture() => new(window: new HealthWindow(0.01, 0.02), minReadGapSeconds: 0, pollSeconds: 1);

    static Func<string, Action<WolverineOptions>> Wire(TrainFixture fx, Captured cap) => cs => w =>
    {
        fx.Reads.Clock = () => DateTimeOffset.UtcNow;
        var delivered = new DeliveredToTable(() => Open(cs));
        var svc = new DeliveryTrainService(new SqlTrainStore(() => Open(cs)), fx.Policies, fx.Reads, delivered);
        var router = new DeliveryTrainRouter(fx.Policies);
        w.AddMessageHandler(new InputHandler<TrainCandidateAdded>(svc));
        w.AddMessageHandler(new InputHandler<TrainGatesChanged>(svc));
        w.AddMessageHandler(new InputHandler<TrainReleaseSeen>(svc));
        w.AddMessageHandler(new InputHandler<TrainDeploymentSeen>(svc));
        w.AddMessageHandler(new InputHandler<TrainSignOffAsked>(svc));
        w.AddMessageHandler(new InputHandler<TrainHold>(svc));
        w.AddMessageHandler(new InputHandler<TrainWake>(svc));
        w.AddMessageHandler(new RouteHandler<ItemMerged>(router.Route));
        w.AddMessageHandler(new RouteHandler<OctopusReleaseCreated>(router.Route));
        w.AddMessageHandler(new RouteHandler<ReleaseCommitResolved>(router.Route));
        w.AddMessageHandler(new RouteHandler<OctopusDeploymentChanged>(router.Route));
        w.AddMessageHandler(new RouteHandler<SignOffRequested>(router.Route));
        w.AddMessageHandler(new CaptureHandler<PromoteRelease>(cap.Promotes));
        w.AddMessageHandler(new CaptureHandler<AnswerSignOff>(cap.Answers));
        w.AddMessageHandler(new CaptureHandler<TrainDecided>(cap.Decisions));
        w.AddMessageHandler(new DeliveredHandler(delivered));
        // pre-built handlers are routed by hand; one sequential durable queue stands for the key partitioning of the gateway
        foreach (var t in new[] { typeof(TrainCandidateAdded), typeof(TrainGatesChanged), typeof(TrainReleaseSeen), typeof(TrainDeploymentSeen), typeof(TrainSignOffAsked), typeof(TrainHold), typeof(TrainWake),
                     typeof(ItemMerged), typeof(OctopusReleaseCreated), typeof(ReleaseCommitResolved), typeof(OctopusDeploymentChanged), typeof(SignOffRequested),
                     typeof(PromoteRelease), typeof(AnswerSignOff), typeof(TrainDecided), typeof(DeliveredTo) })
            w.PublishMessage(t).ToLocalQueue("train");
        w.LocalQueue("train").UseDurableInbox().Sequential();
        w.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
    };

    static async Task<bool> Until(Func<Task<bool>> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 10; i++) { if (await cond()) return true; await Task.Delay(100); }
        return false;
    }

    static DateTimeOffset Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    /// <summary>The baseline release in every environment, through the router as the watcher's events.</summary>
    static async Task Baseline(TrackedSessionHost h)
    {
        await h.TrackSend(new TrainReleaseSeen("orders/uat", "R-1", "1.0.1", Sha('d'), Ago(1)));
        foreach (var key in new[] { "orders/tdd", "orders/uat", "orders/prod" })
        {
            await h.TrackSend(new TrainReleaseSeen(key, "R-1", "1.0.1", Sha('d'), Ago(1)));
            foreach (var env in new[] { "Tdd", "Uat", "Prod" })
            {
                await h.TrackSend(new TrainDeploymentSeen(key, $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Started", Sha('d'), Ago(1)));
                await h.TrackSend(new TrainDeploymentSeen(key, $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Succeeded", Sha('d'), Ago(1)));
            }
        }
    }

    static async Task<TrainRecord?> Row(TrackedSessionHost h, string key) => await new SqlTrainStore(() => Open(h.ConnectionString)).Load(key, default);

    [SqlFact]
    public async Task TheTimers_AreDurableScheduledMessages_AndDriveAReleaseToItsPromotion()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); var cap = new Captured();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, cap));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));
        // the evidence is not complete yet: the train asked Wolverine for a timer, which is a durable scheduled envelope, and says it waits
        Assert.True(await Until(async () => await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'") >= 1, 5));
        Assert.Equal(0, cap.Count(cap.Promotes));
        // the timer fires by itself (nothing else is sent), reads the app again and, once the window is full, asks for the deployment
        Assert.True(await Until(() => Task.FromResult(cap.Count(cap.Promotes) >= 1)), "the timers did not lead to the promotion");
        var promote = cap.Promotes[0];
        Assert.Equal(("orders", "1.0.2", "uat", "PromoteRelease|orders|1.0.2|uat|1"), (promote.Deployable, promote.Version, promote.Environment, promote.IdempotencyKey));
        // (the decision travels as a message of its own, beside the command: it is awaited, not assumed to have arrived with it)
        TrainDecided[] Decided() { lock (cap.Decisions) return [.. cap.Decisions]; }
        Assert.True(await Until(() => Task.FromResult(Decided().Any(d => d.Key == "orders/uat" && d.Action == "promote" && d.Release == "1.0.2"))),
            "the train's decision to promote did not arrive: " + string.Join(" / ", Decided().Select(d => $"{d.Key} {d.Action} {d.Release}")));
        Assert.Contains(Decided(), d => d.Key == "orders/uat" && d.Action == "waiting");
        // the tdd train delivered the item with the tdd deployment (the pipeline's); the prod train has nothing to promote (1.0.2 is not in uat yet)
        Assert.True(await Until(async () => await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo WHERE ItemKey = 'acme/orders#7' AND Environment = 'tdd'") == 1, 10));
        Assert.DoesNotContain(cap.Decisions, d => d.Key == "orders/prod" && d.Action == "promote");
        Assert.Equal(1, cap.Promotes.Count);
        var row = await Row(h, "orders/uat");
        Assert.True(row!.Revision > 5);
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [SqlFact]
    public async Task TheSignOff_IsAnsweredThroughTheHost_AndTheDeliveryLandsInTheTable_ForAllReleasesItCarries()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); var cap = new Captured();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, cap));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Range('d', 'a', 'b'); fx.Reads.Running["tdd"] = "1.0.2";
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 8), Sha('b'), 108, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));
        Assert.True(await Until(() => Task.FromResult(cap.Count(cap.Promotes) >= 1)));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new SignOffRequested("I-2", "D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", Sha('a')));
        Assert.True(await Until(() => Task.FromResult(cap.Count(cap.Answers) >= 1), 10));
        Assert.True(cap.Answers[0].Proceed);
        Assert.Equal("AnswerSignOff|orders|I-2", cap.Answers[0].IdempotencyKey);
        // Octopus then shows the deployment done: item 7 (the release's commit) is delivered to uat in 1.0.2; item 8 is in a later commit and stays a candidate
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", OctopusDeploymentPhase.Succeeded, Sha('a')));
        Assert.True(await Until(async () => await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo WHERE ItemKey = 'acme/orders#7' AND Environment = 'uat' AND Release = '1.0.2'") == 1, 10));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo WHERE ItemKey = 'acme/orders#8' AND Environment = 'uat'"));
        var state = (await Row(h, "orders/uat"))!.State;
        Assert.Contains("acme/orders#8", state.Candidates.Keys);
        Assert.DoesNotContain("acme/orders#7", state.Candidates.Keys);
    }

    [SqlFact]
    public async Task ARedeliveredFact_IsAppliedOnce_AndARestartKeepsTheTrain()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); var cap = new Captured();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, cap));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        var done = new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a'));
        await h.TrackSend(done); await h.TrackSend(done);
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.DeliveredTo WHERE ItemKey = 'acme/orders#7' AND Environment = 'tdd'"));
        var before = (await Row(h, "orders/uat"))!;
        Assert.Contains("1.0.2", before.State.Releases.Keys);
        // the process dies and a new one starts on the same database: the train is as it was, and the timers that survived go on
        await h.RestartAsync(Wire(fx, cap)(h.ConnectionString));
        var after = (await Row(h, "orders/uat"))!;
        Assert.True(after.Revision >= before.Revision);
        Assert.Equal(before.State.Releases.Keys.Order(), after.State.Releases.Keys.Order());
        await h.TrackSend(new TrainWake("orders/uat"));
        Assert.True(await Until(() => Task.FromResult(cap.Count(cap.Promotes) >= 1)), "the restarted train did not promote");
        Assert.Equal(1, cap.Promotes.Select(p => p.IdempotencyKey).Distinct().Count());
    }
}
