using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

// W9.10f: the steps of v1's sweep (plugin/scripts/lib/Verbs.Sweep.ps1, Invoke-SyncBoard) as rules of the saga, for the items the saga owns (where v2 acts, `sync-board` skips them: W9.10e). Each is a rule the sweep applied to
// every item on every pass; here each is an input the saga takes, from a timer or from the watcher of the gateway (Ports/ItemStepsPort.cs), and decides on the state it keeps:
//  - the watch of master's CI after the merge (Invoke-Settle's master-green wait, D5): the merge commit is judged once its CI is done. Green ends the wait; red after the one re-run of the failed jobs (issue 79) parks the item
//    master-broken, and no verdict within timing.masterGreenTimeoutMinutes parks it master-green-timeout. Both waits stay (issue 114): the item is not offered meanwhile, the watcher goes on reading, and the park is lifted
//    when master is green for the merge (the auto-unpark of master-broken, timing.autoUnparkMax times per park, or at the end of the park).
//  - the expiry of a park (Reset-ExpiredPark, Get-Park): a park lasts timing.parkHours; the item then takes up its step again with the counts that parked it from 0.
//  - merge pickup (Invoke-MergePickup): a person's merge is the delivery train's ItemMerged, which ends the Merge step whichever step the item is on (MergeStep) and, on the feature plan, starts the watch of master (DeliverStep).
//  - the demotion of a PASS whose pull request was closed (Invoke-Demote, cause pr-closed): back to Development, counted as a demotion.
//  - clear-hold (Invoke-ClearHoldIfApproved): an inspector's hold on a PASS (a park inspector-escalated, which only an adopted item carries under merge policy auto) ends with a human approval of the head the PASS covers.
//  - the idle owner (the sweep's owner-released-idle): the owner of a held PASS that is still to be merged gives the item up after idleReleaseMinutes; the saga asks the gateway to release the claim.
//  - the settle's re-arm (Invoke-RearmSettle, issue 114): a merged item that has no watch of master gets one.
// Nothing here exists unless the policy has SweepRules (the gateway sets them where the engine acts); the unit tests of the core run without them.

static class SweepFlow
{
    // ---- the watch of master's CI ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The item has been merged and is on its way to delivery: master's CI for the merge commit is awaited, with the timeout counted from now.</summary>
    public static bool ArmMaster(StepContext c)
    {
        var s = c.State;
        if (!c.Policy.WatchesMaster(s.Plan) || s.MergeCommit.Length == 0 || s.MasterState.Length > 0) return false;
        s.MasterState = "waiting"; s.MasterSince = c.Now; s.MasterRerunAt = null; s.MasterRerunRuns = []; s.MasterFailed = []; s.MasterGreenVia = "";
        s.MasterDue = c.Now + c.Policy.Sweep!.MasterTimeout;
        c.TimerChanged = true;
        return true;
    }

    /// <summary>The release of the item reached an environment: master was green for it, whatever the watch had not judged yet. A master park the delivery outlived ends with it.</summary>
    public static void DeliveredSoMasterWasGreen(StepContext c)
    {
        var s = c.State;
        if (s.MasterState.Length == 0 || s.MasterState == "green") return;
        s.MasterState = "green"; s.MasterDue = null;
        c.TimerChanged = true;
        if (s.Parked && SweepParks.IsMaster(s.ParkReason))
        {
            s.Parked = false; s.ParkReason = ""; c.ClearParkTimers();
            c.Decide(WorkItemActions.Resumed, "the item was delivered: master is green for it");
        }
    }

    static bool Watching(WorkItemState s) => s.MasterState is "waiting" or "broken" or "timeout";

    public static StepAnswer Master(StepContext c, MasterObserved o)
    {
        var s = c.State;
        if (!c.Policy.WatchesMaster(s.Plan)) return StepAnswer.Refused($"the {s.Plan} plan watches no master CI");
        if (s.Status != WorkItemStatus.Open || s.Step != StepKind.Deliver || !Watching(s)) return StepAnswer.Stale("the item is not waiting for master's CI");
        if (!MergeSteps.SameHead(s.MergeCommit, o.Commit)) return StepAnswer.Stale($"the observation is about {MergeSteps.Short(o.Commit)}, the merge commit is {MergeSteps.Short(s.MergeCommit)}");
        switch (o.Verdict)
        {
            case MasterVerdict.Pending:
                // the failed jobs were re-run: the timeout counts again from the re-run, and the verdict waits for the newer attempts
                if (o.Rerun is { Count: > 0 } runs && s.MasterRerunRuns.Count == 0)
                {
                    s.MasterRerunAt = c.Now; s.MasterRerunRuns = [.. runs];
                    if (s.MasterState == "waiting") { s.MasterDue = c.Now + c.Policy.Sweep!.MasterTimeout; c.TimerChanged = true; }
                }
                return StepAnswer.Applied;
            case MasterVerdict.Green: return Green(c, o);
            default: return Red(c, o);
        }
    }

    static StepAnswer Green(StepContext c, MasterObserved o)
    {
        var s = c.State; var sweep = c.Policy.Sweep!;
        var parkedByMaster = s.Parked && SweepParks.IsMaster(s.ParkReason);
        if (parkedByMaster && SweepParks.CodeOf(s.ParkReason) == SweepParks.MasterBroken && s.ParkUntil is { } until && until > c.Now)
        {
            // a master-broken park that stands is lifted by the saga itself at most timing.autoUnparkMax times (0 switches the lifts off); past that it is a person's until it expires
            var lifts = s.AutoUnparks.GetValueOrDefault(SweepParks.MasterBroken);
            if (lifts >= sweep.AutoUnparkMax) return StepAnswer.Refused($"the park master-broken was lifted {lifts} times already (timing.autoUnparkMax is {sweep.AutoUnparkMax}); a person lifts it");
            s.AutoUnparks[SweepParks.MasterBroken] = lifts + 1;
        }
        var via = o.Via.Length > 0 ? o.Via : o.Commit;
        s.MasterState = "green"; s.MasterGreenVia = via.ToLowerInvariant(); s.MasterDue = null; s.MasterFailed = [];
        c.TimerChanged = true;
        c.Decide(WorkItemActions.MasterGreen, MergeSteps.SameHead(via, o.Commit) ? $"commit {MergeSteps.Short(o.Commit)}" : $"commit {MergeSteps.Short(o.Commit)}, judged on {MergeSteps.Short(via)}");
        if (parkedByMaster)
        {
            s.Parked = false; s.ParkReason = ""; c.ClearParkTimers();
            c.Decide(WorkItemActions.Resumed, $"master is green again at {MergeSteps.Short(via)}");
        }
        return StepAnswer.Applied;
    }

    static StepAnswer Red(StepContext c, MasterObserved o)
    {
        var s = c.State;
        // only a wait that is still open is broken by a red verdict; a wait that is parked already keeps being read for a green master
        if (s.MasterState != "waiting") return StepAnswer.Refused($"the wait for master is {s.MasterState} already");
        if (s.Parked) return StepAnswer.Refused("the item is parked");
        s.MasterState = "broken"; s.MasterDue = null; s.MasterFailed = [.. o.Failed.Take(10)];
        var failed = string.Join(", ", s.MasterFailed);
        c.Decide(WorkItemActions.MasterBroken, $"commit {MergeSteps.Short(o.Commit)}", failed);
        c.Park($"{SweepParks.MasterBroken}: master is BROKEN at {MergeSteps.Short(o.Commit)} after the merge ({failed})");
        return StepAnswer.Applied;
    }

    public static StepAnswer MasterElapsed(StepContext c, MasterWatchElapsed e)
    {
        var s = c.State;
        if (s.MasterState != "waiting" || s.MasterDue != e.Due || s.Parked || s.Status != WorkItemStatus.Open) return StepAnswer.Stale("the master wait is not the one that ran out");
        var minutes = (int)c.Policy.Sweep!.MasterTimeout.TotalMinutes;
        s.MasterState = "timeout"; s.MasterDue = null;
        c.Decide(WorkItemActions.MasterTimeout, $"no verdict within {minutes} min of {(s.MasterRerunAt is not null ? "the re-run" : "the merge")}");
        c.Park($"{SweepParks.MasterTimeout}: master CI did not go green within {minutes} min of the merge");
        return StepAnswer.Applied;
    }

    /// <summary>The re-arm of a settle that has no watch (issue 114): the watch begins again, with the timeout counted from now.</summary>
    public static StepAnswer Rearm(StepContext c, RearmSettle r)
    {
        var s = c.State;
        if (!c.Policy.WatchesMaster(s.Plan)) return StepAnswer.Refused($"the {s.Plan} plan watches no master CI");
        if (s.Status != WorkItemStatus.Open || s.Step != StepKind.Deliver || s.MergeCommit.Length == 0) return StepAnswer.Refused("the item has no merge to settle");
        if (s.MasterState.Length > 0) return StepAnswer.Refused($"the wait for master is {s.MasterState}");
        ArmMaster(c);
        c.Decide(WorkItemActions.SettleRearmed, r.Reason.Length == 0 ? "merged with no settle step" : r.Reason);
        return StepAnswer.Applied;
    }

    // ---- parks -------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A park that ended by expiry gives the item a fresh start (Reset-ExpiredPark): the counts that parked it start from 0 and the step is armed again. An item past its merge goes on with its delivery (the wait for master, if
    /// it was parked there, is watched still).
    /// </summary>
    public static StepAnswer Expired(StepContext c, ParkExpired e)
    {
        var s = c.State;
        if (!s.Parked || s.ParkUntil != e.Due) return StepAnswer.Stale("the park is not the one that ended");
        var code = SweepParks.CodeOf(s.ParkReason);
        var step = s.Step.ToString();
        s.FixForwardRounds = 0; s.Demotions = 0; s.MechanicalFixes = 0; s.IdleFollowUps = 0; s.Failures.Remove(step); s.Launches.Remove(step);
        c.Decide(WorkItemActions.ParkExpired, $"the park {code} ended at {e.Due:O}");
        if (s.Step == StepKind.Deliver || s.Status == WorkItemStatus.Settling)
        {
            s.Parked = false; s.ParkReason = ""; c.ClearParkTimers();
            c.TimerChanged = true;
        }
        else c.Enter(s.Step, "the park expired");
        return StepAnswer.Applied;
    }

    /// <summary>The owner that waits on a held PASS gives the item up after idleReleaseMinutes: the saga asks the gateway to release the claim; the next trigger assigns the item again.</summary>
    public static StepAnswer OwnerIdle(StepContext c, OwnerIdleElapsed e)
    {
        var s = c.State;
        if (!s.Parked || s.OwnerReleased || s.OwnerIdleDue != e.Due) return StepAnswer.Stale("the owner's patience is not the one that ran out");
        if (s.Step != StepKind.Merge || s.VerdictHead.Length == 0) return StepAnswer.Stale("no PASS waits for its merge");
        s.OwnerReleased = true;
        var reason = $"idle for more than {(int)c.Policy.Sweep!.IdleReleaseAfter.TotalMinutes} min on a hold ({SweepParks.CodeOf(s.ParkReason)})";
        c.Decide(WorkItemActions.OwnerReleased, reason);
        c.Send(new ReleaseOwner($"release|{s.Item}|{s.Generation}|{s.Sequence}", reason));
        c.TimerChanged = true;
        return StepAnswer.Applied;
    }

    // ---- the PASS ----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The pull request of a PASS was closed without merging: the verdict is void, the item goes back to Development (a demotion; past the limit it is parked demotion-limit).</summary>
    public static StepAnswer Closed(StepContext c, PullRequestClosed p)
    {
        var s = c.State;
        if (s.Step != StepKind.Merge || s.VerdictHead.Length == 0 || s.MergeCommit.Length > 0) return StepAnswer.Refused("no PASS waits for its merge");
        if (s.PullRequest > 0 && p.PullRequest != s.PullRequest) return StepAnswer.Stale($"the PASS is on PR #{s.PullRequest}, not #{p.PullRequest}");
        // a PASS of an item without the Auto Merge label is processed (a person merges it): the sweep demoted only a PASS that was still to be merged
        if (s.Parked && SweepParks.CodeOf(s.ParkReason) == SweepParks.PassWithoutAutoMerge) return StepAnswer.Refused("the PASS is processed: a person merges it");
        var d = RecoveryPolicy.Decide(RecoveryCause.FailVerdict, new RecoveryCounters(Demotions: s.Demotions), c.Policy.Recovery);
        s.VerdictHead = ""; s.PullRequest = 0; s.Head = ""; s.ChecksHead = ""; s.ChecksState = ""; s.ChecksFailed = []; s.MergeAwaitingChecks = false;
        if (d.Action == RecoveryAction.Park)
        {
            c.Park($"{SweepParks.DemotionLimit}: PR #{p.PullRequest} was closed without merging; {s.Demotions + 1} demotions, over the limit of {d.Budget}");
            return StepAnswer.Applied;
        }
        s.Demotions++;
        c.Decide(WorkItemActions.Demoted, $"round-trip {s.Demotions} of {d.Budget}", "pr-closed");
        c.Enter(StepKind.Develop, $"demoted: PR #{p.PullRequest} was closed without merging");
        return StepAnswer.Applied;
    }

    /// <summary>A human approved the head the PASS covers: the inspector's hold ends and the merge goes on.</summary>
    public static StepAnswer Approved(StepContext c, ApprovalSeen a)
    {
        var s = c.State;
        if (s.Step != StepKind.Merge || !s.Parked || SweepParks.CodeOf(s.ParkReason) != SweepParks.InspectorEscalated) return StepAnswer.Refused("the item is not held by the inspector");
        if (!MergeSteps.SameHead(a.Head, s.VerdictHead)) return StepAnswer.Stale($"the approval is of {MergeSteps.Short(a.Head)}, the PASS covers {MergeSteps.Short(s.VerdictHead)}");
        s.ApprovedBy = a.By;
        c.Decide(WorkItemActions.HoldCleared, $"{a.By} approved {MergeSteps.Short(a.Head)}");
        c.Enter(StepKind.Merge, "the hold was cleared by an approval");
        return StepAnswer.Applied;
    }
}
