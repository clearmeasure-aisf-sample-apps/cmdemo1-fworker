using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10i: the delivery train's executor records what v1's promotion reads of a deployment the gateway made: <c>merge.promoted.&lt;environment&gt;</c> on the items of the release when PromoteRelease has made (or found) the
/// deployment, and the since-when of a Sign-off the train waits at, on the release's first item.
/// </summary>
public sealed class PromotionExecutorTests
{
    static readonly RepoId Repo = new("o", "r");

    sealed class Octopus(Func<HttpRequestMessage, (int, string)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    sealed class Rig
    {
        public readonly StateProjectionTests.MemoryJournal Journal = new() { CommentId = 1000 };
        public readonly DeliveryExecutorService Executor;
        public readonly PromotionStateWriter Writer;
        public readonly List<string> Log = [];

        public Rig(Func<HttpRequestMessage, (int, string)> answer)
        {
            Journal.State = new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = 1, ["merge"] = new JsonObject { ["sha"] = "abc", ["awaiting"] = "release-queue", ["release"] = "1.0.5" } };
            var versions = new InMemoryProjectionVersions();
            Writer = new PromotionStateWriter(new StateCommentProjection(Journal, versions, new ProjectionEcho(versions)), Repo, Log.Add);
            var promoter = new OctopusPromoter(new HttpClient(new Octopus(answer)) { BaseAddress = new Uri("https://octopus.example/") }, new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string>(), () => "k"));
            var ledger = new InMemoryIdempotencyLedger();
            Executor = new DeliveryExecutorService(new PromoteReleaseHandler(ledger, promoter), new AnswerSignOffHandler(ledger, promoter), Writer);
        }
    }

    static (int, string) Created(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/api/Spaces-1/projects/orders-ui" => (200, """{"Id":"Projects-1"}"""),
        "/api/Spaces-1/projects/Projects-1/releases/1.0.5" => (200, """{"Id":"R5"}"""),
        "/api/Spaces-1/environments" => (200, """{"Items":[]}"""),
        "/api/Spaces-1/releases/R5/deployments" => (200, """{"Items":[]}"""),
        "/api/Spaces-1/deployments/create/untenanted/v1" => (200, """{"DeploymentServerTasks":[{"ServerTaskId":"ServerTasks-41"}]}"""),
        _ => (404, "{}"),
    };

    static readonly PromoteRelease Cmd = new("PromoteRelease|orders|1.0.5|uat|1", "orders", "orders-ui", "1.0.5", "uat", ["o/r#7"]);

    [Fact]
    public async Task ADeploymentTheTrainMade_IsRecordedOnTheItemsOfTheRelease_BeforeTheCommandIsReportedFinished()
    {
        var rig = new Rig(Created);
        var messages = await rig.Executor.PromoteMessages(Cmd, 1, CancellationToken.None);
        Assert.True(Assert.IsType<DeliveryCommandFinished>(Assert.Single(messages)).Ok);
        var record = rig.Journal.State["merge"]!["promoted"]!["uat"]!;
        Assert.Equal(("ServerTasks-41", "1.0.5"), (record["task"]!.ToString(), record["release"]!.ToString()));
        Assert.Equal("release-queue", rig.Journal.State["merge"]!["awaiting"]!.ToString());   // v1's own record is left as it is
        // the command's retry (the ledger answers it) writes nothing again
        var saves = rig.Journal.Saves;
        await rig.Executor.PromoteMessages(Cmd, 1, CancellationToken.None);
        Assert.Equal(saves, rig.Journal.Saves);
    }

    [Fact]
    public async Task ARefusedPromotion_RecordsNothing_AndACommandOfAnOlderShapeNamesNoItems()
    {
        var refused = new Rig(r => r.Method == HttpMethod.Post ? (403, """{"ErrorMessage":"Missing permission: DeploymentCreate"}""") : Created(r));
        await refused.Executor.PromoteMessages(Cmd, 1, CancellationToken.None);
        Assert.Equal(0, refused.Journal.Saves);
        var old = new Rig(Created);
        await old.Executor.PromoteMessages(Cmd with { Items = null }, 1, CancellationToken.None);
        Assert.Equal(0, old.Journal.Saves);
    }

    [Fact]
    public async Task AFaultOfTheRecordIsThrown_SoTheHandlerRunsAgain_AndTheSecondRunRecords()
    {
        var rig = new Rig(Created);
        var faults = 1;
        rig.Journal.Fault = () => faults-- > 0 ? new HttpRequestException("503") : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Executor.PromoteMessages(Cmd, 1, CancellationToken.None));
        Assert.Null(rig.Journal.State["merge"]!["promoted"]);
        await rig.Executor.PromoteMessages(Cmd, 1, CancellationToken.None);   // (the ledger holds the settled command: no second deployment, only the write)
        Assert.Equal("ServerTasks-41", rig.Journal.State["merge"]!["promoted"]!["uat"]!["task"]!.ToString());
    }

    [Fact]
    public async Task ASignOffTheTrainWaitsAt_IsRecordedWithTheTaskAndSince_OnTheFirstItemOnly()
    {
        var rig = new Rig(Created);
        await rig.Executor.PromoteMessages(Cmd, 1, CancellationToken.None);
        var trains = new InMemoryTrainStore();
        var state = new TrainState { Key = "orders/uat", Deployable = "orders", Environment = "uat" };
        var since = new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero);
        state.SignOffs.Add(new TrainSignOff { InterruptionId = "Interruptions-1", TaskId = "ServerTasks-41", Version = "1.0.5", Since = since });
        await trains.Save("orders/uat", state, 0, default);
        var recorder = new SignOffWaitRecorder(trains, rig.Writer);
        var waiting = new TrainDecided("orders/uat", "signoff-waiting", "1.0.5", ["o/r#7", "o/r#9"], ["the Sign-off of task ServerTasks-41 waits: x"], since.AddMinutes(1));
        await recorder.Record(waiting, default);
        Assert.Equal(JournalJson.Iso(since.UtcDateTime), rig.Journal.State["merge"]!["promoted"]!["uat"]!["signoff_wait_since"]!.ToString());
        var saves = rig.Journal.Saves;
        await recorder.Record(waiting, default);                                                     // published again after a restart: nothing changes
        await recorder.Record(waiting with { Action = "promote" }, default);                          // another decision: not this recorder's
        await recorder.Record(waiting with { Release = "1.0.6" }, default);                           // a release the train holds no Sign-off for
        Assert.Equal(saves, rig.Journal.Saves);
    }
}
