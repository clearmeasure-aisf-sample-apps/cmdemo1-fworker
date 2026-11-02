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
        Assert.Equal(ShadowOutcomes.Match, ShadowClassifier.Classify(D(ShadowSide.V1, "wait", reasons: ["it has no deployment to Tdd"]), D(ShadowSide.V2, "wait", reasons: ["it has no deployment to Tdd (and more words)"])).Outcome);
    }

    // ---- defect 5: a wait is compared by what it waits for ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("its deployment to Uat ended Failed (Deployments-9); the operator deploys it again, or the item goes back to Development", "failed")]
    [InlineData("its deployment to Uat ended Canceled (Deployments-9); the operator deploys it again", "failed")]
    [InlineData("its deployment to Tdd is Failed (Deployments-9)", "failed")]
    [InlineData("it has no deployment to Tdd", "none")]
    [InlineData("its deployment to Tdd is still Executing (Deployments-9)", "started")]
    [InlineData("its deployment to Tdd is still Started (D-1.0.2-Tdd)", "started")]
    [InlineData("the tdd app does not run release 2.4.1 yet (/_build: 2.4.0)", "health")]
    [InlineData("the tdd app has not been Healthy long enough yet (two Healthy reads 5 min apart, the later 10 min after the deployment finished)", "health")]
    [InlineData("the tdd app answered Unhealthy on /h at 2026-10-28T10:02:00.000Z, after its deployment of 1 finished: the release waits for a new deployment there", "health")]
    [InlineData("no health URL for tdd (octopus.tddUrl is empty)", "health")]
    [InlineData("#7 carries the 'hold:owner' label", "gates")]
    [InlineData("commit abc1234 in the release is not the merge commit of a factory work item", "gates")]
    public void AWaitsReasonText_IsClassified(string reason, string expected) => Assert.Equal(expected, ShadowReasons.Classify([reason]));

    [Fact]
    public void AWaitWithSeveralReasons_IsClassifiedByTheFirstOfTheOrder_AndNoReasonIsGates()
    {
        Assert.Equal("none", ShadowReasons.Classify(["#7 carries the 'hold:owner' label", "it has no deployment to Tdd"]));
        Assert.Equal("failed", ShadowReasons.Classify(["it has no deployment to Tdd", "its deployment to Uat ended Failed (D)"]));
        Assert.Equal("gates", ShadowReasons.Classify([]));   // v1 lists only the common reasons: a wait for an item's gate has none
    }

    [Fact]
    public void TwoWaitsForDifferentThings_Differ_AndTheReasonNamesTheClasses()
    {
        var v1 = D(ShadowSide.V1, "wait", native: "promotion-waiting", reasons: ["its deployment to Tdd is still Started (D-1)"]);
        var v2 = D(ShadowSide.V2, "wait", native: "waiting", reasons: ["it has no deployment to Tdd"]);
        var (outcome, reason) = ShadowClassifier.Classify(v1, v2);
        Assert.Equal(ShadowOutcomes.Differs, outcome);
        Assert.Contains("v1 decided wait for started (promotion-waiting)", reason);
        Assert.Contains("v2 decided wait for none (waiting)", reason);
        // the comparer waits for the grace on such a pair, as for any difference, and then reports it
        var c = new ShadowComparer(TimeSpan.FromMinutes(5));
        c.Observe(v1); c.Observe(v2);
        Assert.Empty(c.Compare(T0.AddMinutes(1)));
        Assert.Equal(ShadowOutcomes.Differs, Assert.Single(c.Compare(T0.AddMinutes(6))).Outcome);
    }

    [Fact]
    public void AWaitForGatesAlone_V1SaysNothingOf_StillMatches()
    {
        Assert.Equal(ShadowOutcomes.Match, ShadowClassifier.Classify(D(ShadowSide.V1, "wait", native: "promotion-waiting"), D(ShadowSide.V2, "wait", reasons: ["#7 carries the 'hold:owner' label"])).Outcome);
    }

    [Fact]
    public void DifferentActions_Differ_AndTheReasonNamesBothSidesAndTheirWhy()
    {
        var (outcome, reason) = ShadowClassifier.Classify(D(ShadowSide.V1, "promote", native: "promoted"), D(ShadowSide.V2, "wait", native: "waiting", reasons: ["the uat app has not been Healthy long enough"]));
        Assert.Equal(ShadowOutcomes.Differs, outcome);
        Assert.Equal("v1 decided promote (promoted), v2 decided wait for health (waiting) [v2: the uat app has not been Healthy long enough]", reason);
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
    public void ADecisionNobodyAnswered_IsOneSidedAfterTheGrace_AWaitingToo()
    {
        var c = new ShadowComparer(Grace);
        c.Observe(D(ShadowSide.V2, "promote", release: "1.0.2"));
        c.Observe(D(ShadowSide.V1, "wait", release: "1.0.3"));
        c.Observe(D(ShadowSide.V2, "wait", release: "1.0.4"));
        Assert.Empty(c.Compare(T0.AddMinutes(1)));
        Assert.Equal(3, c.Tracked);
        var late = c.Compare(T0.AddMinutes(6)).ToDictionary(r => r.Release, r => r.Outcome);
        Assert.Equal(new Dictionary<string, string> { ["1.0.2"] = ShadowOutcomes.V2Only, ["1.0.3"] = ShadowOutcomes.V1Only, ["1.0.4"] = ShadowOutcomes.V2Only }, late);
        Assert.Equal(0, c.Tracked);   // pending means not compared yet
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
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.3", ["acme/orders#9"], ["it has no deployment to Tdd"], T0));
        r.Now = T0.AddSeconds(5);
        await r.Shadow.Tick(default);
        var rows = r.Store.All.OrderBy(x => x.Release).ToList();
        Assert.Equal(["1.0.2", "1.0.3"], rows.Select(x => x.Release));
        Assert.All(rows, x => Assert.Equal(ShadowOutcomes.Match, x.Outcome));
        Assert.Equal("promoted", rows[0].V1!["native"]!.ToString());
        Assert.Equal("promote", rows[0].V2Action);
    }

    [Fact]
    public async Task WhileTheTrainStillReplays_V1sDecisionsAreHeld_NoV1OnlyRows_UntilTheTrainDecides()
    {
        var r = new Rig();
        r.Shadow.ReplayBegan();
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.3", environment = "Uat", items = new[] { 9 }, reasons = new[] { "it has no deployment to Tdd" } }));
        r.Now = T0.AddMinutes(10);   // far past the grace
        await r.Shadow.Tick(default);
        Assert.Empty(r.Store.All);
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.3", ["acme/orders#9"], ["it has no deployment to Tdd"], r.Now, null, true));
        await r.Shadow.Tick(default);
        var row = Assert.Single(r.Store.All);
        Assert.Equal(ShadowOutcomes.Match, row.Outcome);
    }

    [Fact]
    public async Task ATrainThatDecidesNothingAfterTheReplay_ShowsAsV1OnlyRows_OnceTheHoldHasPassed()
    {
        var r = new Rig();
        r.Shadow.ReplayBegan(); r.Shadow.ReplayBegan();   // (a second call keeps the first time)
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.3", environment = "Uat", items = new[] { 9 }, reasons = new[] { "it has no deployment to Tdd" } }));
        r.Now = T0.AddMinutes(29);
        await r.Shadow.Tick(default);
        Assert.Empty(r.Store.All);
        r.Now = T0.AddMinutes(31);
        await r.Shadow.Tick(default);
        Assert.Equal(ShadowOutcomes.V1Only, Assert.Single(r.Store.All).Outcome);
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
        Assert.Contains("v1 decided wait for health (promotion-waiting)", row.Reason);
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
        DeliveryShadowHandlers.Handle(new PromoteRelease("PromoteRelease|orders|1.0.2|uat|1", "orders", "orders-project", "1.0.2", "uat"), r.Shadow);
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
    public async Task TheClock_DecidesNothing_TheHoursAreInformation()
    {
        var r = new Rig();
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.2", [], [], T0));
        r.Now = T0.AddMinutes(6);
        await r.Shadow.Tick(default);   // stored: the first stored comparison starts the hours
        Assert.False((bool)r.Shadow.Status()["gate"]!["met"]!);
        r.Now = T0.AddHours(4).AddMinutes(7);
        var gate = r.Shadow.Status()["gate"]!;
        Assert.False((bool)gate["met"]!);   // four hours and a release the train alone judged: not a gate
        Assert.True((double)gate["hours"]! >= 4);
        Assert.Null(gate["needsHours"]);
    }

    // ---- the honest gate: compared releases, and nothing live unexplained --------------------------------------------------------------------------------------------

    static async Task<JsonObject> Gate(Rig r, int minutes = 6)
    {
        r.Now = r.Now.AddMinutes(minutes);
        await r.Shadow.Tick(default);
        return (JsonObject)(await r.Shadow.Report(50, null, default))["summary"]!["gate"]!;
    }

    static void BothPromote(Rig r, string release)
    {
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", release, [], [], r.Now));
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release, environment = "Uat" }));
    }

    static JsonNode Declared(JsonObject gate, string id) => ((JsonArray)gate["declared"]!).Single(d => (string)d!["id"]! == id)!;

    [Fact]
    public async Task ThreeMatchedBothSidedReleases_MeetTheGate()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) BothPromote(r, v);
        var gate = await Gate(r);
        Assert.True((bool)gate["met"]!);
        Assert.Equal(3, (int)gate["comparedReleases"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
        Assert.Equal([0, 0, 0], new[] { (int)gate["liveDiffers"]!, (int)gate["liveV1Only"]!, (int)gate["liveV2Only"]! });
        var classes = (JsonArray)gate["outcomeClasses"]!;
        Assert.Equal(3, classes.Count);
        Assert.All(classes, c => { Assert.Equal("match", (string)c!["outcome"]!); Assert.Equal("promote", (string)c["class"]!); });
        // the status block (this process) says the same as the stored report
        Assert.Equal(gate.ToJsonString(), r.Shadow.Status()["gate"]!.ToJsonString());
        var live = (JsonObject)(await r.Shadow.Report(50, null, default))["summary"]!["live"]!;
        Assert.Equal(3, (int)live["counts"]!["match"]!);
        Assert.All((JsonArray)live["seen"]!, x => { Assert.True((bool)x!["v1"]!); Assert.True((bool)x["v2"]!); Assert.True((bool)x["compared"]!); });
    }

    [Fact]
    public async Task TwoReleasesCompared_AreNotEnough()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3" }) BothPromote(r, v);
        var gate = await Gate(r);
        Assert.False((bool)gate["met"]!);
        Assert.Equal(2, (int)gate["comparedReleases"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
    }

    [Fact]
    public async Task ThreeReleasesWhereOneDiffers_AreNotMet_AndOneIsUnexplained()
    {
        var r = new Rig();
        BothPromote(r, "1.0.2"); BothPromote(r, "1.0.3");
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.4", [], ["the uat app has not been Healthy long enough"], r.Now));
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promoted", release = "1.0.4", environment = "Uat" }));
        var gate = await Gate(r);
        Assert.False((bool)gate["met"]!);
        Assert.Equal(3, (int)gate["comparedReleases"]!);   // a difference is a comparison too
        Assert.Equal(1, (int)gate["liveDiffers"]!);
        Assert.Equal(1, (int)gate["unexplainedLive"]!);
        var classes = (JsonArray)gate["outcomeClasses"]!;
        Assert.Equal(2, classes.Count(c => (string)c!["outcome"]! == "match"));
        var differs = classes.Single(c => (string)c!["outcome"]! == "differs")!;
        Assert.Equal("1.0.4", (string)differs["release"]!);
        Assert.Equal("wait:health", (string)differs["class"]!);
    }

    [Fact]
    public async Task AV2OnlyRow_ForAReleaseNoQueuedItemNames_IsDeclared_NotCompared()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) BothPromote(r, v);
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.9", [], [], r.Now));   // v1's pass never names 1.0.9: no Release Queue item carries it
        var gate = await Gate(r);
        Assert.Equal(3, (int)gate["comparedReleases"]!);
        Assert.Equal(1, (int)gate["liveV2Only"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
        Assert.True((bool)gate["met"]!);
        var declared = Declared(gate, "queue-only-v1");
        Assert.Equal(["1.0.9"], ((JsonArray)declared["releases"]!).Select(x => (string)x!));
        Assert.False(string.IsNullOrEmpty((string)declared["reason"]!));
        Assert.DoesNotContain(((JsonArray)gate["outcomeClasses"]!), c => (string)c!["release"]! == "1.0.9");
    }

    [Fact]
    public async Task AV2OnlyRow_ForAReleaseV1Named_IsNotDeclared()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) BothPromote(r, v);
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "1.0.9", [], [], r.Now));
        r.Shadow.ObserveV1(V1(new { item = 9, action = "promotion-deferred", release = "1.0.9", reason = "sweep-budget" }));   // v1 had it in its queue and judged nothing
        var gate = await Gate(r);
        Assert.Equal(1, (int)gate["liveV2Only"]!);
        Assert.Equal(1, (int)gate["unexplainedLive"]!);
        Assert.False((bool)gate["met"]!);
        Assert.Empty((JsonArray)Declared(gate, "queue-only-v1")["releases"]!);
    }

    [Fact]
    public async Task AV2OnlyRow_OfALaterEnvironment_ForAReleaseV1JudgesInAnEarlierOne_IsDeclared_ByRule()
    {
        // tdd, 2026-11-01: v1 judges a release in the first environment it has not reached (uat) and says nothing of prod; the prod train says "it has no deployment to uat". Six such rows were "unexplained".
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" })
        {
            var why = "its deployment to Tdd ended Failed (Deployments-9)";
            r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], [why], r.Now));
            r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = v, environment = "Uat", reasons = new[] { why } }));
            r.Shadow.ObserveV2(new TrainDecided("orders/prod", "waiting", v, [], ["it has no deployment to uat"], r.Now));
        }
        var gate = await Gate(r);
        Assert.Equal(3, (int)gate["comparedReleases"]!);
        Assert.Equal(3, (int)gate["liveV2Only"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
        Assert.True((bool)gate["met"]!);
        Assert.Equal(["1.0.2", "1.0.3", "1.0.4"], ((JsonArray)Declared(gate, "chain-position")["releases"]!).Select(x => (string)x!));
        Assert.Empty((JsonArray)Declared(gate, "queue-only-v1")["releases"]!);
    }

    [Fact]
    public async Task AV2OnlyRow_OfAnEarlierEnvironment_ForAReleaseV1JudgesInALaterOne_IsNotDeclared()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) BothPromote(r, v);
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.8", environment = "Prod", reasons = new[] { "the prod gate" } }));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.8", [], ["x"], r.Now));
        var gate = await Gate(r);
        Assert.Equal(2, (int)gate["unexplainedLive"]!);   // the v1-only row of prod and the v2-only row of uat
        Assert.Empty((JsonArray)Declared(gate, "chain-position")["releases"]!);
    }

    [Fact]
    public async Task AV1Row_ForAnOlderRelease_ANewerV2DecisionSupersedes_IsDeclared()
    {
        var r = new Rig();
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.9", environment = "Uat", reasons = new[] { "it has no deployment to Tdd" } }));   // 1.0.9 is older than 1.0.10 (numbers, not text)
        foreach (var v in new[] { "1.0.10", "1.0.11", "1.0.12" }) BothPromote(r, v);
        var gate = await Gate(r);
        Assert.Equal(1, (int)gate["liveV1Only"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
        Assert.True((bool)gate["met"]!);
        Assert.Equal(["1.0.9"], ((JsonArray)Declared(gate, "newest-only")["releases"]!).Select(x => (string)x!));
    }

    [Fact]
    public async Task AV1Row_ForANewerRelease_ThanAnyV2Decided_IsNotDeclared()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) BothPromote(r, v);
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.5", environment = "Uat", reasons = new[] { "it has no deployment to Tdd" } }));
        var gate = await Gate(r);
        Assert.Equal(1, (int)gate["liveV1Only"]!);
        Assert.Equal(1, (int)gate["unexplainedLive"]!);
        Assert.False((bool)gate["met"]!);
    }

    [Fact]
    public async Task ReleasesCompared_ByFailedWaits_ShowTheirClass()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" })
        {
            var why = "its deployment to Tdd ended Failed (Deployments-9)";
            r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], [why], r.Now));
            r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = v, environment = "Uat", reasons = new[] { why } }));
        }
        var gate = await Gate(r);
        Assert.True((bool)gate["met"]!);   // compared and agreeing...
        Assert.All((JsonArray)gate["outcomeClasses"]!, c => Assert.Equal("wait:failed", (string)c!["class"]!));   // ...but the report shows that every release is in the deployment-failed class
    }

    [Theory]
    [InlineData("1.0.9", "1.0.10", -1)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.0.2", "1.0.2", 0)]
    [InlineData("1.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void ReleaseNames_AreOrderedByTheirNumbers(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(ShadowGate.CompareReleases(a, b)));

    [Fact]
    public async Task TheLiveGate_CountsWhatTheStoredGateCounts_NotWhatWasObserved()
    {
        var s = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) s.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = v, environment = "Uat" }));
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) s.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], ["#7 carries the 'hold:owner' label"], T0));
        var before = await s.Shadow.Report(10, null, default);
        Assert.Equal(3, (int)before["live"]!["pending"]!);
        Assert.Equal(0, (int)before["live"]!["gate"]!["releases"]!);   // observed, not stored: the gate has seen nothing
        Assert.False((bool)before["live"]!["gate"]!["met"]!);
        s.Now = T0.AddMinutes(6);
        await s.Shadow.Tick(default);
        var after = await s.Shadow.Report(10, null, default);
        Assert.Equal(3, (int)after["summary"]!["total"]!);
        Assert.Equal(3, (int)after["summary"]!["counts"]!["match"]!);
        Assert.Equal(0, (int)after["live"]!["pending"]!);
        Assert.Equal(3, (int)after["summary"]!["gate"]!["releases"]!);
        Assert.Equal(after["summary"]!["gate"]!.ToJsonString(), after["live"]!["gate"]!.ToJsonString());
        Assert.Equal(3, (int)s.Shadow.Status()["gate"]!["releases"]!);
        Assert.True((bool)after["live"]!["gate"]!["met"]!);
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

    // ---- defect 2: a later outcome of the train retracts what it said before ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AWaitingThatTheTrainWithdrew_IsNeverReported_IdleRetractsIt()
    {
        var r = new Rig();
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.2", [], ["the tdd app has not been Healthy long enough yet"], T0));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "idle", "1.0.2", [], [], T0.AddMinutes(1)));
        r.Now = T0.AddMinutes(10);
        await r.Shadow.Tick(default);
        Assert.Empty(r.Store.All);   // it would have been a stale v2-only row
        Assert.Equal(0, (int)r.Shadow.Status()["pending"]!);
    }

    [Fact]
    public async Task ADeliveredRelease_RetractsItselfAndTheOnesItSuperseded()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) r.Shadow.ObserveV2(new TrainDecided("orders/uat", "following", v, [], ["a deployment"], T0));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "delivered", "1.0.4", ["acme/orders#7"], ["delivered in release 1.0.4, superseding 1.0.2, 1.0.3"], T0.AddMinutes(1), ["1.0.2", "1.0.3"]));
        r.Now = T0.AddMinutes(10);
        await r.Shadow.Tick(default);
        Assert.Empty(r.Store.All);
    }

    [Fact]
    public async Task ARetractionKeepsV1sSide_SoTheReleaseDecidedAgainIsComparedWithIt()
    {
        var r = new Rig();
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.2", environment = "Uat", reasons = new[] { "it has no deployment to Tdd" } }));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.2", [], ["it has no deployment to Tdd"], T0));
        r.Now = T0.AddSeconds(5);
        await r.Shadow.Tick(default);
        Assert.Equal(ShadowOutcomes.Match, Assert.Single(r.Store.All).Outcome);
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "idle", "1.0.2", [], [], T0.AddMinutes(1)));
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = "1.0.2", environment = "Uat", reasons = new[] { "it has no deployment to Tdd" } }));   // v1 repeats itself
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.2", [], ["it has no deployment to Tdd"], T0.AddMinutes(2)));
        r.Now = T0.AddMinutes(3);
        await r.Shadow.Tick(default);
        Assert.Equal(2, r.Store.All.Count);   // reported again, against v1's decision (not as a v2-only row)
        Assert.All(r.Store.All, x => Assert.Equal(ShadowOutcomes.Match, x.Outcome));
    }

    [Fact]
    public async Task AnIdleWithoutARelease_RetractsNothing()
    {
        var r = new Rig();
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", "1.0.2", [], ["x"], T0));
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "idle", "", [], [], T0.AddMinutes(1)));
        Assert.Equal(1, (int)r.Shadow.Status()["pending"]!);
    }

    // ---- defect 6: the gate counts live decisions, not the replay -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplayRows_AreStoredAndFlagged_AndTheGateCountsOnlyLiveOnes()
    {
        var r = new Rig();
        // the replay: three old releases judged once, 40 days after the first of them
        foreach (var v in new[] { "2.4.1", "2.4.2", "2.4.3" }) r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], ["x"], T0, null, Replay: true));
        r.Now = T0.AddMinutes(6);
        await r.Shadow.Tick(default);
        Assert.Equal(3, r.Store.All.Count);
        Assert.All(r.Store.All, x => Assert.True(x.Replay));
        Assert.True((bool)r.Store.All[0].V2!["replay"]!);
        var status = r.Shadow.Status();
        Assert.Equal(0, (int)status["gate"]!["releases"]!);
        Assert.False((bool)status["gate"]!["met"]!);
        Assert.Equal(3L, (long)status["v2"]!["replayComparisons"]!);
        r.Now = T0.AddDays(2);
        Assert.False((bool)r.Shadow.Status()["gate"]!["met"]!);   // the hours run from the first live comparison, not from the replay's
        // one live decision: one release, the hours start now
        r.Shadow.ObserveV2(new TrainDecided("orders/uat", "promote", "2.4.4", [], [], r.Now));
        r.Now = r.Now.AddMinutes(6);
        await r.Shadow.Tick(default);
        Assert.Equal(1, (int)r.Shadow.Status()["gate"]!["releases"]!);
        var report = await r.Shadow.Report(10, null, default);
        Assert.Equal(4, (int)report["summary"]!["total"]!);
        Assert.Equal(3, (int)report["summary"]!["replay"]!["comparisons"]!);
        Assert.Equal(1, (int)report["summary"]!["live"]!["releases"]!);
        Assert.Equal(4, (int)report["summary"]!["releases"]!);
        Assert.Equal(1, (int)report["summary"]!["gate"]!["releases"]!);
        Assert.False((bool)report["summary"]!["gate"]!["met"]!);
        r.Now = r.Now.AddHours(4);
        var late = (await r.Shadow.Report(10, null, default))["summary"]!["gate"]!;
        Assert.False((bool)late["met"]!);   // the hours are information; and nothing live was compared with v1 (the replayed rows are no comparison at all)
        Assert.True((double)late["hours"]! >= 4);
        Assert.Equal(0, (int)late["comparedReleases"]!);
    }

    [Fact]
    public async Task ReplayedComparisons_AreNotComparedReleases_EvenWhenBothSidesAgree()
    {
        var r = new Rig();
        foreach (var v in new[] { "2.4.1", "2.4.2", "2.4.3" })
        {
            r.Shadow.ObserveV2(new TrainDecided("orders/uat", "waiting", v, [], ["x"], T0, null, Replay: true));
            r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = v, environment = "Uat" }));
        }
        var gate = await Gate(r);
        Assert.Equal(3, r.Store.All.Count(x => x.Replay && x.Outcome == ShadowOutcomes.Match));
        Assert.False((bool)gate["met"]!);
        Assert.Equal(0, (int)gate["comparedReleases"]!);
        Assert.Equal(0, (int)gate["unexplainedLive"]!);
        Assert.Empty((JsonArray)gate["outcomeClasses"]!);
        Assert.Equal(0, (int)((JsonObject)(await r.Shadow.Report(50, null, default))["summary"]!["live"]!["counts"]!)["match"]!);
    }

    [Fact]
    public async Task AV1OnlyRow_IsNoEvidenceThatTheTrainRan()
    {
        var r = new Rig();
        foreach (var v in new[] { "1.0.2", "1.0.3", "1.0.4" }) r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-waiting", release = v, environment = "Uat" }));
        r.Now = T0.AddMinutes(6);
        await r.Shadow.Tick(default);
        Assert.Equal(3, r.Store.All.Count(x => x.Outcome == ShadowOutcomes.V1Only));
        Assert.Equal(0, (int)r.Shadow.Status()["gate"]!["releases"]!);
        Assert.Equal(0, (int)(await r.Shadow.Report(10, null, default))["summary"]!["gate"]!["releases"]!);
    }

    [Fact]
    public void V1sUndecidedActions_AreCountedByName_NotLostSilently()
    {
        var r = new Rig();
        r.Shadow.ObserveV1(V1(new { item = 7, action = "promotion-deferred", release = "1.0.2", reason = "sweep-budget" }, new { item = 7, action = "promotion-error", release = "1.0.3", environment = "Uat" }, new { item = 7, action = "promotion-deferred", release = "1.0.4", reason = "sweep-budget" }));
        var undecided = (JsonObject)r.Shadow.Status()["v1"]!["undecided"]!;
        Assert.Equal(2L, (long)undecided["promotion-deferred"]!);
        Assert.Equal(1L, (long)undecided["promotion-error"]!);
    }
}
