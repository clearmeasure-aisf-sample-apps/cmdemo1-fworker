using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>W5.2: the classification of the shadow comparison, the pairing of v1's pass with the train's decisions, the recording of the train's commands, and the report.</summary>
public class DeliveryShadowTests
{
    static ShadowDecision D(ShadowSide side, string action, string release = "1.0.2", string native = "", string[]? reasons = null, DateTimeOffset? at = null)
        => new(side, "orders", "uat", release, action, native.Length > 0 ? native : action, reasons ?? [], [], at ?? T0);

    // ---- classification -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameAction_IsAMatch_AndAWaitIsAMatchWhateverItsWords()
    {
        Assert.Equal((ShadowOutcomes.Match, ""), ShadowClassifier.Classify(D(ShadowSide.V1, "promote", native: "promoted"), D(ShadowSide.V2, "promote")));
        Assert.Equal(ShadowOutcomes.Match, ShadowClassifier.Classify(D(ShadowSide.V1, "wait", reasons: ["it has no deployment to tdd"]), D(ShadowSide.V2, "wait", reasons: ["no tdd deployment"])).Outcome);
    }

    [Fact]
    public void DifferentActions_Differ_AndTheReasonNamesBothSidesAndTheirWhy()
    {
        var (outcome, reason) = ShadowClassifier.Classify(D(ShadowSide.V1, "promote", native: "promoted"), D(ShadowSide.V2, "wait", native: "waiting", reasons: ["the uat app has not been Healthy long enough"]));
        Assert.Equal(ShadowOutcomes.Differs, outcome);
        Assert.Equal("v1 decided promote (promoted), v2 decided wait (waiting) [v2: the uat app has not been Healthy long enough]", reason);
    }

    [Fact]
    public void OneSide_IsV1Only_OrV2Only()
    {
        Assert.Equal(ShadowOutcomes.V1Only, ShadowClassifier.Classify(D(ShadowSide.V1, "promote"), null).Outcome);
        Assert.Equal(ShadowOutcomes.V2Only, ShadowClassifier.Classify(null, D(ShadowSide.V2, "promote")).Outcome);
        Assert.Throws<ArgumentException>(() => ShadowClassifier.Classify(null, null));
    }

    [Theory]
    [InlineData("promoted", null, "promote")]
    [InlineData("promotion-waiting", null, "wait")]
    [InlineData("promotion-following", null, "follow")]
    [InlineData("promotion-following", "Proceed", "signoff-proceed")]
    [InlineData("promotion-following", "Abort", "signoff-abort")]
    [InlineData("promotion-following", "waiting", "signoff-wait")]
    [InlineData("promotion-error", null, null)]
    [InlineData("promotion-deferred", null, null)]
    public void V1sActions_AreNormalized(string action, string? signoff, string? expected) => Assert.Equal(expected, ShadowActions.OfV1(action, signoff));

    [Theory]
    [InlineData("promote", "promote")]
    [InlineData("waiting", "wait")]
    [InlineData("following", "follow")]
    [InlineData("signoff-waiting", "signoff-wait")]
    [InlineData("signoff-proceed", "signoff-proceed")]
    [InlineData("signoff-abort", "signoff-abort")]
    [InlineData("idle", null)]
    [InlineData("delivered", null)]
    [InlineData("held", null)]
    [InlineData("observing", null)]
    public void TheTrainsActions_AreNormalized(string action, string? expected) => Assert.Equal(expected, ShadowActions.OfTrain(action));

    // ---- pairing -------------------------------------------------------------------------------------------------------------------------------------------

    static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    [Fact]
    public void AnAgreement_IsReportedAtOnce_ADifferenceOnlyAfterTheGrace()
    {
        var c = new ShadowComparer(Grace);
        c.Observe(D(ShadowSide.V2, "promote"));
        Assert.Empty(c.Compare(T0.AddSeconds(10)));   // v1 has not had its pass yet
        c.Observe(D(ShadowSide.V1, "wait", at: T0.AddSeconds(20)));
        Assert.Empty(c.Compare(T0.AddSeconds(30)));   // they differ now, but v1 may still catch up
        c.Observe(D(ShadowSide.V1, "promote", native: "promoted", at: T0.AddSeconds(60)));
        var r = Assert.Single(c.Compare(T0.AddSeconds(61)));
        Assert.Equal(ShadowOutcomes.Match, r.Outcome);
        Assert.Empty(c.Compare(T0.AddMinutes(30)));   // reported once
    }

    [Fact]
    public void ADifference_ThatDoesNotHeal_IsReportedAfterTheGrace()
    {
        var c = new ShadowComparer(Grace);
        c.Observe(D(ShadowSide.V2, "promote", at: T0));
        c.Observe(D(ShadowSide.V1, "wait", native: "promotion-waiting", at: T0.AddSeconds(30)));
        Assert.Empty(c.Compare(T0.AddMinutes(4)));
        var r = Assert.Single(c.Compare(T0.AddMinutes(6)));
        Assert.Equal(ShadowOutcomes.Differs, r.Outcome);
        Assert.Equal(("orders", "uat", "1.0.2"), (r.Deployable, r.Environment, r.Release));
    }

    [Fact]
    public void AnActionNobodyAnswered_IsAFindingAfterTheGrace_AWaitingIsNot()
    {
        var c = new ShadowComparer(Grace);
        c.Observe(D(ShadowSide.V2, "promote", release: "1.0.2"));
        c.Observe(D(ShadowSide.V1, "wait", release: "1.0.3"));
        c.Observe(D(ShadowSide.V2, "wait", release: "1.0.4"));
        Assert.Empty(c.Compare(T0.AddMinutes(1)));
        var late = c.Compare(T0.AddMinutes(6));
        var r = Assert.Single(late);
        Assert.Equal((ShadowOutcomes.V2Only, "1.0.2"), (r.Outcome, r.Release));
        c.Observe(D(ShadowSide.V1, "promote", release: "1.0.5", native: "promoted", at: T0.AddMinutes(7)));
        Assert.Equal(ShadowOutcomes.V1Only, Assert.Single(c.Compare(T0.AddMinutes(13))).Outcome);
    }

    [Fact]
    public void V1RepeatingItself_IsNoNews_ButANewReasonOrActionIs()
    {
        var c = new ShadowComparer(Grace);
        Assert.True(c.Observe(D(ShadowSide.V1, "wait", reasons: ["a"])));
        Assert.False(c.Observe(D(ShadowSide.V1, "wait", reasons: ["a"], at: T0.AddMinutes(1))));
        Assert.True(c.Observe(D(ShadowSide.V1, "wait", reasons: ["b"], at: T0.AddMinutes(2))));
        Assert.True(c.Observe(D(ShadowSide.V1, "promote", at: T0.AddMinutes(3))));
    }

    // ---- the service ---------------------------------------------------------------------------------------------------------------------------------------

    sealed class Rig
    {
        public readonly TrainFixture Fx = new();
        public readonly InMemoryShadowComparisonStore Store = new();
        public readonly DeliveryShadow Shadow;
        public DateTimeOffset Now = T0;
        public Rig() => Shadow = new DeliveryShadow(Fx.Policies, Store, () => Now, Grace);
    }

    static JsonArray V1(params object[] actions) => new(actions.Select(a => (JsonNode?)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(a))).ToArray());

    [Fact]
    public async Task V1sPass_IsReadFromItsActions_ByOctopusEnvironmentName()
    {
        var r = new Rig();
        r.Shadow.ObserveV1(V1(
            new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat", task = "ServerTasks-1", items = new[] { 7, 8 } },
            new { item = 7, action = "promotion-waiting", release = "1.0.3", environment = "Uat", items = new[] { 9 }, reasons = new[] { "it has no deployment to Tdd" } },
            new { item = 7, action = "promotion-waiting", release = "1.0.4", reason = "release-not-in-octopus" },          // no environment: nothing to compare
            new { item = 7, action = "promotion-error", release = "1.0.5", environment = "Uat" },
            new { item = 7, action = "promoted", release = "1.0.6", environment = "Staging" }));                          // an environment of no chain
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.2", ["acme/orders#7", "acme/orders#8"], [], T0));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.3", ["acme/orders#9"], ["no tdd deployment"], T0));
        r.Now = T0.AddSeconds(5);
        await r.Shadow.Tick(default);
        var rows = r.Store.All.OrderBy(x => x.Release).ToList();
        Assert.Equal(["1.0.2", "1.0.3"], rows.Select(x => x.Release));
        Assert.All(rows, x => Assert.Equal(ShadowOutcomes.Match, x.Outcome));
        Assert.Equal("promoted", rows[0].V1!["native"]!.ToString());
        Assert.Equal("promote", rows[0].V2Action);
    }

    [Fact]
    public async Task ATrainRun_FeedsTheShadow_AndAgreesWithV1_OnThePromotion()
    {
        var r = new Rig(); var f = r.Fx;
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Mature();
        Feed(r, f);
        Assert.Single(f.Of<PromoteRelease>());
        r.Now = f.Now;
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat", task = "ServerTasks-9", items = new[] { 7 } }));
        r.Now = f.Now.AddSeconds(30);
        await r.Shadow.Tick(default);
        var row = Assert.Single(r.Store.All, x => x.Release == "1.0.2" && x.V2Action == "promote");
        Assert.Equal(ShadowOutcomes.Match, row.Outcome);
        var status = r.Shadow.Status();
        Assert.Equal(1L, (long)status["v2"]!["commandsRecorded"]!);
        Assert.Equal(0L, (long)status["v2"]!["commandsExecuted"]!);
    }

    [Fact]
    public async Task WhenV1WaitsAndTheTrainPromotes_TheReportSaysDiffers_WithBothReasons()
    {
        var r = new Rig(); var f = r.Fx;
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Mature();
        Feed(r, f);
        r.Now = f.Now;
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.2", environment = "Uat", items = new[] { 7 }, reasons = new[] { "the tdd app has not been Healthy long enough yet" } }));
        r.Now = f.Now.AddMinutes(6);
        await r.Shadow.Tick(default);
        var row = Assert.Single(r.Store.All, x => x.Outcome == ShadowOutcomes.Differs);
        Assert.Contains("v1 decided wait (promotion-waiting)", row.Reason);
        Assert.Contains("v2 decided promote (promote)", row.Reason);
        Assert.Contains("[v1: the tdd app has not been Healthy long enough yet]", row.Reason);
    }

    static void Feed(Rig r, TrainFixture f)
    {
        foreach (var m in f.All)
            switch (m)
            {
                case TrainDecided d: r.Shadow.ObserveV2(d); break;
                case PromoteRelease p: r.Shadow.RecordCommand(p); break;
                case AnswerSignOff a: r.Shadow.RecordCommand(a); break;
            }
    }

    [Fact]
    public async Task ACommandOfTheTrain_IsRecorded_NeverExecuted_AndReportedAsSuch()
    {
        var r = new Rig();
        DeliveryShadowHandlers.Handle(new PromoteRelease("PromoteRelease|orders|1.0.2|uat", "orders", "orders-project", "1.0.2", "uat"), r.Shadow);
        DeliveryShadowHandlers.Handle(new AnswerSignOff("AnswerSignOff|orders|I-1", "orders", "I-1", "T-1", true, "ok"), r.Shadow);
        var report = await r.Shadow.Report(10, null, default);
        var cmds = (JsonArray)report["recentCommands"]!;
        Assert.Equal(["AnswerSignOff", "PromoteRelease"], cmds.Select(c => (string)c!["command"]!));
        Assert.All(cmds, c => Assert.False((bool)c!["executed"]!));
        Assert.Equal(2L, (long)report["live"]!["v2"]!["commandsRecorded"]!);
        Assert.Equal(0L, (long)report["live"]!["v2"]!["commandsExecuted"]!);
    }

    [Fact]
    public async Task AStoreThatIsDown_KeepsTheComparisons_ForTheNextTick()
    {
        var r = new Rig();
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.2", [], [], T0));
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat" }));
        r.Store.Fail = true;
        await r.Shadow.Tick(default);
        Assert.Empty(r.Store.All);
        Assert.Equal(1L, (long)r.Shadow.Status()["storeFailures"]!);
        Assert.Equal(1, (int)r.Shadow.Status()["unsaved"]!);
        r.Store.Fail = false;
        await r.Shadow.Tick(default);
        Assert.Single(r.Store.All);
        Assert.Equal(0, (int)r.Shadow.Status()["unsaved"]!);
    }

    [Fact]
    public async Task TheGate_IsMetAfterFourHours_OrThreeReleases()
    {
        var r = new Rig();
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.2", [], ["x"], T0));
        Assert.False((bool)r.Shadow.Status()["gate"]!["met"]!);
        r.Now = T0.AddHours(4).AddMinutes(1);
        Assert.True((bool)r.Shadow.Status()["gate"]!["met"]!);
        var s = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) s.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], ["x"], T0));
        var report = await s.Shadow.Report(10, null, default);
        Assert.True((bool)report["live"]!["gate"]!["met"]!);
        Assert.Equal(3, (int)report["live"]!["gate"]!["releases"]!);
    }

    [Fact]
    public async Task TheReport_ListsTheNewestFirst_AndNarrowsToAnOutcome()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3" })
        {
            r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", v, [], [], T0));
            r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release = v, environment = "Uat" }));
        }
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.4", [], [], T0));
        r.Now = T0.AddMinutes(10);
        await r.Shadow.Tick(default);
        var all = await r.Shadow.Report(10, null, default);
        Assert.Equal(3, (int)all["summary"]!["total"]!);
        Assert.Equal(2, (int)all["summary"]!["counts"]!["match"]!);
        Assert.Equal(1, (int)all["summary"]!["counts"]!["v2-only"]!);
        Assert.Equal("1.0.4", (string)all["comparisons"]![0]!["release"]!);
        var narrow = await r.Shadow.Report(10, ShadowOutcomes.V2Only, default);
        Assert.Single((JsonArray)narrow["comparisons"]!);
    }
}
