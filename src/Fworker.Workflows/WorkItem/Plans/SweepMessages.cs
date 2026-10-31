using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.WorkItem;

// W9.10f: the messages of the steps v1's sweep took for every item and the saga now takes for the items it owns (docs/design/fworker-wolverine-implementation.md, "W9.10f as built"): the watch of master's CI after the merge
// (D5's master-green, master-broken and master-timeout waits), the expiry of a park, the demotion of a PASS whose pull request was closed, the clearing of a hold by a person's approval, the release of an owner that waits out
// a hold, and the re-arm of a settle that lost its watch. They join the inputs of the WorkItemSaga: addressed by the item, carrying the generation, and a business key (or, for a timer, the time it was scheduled for).

/// <summary>
/// The rules of the sweep's steps, from config/instance.json (<c>timing.parkHours</c>, <c>timing.masterGreenTimeoutMinutes</c>, <c>timing.autoUnparkMax</c>, <c>idleReleaseMinutes</c>). Present in the policy only where the
/// engine acts; without it a park never expires, nothing watches master and no owner is released (the core's saga, as the unit tests run it).
/// </summary>
/// <param name="ParkTime">How long a park lasts before it expires (<c>timing.parkHours</c>, 24 h). An expired park gives the item a fresh start: its fix-forward rounds, demotions and failures are counted from 0 again (Reset-ExpiredPark).</param>
/// <param name="MasterTimeout">How long master's CI may take after the merge before the item is parked master-green-timeout (<c>timing.masterGreenTimeoutMinutes</c>, 30 min); a re-run of the failed jobs counts it again from the re-run.</param>
/// <param name="AutoUnparkMax">How many times master-broken is lifted by itself (<c>timing.autoUnparkMax</c>, 3): after that the park is a person's until it expires. 0 switches the lifts off.</param>
/// <param name="IdleRelease">How long an owner may wait on a held PASS before it gives the item up (<c>idleReleaseMinutes</c>, 60 min).</param>
/// <param name="MasterWatch">Whether the saga watches master's CI for the merge commit of a code item (the settle's master-green wait). The feature plan only: a documentation change ends at the merge.</param>
public sealed record SweepRules(TimeSpan ParkTime, TimeSpan MasterTimeout, int AutoUnparkMax = 3, TimeSpan? IdleRelease = null, bool MasterWatch = true)
{
    public static readonly SweepRules Default = new(TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), 3, TimeSpan.FromMinutes(60));

    public TimeSpan IdleReleaseAfter => IdleRelease ?? TimeSpan.FromMinutes(60);
}

/// <summary>What master's CI says of the merge commit.</summary>
public enum MasterVerdict { Pending, Green, Red }

/// <summary>A workflow run whose failed jobs were re-run, and the attempt that was re-run: a newer, completed attempt is what the verdict waits for (issue 79).</summary>
public sealed class RerunRun
{
    public long Id { get; set; }
    public int Attempt { get; set; }
}

/// <summary>
/// What master's CI says of the item's merge commit now (the watcher reads it and publishes this). Green: the merge commit, or for a wait that was parked a later commit of the default branch that contains it, passed
/// (<paramref name="Via"/> names that commit). Red: a gate failed, nothing is pending and the one re-run of the failed jobs (issue 79) has been judged; <paramref name="Failed"/> names the failed checks. Pending with
/// <paramref name="Rerun"/> set: the failed jobs were just re-run, so the wait counts its timeout again from now.
/// </summary>
[MessageIdentity("workitem.master-observed")]
public sealed record MasterObserved(ItemId Item, int Generation, string Key, string Commit, MasterVerdict Verdict, IReadOnlyList<string> Failed, string Via = "", IReadOnlyList<RerunRun>? Rerun = null) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>The timer of the master-green wait: <paramref name="Due"/> is the time it was scheduled for. When it fires and master has not been judged, the item is parked master-green-timeout.</summary>
[MessageIdentity("workitem.master-watch-elapsed")]
public sealed record MasterWatchElapsed(ItemId Item, int Generation, DateTimeOffset Due) : WorkItemInput(Item), IGenerationInput;

/// <summary>The timer of a park's end (<see cref="WorkItemState.ParkUntil"/>): the park expires and the item takes up its step again with the counts that parked it started from 0.</summary>
[MessageIdentity("workitem.park-expired")]
public sealed record ParkExpired(ItemId Item, int Generation, DateTimeOffset Due) : WorkItemInput(Item), IGenerationInput;

/// <summary>The timer of the owner's patience: the item has been held (parked with its PASS recorded) for the whole <c>idleReleaseMinutes</c> and the owner that waits on it gives it up.</summary>
[MessageIdentity("workitem.owner-idle-elapsed")]
public sealed record OwnerIdleElapsed(ItemId Item, int Generation, DateTimeOffset Due) : WorkItemInput(Item), IGenerationInput;

/// <summary>The item's pull request was closed without being merged. A PASS that waits for its merge is void then: the item goes back to Development (v1's demote with the cause pr-closed).</summary>
[MessageIdentity("workitem.pr-closed")]
public sealed record PullRequestClosed(ItemId Item, int Generation, string Key, int PullRequest) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>A person on the list of human approvers (<c>humanApprovers</c>) approved the pull request at <paramref name="Head"/>. It clears the inspector's hold on a PASS for that head (clear-hold).</summary>
[MessageIdentity("workitem.approval-seen")]
public sealed record ApprovalSeen(ItemId Item, int Generation, string Key, int PullRequest, string Head, string By) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>An item that was merged and has no watch of master's CI (its state was saved before the watch existed, or an older gateway erased it) gets one again: the settle's re-arm (issue 114).</summary>
[MessageIdentity("workitem.rearm-settle")]
public sealed record RearmSettle(ItemId Item, int Generation, string Key, string Reason) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// W9.10k: the plant manager ended the lease of the item's owner (manager-action release-owner). The saga records the decision and asks the gateway to give the claim up (<see cref="ReleaseOwner"/>, published as
/// <see cref="OwnerReleaseRequested"/>), so that the item is offered again; it changes no step and no park. The key names the call: the same call is applied once.
/// </summary>
[MessageIdentity("workitem.owner-release-asked")]
public sealed record OwnerReleaseAsked(ItemId Item, int Generation, string Key, string Reason) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>
/// W9.10k: the plant manager put the item back to be offered at once (manager-action requeue). The saga records the decision; the offer itself comes from its state (a step a session works, no owner, not parked), which the
/// gateway reads at its next sweep after dropping the offer that waited. Refused for an item that is parked, delivered or on a step no session works.
/// </summary>
[MessageIdentity("workitem.requeue")]
public sealed record RequeueWorkItem(ItemId Item, int Generation, string Key, string Reason) : WorkItemInput(Item), IGenerationInput, IKeyedInput;

/// <summary>The owner session of the item gives it up (published as <see cref="OwnerReleaseRequested"/>): the claim and its ref go, the next trigger assigns the item again.</summary>
public sealed record ReleaseOwner(string Key, string Reason) : StateCommand(Key);

/// <summary>The gateway releases the owner of the item (the claim in its state comment and the claim ref). Idempotent: an item with no claim is released already.</summary>
[MessageIdentity("workitem.release-owner")]
public sealed record OwnerReleaseRequested(ItemId Item, string Key, string Reason, int Generation = 0) : ItemCommand(Item);

/// <summary>The words of the park reasons the sweep's steps use (the codes of the state comment's park, as v1 writes them).</summary>
public static class SweepParks
{
    public const string MasterBroken = "master-broken";
    public const string MasterTimeout = "master-green-timeout";
    public const string InspectorEscalated = "inspector-escalated";
    /// <summary>A PASS recorded for an item without the Auto Merge label: the verdict is processed and a person merges it (the sweep's unprocessedPass is false for it, so it is neither demoted for a closed pull request nor has an owner that waits).</summary>
    public const string PassWithoutAutoMerge = "pass-without-auto-merge";
    public const string DemotionLimit = "demotion-limit";

    /// <summary>The code of a park reason: the text before the first colon.</summary>
    public static string CodeOf(string reason)
    {
        var colon = reason.IndexOf(':');
        return (colon > 0 ? reason[..colon] : reason).Trim();
    }

    public static bool IsMaster(string reason) => CodeOf(reason) is MasterBroken or MasterTimeout;
}
