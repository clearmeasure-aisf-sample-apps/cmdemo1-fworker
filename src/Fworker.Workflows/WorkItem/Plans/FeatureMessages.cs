using System.Text.Json.Serialization;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.WorkItem;

// W7.3 (#340): the messages of the feature plan's steps. They join the inputs of the WorkItemSaga (WorkItemMessages.cs, DocsMessages.cs): addressed by the item, carrying the generation and a business key. Three groups:
//  - the checks of the pull request's head (ChecksObserved): Functional Testing runs with CI alongside, so the saga hears what CI says of the head under test (delivery-speed.md 5);
//  - what the CapacityScheduler says of the item's slot, as inputs of the saga (SlotAssigned, SlotHeld, SessionLost): the saga asks for a slot when a step needs a session and learns the grant, the wait and the loss;
//  - what the saga asks of the outside besides the merge (SlotWanted, and the StateCommand records that the saga keeps in its state until they are published).

/// <summary>The rules of the feature plan, from the gateway's configuration. Present in the policy only where the engine carries the plan (<c>intake.v2Plans</c> names it); without it the plan's steps are the core's.</summary>
/// <param name="UseScheduler">Whether the plan's session steps ask the CapacityScheduler for a slot (<c>scheduler.featureSlots</c>, on by default): an item waits for its grant before a session is offered to it.</param>
public sealed record FeaturePlanRules(bool UseScheduler = true)
{
    public static readonly FeaturePlanRules Default = new();
}

/// <summary>What CI says of a head.</summary>
public enum ChecksVerdict { Pending, Green, Red }

/// <summary>
/// What the checks of the pull request's head <paramref name="Head"/> say now (the watcher reads them and publishes this; the rule for a red head, the one re-run and the master defect, is the rule script's and is applied
/// before the watcher says Red). <paramref name="Failed"/> names the failed checks. A pending observation is a sign of life for the step that waits for CI. On the feature plan, Red on the head under test, before or
/// after the PASS and before the merge, is a fix-forward round (never a quality demotion); Green on the verdict's head lets the merge be asked for.
/// </summary>
[MessageIdentity("workitem.checks-observed")]
public sealed record ChecksObserved(ItemId Item, int Generation, string Key, string Head, ChecksVerdict Verdict, IReadOnlyList<string> Failed) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

// The three scheduler inputs carry the time the scheduler (or the plant) said it: the scheduler's events travel through queues of their own and can arrive in another order than it published them, so the saga keeps the
// time of the last one it applied (<see cref="WorkItemState.SlotStamp"/>) and takes an older one for stale.

/// <summary>The CapacityScheduler granted the item a slot (<c>SlotGranted</c>, routed by <see cref="WorkItemRouting"/>): the session may be offered, and the step's lease starts.</summary>
[MessageIdentity("workitem.slot-assigned")]
public sealed record SlotAssigned(ItemId Item, int Generation, string Key, string SlotId, string Runtime, string? Host, string Phase, DateTimeOffset At, DateTimeOffset ExpiresAt) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>The scheduler says why the item waits (<c>Held</c>): a file overlap with another item, a runtime that is off or full. A slot the item held is gone when the wait is a hold; the lease is paused until the grant.</summary>
[MessageIdentity("workitem.slot-held")]
public sealed record SlotHeld(ItemId Item, int Generation, string Key, string Code, string Detail, string? By, DateTimeOffset At) : WorkItemInput(Item), IGenerationInput, IKeyedInput
{
    public string Waiting => Detail.Length == 0 ? Code : $"{Code} {Detail}";
}

/// <summary>The session of the item's slot is over without the item's step being done: the scheduler gave the slot up because its end was never signalled (<c>SlotExpired</c>), or the plant lost the session. The item asks again as an owned item.</summary>
[MessageIdentity("workitem.session-lost")]
public sealed record SessionLost(ItemId Item, int Generation, string Key, string SlotId, string Reason, DateTimeOffset At) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// The saga asks the gateway for a slot (the gateway adds what the saga does not know, the item's labels, and sends the scheduler's <c>RequestSlot</c>). The key names the arm, so asking again for the same arm is one request.
/// <paramref name="FootprintFromPullRequest"/> says whether the footprint is the pull request's changed files (else the Technical Design's <c>Files:</c> line).
/// </summary>
[MessageIdentity("workitem.slot-wanted")]
public sealed record SlotWanted(ItemId Item, int Generation, string ArmId, string Key, string Phase, bool Resume, int ColumnDepth, IReadOnlyList<string> Footprint, bool FootprintFromPullRequest, DateTimeOffset RequestedAt, bool TopRank = false) : ItemCommand(Item);

/// <summary>
/// A command the saga keeps in its state until the transition that made it is published (so a handler that died between the save and the publish publishes it again, as with the decisions). The records are closed:
/// <see cref="WorkItemSaga.CommandsOf"/> turns each into the message of the outside world it stands for. They carry no item (the state knows it) and a key made when they were created.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AskSlot), "slot")]
[JsonDerivedType(typeof(EndSession), "end")]
[JsonDerivedType(typeof(ClearSlot), "clear")]
[JsonDerivedType(typeof(ChangeFootprint), "footprint")]
[JsonDerivedType(typeof(LeaveTrain), "leave")]
public abstract record StateCommand(string Key);

/// <summary>Ask for a slot for a session of <paramref name="Phase"/> (published as <see cref="SlotWanted"/>).</summary>
public sealed record AskSlot(string Key, string ArmId, string Phase, bool Resume, int ColumnDepth, List<string> Footprint, bool FromPullRequest, DateTimeOffset At, bool TopRank = false) : StateCommand(Key);

/// <summary>The item's session needs no slot any more (published as the scheduler's <c>SessionEnded</c>): <paramref name="SlotId"/> names the slot.</summary>
public sealed record EndSession(string Key, string SlotId) : StateCommand(Key);

/// <summary>The item is no longer in flight for the scheduler: merged, parked, completed or handed back (published as the scheduler's <c>ClearItem</c>).</summary>
public sealed record ClearSlot(string Key, string Reason) : StateCommand(Key);

/// <summary>The pull request's changed files replace the item's footprint in the scheduler (published as the scheduler's <c>FootprintChanged</c>).</summary>
public sealed record ChangeFootprint(string Key, List<string> Files) : StateCommand(Key);

/// <summary>The item left the merge step without a merge: its place in the repository's merge line is released (published as the train's <c>MergeTrainLeft</c>).</summary>
public sealed record LeaveTrain(string Key, string Reason) : StateCommand(Key);
