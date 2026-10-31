using Fworker.Contracts.Ids;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.SagaFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10h: the saga's side of the inputs its producers send (docs/design/fworker-wolverine-implementation.md W9.10h): the issue closed by a person, a hold label that parks the item and its removal, the card moved back, and
/// the resume that offers a parked item again. The producers themselves (the ingress inbox, the admin endpoint, the runtime manager) are tested in Gateway.Tests.
/// </summary>
public class SagaProducerInputsTests
{
    static Task<WorkItemResult> Closed(SagaFixture fx, string key = "closed-1") => fx.Send(new ItemClosed(Item, key, "webhook"));
    static Task<WorkItemResult> Hold(SagaFixture fx, string? hold, string key) => fx.Send(new HoldObserved(Item, 0, key, hold));
    static Task<WorkItemResult> Moved(SagaFixture fx, string column, StepKind? step, string key = "moved-1") => fx.Send(new CardMoved(Item, 0, key, column, step));

    // ---- the issue closed ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StepKind.Design)]
    [InlineData(StepKind.Develop)]
    [InlineData(StepKind.Verify)]
    public async Task AClosedIssue_BeforeTheMerge_CompletesTheInstance_AndFreesWhatItHeld(StepKind step)
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(step);
        var r = await Closed(fx);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
        Assert.Contains(WorkItemActions.Completed, fx.Actions);
        Assert.Contains("the issue was closed", fx.Decisions.Last().Reasons);
        Assert.Null(fx.State.LeaseDue);
        // and the next generation can start: a completed instance that is reopened begins again
        var again = await fx.Send(new ReopenWorkItem(Item, "the issue was reopened", "reopened-1"));
        Assert.Equal((WorkItemOutcome.Applied, 2), (again.Decision.Outcome, again.Decision.Generation));
    }

    [Fact]
    public async Task AClosedIssue_OfAParkedItemAtTheMerge_CompletesIt_ButNotWhileTheMergeStepDecides()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Merge);
        // a merge closes its own issue ("Closes #N"): the merge step is on its way, the close is not a person's
        var merging = await Closed(fx, "closed-merge");
        Assert.Equal(WorkItemOutcome.Refused, merging.Decision.Outcome);
        Assert.Equal(WorkItemStatus.Open, fx.State.Status);
        // parked at the merge (blocked for good) and then closed by a person: nothing is left to merge
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "merge-blocked"));
        var parked = await Closed(fx, "closed-parked");
        Assert.Equal(WorkItemOutcome.Applied, parked.Decision.Outcome);
        Assert.Equal(WorkItemStatus.Completed, fx.State.Status);
    }

    [Fact]
    public async Task AClosedIssue_OfAMergedOrDeliveredItem_ChangesNothing()
    {
        var merged = new SagaFixture();
        await merged.Start(plan: "feature");
        await merged.Merged();
        Assert.Equal(WorkItemOutcome.Refused, (await Closed(merged)).Decision.Outcome);
        Assert.Equal(StepKind.Deliver, merged.State.Step);

        var delivered = new SagaFixture();
        await delivered.Start(plan: "feature");
        await delivered.Merged();
        await delivered.Delivered("prod");
        Assert.Equal(WorkItemStatus.Settling, delivered.State.Status);
        Assert.Equal(WorkItemOutcome.Refused, (await Closed(delivered)).Decision.Outcome);
        Assert.Equal(WorkItemStatus.Settling, delivered.State.Status);
    }

    [Fact]
    public async Task ACloseThatIsRepeated_IsAppliedOnce_AndAnItemWithoutASaga_AnswersNotFound()
    {
        var fx = new SagaFixture();
        Assert.Equal(WorkItemOutcome.NotFound, (await Closed(fx)).Decision.Outcome);
        await fx.Start(plan: "docs");
        Assert.Equal(WorkItemOutcome.Applied, (await Closed(fx, "k")).Decision.Outcome);
        var sequence = fx.State.Sequence;
        Assert.Equal(WorkItemOutcome.Duplicate, (await Closed(fx, "k")).Decision.Outcome);   // (a redelivery publishes what the transition published; it decides nothing new)
        Assert.Equal(sequence, fx.State.Sequence);
        Assert.Equal(WorkItemOutcome.NotFound, (await Closed(fx, "k2")).Decision.Outcome);   // (the instance is completed now)
    }

    // ---- a hold label --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHoldLabel_ParksTheItem_AndItsRemoval_LiftsThatPark_AndArmsTheStepAgain()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        var arm = fx.State.ArmId;
        Assert.Equal(WorkItemOutcome.Applied, (await Hold(fx, "hold:owner", "h-1")).Decision.Outcome);
        Assert.Equal((true, "hold: hold:owner"), (fx.State.Parked, fx.State.ParkReason));
        Assert.Null(fx.State.LeaseDue);
        // a second observation of the same hold, or a hold on a parked item, changes nothing
        Assert.Equal(WorkItemOutcome.Refused, (await Hold(fx, "hold:owner", "h-2")).Decision.Outcome);
        var lifted = await Hold(fx, null, "h-3");
        Assert.Equal(WorkItemOutcome.Applied, lifted.Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.NotEqual(arm, fx.State.ArmId);   // (a new arm: the step starts again)
        Assert.Equal([WorkItemActions.Parked, WorkItemActions.Resumed, WorkItemActions.StepEntered], fx.Actions.Skip(2));
    }

    [Fact]
    public async Task TheRemovalOfAHoldLabel_LiftsOnlyAParkItMade_NotAPersonsOrTheSagas()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "runtime-failed"));
        Assert.Equal(WorkItemOutcome.Refused, (await Hold(fx, null, "h-1")).Decision.Outcome);
        Assert.Equal((true, "runtime-failed"), (fx.State.Parked, fx.State.ParkReason));
        // and with no hold at all, nothing happens to an item that runs
        var running = new SagaFixture();
        await running.Start(plan: "docs");
        Assert.Equal(WorkItemOutcome.Refused, (await Hold(running, null, "h-2")).Decision.Outcome);
        Assert.False(running.State.Parked);
    }

    [Fact]
    public async Task AHoldLabel_OnADeliveredItem_IsRefused()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "feature");
        await fx.Merged();
        Assert.Equal(WorkItemOutcome.Refused, (await Hold(fx, "hold:owner", "h-1")).Decision.Outcome);
        Assert.False(fx.State.Parked);
    }

    // ---- the card moved back ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACardMovedBackToAnEarlierStepsColumn_SendsTheItemBackToThatStep()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Verify);
        var arm = fx.State.ArmId;
        var r = await Moved(fx, "Development", StepKind.Develop);
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.Equal((StepKind.Develop, WorkItemStatus.Open), (fx.State.Step, fx.State.Status));
        Assert.NotEqual(arm, fx.State.ArmId);
        var last = fx.Decisions.TakeLast(2).ToList();
        Assert.Equal([WorkItemActions.MovedBack, WorkItemActions.StepEntered], last.Select(d => d.Action));
        Assert.Equal(fx.State.CardSeq, last[1].Sequence);   // (the version the card projection carries for the step the item is on)
    }

    [Fact]
    public async Task ACardMoveToTheStepsOwnColumn_Forward_ToNoStepsColumn_OrOnAParkedItem_ChangesNothing()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.WalkTo(StepKind.Develop);
        var arm = fx.State.ArmId;
        Assert.Equal(WorkItemOutcome.Refused, (await Moved(fx, "Development", StepKind.Develop, "m-1")).Decision.Outcome);
        Assert.Equal(WorkItemOutcome.Refused, (await Moved(fx, "Functional Testing", StepKind.Verify, "m-2")).Decision.Outcome);   // ahead: the saga follows the work, not the card
        Assert.Equal(WorkItemOutcome.Refused, (await Moved(fx, "Todo", null, "m-3")).Decision.Outcome);
        Assert.Equal((StepKind.Develop, arm), (fx.State.Step, fx.State.ArmId));
        await fx.Send(new ParkWorkItem(Item, 0, "park-1", "runtime-failed"));
        Assert.Equal(WorkItemOutcome.Refused, (await Moved(fx, "Technical Design", StepKind.Design, "m-4")).Decision.Outcome);
        Assert.Equal(StepKind.Develop, fx.State.Step);
    }

    [Fact]
    public async Task ACardMovedBack_OnAMergedItem_IsRefused()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "feature");
        await fx.Merged();
        Assert.Equal(WorkItemOutcome.Refused, (await Moved(fx, "Technical Design", StepKind.Design)).Decision.Outcome);
        Assert.Equal(StepKind.Deliver, fx.State.Step);
    }

    [Fact]
    public async Task EveryStepEntry_RecordsTheSequenceOfItsDecision_AsTheCardsVersion()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        Assert.Equal(fx.Decisions.Last(d => d.Action == WorkItemActions.StepEntered).Sequence, fx.State.CardSeq);
        await fx.WalkTo(StepKind.Verify);
        Assert.Equal(fx.Decisions.Last(d => d.Action == WorkItemActions.StepEntered).Sequence, fx.State.CardSeq);
        Assert.True(fx.State.CardSeq > 2);
    }

    // ---- the unpark offers the item again ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AResume_AfterAPark_ArmsTheStepAgain_WithTheBudgetsOfThePhaseStartedOver()
    {
        var fx = new SagaFixture();
        await fx.Start(plan: "docs");
        await fx.Budget(BudgetKind.Failure); await fx.Budget(BudgetKind.Failure);
        Assert.True(fx.State.Parked);   // (the failure budget of the phase parked it)
        var r = await fx.Send(new ResumeWorkItem(Item, 0, "resume-1"));
        Assert.Equal(WorkItemOutcome.Applied, r.Decision.Outcome);
        Assert.False(fx.State.Parked);
        Assert.NotNull(fx.State.LeaseDue);
        Assert.Equal(0, fx.State.FailuresOf(StepKind.Design));
    }
}
