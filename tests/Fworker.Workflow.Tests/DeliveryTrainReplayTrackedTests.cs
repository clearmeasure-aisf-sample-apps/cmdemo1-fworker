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
/// W5.2 on a Wolverine tracked session over SQL Server: the delivery train fed by the router, its outputs only recorded (nothing in the host can execute a PromoteRelease or an AnswerSignOff): the
/// replay of Octopus's history and a read that fails, with the train's state and timers in the Wolverine database. They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class DeliveryTrainReplayTrackedTests
{
    /// <summary>The decisions the train published, as the host's handler of them sees them.</summary>
    sealed class Outputs
    {
        long decisions;
        public long Decisions => Interlocked.Read(ref decisions);
        public void Decided() => Interlocked.Increment(ref decisions);
    }

    sealed class InputHandler<T>(DeliveryTrainService svc) : MessageHandler<T> where T : TrainInput
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            var r = await svc.Handle(m, cancellation);
            foreach (var o in r.Messages) await context.PublishAsync(o);
            if (r.WakeAt is { } due) await context.PublishAsync(DeliveryTrainHandlers.WakeFor(r, m.Key)!, new DeliveryOptions { ScheduledTime = due });
        }
    }

    sealed class RouteHandler<T>(Func<T, IReadOnlyList<TrainInput>> route) : MessageHandler<T>
    {
        protected override async Task HandleAsync(T m, MessageContext context, CancellationToken cancellation)
        {
            foreach (var i in route(m)) await context.PublishAsync(i);
        }
    }

    sealed class RecordHandler<T>(Action<T> record) : MessageHandler<T>
    {
        protected override Task HandleAsync(T m, MessageContext context, CancellationToken cancellation) { record(m); return Task.CompletedTask; }
    }

    sealed class DeliveredHandler(DeliveredToTable table) : MessageHandler<DeliveredTo>
    {
        protected override Task HandleAsync(DeliveredTo m, MessageContext context, CancellationToken cancellation) => table.Record(m, DateTimeOffset.UtcNow, cancellation);
    }

    static FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);

    static TrainFixture Fixture() => new(window: new HealthWindow(0.01, 0.02), minReadGapSeconds: 0, pollSeconds: 1);

    static Func<string, Action<WolverineOptions>> Wire(TrainFixture fx, Outputs outputs) => cs => w =>
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
        w.AddMessageHandler(new InputHandler<TrainReplay>(svc));
        w.AddMessageHandler(new RouteHandler<OctopusReplayBegan>(router.Route));
        w.AddMessageHandler(new RouteHandler<OctopusReplayComplete>(router.Route));
        w.AddMessageHandler(new RouteHandler<ItemMerged>(router.Route));
        w.AddMessageHandler(new RouteHandler<OctopusReleaseCreated>(router.Route));
        w.AddMessageHandler(new RouteHandler<ReleaseCommitResolved>(router.Route));
        w.AddMessageHandler(new RouteHandler<OctopusDeploymentChanged>(router.Route));
        w.AddMessageHandler(new RouteHandler<SignOffRequested>(router.Route));
        // the only handlers of the train's outputs: the decisions are counted, the commands go nowhere
        w.AddMessageHandler(new RecordHandler<TrainDecided>(_ => outputs.Decided()));
        w.AddMessageHandler(new RecordHandler<PromoteRelease>(_ => { }));
        w.AddMessageHandler(new RecordHandler<AnswerSignOff>(_ => { }));
        w.AddMessageHandler(new DeliveredHandler(delivered));
        foreach (var t in new[] { typeof(TrainCandidateAdded), typeof(TrainGatesChanged), typeof(TrainReleaseSeen), typeof(TrainDeploymentSeen), typeof(TrainSignOffAsked), typeof(TrainHold), typeof(TrainWake), typeof(TrainReplay), typeof(OctopusReplayBegan), typeof(OctopusReplayComplete),
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

    static async Task Baseline(TrackedSessionHost h)
    {
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

    static async Task NoDeadLetters(TrackedSessionHost h)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(h.ConnectionString); await c.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT message_type, exception_type, LEFT(exception_message, 300) FROM wolverine.wolverine_dead_letters", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await r.ReadAsync()) rows.Add($"{r.GetValue(0)} {r.GetValue(1)} {r.GetValue(2)}");
        Assert.True(rows.Count == 0, string.Join(" // ", rows.Distinct()));
    }

    /// <summary>tdd 1.0.1069 and 1.0.1071: each start replayed about 120 Octopus events to every train and left one durable TrainWake per input (about 360), all due when the hold ran out. Now: one per train.</summary>
    [SqlFact]
    public async Task AReplayOfManyEvents_LeavesOneScheduledTrainWakePerTrain_NotOnePerInput()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, new Outputs()));
        await h.TrackSend(new OctopusReplayBegan());
        for (var i = 0; i < 30; i++)
        {
            var v = $"1.0.{10 + i}"; var at = Ago(1).AddMinutes(i);
            await h.TrackSend(new OctopusReleaseCreated($"R-{i}", v, Sha('a'), at));
            await h.TrackSend(new ReleaseCommitResolved($"R-{i}", v, Sha('a')));
            foreach (var env in new[] { "Tdd", "Uat" })
            {
                await h.TrackSend(new OctopusDeploymentChanged($"D-{i}-{env}", $"T-{i}-{env}", $"R-{i}", v, env, OctopusDeploymentPhase.Started, Sha('a'), at));
                await h.TrackSend(new OctopusDeploymentChanged($"D-{i}-{env}", $"T-{i}-{env}", $"R-{i}", v, env, OctopusDeploymentPhase.Succeeded, Sha('a'), at));
            }
        }
        var trains = await h.Count("SELECT COUNT(*) FROM factory.DeliveryTrains");
        Assert.True(trains >= 1);
        Assert.Equal(trains, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type LIKE '%wake%'"));
        Assert.Equal(trains, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled'"));
        await NoDeadLetters(h);
    }

    /// <summary>The events of the watcher's replay as it publishes them (Watcher.Poll): every release first, every deployment after, Started then Succeeded, an old release deployed to uat among newer ones.</summary>
    [SqlFact]
    public async Task AReplayShapedLikeTdds_IsAppliedWithoutDecisions_AndJudgedOnce()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); var outputs = new Outputs();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, outputs));
        await Baseline(h);
        char[] commits = ['a', 'b', 'e', 'f', 'g'];
        fx.Range('d', 'a'); fx.Range('d', 'a', 'b'); fx.Range('d', 'a', 'b', 'e'); fx.Range('e', 'f'); fx.Range('e', 'f', 'g');
        fx.Reads.Running["tdd"] = "1.0.6";
        var decided0 = outputs.Decisions; var reads0 = fx.Reads.Probes + fx.Reads.Compares;   // (the baseline is not part of the replay)
        await h.TrackSend(new OctopusReplayBegan());
        for (var i = 0; i < commits.Length; i++)
            await h.TrackSend(new ItemMerged(new ItemId(Repo, 2 + i), Sha(commits[i]), 102 + i, new ItemMergedGates(true, "", true, false, false, []), [], "poll"));
        for (var i = 0; i < commits.Length; i++) await h.TrackSend(new OctopusReleaseCreated($"R-{2 + i}", $"1.0.{2 + i}", Sha(commits[i])));
        for (var i = 0; i < commits.Length; i++)
        {
            await h.TrackSend(new OctopusDeploymentChanged($"D-{2 + i}-Tdd", $"T-{2 + i}-Tdd", $"R-{2 + i}", $"1.0.{2 + i}", "Tdd", OctopusDeploymentPhase.Started, Sha(commits[i])));
            await h.TrackSend(new OctopusDeploymentChanged($"D-{2 + i}-Tdd", $"T-{2 + i}-Tdd", $"R-{2 + i}", $"1.0.{2 + i}", "Tdd", OctopusDeploymentPhase.Succeeded, Sha(commits[i])));
        }
        await h.TrackSend(new OctopusDeploymentChanged("D-4-Uat", "T-4-Uat", "R-4", "1.0.4", "Uat", OctopusDeploymentPhase.Started, Sha('e')));
        await h.TrackSend(new OctopusDeploymentChanged("D-4-Uat", "T-4-Uat", "R-4", "1.0.4", "Uat", OctopusDeploymentPhase.Succeeded, Sha('e')));
        Assert.Equal(decided0, outputs.Decisions);   // nothing is decided on the partial state
        Assert.Equal(reads0, fx.Reads.Probes + fx.Reads.Compares);
        await h.TrackSend(new OctopusReplayComplete());
        Assert.True(await Until(() => Task.FromResult(outputs.Decisions > decided0)), "the train did not judge its state at the end of the replay");
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task AReadThatFails_LosesNoInput_TheMessageIsHandled_AndNothingGoesToTheDeadLetters()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture();
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, new Outputs()));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        fx.Reads.Fail = new HttpRequestException("the read budget is low: the train does not read");
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));   // judged: the read fails
        Assert.True(await h.Count("SELECT COUNT(*) FROM factory.DeliveryTrains WHERE [Key] = 'orders/uat' AND StateJson LIKE '%\"1.0.2\"%' AND StateJson LIKE '%Succeeded%'") == 1, "the input was lost with the failed read");
        await NoDeadLetters(h);
    }
}
