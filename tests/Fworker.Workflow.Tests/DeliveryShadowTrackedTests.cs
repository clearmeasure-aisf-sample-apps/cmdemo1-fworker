using System.Text.Json.Nodes;
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
/// W5.2 on a Wolverine tracked session over SQL Server: the delivery train fed by the router as in the shadow run, the only handlers of its outputs the shadow's recording ones
/// (DeliveryShadowHandlers; nothing in the host can execute a PromoteRelease or an AnswerSignOff), v1's pass given to the shadow, and the comparison stored in the Wolverine database.
/// They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class DeliveryShadowTrackedTests
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

    // the shadow's handlers, called as the gateway's static ones are
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

    static Func<string, Action<WolverineOptions>> Wire(TrainFixture fx, Action<DeliveryShadow> made) => cs => w =>
    {
        fx.Reads.Clock = () => DateTimeOffset.UtcNow;
        var shadow = new DeliveryShadow(fx.Policies, new SqlShadowComparisonStore(() => Open(cs)));
        made(shadow);
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
        // the only handlers of the train's outputs: the shadow's recording ones
        w.AddMessageHandler(new RecordHandler<TrainDecided>(m => DeliveryShadowHandlers.Handle(m, shadow)));
        w.AddMessageHandler(new RecordHandler<PromoteRelease>(m => DeliveryShadowHandlers.Handle(m, shadow)));
        w.AddMessageHandler(new RecordHandler<AnswerSignOff>(m => DeliveryShadowHandlers.Handle(m, shadow)));
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

    static JsonArray V1(object action) => new(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(action)));

    static async Task NoDeadLetters(TrackedSessionHost h)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(h.ConnectionString); await c.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT message_type, exception_type, LEFT(exception_message, 300) FROM wolverine.wolverine_dead_letters", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await r.ReadAsync()) rows.Add($"{r.GetValue(0)} {r.GetValue(1)} {r.GetValue(2)}");
        Assert.True(rows.Count == 0, string.Join(" // ", rows.Distinct()));
    }

    static long Recorded(DeliveryShadow s) => (long)s.Status()["v2"]!["commandsRecorded"]!;

    [SqlFact]
    public async Task TheTrainsPromotion_IsRecordedNotExecuted_ComparedWithV1_AndStored()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); DeliveryShadow? shadow = null;
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, s => shadow = s));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));
        // the timers lead to the train's decision to promote; the shadow records the command and nothing else happens
        Assert.True(await Until(() => Task.FromResult(Recorded(shadow!) >= 1)), "the train did not decide to promote");
        // v1's pass did the same
        shadow!.ObserveV1(V1(new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat", task = "ServerTasks-9", items = new[] { 7 } }));
        var store = new SqlShadowComparisonStore(() => Open(h.ConnectionString));
        // (the train's decision is a message beside the command that was recorded: the comparison is ticked until it has both sides)
        IReadOnlyList<ShadowComparisonRow> rows = [];
        Assert.True(await Until(async () => { await shadow.Tick(default); rows = await store.Recent(20, null, default); return rows.Any(r => r.Release == "1.0.2" && r.V2Action == "promote"); }), "the comparison of the promotion was not stored");
        var promote = Assert.Single(rows, r => r.Release == "1.0.2" && r.V2Action == "promote");
        Assert.Equal(ShadowOutcomes.Match, promote.Outcome);
        Assert.Equal("promoted", promote.V1!["native"]!.ToString());
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Release = '1.0.2' AND Outcome = 'match'"));
        var summary = await store.Summary(default);
        Assert.True(summary.Counts[ShadowOutcomes.Match] >= 1);
        Assert.True(summary.Releases >= 1);
        // the honest gate's inputs, read back from the database: the live outcomes and the sides seen of the release
        Assert.True(summary.LiveCounts![ShadowOutcomes.Match] >= 1);
        var seen = Assert.Single(summary.LiveSeen!, x => x.Release == "1.0.2");
        Assert.True(seen.V1Seen && seen.V2Seen && seen.Compared && !seen.Differs);
        Assert.Equal("promote", seen.Class);
        await NoDeadLetters(h);
        var report = await shadow.Report(10, null, default);
        var gate = report["summary"]!["gate"]!;
        Assert.Equal(1, (int)gate["comparedReleases"]!);   // one release compared is no gate: it needs 3
        Assert.False((bool)gate["met"]!);
        Assert.Equal("promote", (string)gate["outcomeClasses"]![0]!["class"]!);
        Assert.Equal("PromoteRelease", (string)report["recentCommands"]![0]!["command"]!);
        Assert.False((bool)report["recentCommands"]![0]!["executed"]!);
    }

    /// <summary>The events of the watcher's replay as it publishes them (Watcher.Poll): every release first, every deployment after, Started then Succeeded, an old release deployed to uat among newer ones.</summary>
    [SqlFact]
    public async Task AReplayShapedLikeTdds_IsAppliedWithoutDecisions_JudgedOnce_AndItsRowsAreFlaggedAndNotCounted()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); DeliveryShadow? shadow = null;
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, s => shadow = s));
        await Baseline(h);
        char[] commits = ['a', 'b', 'e', 'f', 'g'];
        fx.Range('d', 'a'); fx.Range('d', 'a', 'b'); fx.Range('d', 'a', 'b', 'e'); fx.Range('e', 'f'); fx.Range('e', 'f', 'g');
        fx.Reads.Running["tdd"] = "1.0.6";
        var decided0 = (long)shadow!.Status()["v2"]!["decisions"]!; var reads0 = fx.Reads.Probes + fx.Reads.Compares;   // (the baseline is not part of the replay)
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
        Assert.Equal(decided0, (long)shadow!.Status()["v2"]!["decisions"]!);   // nothing is decided on the partial state
        Assert.Equal(reads0, fx.Reads.Probes + fx.Reads.Compares);
        await h.TrackSend(new OctopusReplayComplete());
        Assert.True(await Until(() => Task.FromResult((long)shadow.Status()["v2"]!["decisions"]! > decided0)), "the train did not judge its state at the end of the replay");
        // v1's pass says the same of the newest release; the pair is stored at once and flagged
        shadow.ObserveV1(V1(new { item = 6, action = "promotion-waiting", release = "1.0.6", environment = "Uat", items = new[] { 6 }, reasons = new[] { "the tdd app has not been Healthy long enough yet" } }));
        await shadow.Tick(default);
        Assert.True(await h.Count("SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Replay = 1") >= 1);
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Replay = 0 AND V2Action <> ''"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.DeliveryShadowComparisons WHERE Reason LIKE '%no deployment to%'"));
        var report = await shadow.Report(20, null, default);
        Assert.Equal(0, (int)report["summary"]!["gate"]!["releases"]!);
        Assert.False((bool)report["summary"]!["gate"]!["met"]!);
        Assert.True((int)report["summary"]!["replay"]!["comparisons"]! >= 1);
        await NoDeadLetters(h);
    }

    [SqlFact]
    public async Task AReadThatFails_LosesNoInput_TheMessageIsHandled_AndNothingGoesToTheDeadLetters()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); DeliveryShadow? shadow = null;
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, s => shadow = s));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        fx.Reads.Fail = new HttpRequestException("the read budget is low: the shadow run does not read");
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));   // judged: the read fails
        Assert.True(await h.Count("SELECT COUNT(*) FROM factory.DeliveryTrains WHERE [Key] = 'orders/uat' AND StateJson LIKE '%\"1.0.2\"%' AND StateJson LIKE '%Succeeded%'") == 1, "the input was lost with the failed read");
        await NoDeadLetters(h);
        Assert.NotNull(shadow);
    }

    [SqlFact]
    public async Task ASignOff_IsRecordedAsAnswered_AndComparedWithV1sAnswer()
    {
        if (!TrackedSessionHost.Available) return;
        var fx = Fixture(); DeliveryShadow? shadow = null;
        await using var h = await TrackedSessionHost.StartAsync(Wire(fx, s => shadow = s));
        await Baseline(h);
        fx.Range('d', 'a'); fx.Reads.Running["tdd"] = "1.0.2";
        await h.TrackSend(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
        await h.TrackSend(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));
        Assert.True(await Until(() => Task.FromResult(Recorded(shadow!) >= 1)));
        await h.TrackSend(new OctopusDeploymentChanged("D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", OctopusDeploymentPhase.Started, Sha('a')));
        await h.TrackSend(new SignOffRequested("I-2", "D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", Sha('a')));
        Assert.True(await Until(() => Task.FromResult(Recorded(shadow!) >= 2), 10));
        shadow!.ObserveV1(V1(new { item = 7, action = "promotion-following", release = "1.0.2", environment = "Uat", task = "T-2-Uat", state = "Executing", ours = true, signoff = "Proceed", answered = 1 }));
        await shadow.Tick(default);
        var rows = await new SqlShadowComparisonStore(() => Open(h.ConnectionString)).Recent(20, null, default);
        Assert.Contains(rows, r => r.V2Action == "signoff-proceed" && r.V1Action == "signoff-proceed" && r.Outcome == ShadowOutcomes.Match);
        Assert.Equal(2L, Recorded(shadow));
    }
}
