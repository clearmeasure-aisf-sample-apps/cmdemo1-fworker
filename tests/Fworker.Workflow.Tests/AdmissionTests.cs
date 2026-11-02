using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// V1.1: the admission. After every pass of the board reconciler every open card in a step column that has no open saga gets one: adopted from v1's state comment (with its live claim, so no second session is asked for), started at
/// the step of its column (a card a person moved, an item whose start was lost), or reopened (a completed saga whose issue is open again). Here with the in-memory stores and the adoption's fake GitHub.
/// </summary>
public class AdmissionTests
{
    static readonly RepoId Home = Item.Repository;

    sealed class World
    {
        public readonly AdoptionRunnerTests.FakeReads Reads = new();
        public readonly InMemoryWorkItemStore Items = new();
        public readonly AdoptionRunnerTests.Projected Projections = new();
        public readonly InMemoryCapacityStore Capacity = new();
        public readonly List<object> Published = [];
        public readonly List<string> Log = [];
        public bool Leader = true, AdoptionDone = true, HostUp = true;
        public readonly Dictionary<string, DateTimeOffset> SeenReopened = [];
        public readonly HashSet<int> Excluded = [];
        public DateTimeOffset Clock = Now;
        public AdmissionConfig Config = new("factory worker", "AI Factory", "Demo", "", "<!-- aisf:docs-only -->", Settings().Policy, MaxStarts: 6);
        public AdoptionSettings Mapping = Settings();
        public readonly HashSet<int> ClosedIssues = [];
        Admission? admission;

        public Admission Admission => admission ??= Make();

        Admission Make()
        {
            var ports = new AdoptionPorts(Reads, Items, Projections, Capacity, new InMemoryMergeTrainStore(), new InMemoryTrainStore(), new InMemoryQcEnvironmentStore(), new InMemoryMergedItemStore(), new InMemoryDeliveredBackfill(), null,
                m => { if (!HostUp) return Task.FromResult(false); lock (Published) Published.Add(m); return Task.FromResult(true); }, (m, due) => Task.FromResult(true));
            var options = new AdoptionOptions(Home, Mapping, () => []);
            var runner = new AdoptionRunner(options, ports, () => Clock);
            return new Admission(options, Config with { Policy = Mapping.Policy }, runner, ports, () => Leader, () => AdoptionDone, (id, _) => Task.FromResult(ClosedIssues.Contains(id.Number)), () => Clock, Log.Add, () => Excluded, key => SeenReopened.TryGetValue(key, out var at) ? at : null, key => SeenReopened.Remove(key));
        }

        public World Put(int n, string column, JsonObject? state, string[]? labels = null, string body = "The body.", bool issueOpen = true)
        {
            Reads.Board.RemoveAll(c => c.Number == n);
            Reads.Board.Add(new BoardCard(Home, n, column, true, $"item {n}", labels ?? ["factory worker", "Auto Merge"]));
            Reads.Records[n] = new V1ItemRecord(issueOpen, labels ?? ["factory worker", "Auto Merge"], body, Now.AddDays(-2), state, false);
            return this;
        }

        public Task<string> Run() => Admission.Run([.. Reads.Board], default);
        public IEnumerable<T> Of<T>() { lock (Published) return Published.OfType<T>().ToList(); }
        public WorkItemState Row(int n) => Items.States[new ItemId(Home, n).ToString()];
    }

    // ---- adopted: the card has v1's state comment ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACardWithV1sStateComment_IsAdopted_NotStarted()
    {
        var w = new World().Put(8, Design, State(extra: "\"design\": { \"docs_only\": false }"));
        Assert.Equal("done", await w.Run());
        Assert.Equal((StepKind.Design, "feature"), (w.Row(8).Step, w.Row(8).Plan));
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Equal((1, 0, 1), (w.Admission.Report.Adopted, w.Admission.Report.Started, w.Admission.Report.Passes));
        Assert.Contains(w.Of<WorkItemDecided>(), d => d.Action == WorkItemActions.Adopted);
    }

    // ---- in flight with a live claim: the main risk -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemV1RunsNow_IsAdoptedWithItsLiveClaim_AndNoSecondSessionIsAskedFor()
    {
        var w = new World().Put(7, Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))));
        await w.Run();
        var row = w.Row(7);
        // the row holds the session the claim stands for: its slot, its lease (the claim's), no request for another
        Assert.Equal((StepKind.Develop, true, false, "adopted|acme/orders#7|1"), (row.Step, row.SlotKnown, row.SlotAsked, row.SlotId));
        Assert.Equal(Now.AddMinutes(25), row.LeaseDue);
        Assert.Empty(row.Outbox);
        Assert.Empty(w.Of<SlotWanted>());
        Assert.Empty(w.Of<SessionStartRequested>());
        Assert.Empty(w.Of<StartWorkItem>());
        // the scheduler's ledger holds the slot, so that it grants none
        Assert.Equal(["acme/orders#7"], w.Capacity.State(CapacityKeys.Factory)!.Slots.Keys);
        // the next pass finds the saga and does nothing at all (no second adoption, no second slot)
        var published = w.Published.Count;
        await w.Run();
        Assert.Equal(published, w.Published.Count);
        Assert.Equal(["acme/orders#7"], w.Capacity.State(CapacityKeys.Factory)!.Slots.Keys);
        Assert.Equal(1, w.Admission.Report.Adopted);
    }

    [Fact]
    public async Task AnItemV1RunsNow_WhoseClaimIsNotAnOwners_HoldsNoSlot_ButItsLease()
    {
        var w = new World().Put(7, Develop, State(extra: Claim(Develop, false, TimeSpan.FromMinutes(25))));
        await w.Run();
        Assert.Equal((true, Now.AddMinutes(25)), (w.Row(7).EverGranted, w.Row(7).LeaseDue));
        Assert.Empty(w.Of<SlotWanted>());
    }

    // ---- started at its column: no state comment ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Design, StepKind.Design)]
    [InlineData(Develop, StepKind.Develop)]
    [InlineData(Verify, StepKind.Verify)]
    public async Task ACardWithNoStateComment_IsStartedAtTheStepOfItsColumn(string column, StepKind step)
    {
        var w = new World().Put(12, column, null);
        await w.Run();
        var start = Assert.Single(w.Of<StartWorkItem>());
        Assert.Equal((new ItemId(Home, 12), "board", "feature", step), (start.Item, start.Source, start.Plan, start.Step));
        Assert.Equal((1, 0), (w.Admission.Report.Started, w.Admission.Report.Adopted));
        Assert.Contains(w.Log, l => l.Contains("started acme/orders#12") && l.Contains(step.ToString()));
        // (the host answers with the saga: nothing is made here)
        Assert.Empty(w.Items.States);
    }

    [Fact]
    public async Task TheBodysDocsMarker_ChoosesTheDocsPlan()
    {
        var w = new World().Put(12, Develop, null, body: "Docs. <!-- aisf:docs-only -->");
        await w.Run();
        Assert.Equal(("docs", StepKind.Develop), (Assert.Single(w.Of<StartWorkItem>()).Plan, Assert.Single(w.Of<StartWorkItem>()).Step!.Value));
    }

    [Fact]
    public async Task AStartAtAStep_BeginsTheSagaThere_OnAnyPlanThatHasTheStep()
    {
        var fx = new SagaFixture();
        await fx.Send(new StartWorkItem(SagaFixture.Item, "board", "feature", Step: StepKind.Verify));
        Assert.Equal((StepKind.Verify, "feature", "1.1", WorkItemStatus.Open), (fx.State.Step, fx.State.Plan, fx.State.ArmId, fx.State.Status));
        Assert.Equal([WorkItemActions.Started, WorkItemActions.StepEntered], fx.Actions);
        // the hotfix plan has no Design: the step it begins at is its first
        var hot = new SagaFixture();
        await hot.Send(new StartWorkItem(SagaFixture.Item, "board", "hotfix", Step: StepKind.Design));
        Assert.Equal(StepKind.Develop, hot.State.Step);
        // a step no column stands for is the plan's first
        var merge = new SagaFixture();
        await merge.Send(new StartWorkItem(SagaFixture.Item, "board", "feature", Step: StepKind.Merge));
        Assert.Equal(StepKind.Design, merge.State.Step);
        // no step: as before
        var plain = new SagaFixture();
        await plain.Send(new StartWorkItem(SagaFixture.Item, "intake"));
        Assert.Equal(StepKind.Design, plain.State.Step);
    }

    [Fact]
    public async Task AStartThatNamesAStep_ForAnItemThatHasASaga_ChangesNothingAndPublishesNothing()
    {
        // Replenish and the admission in one pass, a pass twice, a redelivery: the first start makes the saga, every other is a no-op
        var fx = new SagaFixture();
        await fx.Send(new StartWorkItem(SagaFixture.Item, "replenish", "feature", Step: StepKind.Design));
        var json = fx.State.ToJson();
        var (decisions, timers, commands) = (fx.Decisions.Count, fx.Timers.Count, fx.Commands.Count);
        foreach (var again in new[] { new StartWorkItem(SagaFixture.Item, "board", "feature", Step: StepKind.Design), new StartWorkItem(SagaFixture.Item, "board", "feature", Step: StepKind.Develop), new StartWorkItem(SagaFixture.Item, "replenish", "feature", Step: StepKind.Design) })
        {
            var r = await fx.Send(again);
            Assert.NotEqual(WorkItemOutcome.Applied, r.Decision.Outcome);
        }
        Assert.Equal(json, fx.State.ToJson());
        Assert.Equal((decisions, timers, commands), (fx.Decisions.Count, fx.Timers.Count, fx.Commands.Count));
    }

    [Fact]
    public async Task AnIssueYoungerThanTheMinimumAge_IsNotStartedFromTheBoard_UntilItIsOldEnough()
    {
        var w = new World().Put(12, Design, null);
        w.Reads.Records[12] = w.Reads.Records[12] with { CreatedAt = Now.AddSeconds(-10) };
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        w.Clock = Now.AddSeconds(120);
        await w.Run();
        Assert.Single(w.Of<StartWorkItem>());
    }

    // ---- moved by a person ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACardInTodo_IsNotAdmitted_UntilAPersonMovesItIntoAColumn()
    {
        var w = new World().Put(12, "Todo", null);
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Equal(0, w.Reads.ItemReads);   // (a card of no step costs no read)
        w.Put(12, Develop, null);   // the person moves it
        await w.Run();
        Assert.Equal(StepKind.Develop, Assert.Single(w.Of<StartWorkItem>()).Step);
    }

    // ---- reopened ------------------------------------------------------------------------------------------------------------------------------------------------

    static async Task Completed(World w, int n, TimeSpan ago)
    {
        var done = new WorkItemState { Item = new ItemId(Home, n).ToString(), Generation = 1, Plan = "feature", Status = WorkItemStatus.Completed, CompletedAt = Now - ago, Step = StepKind.Merge };
        Assert.True(await w.Items.Save(done.Item, done, 0, default));
    }

    [Fact]
    public async Task AnItemWhoseSagaCompleted_AndWhoseCardTheBoardSawClosedAndNowSeesOpen_IsReopened_Once()
    {
        var w = new World().Put(12, Develop, null);
        await Completed(w, 12, TimeSpan.FromHours(3));
        w.SeenReopened["acme/orders#12"] = Now.AddHours(-1);
        await w.Run();
        var reopen = Assert.Single(w.Of<ReopenWorkItem>());
        Assert.Empty(w.SeenReopened);   // (the proof is used up)
        Assert.Equal(("admission-reopen|1", new ItemId(Home, 12)), (reopen.Key, reopen.Item));
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Equal(1, w.Admission.Report.Reopened);
        // (the saga takes it as the next generation)
        var fx = new SagaFixture();
        await fx.Send(new StartWorkItem(SagaFixture.Item, "board", "feature"));
        await fx.Send(new ItemClosed(SagaFixture.Item, "c1", "test"));
        Assert.False(fx.State.IsOpen);
        await fx.Send(new ReopenWorkItem(SagaFixture.Item, "the issue was reopened", "admission-reopen|1"));
        Assert.Equal((2, true), (fx.State.Generation, fx.State.IsOpen));
    }

    [Fact]
    public async Task AMergedItemWhoseCloseAndCardMoveFailed_IsNotReopened_ItsCardWasNeverSeenClosed()
    {
        // the saga completed, the issue is still open and the card still stands in a step column: nothing shows a person reopened it
        var w = new World().Put(12, Verify, null);
        await Completed(w, 12, TimeSpan.FromHours(3));
        await w.Run(); await w.Run();
        Assert.Empty(w.Of<ReopenWorkItem>());
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Equal(0, w.Admission.Report.Reopened);
        Assert.Equal(2, (int)w.Admission.Report.ToJson()["skippedBy"]!["completed-no-proof"]!);   // (looked at again every pass, so the board's proof is seen the pass it comes)
    }

    [Fact]
    public async Task ABoardProofFromBeforeTheSagaEnded_OrForAMergedItem_ProvesNothing()
    {
        var w = new World().Put(12, Develop, null).Put(13, Develop, null);
        await Completed(w, 12, TimeSpan.FromHours(3));
        w.SeenReopened["acme/orders#12"] = Now.AddHours(-5);   // the card was closed and opened while the saga ran: before its end
        var merged = new WorkItemState { Item = "acme/orders#13", Generation = 1, Plan = "feature", Status = WorkItemStatus.Completed, CompletedAt = Now.AddHours(-3), Step = StepKind.Deliver, MergeCommit = new string('a', 40) };
        Assert.True(await w.Items.Save(merged.Item, merged, 0, default));
        w.SeenReopened["acme/orders#13"] = Now.AddHours(-1);
        await w.Run();
        Assert.Empty(w.Of<ReopenWorkItem>());
        Assert.Equal(2, (int)w.Admission.Report.ToJson()["skippedBy"]!["completed-no-proof"]!);
    }

    [Fact]
    public async Task AReopenedV1Item_WithANewerStateCommentWrittenAfterTheSagaEnded_GoesThroughTheReconcilersReopenPath()
    {
        var w = new World().Put(12, Develop, State(rev: 9, extra: "\"design\": { \"docs_only\": false }"));
        await Completed(w, 12, TimeSpan.FromHours(3));
        await w.Run();
        Assert.Empty(w.Of<ReopenWorkItem>());
        var row = w.Row(12);
        Assert.Equal((2, true, StepKind.Develop, 9L), (row.Generation, row.IsOpen, row.Step, (long)row.CommentRev));
        Assert.Equal(1, w.Admission.Report.Reopened);
    }

    [Fact]
    public async Task AStateCommentWrittenAroundTheCompletion_IsTheProjectionsEcho_NotAReopening()
    {
        var comment = State(rev: 9); comment["updated"] = Iso(TimeSpan.FromMinutes(19));
        var w = new World().Put(12, Develop, comment);
        await Completed(w, 12, TimeSpan.FromMinutes(20));
        await w.Run();
        Assert.False(w.Row(12).IsOpen);
        Assert.Equal(0, w.Admission.Report.Reopened);
    }

    [Fact]
    public async Task APassReopensNoMoreThanTheLimit_AndItemsTheAdoptionCouldNotFenceAreLeftAlone()
    {
        var w = new World { Config = new("factory worker", "AI Factory", "Demo", "", "<!-- aisf:docs-only -->", Settings().Policy, MaxStarts: 6) { MaxReopens = 1 } };
        foreach (var n in new[] { 12, 13, 14 }) { w.Put(n, Develop, null); await Completed(w, n, TimeSpan.FromHours(3)); w.SeenReopened[$"acme/orders#{n}"] = Now.AddHours(-1); }
        w.Excluded.Add(14);
        await w.Run();
        Assert.Equal([12], w.Of<ReopenWorkItem>().Select(r => r.Item.Number));
        await w.Run();
        Assert.Equal([12, 13], w.Of<ReopenWorkItem>().Select(r => r.Item.Number));   // (one a pass; the proof of 12 is used up, so the next pass reaches 13)
        Assert.DoesNotContain(w.Of<ReopenWorkItem>(), r => r.Item.Number == 14);
    }

    [Fact]
    public async Task AHeldCard_GetsNoSaga_UntilTheHoldIsLifted()
    {
        var w = new World { Config = new("factory worker", "AI Factory", "Demo", "", "<!-- aisf:docs-only -->", Settings().Policy, MaxStarts: 6) { HoldLabels = ["on-ice"] } };
        w.Put(12, Design, null, ["factory worker", "hold:owner"]).Put(13, Design, null, ["factory worker", "on-ice"]).Put(14, Design, State(), ["factory worker", "hold:review"]);
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Empty(w.Items.States);   // (a held card with v1's state comment is not adopted either)
        Assert.Equal(3, (int)w.Admission.Report.ToJson()["skippedBy"]!["held"]!);
        w.Put(12, Design, null); w.Clock = Now.AddMinutes(6);   // the hold is lifted; the card is looked at again after the skip time
        await w.Run();
        Assert.Equal([12], w.Of<StartWorkItem>().Select(s => s.Item.Number));
    }

    [Fact]
    public void TheAdoptionsMapping_PutsAHotfixItemOnTheHotfixPlan_BeginningAtDevelopWhenItsCardIsInDesign_AndSkipsItWhereThePlanIsNotCarried()
    {
        var inDesign = Map(Design, State(), with: s => s with { Labels = ["factory worker", "hotfix"] });
        Assert.Equal(("hotfix", StepKind.Develop), (inDesign.State!.Plan, inDesign.State.Step));
        var inVerify = Map(Verify, State(), with: s => s with { Labels = ["factory worker", "hotfix"] });
        Assert.Equal(("hotfix", StepKind.Verify), (inVerify.State!.Plan, inVerify.State.Step));
        Assert.Equal("plan-not-carried: hotfix", Map(Develop, State(), Settings(p => p with { CarriedPlans = ["docs", "feature"] }), s => s with { Labels = ["factory worker", "hotfix"] }).SkipReason);
        Assert.Equal("feature", Map(Develop, State()).State!.Plan);
    }

    [Fact]
    public async Task AHotfixCard_IsStartedOnTheHotfixPlan_AtItsColumnButNeverBeforeDevelop_AndAdoptedOntoIt_WhereTheEngineCarriesThePlan()
    {
        var w = new World().Put(12, Design, null, ["factory worker", "hotfix"]).Put(13, Verify, null, ["factory worker", "hotfix"])
            .Put(14, Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))), ["factory worker", "hotfix"]);
        await w.Run();
        var starts = w.Of<StartWorkItem>().OrderBy(x => x.Item.Number).ToList();
        Assert.Equal([(12, "hotfix", StepKind.Develop), (13, "hotfix", StepKind.Verify)], starts.Select(x => (x.Item.Number, x.Plan!, x.Step!.Value)));
        // the adopted one holds the plan and its live claim's slot
        Assert.Equal(("hotfix", StepKind.Develop, true), (w.Row(14).Plan, w.Row(14).Step, w.Row(14).SlotKnown));
        Assert.Empty(w.Of<SlotWanted>());
        // the hotfix plan begins at Develop in the saga: a start at Design on it is the plan's first step
        var fx = new SagaFixture();
        await fx.Send(new StartWorkItem(SagaFixture.Item, "board", "hotfix", Step: StepKind.Design));
        Assert.Equal(StepKind.Develop, fx.State.Step);
    }

    [Fact]
    public async Task AHotfixCard_WhereTheEngineDoesNotCarryThePlan_IsLeftAlone()
    {
        var w = new World { Mapping = Settings(p => p with { CarriedPlans = ["docs", "feature"] }) }.Put(12, Design, null, ["factory worker", "hotfix"]).Put(14, Develop, State(), ["factory worker", "hotfix"]);
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Empty(w.Items.States);
        Assert.Equal(2, (int)w.Admission.Report.ToJson()["skippedBy"]!["plan-not-carried"]!);
    }

    [Fact]
    public async Task AnItemTheSagaHandedToV1_IsNotReopened_WhateverTheCardAndIssueSay()
    {
        var w = new World { Mapping = Settings(p => p with { CarriedPlans = ["docs"] }) }.Put(12, Develop, null);
        await Completed(w, 12, TimeSpan.FromHours(3));   // (a feature saga: the plan this engine does not carry)
        await w.Run();
        Assert.Empty(w.Of<ReopenWorkItem>());
        Assert.Empty(w.Of<StartWorkItem>());
        // and the saga itself refuses it
        var fx = new SagaFixture(WorkItemPolicy.Default with { CarriedPlans = ["docs"] });
        await fx.Store.Save(SagaFixture.Item.ToString(), new WorkItemState { Item = SagaFixture.Item.ToString(), Generation = 1, Plan = "feature", Status = WorkItemStatus.Completed, CompletedAt = Now }, 0, default);
        await fx.Send(new ReopenWorkItem(SagaFixture.Item, "x", "k"));
        Assert.Equal((1, false), (fx.State.Generation, fx.State.IsOpen));
    }

    [Fact]
    public async Task ACompletedSagaThatEndedJustNow_IsNotReopened_TheCompletionsOwnMoveOfTheCardIsStillUnderway()
    {
        var w = new World().Put(12, Develop, null);
        await Completed(w, 12, TimeSpan.FromMinutes(1));
        await w.Run();
        Assert.Empty(w.Of<ReopenWorkItem>());
        Assert.Empty(w.Of<StartWorkItem>());
    }

    // ---- not admitted -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatIsNotTheEnginesToAdmit_IsSkippedWithItsReason()
    {
        var w = new World().Put(1, Design, null, ["factory worker", "env:tdd"], issueOpen: false).Put(2, Design, null, ["bug", "env:tdd"]).Put(3, Design, null, ["factory worker", "AI Factory", "env:tdd"])
            .Put(4, Design, null, ["factory worker", "Demo", "env:tdd"]).Put(5, Design, null, ["factory worker", "env:uat"]).Put(7, Design, null, ["factory worker"]);
        w.Config = w.Config with { EnvLabel = "env:tdd" };
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        var json = w.Admission.Report.ToJson()["skippedBy"]!.AsObject();
        Assert.Equal(1, (int)json["issue-closed"]!);
        Assert.Equal(1, (int)json["no-factory-label"]!);
        Assert.Equal(1, (int)json["rival-label"]!);
        Assert.Equal(1, (int)json["demo-label"]!);
        Assert.Equal(2, (int)json["env-scope"]!);   // (env:uat; and #7 carries no env: label at all while scoping is on)
        // a card skipped is not read again at once
        var reads = w.Reads.ItemReads;
        await w.Run();
        Assert.Equal(reads, w.Reads.ItemReads);
    }

    [Fact]
    public async Task APlanTheEngineDoesNotCarry_IsLeftToV1()
    {
        var w = new World { Mapping = Settings(p => p with { CarriedPlans = ["docs"] }) }.Put(12, Design, null);
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
        Assert.Equal(1, (int)w.Admission.Report.ToJson()["skippedBy"]!["plan-not-carried"]!);
    }

    [Fact]
    public async Task AnEngineThatCarriesNoPlan_AdmitsNothing_AndReadsNothing()
    {
        var w = new World { Mapping = Settings(p => p with { CarriedPlans = [] }) }.Put(12, Design, null).Put(13, Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))));
        Assert.Equal("off: no plan is carried", await w.Run());
        Assert.Equal((0, 0), (w.Reads.ItemReads, w.Published.Count));
    }

    [Fact]
    public async Task AnItemWhoseDependenciesAreNotMet_WaitsForThem_AndStartsOnceTheyAre()
    {
        var w = new World().Put(12, Design, null, body: "Depends on #20").Put(20, Develop, null);
        await w.Run();
        Assert.DoesNotContain(w.Of<StartWorkItem>(), s => s.Item.Number == 12);
        Assert.Equal(1, (int)w.Admission.Report.ToJson()["skippedBy"]!["depends-on"]!);
        w.Reads.Board.RemoveAll(c => c.Number == 20); w.Reads.Board.Add(new BoardCard(Home, 20, Done, true, "item 20"));
        w.Clock = Now.AddMinutes(2);   // (the dependency is looked at again a minute later)
        await w.Run();
        Assert.Contains(w.Of<StartWorkItem>(), s => s.Item.Number == 12);
    }

    [Fact]
    public async Task APassStartsNoMoreThanTheLimit_AndTheRestWaitForTheNext()
    {
        var w = new World { Config = new("factory worker", "AI Factory", "Demo", "", "<!-- aisf:docs-only -->", Settings().Policy, MaxStarts: 2) };
        for (var n = 30; n < 35; n++) w.Put(n, Design, null);
        await w.Run();
        Assert.Equal([30, 31], w.Of<StartWorkItem>().Select(s => s.Item.Number));
        await w.Run();
        Assert.Equal(4, w.Of<StartWorkItem>().Count());   // (the host has made no saga here, so the first two are offered again with the next: the host answers a repeat with "the item is open")
    }

    [Fact]
    public async Task TheAdmissionWaitsForTheLeaderAndForTheAdoptionOfTheTerm()
    {
        var w = new World().Put(12, Design, null);
        w.Leader = false;
        Assert.Equal("not-leader", await w.Run());
        w.Leader = true; w.AdoptionDone = false;
        Assert.Equal("waiting: adoption", await w.Run());
        Assert.Empty(w.Of<StartWorkItem>());
        w.AdoptionDone = true;
        Assert.Equal("done", await w.Run());
        Assert.Single(w.Of<StartWorkItem>());
    }

    [Fact]
    public async Task AHostThatIsNotUp_IsAnErrorOfThePass_AndTheCardIsOfferedAgain()
    {
        var w = new World().Put(12, Design, null);
        w.HostUp = false;
        await w.Run();
        Assert.Equal((0, 1), (w.Admission.Report.Started, w.Admission.Report.Errors));
        w.HostUp = true;
        await w.Run();
        Assert.Equal(1, w.Admission.Report.Started);
    }

    [Fact]
    public async Task ACardOfAnotherRepository_OrOutsideAStepColumn_IsNotTheAdmissions()
    {
        var w = new World().Put(12, Release, null).Put(13, Done, null);
        w.Reads.Board.Add(new BoardCard(new RepoId("acme", "other"), 14, Design, true, "other repository"));
        await w.Run();
        Assert.Empty(w.Of<StartWorkItem>());
    }
}
