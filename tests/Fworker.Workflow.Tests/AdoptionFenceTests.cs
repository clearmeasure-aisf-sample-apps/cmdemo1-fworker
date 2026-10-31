using System.Reflection;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10n: the fencing of a re-adoption after FACTORY_V2_OFF. Rows F of the table in the plan: the generation a mapping is made for, the new instance an item v1 touched gets (and the old one's timers and commands that end), the
/// open instance of an item v1 ended (closed), the ended instance v1 opened again, the ledger's entries and the merge line's and the delivery train's rows of the instance that ended, and the scenario the plan names: an item v1
/// merged during the switch is not touched by v2 afterwards, and no stale timer acts.
/// </summary>
public class AdoptionFenceTests
{
    static string Claimed(TimeSpan? lease = null) => Join(Claim(Develop, true, lease ?? TimeSpan.FromMinutes(25)), "\"fix_forward_rounds\": 1");

    static AdoptionMapping MapFor(WorkItemState? existing, string column, JsonObject state, bool afterInterval, AdoptionSettings? settings = null, Func<AdoptionSnapshot, AdoptionSnapshot>? with = null) =>
        AdoptionMapper.Map(Snapshot(column, state, with), settings ?? Settings(), Now, AdoptionFence.GenerationFor(existing, afterInterval));

    /// <summary>The saga's row of an item adopted at a first start, as v2 left it when the kill switch went on.</summary>
    static AdoptionResult First(string column, JsonObject state) => AdoptionReconciler.Reconcile(null, 0, 0, MapFor(null, column, state, false), Now);

    static WorkItemSaga Saga() => new(new FakeDocsPort(), Settings().Policy);

    static WorkItemOutcome Outcome(WorkItemState state, WorkItemInput input) => Saga().Apply(state, input, Now).Result.Decision.Outcome;

    // ---- the generation a mapping is made for -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void F1_TheGenerationIsOneWithoutARow_TheRowsOwnWithoutABreak_AndTheNextAfterOne()
    {
        var row = First(Develop, State(extra: Claimed())).State!;
        row.Generation = 3;
        Assert.Equal([1, 1, 3, 4], [AdoptionFence.GenerationFor(null, false), AdoptionFence.GenerationFor(null, true), AdoptionFence.GenerationFor(row, false), AdoptionFence.GenerationFor(row, true)]);
    }

    [Fact]
    public void F2_TheMappingNamesItsArmItsAdoptedSlotAndItsRequestAfterTheGenerationItWasMadeFor()
    {
        var m = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claimed())), Settings(), Now, generation: 4);
        Assert.Equal((4, "4.1", "adopted|acme/orders#7|4", 4), (m.State!.Generation, m.State.ArmId, m.State.SlotId, m.Slot!.Generation));
        var asks = AdoptionMapper.Map(Snapshot(Design, State()), Settings(), Now, generation: 4).State!;
        Assert.Equal("ask|acme/orders#7|4|1", Assert.IsType<AskSlot>(Assert.Single(asks.Outbox)).Key);
    }

    [Fact]
    public void F3_ASlotTheLedgerHoldsForAnEarlierInstanceIsNotTakenForTheNewOnes_ItsOwnGenerationAndAnUnnamedOneAre()
    {
        LedgerSlot Held(int generation) => new() { SlotId = "s9", Item = Item.ToString(), Runtime = "local", Request = new WaitingRequest { Item = Item.ToString(), Generation = generation } };
        var old = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claimed()), s => s with { HeldSlot = Held(1) }), Settings(), Now, generation: 2);
        Assert.Equal(("adopted|acme/orders#7|2", true), (old.State!.SlotId, old.Slot is not null));
        var same = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claimed()), s => s with { HeldSlot = Held(2) }), Settings(), Now, generation: 2);
        Assert.Equal(("s9", true), (same.State!.SlotId, same.Slot is null));
        var unnamed = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claimed()), s => s with { HeldSlot = Held(0) }), Settings(), Now, generation: 2);
        Assert.Equal("s9", unnamed.State!.SlotId);
    }

    // ---- an item v1 touched is a new instance -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void F4_AnItemTheCommentChangedAfterABreakIsTheNextGeneration_WithANewArm_ItsTimersAndRequestNamingIt_AndTheLedgerFenced()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        Assert.Contains(first.Timers, t => t.Message is PhaseLeaseExpired { Generation: 1 });
        // v1 moved the item to Functional Testing and its owner's claim ran out
        var moved = MapFor(first.State, Verify, State(rev: 8, extra: Join("\"fix_forward_rounds\": 1", Claim(Verify, true, TimeSpan.FromMinutes(-1)))), afterInterval: true);
        var r = AdoptionReconciler.Reconcile(first.State, 1, 0, moved, Now, afterInterval: true);
        Assert.Equal(AdoptionKind.Updated, r.Kind);
        var s = r.State!;
        Assert.Equal((2, "2.1", StepKind.Verify, 2L, 8), (s.Generation, s.ArmId, s.Step, s.Sequence, s.CommentRev));
        Assert.Equal((2, (int?)1), (r.Decision!.Generation, r.FencedFrom));
        Assert.Contains("new generation 2", r.Decision.Reasons[1]);
        Assert.Equal(new LedgerFence(Item, 2), r.Fence);
        // the new instance asks for its slot under its own number; what the old held is fenced by the generation, not asked back by commands that would be stale
        var wanted = Assert.IsType<SlotWanted>(Assert.Single(r.Commands));
        Assert.Equal((2, "2.1"), (wanted.Generation, wanted.ArmId));
        Assert.Equal("ask|acme/orders#7|2|1", Assert.IsType<AskSlot>(Assert.Single(s.Outbox)).Key);
        Assert.All(r.Timers, t => Assert.Equal(2, ((IGenerationInput)t.Message).Generation));
    }

    [Fact]
    public void F5_ACommentChangeOnTheSameStepIsAlsoANewInstanceAfterABreak_ButNotWithoutOne()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var renewed = State(rev: 9, extra: Join(Claim(Develop, true, TimeSpan.FromMinutes(28)), "\"fix_forward_rounds\": 2"));
        var quiet = AdoptionReconciler.Reconcile(first.State, 1, 0, MapFor(first.State, Develop, renewed, false), Now);
        Assert.Equal((AdoptionKind.Updated, 1, "1.1", (LedgerFence?)null), (quiet.Kind, quiet.State!.Generation, quiet.State.ArmId, quiet.Fence));
        var broken = AdoptionReconciler.Reconcile(first.State, 1, 0, MapFor(first.State, Develop, renewed, true), Now, afterInterval: true);
        Assert.Equal((AdoptionKind.Updated, 2, "2.1"), (broken.Kind, broken.State!.Generation, broken.State.ArmId));
        // the counters of the saga survive the new instance as they survive any update (they only grow); its keys and its decisions go on
        Assert.Equal((2, 2L), (broken.State.FixForwardRounds, broken.State.Sequence));
        Assert.Equal(first.State!.Keys, broken.State.Keys);
        // the slot it held is the old instance's: the live claim is adopted as a slot of the new one
        Assert.Equal("adopted|acme/orders#7|2", broken.State.SlotId);
        Assert.Equal(2, broken.Slot!.Generation);
    }

    [Fact]
    public void F6_AnItemTheCommentDoesNotChangeKeepsItsInstance_AfterABreakToo_AndItsTimersStillAct()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var again = AdoptionReconciler.Reconcile(first.State, 1, 0, MapFor(first.State, Develop, State(rev: 6, extra: Claimed()), true), Now, afterInterval: true);
        Assert.Equal((AdoptionKind.Unchanged, 1, "1.1", (LedgerFence?)null), (again.Kind, again.State!.Generation, again.State.ArmId, again.Fence));
        var lease = first.Timers.Select(t => t.Message).OfType<PhaseLeaseExpired>().Single();
        Assert.NotEqual(WorkItemOutcome.NotFound, Outcome(again.State, lease));
        // run again on the result: the comment is not newer than the one adopted
        var twice = AdoptionReconciler.Reconcile(again.State, 1, 0, MapFor(again.State, Develop, State(rev: 6, extra: Claimed()), true), Now, afterInterval: true);
        Assert.Equal(AdoptionKind.Kept, twice.Kind);
    }

    [Fact]
    public void F7_TheOldInstancesTimersAndInputsEndAsNotFound_TheNewOnesAct()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var oldLease = first.Timers.Select(t => t.Message).OfType<PhaseLeaseExpired>().Single();
        var r = AdoptionReconciler.Reconcile(first.State, 1, 0, MapFor(first.State, Verify, State(rev: 8, extra: Claim(Verify, true, TimeSpan.FromMinutes(25))), true), Now, afterInterval: true);
        var s = r.State!;
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, oldLease));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new StepCompleted(Item, 1, "1.1", "done|1.1")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new BudgetSpent(Item, 1, "1.1", "b1", BudgetKind.Failure, "x")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new ParkExpired(Item, 1, Now)));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new MasterWatchElapsed(Item, 1, Now)));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new OwnerIdleElapsed(Item, 1, Now)));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new SettleWindowElapsed(Item, 1, "1.2")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new PullRequestClosed(Item, 1, "closed|11", 11)));
        // the new instance answers its own arm
        Assert.Equal(WorkItemOutcome.Applied, Outcome(s, new LeaseRenewed(Item, 2, "2.1", "renew-1")));
    }

    [Fact]
    public void F8_AfterABreakTheDecisionsTheProjectionNeverWroteAndTheCardItNeverMovedDoNotHoldTheCommentBack_WithoutOneTheyDo()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var saga = WorkItemState.FromJson(first.State!.ToJson()); saga.Sequence = 3; saga.CardSeq = 3;
        var comment = State(rev: 9, extra: Join("\"fix_forward_rounds\": 3", Claim(Develop, true, TimeSpan.FromMinutes(25))));
        // the projection is behind (2 of 3) and the card too: v1 acted on the comment alone
        Assert.Equal(AdoptionKind.Kept, AdoptionReconciler.Reconcile(saga, 2, 2, MapFor(saga, Develop, comment, false), Now).Kind);
        var r = AdoptionReconciler.Reconcile(saga, 2, 2, MapFor(saga, Develop, comment, true), Now, afterInterval: true);
        Assert.Equal((AdoptionKind.Updated, 2, 3), (r.Kind, r.State!.Generation, r.State.FixForwardRounds));
        // (and the decisions of the old instance that the projection still has are left to it by the generation: SagaStateProjection.Project returns for a decision of another generation)
        Assert.Equal(2, r.Decision!.Generation);
    }

    [Fact]
    public void F9_AMappingMadeForAnotherGenerationThanTheFenceNeedsIsRefused()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var wrong = AdoptionMapper.Map(Snapshot(Verify, State(rev: 8)), Settings(), Now);   // (made for generation 1)
        var e = Assert.Throws<ArgumentException>(() => AdoptionReconciler.Reconcile(first.State, 1, 0, wrong, Now, afterInterval: true));
        Assert.Contains("maps it for 2", e.Message);
    }

    // ---- an item v1 ended is closed ---------------------------------------------------------------------------------------------------------------------------------------

    static string Settled() => Merged("release-queue", $"\"done_at\": \"{Iso(TimeSpan.FromHours(1))}\"");

    [Fact]
    public void F10_TheOpenInstanceOfAnItemV1SettledIsClosed_NothingOfItRemainsToAct()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var oldLease = first.Timers.Select(t => t.Message).OfType<PhaseLeaseExpired>().Single();
        var skipped = MapFor(first.State, Develop, State(rev: 9, extra: Settled()), true);
        Assert.Equal("settled", skipped.SkipReason);
        var r = AdoptionReconciler.Reconcile(first.State, 1, 0, skipped, Now, afterInterval: true);
        Assert.Equal((AdoptionKind.Closed, true), (r.Kind, r.Save));
        var s = r.State!;
        Assert.Equal((WorkItemStatus.Completed, 1, 2L, (DateTimeOffset?)Now), (s.Status, s.Generation, s.Sequence, s.CompletedAt));
        Assert.Null(s.LeaseDue);
        Assert.Empty(r.Timers);
        Assert.Equal((WorkItemActions.AdoptedClosed, 1, (string?)null), (r.Decision!.Action, r.Decision.Generation, r.Decision.Step));
        Assert.Contains("settled", r.Decision.Reasons[0]);
        Assert.False(V1StateFormatWants(r.Decision.Action));
        Assert.Equal(new LedgerFence(Item, null), r.Fence);
        // the session and the scheduler's request of the instance are asked back, under its own generation
        Assert.Contains(r.Commands, c => c is SessionEnded e && e.SlotId == first.State!.SlotId);
        Assert.Contains(r.Commands, c => c is ClearItem { Generation: 1 });
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, oldLease));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new StepCompleted(Item, 0, "1.1", "done|1.1")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(s, new MergeSeen(Item, "merged|x", new string('c', 40), 11)));
    }

    // (the projection's own list: the action is not one of those it writes to the comment, which is where this decision came from)
    static bool V1StateFormatWants(string action) => action is WorkItemActions.Merged or WorkItemActions.Completed or WorkItemActions.Parked or WorkItemActions.StepEntered or WorkItemActions.Settling;

    [Theory]
    [InlineData("docs-merged")]
    [InlineData("card-done")]
    [InlineData("delivered-and-settled")]
    public void F11_TheOtherReasonsThatMeanV1EndedTheItemCloseTheRowToo(string reason)
    {
        var open = First(Develop, State(rev: 5, extra: Claimed())).State!;
        Assert.True(AdoptionFence.V1Ended(reason, open));
    }

    [Fact]
    public void F12_AClosedIssueClosesTheRowOnlyWhileTheSagaHasNotMerged_TheOtherSkipsLeaveTheRowToItsOwnInputs()
    {
        var open = First(Develop, State(rev: 5, extra: Claimed())).State!;
        Assert.True(AdoptionFence.V1Ended("issue-closed", open));
        var merged = WorkItemState.FromJson(open.ToJson()); merged.MergeCommit = new string('a', 40);
        Assert.False(AdoptionFence.V1Ended("issue-closed", merged));
        foreach (var reason in new[] { "no-state-comment", "hold-column: Blocked", "column-of-no-step: Todo", "not-on-the-board", "plan-not-carried: hotfix" })
            Assert.False(AdoptionFence.V1Ended(reason, open));
        // the issue closed with the card in a hold column: skipped, and the row stays (the saga's own inputs decide)
        var r = AdoptionReconciler.Reconcile(open, 1, 0, AdoptionMapper.Map(Snapshot(Develop, State(rev: 9), s => s with { IssueOpen = false }), Settings(), Now, 2), Now, afterInterval: true);
        Assert.Equal(AdoptionKind.Closed, r.Kind);
        var held = AdoptionReconciler.Reconcile(open, 1, 0, AdoptionMapper.Map(Snapshot(Release, State(rev: 9)), Settings(), Now, 2), Now, afterInterval: true);
        Assert.Equal(("hold-column: Release Queue", AdoptionKind.Skipped), (held.Reason, held.Kind));
    }

    [Fact]
    public void F13_WithoutABreakNothingIsClosed_ThatIsTheRuleOfTheStart()
    {
        var open = First(Develop, State(rev: 5, extra: Claimed())).State!;
        var r = AdoptionReconciler.Reconcile(open, 1, 0, AdoptionMapper.Map(Snapshot(Develop, State(rev: 9, extra: Settled())), Settings(), Now), Now);
        Assert.Equal((AdoptionKind.Skipped, null), (r.Kind, (object?)r.Fence));
    }

    [Fact]
    public void F14_AClosedInstanceThatHadAPlaceInTheLineLeavesItWithItsOwnGeneration()
    {
        var pass = First(Verify, State(rev: 5, extra: Pass()));
        Assert.Equal(StepKind.Merge, pass.State!.Step);
        var s = WorkItemState.FromJson(pass.State.ToJson()); s.TrainEntered = true;
        var r = AdoptionReconciler.Reconcile(s, 1, 0, MapFor(s, Verify, State(rev: 9, extra: Settled()), true), Now, afterInterval: true);
        Assert.Equal(AdoptionKind.Closed, r.Kind);
        Assert.Contains(r.Commands, c => c is MergeTrainLeft { Generation: 1 });
    }

    // ---- an item v1 opened again ------------------------------------------------------------------------------------------------------------------------------------------------

    static WorkItemState Ended()
    {
        var s = WorkItemState.FromJson(First(Develop, State(rev: 5, extra: Claimed())).State!.ToJson());
        s.Status = WorkItemStatus.Completed; s.CompletedAt = Now.AddHours(-3); s.LeaseDue = null; s.Sequence = 9; s.CommentRev = 6; s.Keys = ["k1"];
        return s;
    }

    [Fact]
    public void F15_AnEndedInstanceThatV1OpenedAgainIsReopenedAsTheNextGeneration_ItsDecisionsNumberedAfterTheOldOnes()
    {
        var ended = Ended();
        var r = AdoptionReconciler.Reconcile(ended, 9, 0, MapFor(ended, Develop, State(rev: 12, extra: Claimed()), true), Now, afterInterval: true);
        Assert.Equal(AdoptionKind.Reopened, r.Kind);
        var s = r.State!;
        Assert.Equal((WorkItemStatus.Open, 2, "2.1", 10L, 12, 1), (s.Status, s.Generation, s.ArmId, s.Sequence, s.CommentRev, r.FencedFrom));
        Assert.Empty(s.Keys);
        Assert.Equal(new LedgerFence(Item, 2), r.Fence);
        Assert.Equal("adopted|acme/orders#7|2", r.Slot!.SlotId);
        Assert.Contains(r.Timers, t => t.Message is PhaseLeaseExpired { Generation: 2 });
    }

    [Fact]
    public void F16_AnEndedInstanceIsNotReopenedWithoutABreak_NorByACommentThatIsNotNewer_NorForAMergedItem()
    {
        var ended = Ended();
        Assert.Equal(AdoptionKind.Skipped, AdoptionReconciler.Reconcile(ended, 9, 0, MapFor(ended, Develop, State(rev: 12, extra: Claimed()), false), Now).Kind);
        Assert.Equal(AdoptionKind.Skipped, AdoptionReconciler.Reconcile(ended, 9, 0, MapFor(ended, Develop, State(rev: 6, extra: Claimed()), true), Now, afterInterval: true).Kind);
        Assert.Equal(AdoptionKind.Skipped, AdoptionReconciler.Reconcile(ended, 9, 0, MapFor(ended, Verify, State(rev: 12, extra: Merged("master-green")), true), Now, afterInterval: true).Kind);
    }

    // ---- the scheduler's ledger -----------------------------------------------------------------------------------------------------------------------------------------------------

    static CapacityState Ledger(int generation)
    {
        var ledger = new CapacityState();
        var key = Item.ToString();
        var request = new WaitingRequest { Item = key, Generation = generation, Runtime = "cloud", Phase = "develop" };
        ledger.Slots[key] = new LedgerSlot { SlotId = "s1", Item = key, Runtime = "cloud", Request = request };
        ledger.Queue[key] = new WaitingRequest { Item = key, Generation = generation };
        ledger.InFlight[key] = new InFlightItem { Item = key, Generation = generation };
        ledger.Holds[key] = new HoldRecord { By = 3 };
        var other = ItemId.Parse("acme/orders#8").ToString();
        ledger.Slots[other] = new LedgerSlot { SlotId = "s2", Item = other, Runtime = "cloud", Request = new WaitingRequest { Item = other, Generation = 1 } };
        ledger.InFlight[other] = new InFlightItem { Item = other, Generation = 1 };
        return ledger;
    }

    [Fact]
    public void L1_TheFenceTakesWhatTheLedgerHoldsForAnEarlierInstance_AndOnlyThatItemsEntries()
    {
        var ledger = Ledger(1);
        var next = AdoptionLedger.Fence(ledger, new LedgerFence(Item, 2));
        var key = Item.ToString();
        Assert.Equal((false, false, false, false), (next.Slots.ContainsKey(key), next.Queue.ContainsKey(key), next.InFlight.ContainsKey(key), next.Holds.ContainsKey(key)));
        Assert.Equal(["acme/orders#8"], next.Slots.Keys);
        Assert.Equal(["acme/orders#8"], next.InFlight.Keys);
        Assert.True(ledger.Slots.ContainsKey(key));   // (a copy)
    }

    [Fact]
    public void L2_TheFenceKeepsTheEntriesOfTheGenerationItKeeps_SoRunningItAgainChangesNothing()
    {
        var ledger = Ledger(2);
        var next = AdoptionLedger.Fence(ledger, new LedgerFence(Item, 2));
        Assert.Equal(ledger.ToJson(), next.ToJson());
    }

    [Fact]
    public void L3_AnEntryThatNamesNoGenerationIsTheEarlierInstancesToo_AndAFenceForAnEndedItemTakesEverything()
    {
        var ledger = Ledger(0);
        Assert.DoesNotContain(Item.ToString(), AdoptionLedger.Fence(ledger, new LedgerFence(Item, 2)).Slots.Keys);
        var ended = AdoptionLedger.Fence(Ledger(2), new LedgerFence(Item, null));
        Assert.Equal(["acme/orders#8"], ended.Slots.Keys);
        Assert.Equal(["acme/orders#8"], ended.InFlight.Keys);
        Assert.Empty(ended.Queue);
    }

    [Fact]
    public void L4_TheAdoptedSlotOfTheNewInstanceIsAppliedAfterTheFence_WhereTheOldOneWouldHaveBlockedIt()
    {
        var first = First(Develop, State(rev: 5, extra: Claimed()));
        var ledger = AdoptionLedger.Apply(new CapacityState(), first.Slot, first.Flight);
        var r = AdoptionReconciler.Reconcile(first.State, 1, 0, MapFor(first.State, Develop, State(rev: 9, extra: Join(Claim(Develop, true, TimeSpan.FromMinutes(28)), "\"fix_forward_rounds\": 2")), true), Now, afterInterval: true);
        // without the fence the old slot stays (Apply never replaces what the ledger holds)
        Assert.Equal(first.Slot!.SlotId, AdoptionLedger.Apply(ledger, r.Slot, r.Flight).Slots[Item.ToString()].SlotId);
        var fenced = AdoptionLedger.Apply(AdoptionLedger.Fence(ledger, r.Fence!), r.Slot, r.Flight);
        var held = fenced.Slots[Item.ToString()];
        Assert.Equal(("adopted|acme/orders#7|2", 2), (held.SlotId, held.Request.Generation));
        Assert.Equal(2, fenced.InFlight[Item.ToString()].Generation);
    }

    [Fact]
    public async Task L5_TheSchedulerLeavesTheNewInstancesEntriesAloneWhenTheOldOnesClearArrives()
    {
        var fx = new CapacityFixture();
        var item = CapacityFixture.I(1);
        await fx.Send(new RequestSlot(item, fx.K(), 2, "cursor-cloud", CapacityPhases.Develop, new PlanRank(false, null, false), false, false, 2, fx.Now, [], Footprint.Empty));
        var granted = fx.State.Slots.ContainsKey(item.ToString());
        Assert.True(granted || fx.State.Queue.ContainsKey(item.ToString()));
        // a ClearItem of generation 1 (queued before the interval, replayed from the outbox after it)
        var stale = await fx.Send(new ClearItem(item, fx.K(), "left", 1));
        Assert.Equal(CapacityOutcome.Stale, stale.Outcome);
        Assert.True(fx.State.InFlight.ContainsKey(item.ToString()) || fx.State.Queue.ContainsKey(item.ToString()) || fx.State.Slots.ContainsKey(item.ToString()));
        Assert.Equal(2, CapacityScheduler.GenerationOf(fx.State, item.ToString()));
        // its own generation, and an unnamed one (every sender before W9.10n), clear
        var own = await fx.Send(new ClearItem(item, fx.K(), "left", 2));
        Assert.NotEqual(CapacityOutcome.Stale, own.Outcome);
    }

    [Fact]
    public async Task L6_AClearWithoutAGenerationClearsAsBefore_AndAFootprintOfAnEarlierInstanceChangesNothing()
    {
        var fx = new CapacityFixture();
        var item = CapacityFixture.I(1);
        await fx.Send(new RequestSlot(item, fx.K(), 2, "cursor-cloud", CapacityPhases.Develop, new PlanRank(false, null, false), false, false, 2, fx.Now, [], new Footprint(["src/a.cs"], FootprintSource.Design)));
        Assert.True(fx.State.InFlight.ContainsKey(item.ToString()));
        Assert.Equal(CapacityOutcome.Stale, (await fx.Send(new FootprintChanged(item, fx.K(), ["src/z.cs"], 1))).Outcome);
        Assert.Contains("src/a.cs", fx.State.InFlight[item.ToString()].Footprint.Entries);
        Assert.Equal(CapacityOutcome.Applied, (await fx.Send(new FootprintChanged(item, fx.K(), ["src/z.cs"], 2))).Outcome);
        Assert.Equal(CapacityOutcome.Applied, (await fx.Send(new ClearItem(item, fx.K(), "left"))).Outcome);
        Assert.False(fx.State.InFlight.ContainsKey(item.ToString()));
    }

    // ---- the merge line and the delivery train -------------------------------------------------------------------------------------------------------------------------------

    static MergeTrainEntry Entry(int n, int generation = 1, int pr = 11, long order = 1) => new() { Item = $"acme/orders#{n}", Generation = generation, ArmId = $"{generation}.4", PullRequest = pr, Order = order };

    static WorkItemState Waiting(int n, int generation = 1, int pr = 11)
    {
        var s = First(Verify, State(rev: 5, extra: Pass())).State!;
        s.Item = $"acme/orders#{n}"; s.Generation = generation; s.PullRequest = pr; s.TrainEntered = true; s.Step = StepKind.Merge; s.MergeCommit = "";
        return s;
    }

    [Fact]
    public void M1_AnEntryThatStillStandsForAnOpenSagasRequestStaysInTheLine()
    {
        var line = MergeTrainState.New("acme/orders"); line.Line.Add(Entry(7));
        var (next, removed) = AdoptionTrainRows.ValidateMergeLine(line, new Dictionary<string, WorkItemState> { ["acme/orders#7"] = Waiting(7) });
        Assert.Empty(removed);
        Assert.Equal(line.ToJson(), next.ToJson());
    }

    [Theory]
    [InlineData("no row")]
    [InlineData("ended")]
    [InlineData("generation")]
    [InlineData("step")]
    [InlineData("merged")]
    [InlineData("no place")]
    [InlineData("pull request")]
    public void M2_AnEntryBuiltBeforeTheIntervalGoesWhenTheSagaNoLongerStandsBehindIt(string case_)
    {
        var line = MergeTrainState.New("acme/orders"); line.Line.Add(Entry(7)); line.Line.Add(Entry(8, order: 2));
        var saga = Waiting(7);
        switch (case_)
        {
            case "ended": saga.Status = WorkItemStatus.Completed; break;
            case "generation": saga.Generation = 2; break;
            case "step": saga.Step = StepKind.Deliver; break;
            case "merged": saga.MergeCommit = new string('a', 40); break;
            case "no place": saga.TrainEntered = false; break;
            case "pull request": saga.PullRequest = 12; break;
        }
        var sagas = new Dictionary<string, WorkItemState> { ["acme/orders#8"] = Waiting(8) };
        if (case_ != "no row") sagas["acme/orders#7"] = saga;
        var (next, removed) = AdoptionTrainRows.ValidateMergeLine(line, sagas);
        Assert.Equal("acme/orders#7", Assert.Single(removed).Row);
        Assert.Equal(["acme/orders#8"], next.Line.Select(e => e.Item));
        Assert.Equal(2, line.Line.Count);   // (a copy)
    }

    [Fact]
    public async Task M3_ALeaveOfAnEarlierInstanceDoesNotTakeTheNewInstancesPlace_ALeaveWithoutAGenerationDoes()
    {
        var store = new InMemoryMergeTrainStore();
        var svc = new MergeTrainService(store, new MergeTrainSettings(MergeStrategy.Direct, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), null), () => AdoptionFixture.Now);
        var id = new ItemId("acme", "orders", 7);
        await svc.Handle(new ReadyToMerge("acme/orders", new MergeRequested(id, 2, "2.1", 1, 11, new string('a', 40), 0, "")));
        await svc.Handle(new MergeTrainLeft("acme/orders", id, "left", 1));
        Assert.Single(store.States["acme/orders"].Line);
        await svc.Handle(new MergeTrainLeft("acme/orders", id, "left", 2));
        Assert.Empty(store.States["acme/orders"].Line);
        await svc.Handle(new ReadyToMerge("acme/orders", new MergeRequested(id, 2, "2.1", 1, 11, new string('a', 40), 0, "")));
        await svc.Handle(new MergeTrainLeft("acme/orders", id, "merged elsewhere"));
        Assert.Empty(store.States["acme/orders"].Line);
    }

    static TrainCandidate Candidate(int n, string commit) => new() { Item = $"acme/orders#{n}", Commit = commit, PullRequest = 10 + n, MergedAt = Now };

    [Fact]
    public void D1_ACandidateStaysWhenTheMergedItemsTableHoldsItsMerge_AndGoesWhenItDoesNotOrItWasDelivered()
    {
        var train = TrainState.New("cmdemo1-ui/tdd");
        var a = new string('a', 40); var b = new string('b', 40); var c = new string('c', 40);
        train.Candidates["acme/orders#7"] = Candidate(7, a);
        train.Candidates["acme/orders#8"] = Candidate(8, b);       // (the item was reopened and merged again as c)
        train.Candidates["acme/orders#9"] = Candidate(9, c);
        train.DeliveredItems.Add($"acme/orders#9|{c}");
        var merged = new HashSet<string> { $"acme/orders#7|{a}", $"acme/orders#8|{c}", $"acme/orders#9|{c}" };
        var (next, removed) = AdoptionTrainRows.ValidateCandidates(train, merged);
        Assert.Equal(["acme/orders#7"], next.Candidates.Keys);
        Assert.Equal(["acme/orders#8", "acme/orders#9"], removed.Select(r => r.Row));
        Assert.Equal(3, train.Candidates.Count);   // (a copy)
    }

    // ---- every timer and command carries the instance -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void G1_EveryTimerTheSagaSchedulesNamesItsGeneration()
    {
        var open = First(Develop, State(rev: 5, extra: Claimed())).State!;
        open.Generation = 3; open.ArmId = "3.2";
        open.MergeRetryAt = Now.AddMinutes(1); open.MasterState = "waiting"; open.MasterDue = Now.AddMinutes(30);
        var parked = WorkItemState.FromJson(open.ToJson()); parked.Parked = true; parked.ParkUntil = Now.AddHours(1); parked.OwnerIdleDue = Now.AddMinutes(10); parked.OwnerReleased = false;
        var settling = WorkItemState.FromJson(open.ToJson()); settling.Status = WorkItemStatus.Settling; settling.SettleUntil = Now.AddHours(1);
        var timers = WorkItemSaga.TimersOf(open).Concat(WorkItemSaga.TimersOf(parked)).Concat(WorkItemSaga.TimersOf(settling)).Select(t => t.Message).ToList();
        Assert.Equal(["MasterWatchElapsed", "MergeRetryDue", "OwnerIdleElapsed", "ParkExpired", "PhaseLeaseExpired", "SettleWindowElapsed"], timers.Select(t => t.GetType().Name).Distinct().Order());
        Assert.All(timers, t => Assert.Equal(3, Assert.IsAssignableFrom<IGenerationInput>(t).Generation));
    }

    [Fact]
    public void G2_EveryCommandTheSagaSendsNamesItsGeneration_ExceptTheEndOfASessionWhichNamesItsSlot()
    {
        var s = First(Verify, State(rev: 5, extra: Pass())).State!;
        s.Generation = 4; s.ArmId = "4.2"; s.MergeRequestEmitted = true; s.MergeRequest = new MergeRequestState { Attempt = 1, PullRequest = 11, Head = new string('b', 40) };
        s.Outbox =
        [
            new AskSlot("ask|1", s.ArmId, "verify", false, 3, [], false, Now), new EndSession("end|1", "s1"), new ClearSlot("clear|1", "x"), new StartSession("start|1", s.ArmId, "s1", "cloud", null, "verify"),
            new ChangeFootprint("fp|1", ["a.cs"]), new LeaveTrain("leave|1", "x"), new ReleaseOwner("release|1", "x"),
        ];
        var commands = WorkItemSaga.CommandsOf(s);
        Assert.Equal(8, commands.Count);
        foreach (var c in commands)
        {
            if (c is SessionEnded) continue;
            var property = c.GetType().GetProperty("Generation", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{c.GetType().Name} names no generation");
            Assert.Equal(4, (int)property!.GetValue(c)!);
        }
    }

    // ---- the scenario of the plan -----------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S1_AnItemV1MergedDuringTheSwitchIsNotTouchedByV2Afterwards_AndNoStaleTimerActs()
    {
        // two items run on v2 when the kill switch goes on: #7 in Development with an owner's session, #8 a PASS waiting for its merge
        var seven = First(Develop, State(rev: 5, extra: Claimed()));
        var eight = AdoptionReconciler.Reconcile(null, 0, 0, AdoptionMapper.Map(Snapshot(Verify, State(rev: 5, extra: Pass()), s => s with { Item = ItemId.Parse("acme/orders#8") }), Settings(), Now), Now);
        var oldTimers = seven.Timers.Concat(eight.Timers).Select(t => t.Message).Cast<WorkItemInput>().ToList();
        Assert.NotEmpty(oldTimers);
        var ledger = AdoptionLedger.Apply(new CapacityState(), seven.Slot, seven.Flight);
        ledger = AdoptionLedger.Apply(ledger, eight.Slot, eight.Flight);
        var line = MergeTrainState.New("acme/orders"); line.Line.Add(Entry(8, order: 1));
        eight.State!.TrainEntered = true;

        // v1 acts while it is on: it merges #7 and settles it (done), and merges #8, which waits for master's CI
        // (#7: merged and settled; #8: merged, awaiting master-green)
        var settledSeven = AdoptionReconciler.Reconcile(seven.State, 1, 0, AdoptionMapper.Map(Snapshot(Develop, State(rev: 11, extra: Settled()), s => s), Settings(), Now, 2), Now, afterInterval: true);
        var mergedEight = AdoptionReconciler.Reconcile(eight.State, 1, 0, AdoptionMapper.Map(Snapshot(Verify, State(rev: 11, extra: Merged("master-green")), s => s with { Item = ItemId.Parse("acme/orders#8") }), Settings(), Now, 2), Now, afterInterval: true);

        // the switch is unset: #7 is closed and #8 is the next generation, on its Deliver step
        Assert.Equal(AdoptionKind.Closed, settledSeven.Kind);
        Assert.Equal((AdoptionKind.Updated, 2, StepKind.Deliver), (mergedEight.Kind, mergedEight.State!.Generation, mergedEight.State.Step));
        var sagas = new Dictionary<string, WorkItemState> { ["acme/orders#7"] = settledSeven.State!, ["acme/orders#8"] = mergedEight.State! };

        // the ledger holds nothing of #7 and nothing of #8's first instance; the line holds nothing built before the interval
        ledger = AdoptionLedger.Fence(AdoptionLedger.Fence(ledger, settledSeven.Fence!), mergedEight.Fence!);
        Assert.Empty(ledger.Slots.Keys.Concat(ledger.InFlight.Keys).Concat(ledger.Queue.Keys));
        Assert.Empty(AdoptionTrainRows.ValidateMergeLine(line, sagas).State.Line);

        // no timer that was scheduled before the interval acts: the saga answers every one of them NotFound
        foreach (var timer in oldTimers) Assert.Equal(WorkItemOutcome.NotFound, Outcome(sagas[timer.Item.ToString()], timer));
        // v2 does nothing more to #7: every input finds a completed instance
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(sagas["acme/orders#7"], new StepCompleted(Item, 0, "1.1", "late-done")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(sagas["acme/orders#7"], new HoldObserved(Item, 0, "late-hold", "hold:owner")));
        Assert.Equal(WorkItemOutcome.NotFound, Outcome(sagas["acme/orders#7"], new ParkWorkItem(Item, 0, "late-park", "x")));
        // and #8's new instance carries its own timers
        Assert.All(mergedEight.Timers, t => Assert.Equal(2, ((IGenerationInput)t.Message).Generation));
    }
}
