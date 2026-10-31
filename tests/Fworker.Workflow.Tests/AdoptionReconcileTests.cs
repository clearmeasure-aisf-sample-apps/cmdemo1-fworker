using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10m: the rule of the reconciliation (the state comment wins when its revision is newer), the edits of the scheduler's ledger and of the delivery train that adoption makes, and the idempotence of all of it.
/// Rows R (the reconciliation), C (the ledger) and T (the train) of the table in the plan.
/// </summary>
public class AdoptionReconcileTests
{
    static AdoptionMapping Map(string column, JsonObject state, AdoptionSettings? settings = null) => AdoptionMapper.Map(Snapshot(column, state), settings ?? Settings(), Now);

    static string Claimed() => Join(Claim(Develop, true, TimeSpan.FromMinutes(25)), "\"fix_forward_rounds\": 1");

    // ---- R: the upsert at every start -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void R1_AnItemWithoutARowIsAdopted_WithOneDecisionItsTimersAndItsCommands()
    {
        var m = Map(Develop, State(extra: Claimed()));
        var r = AdoptionReconciler.Reconcile(null, 0, m, Now);
        Assert.Equal(AdoptionKind.Adopted, r.Kind);
        Assert.True(r.Save);
        var d = r.Decision!;
        Assert.Equal((WorkItemActions.Adopted, 1L, 1, "Develop"), (d.Action, d.Sequence, d.Generation, d.Step));
        Assert.Equal((1L, "adopt|5"), (r.State!.Sequence, r.State.LastKey));
        Assert.Equal([d], r.State.Emitted);
        Assert.Contains(r.Timers, t => t.Message is PhaseLeaseExpired);
        Assert.Equal(m.Slot, r.Slot);
    }

    [Fact]
    public void R2_AnItemThatAsksForASlotAdoptsWithTheRequestAsACommand()
    {
        var r = AdoptionReconciler.Reconcile(null, 0, Map(Design, State()), Now);
        var wanted = Assert.IsType<SlotWanted>(Assert.Single(r.Commands));
        Assert.Equal(("design", r.State!.ArmId), (wanted.Phase, wanted.ArmId));
    }

    [Fact]
    public void R3_AdoptingAgainWhatWasAdoptedAndNotChangedChangesNothingButTheRevisionRead()
    {
        var first = AdoptionReconciler.Reconcile(null, 0, Map(Develop, State(rev: 5, extra: Claimed())), Now);
        var again = AdoptionReconciler.Reconcile(first.State, first.State!.Sequence, Map(Develop, State(rev: 6, extra: Claimed())), Now);
        Assert.Equal(AdoptionKind.Unchanged, again.Kind);
        Assert.Null(again.Decision);
        Assert.Empty(again.Timers.Concat<object>(again.Commands));
        Assert.Equal(6, again.State!.CommentRev);
        Assert.Equal(first.State.Sequence, again.State.Sequence);
    }

    [Fact]
    public void R4_ACommentThatIsNotNewerLeavesTheRowAlone()
    {
        var first = AdoptionReconciler.Reconcile(null, 0, Map(Develop, State(rev: 5, extra: Claimed())), Now);
        var r = AdoptionReconciler.Reconcile(first.State, 1, Map(Verify, State(rev: 5)), Now);   // (the board says another thing, the revision is the same)
        Assert.Equal(AdoptionKind.Kept, r.Kind);
        Assert.False(r.Save);
        Assert.Same(first.State, r.State);
    }

    [Fact]
    public void R5_ANewerCommentWins_TheItemMovedToAnotherStepTakesANewArm_AndTheOldSessionGoesBack()
    {
        var first = AdoptionReconciler.Reconcile(null, 0, Map(Develop, State(rev: 5, extra: Claimed())), Now);
        var moved = Map(Verify, State(rev: 8, extra: Join("\"fix_forward_rounds\": 1", Claim(Verify, true, TimeSpan.FromMinutes(-1)))));
        var r = AdoptionReconciler.Reconcile(first.State, 1, moved, Now);
        Assert.Equal(AdoptionKind.Updated, r.Kind);
        var s = r.State!;
        Assert.Equal((StepKind.Verify, "1.2", 8, 2L, 1), (s.Step, s.ArmId, s.CommentRev, s.Sequence, s.Generation));
        Assert.Equal((WorkItemActions.Adopted, 2L), (r.Decision!.Action, r.Decision.Sequence));
        Assert.Contains("Step", r.Decision.Reasons[1]);
        // the live session of the old step ends, and the new step asks (the claim ran out)
        Assert.Contains(r.Commands, c => c is Fworker.Workflows.Capacity.SessionEnded e && e.SlotId == first.State!.SlotId);
        Assert.Contains(r.Commands, c => c is SlotWanted w && w.Phase == "verify" && w.ArmId == "1.2");
    }

    [Fact]
    public void R6_ANewerCommentOnTheSameStepKeepsTheArmAndTheSagasOwnSlot()
    {
        var first = AdoptionReconciler.Reconcile(null, 0, Map(Develop, State(rev: 5, extra: Claimed())), Now);
        var saga = WorkItemState.FromJson(first.State!.ToJson());
        saga.SlotId = "s3"; saga.SlotRuntime = "local"; saga.Sequence = 4; saga.Keys.Add("k1"); saga.ArmId = "1.4"; saga.ArmSeq = 4;
        var renewed = Map(Develop, State(rev: 9, extra: Join(Claim(Develop, true, TimeSpan.FromMinutes(28)), "\"fix_forward_rounds\": 2")));
        var r = AdoptionReconciler.Reconcile(saga, 4, renewed, Now);
        Assert.Equal(AdoptionKind.Updated, r.Kind);
        var s = r.State!;
        Assert.Equal(("1.4", "s3", 2, 5L), (s.ArmId, s.SlotId, s.FixForwardRounds, s.Sequence));
        Assert.Equal(["k1"], s.Keys);
        Assert.Equal(Now + TimeSpan.FromMinutes(28), s.LeaseDue);   // (the claim was renewed: the later lease)
        Assert.Null(r.Slot);
    }

    [Fact]
    public void R7_ADecisionTheProjectionHasNotWrittenYetIsNewerThanTheComment()
    {
        var first = AdoptionReconciler.Reconcile(null, 0, Map(Develop, State(rev: 5, extra: Claimed())), Now);
        var saga = WorkItemState.FromJson(first.State!.ToJson()); saga.Sequence = 3;
        var r = AdoptionReconciler.Reconcile(saga, 2, Map(Verify, State(rev: 9)), Now);
        Assert.Equal(AdoptionKind.Kept, r.Kind);
        Assert.Contains("projection", r.Reason);
    }

    [Fact]
    public void R8_ARevisionThatMovedOnlyBecauseTheSagasOwnProjectionWroteTheCommentChangesNothing()
    {
        // the saga started this item itself (CommentRev 0), parked it, and the projection wrote the park: the comment is newer and says the same
        var parked = State(rev: 7, extra: Park("same-criterion-repeated"));
        var saga = WorkItemState.FromJson(Mapped(Develop, parked).ToJson());
        saga.CommentRev = 0; saga.Sequence = 6; saga.Source = "started"; saga.Generation = 2; saga.ArmId = "2.3"; saga.ArmSeq = 3;
        var r = AdoptionReconciler.Reconcile(saga, 6, Map(Develop, parked), Now);
        Assert.Equal(AdoptionKind.Unchanged, r.Kind);
        Assert.Equal((2, "2.3", 6L, 7, "started"), (r.State!.Generation, r.State.ArmId, r.State.Sequence, r.State.CommentRev, r.State.Source));
    }

    [Fact]
    public void R9_ACompletedInstanceIsNotBroughtBack_TheFencingOfAReopenedItemIsW910n()
    {
        var done = WorkItemState.FromJson(Mapped(Develop, State()).ToJson()); done.Status = WorkItemStatus.Completed;
        var r = AdoptionReconciler.Reconcile(done, 0, Map(Develop, State(rev: 9)), Now);
        Assert.Equal((AdoptionKind.Skipped, false), (r.Kind, r.Save));
    }

    [Fact]
    public void R10_ASkippedMappingIsSkipped_AndTheRowOfAnItemV1HasSettledIsLeftForTheFencing()
    {
        var saga = WorkItemState.FromJson(Mapped(Develop, State()).ToJson());
        var r = AdoptionReconciler.Reconcile(saga, 0, Map(Develop, State(rev: 9, extra: Merged("release-queue", $"\"done_at\": \"{Iso(TimeSpan.FromHours(1))}\""))), Now);
        Assert.Equal((AdoptionKind.Skipped, "settled", false), (r.Kind, r.Reason, r.Save));
        Assert.Null(r.State);
    }

    [Fact]
    public void R11_TheRowsBesideTheStateAreGivenWhateverTheRuleDecided()
    {
        var qc = """ "qc": { "11": { "pr": 11, "state": "ready" } } """;
        var saga = AdoptionReconciler.Reconcile(null, 0, Map(Verify, State(rev: 5, extra: Join(Merged("master-green"), qc))), Now).State!;
        var kept = AdoptionReconciler.Reconcile(saga, 1, Map(Verify, State(rev: 5, extra: Join(Merged("master-green"), qc))), Now);
        Assert.Equal(AdoptionKind.Kept, kept.Kind);
        Assert.Single(kept.QcRows);
        Assert.NotNull(kept.MergedRow);
    }

    [Fact]
    public void R12_AMergedItemParkedByTheCommentAfterTheSagaWasAdoptedIsUpdated_WithTheNewArm()
    {
        var saga = AdoptionReconciler.Reconcile(null, 0, Map(Verify, State(rev: 5, extra: Merged("master-green"))), Now).State!;
        var broken = Map(Verify, State(rev: 9, extra: Join(Merged("master-broken"), Park("master-broken", more: "\"failed\": [\"Build result\"]"))));
        var r = AdoptionReconciler.Reconcile(saga, 1, broken, Now);
        Assert.Equal(AdoptionKind.Updated, r.Kind);
        Assert.Equal(("broken", true, "1.2"), (r.State!.MasterState, r.State.Parked, r.State.ArmId));
        Assert.Contains(r.Timers, t => t.Message is ParkExpired);
    }

    // ---- C: the scheduler's ledger ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void C1_TheAdoptedSlotIsInTheLedger_AndTheItemIsInFlight_WithoutCountingAnExecution()
    {
        var m = Map(Develop, State(extra: Claimed()));
        var ledger = AdoptionLedger.Apply(new CapacityState(), m.Slot, m.Flight);
        var held = ledger.Slots[Item.ToString()];
        Assert.Equal((m.State!.SlotId, "cloud", SlotKind.Guaranteed, Now + TimeSpan.FromMinutes(25), true), (held.SlotId, held.Runtime, held.Kind, held.ExpiresAt, held.EnteredFlight));
        Assert.Contains(Item.ToString(), ledger.InFlight.Keys);
        Assert.Empty(ledger.ExecutionsToday);
    }

    [Fact]
    public void C2_AdoptingTwiceIsTheSameLedger()
    {
        var m = Map(Develop, State(extra: Claimed()));
        var once = AdoptionLedger.Apply(new CapacityState(), m.Slot, m.Flight);
        Assert.Equal(once.ToJson(), AdoptionLedger.Apply(once, m.Slot, m.Flight).ToJson());
    }

    [Fact]
    public void C3_ASlotTheLedgerHoldsIsNotReplaced_AndTheGivenLedgerIsNotChanged()
    {
        var m = Map(Develop, State(extra: Claimed()));
        var ledger = new CapacityState();
        ledger.Slots[Item.ToString()] = new LedgerSlot { SlotId = "s1", Item = Item.ToString(), Runtime = "local" };
        var next = AdoptionLedger.Apply(ledger, m.Slot, m.Flight);
        Assert.Equal("s1", next.Slots[Item.ToString()].SlotId);
        Assert.Empty(AdoptionLedger.Apply(new CapacityState(), null, null).Slots);
        Assert.Equal("s1", ledger.Slots[Item.ToString()].SlotId);
    }

    [Fact]
    public void C4_AnAdoptedSlotTakesTheItemOutOfTheQueue()
    {
        var m = Map(Develop, State(extra: Claimed()));
        var ledger = new CapacityState();
        ledger.Queue[Item.ToString()] = new WaitingRequest { Item = Item.ToString() };
        Assert.DoesNotContain(Item.ToString(), AdoptionLedger.Apply(ledger, m.Slot, m.Flight).Queue.Keys);
    }

    [Fact]
    public void C5_AFeaturePassInTheMergeLineIsInFlightWithoutASlot()
    {
        var m = Map(Verify, State(extra: Pass()));
        var ledger = AdoptionLedger.Apply(new CapacityState(), m.Slot, m.Flight);
        Assert.Equal((false, true), (ledger.Slots.ContainsKey(Item.ToString()), ledger.InFlight.ContainsKey(Item.ToString())));
    }

    // ---- T: the delivery train's Sign-offs ----------------------------------------------------------------------------------------------------------------------

    static JsonObject Promoted(string env = "tdd", string task = "T-1", string release = "1.0.7", TimeSpan? waitSince = null) =>
        State(extra: Merged("release-queue", $"\"promoted\": {{ \"{env}\": {{ \"task\": \"{task}\", \"at\": \"{Iso(TimeSpan.FromMinutes(40))}\", \"release\": \"{release}\"{(waitSince is { } w ? $", \"signoff_wait_since\": \"{Iso(w)}\"" : "")} }} }}"));

    static PendingSignOff Wait(string task = "T-1", string id = "I-1") => new(id, "D-1", task, "1.0.7", Now - TimeSpan.FromMinutes(5));

    [Fact]
    public void T1_ThePromotionsV1RecordedAreRead()
    {
        var p = Assert.Single(AdoptionTrain.Promotions(Item.ToString(), Promoted(waitSince: TimeSpan.FromMinutes(25))));
        Assert.Equal(("tdd", "T-1", "1.0.7", Now - TimeSpan.FromMinutes(40), (DateTimeOffset?)(Now - TimeSpan.FromMinutes(25))), (p.Environment, p.TaskId, p.Release, p.At, p.SignOffWaitSince));
    }

    [Fact]
    public void T2_ASignOffAtTheGatewaysOwnDeploymentIsTheTrainsToAnswer_WaitingSinceV1RecordedItAndCarryingTheReleasesItems()
    {
        var train = TrainState.New("cmdemo1-ui/tdd");
        var next = AdoptionTrain.SignOffs(train, [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted(waitSince: TimeSpan.FromMinutes(25))));
        var so = Assert.Single(next.SignOffs);
        Assert.Equal(("I-1", "T-1", "1.0.7", Now - TimeSpan.FromMinutes(25), false), (so.InterruptionId, so.TaskId, so.Version, so.Since, so.Answered));
        Assert.Equal((Now - TimeSpan.FromMinutes(40), 1), (next.Requested["1.0.7"], next.RequestNumbers["1.0.7"]));
        Assert.Equal([Item.ToString()], next.RequestItems["1.0.7"]);
        Assert.Empty(train.SignOffs);   // (a copy)
    }

    [Fact]
    public void T3_WithoutARecordedWaitTheSignOffWaitsSinceOctopusShowsIt()
    {
        var next = AdoptionTrain.SignOffs(TrainState.New("cmdemo1-ui/tdd"), [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted()));
        Assert.Equal(Now - TimeSpan.FromMinutes(5), Assert.Single(next.SignOffs).Since);
    }

    [Fact]
    public void T4_ASignOffAtSomebodyElsesDeploymentIsNotTheTrainsToAnswer()
    {
        var next = AdoptionTrain.SignOffs(TrainState.New("cmdemo1-ui/tdd"), [Wait(task: "T-other")], AdoptionTrain.Promotions(Item.ToString(), Promoted()));
        Assert.Empty(next.SignOffs);
        Assert.Empty(next.Requested);
    }

    [Fact]
    public void T5_APromotionToAnotherEnvironmentIsNotThisTrains()
    {
        var next = AdoptionTrain.SignOffs(TrainState.New("cmdemo1-ui/tdd"), [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted(env: "uat")));
        Assert.Empty(next.SignOffs);
    }

    [Fact]
    public void T6_ASignOffTheTrainKnowsIsLeftAsItIs_ItMayHaveBeenAnswered()
    {
        var train = TrainState.New("cmdemo1-ui/tdd");
        train.SignOffs.Add(new TrainSignOff { InterruptionId = "I-1", TaskId = "T-1", Version = "1.0.7", Since = Now.AddHours(-1), Answered = true, AnsweredAt = Now.AddMinutes(-2) });
        var next = AdoptionTrain.SignOffs(train, [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted()));
        Assert.Equal(train.ToJson(), next.ToJson());
    }

    [Fact]
    public void T7_TwoItemsOfOneReleaseAreBothCarried_AndTheEarliestRecordedWaitCounts()
    {
        var other = ItemId.Parse("acme/orders#8");
        var promotions = AdoptionTrain.Promotions(Item.ToString(), Promoted(waitSince: TimeSpan.FromMinutes(25))).Concat(AdoptionTrain.Promotions(other.ToString(), Promoted(waitSince: TimeSpan.FromMinutes(35)))).ToList();
        var next = AdoptionTrain.SignOffs(TrainState.New("cmdemo1-ui/tdd"), [Wait()], promotions);
        Assert.Equal(Now - TimeSpan.FromMinutes(35), Assert.Single(next.SignOffs).Since);
        Assert.Equal(["acme/orders#7", "acme/orders#8"], next.RequestItems["1.0.7"]);
    }

    [Fact]
    public void T8_AdoptingTheSignOffsTwiceIsTheSameTrain()
    {
        var once = AdoptionTrain.SignOffs(TrainState.New("cmdemo1-ui/tdd"), [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted()));
        Assert.Equal(once.ToJson(), AdoptionTrain.SignOffs(once, [Wait()], AdoptionTrain.Promotions(Item.ToString(), Promoted())).ToJson());
    }
}
