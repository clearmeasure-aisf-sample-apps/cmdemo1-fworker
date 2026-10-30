using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

// W7.3 (#340): the feature plan's steps (Design, Develop, Verify, Merge, Deliver), where the engine carries the plan (intake.v2Plans names it; the policy then has FeatureRules). They are the docs plan's steps (W6.6) plus
// what a code change adds, each rule one v1 applies in a verb script or one of D5's recovery rows:
//  - Design: the reviews of the column and the Technical Design's `Files:` line, the footprint of D6 (Verbs.Item.ps1, record-review, append-body). The marker of a docs-only design still switches the item to the docs plan.
//  - Develop: the same reviews and a recorded pull request (Verb_complete: no-open-pr); the pull request's changed files replace the footprint; a new head after the PASS sends the item back to Verify.
//  - Verify: Functional Testing with CI alongside (delivery-speed.md 5): the item enters Verify when Development completes, whether the checks are green or still running (v1 refuses a red head before that). CI runs beside
//    the session and the saga hears it as ChecksObserved: red on the head under test, before or after the PASS and before the merge, is a fix-forward round (D5's CiRed row, fixForwardMaxRounds), never a quality demotion,
//    whichever finishes first; the owner goes back to Development and Functional Testing runs again on the next head. A FAIL verdict is the quality demotion of the docs plan.
//  - Merge: "the merge still needs both" (5.4): the step asks to merge only when the checks of the head the PASS covers are green (until then it waits without using merge calls and renews its lease on pending checks), the
//    merge goes through the repository's merge line (W7.2) and counts in the merge queue depth (mergeQueueDepth), which a documentation change never does. Leaving the step without a merge releases the place in the line.
//  - Deliver: the core's (the delivery train's facts, the settle window).
// Every session step asks the CapacityScheduler for a slot when it is entered (W7.1; StepContext.AskSlot): the item waits for the grant (or for the hold that file overlap puts on it) before a session is offered, and the
// slot goes back when the step needs no session, the session is lost, or the item is parked, handed back or done. Nothing here is reached on another plan, or where the engine does not carry the feature plan.

/// <summary>Design on the feature plan: reviews, the docs-only marker (the core's), and the footprint of the design.</summary>
public class FeatureDesignStep : DesignStep
{
    public override void OnEnter(StepContext c)
    {
        DocsReviews.Reset(c);
        c.AskSlot(StepKind.Design);
    }

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is ReviewRecorded r) return DocsReviews.Record(c, r, c.Policy.DocsRules.DesignRounds(c.State.DesignMarker));
        var answer = base.Handle(c, input);
        // the Technical Design's `Files:` line is the item's footprint until the first push (D6); a design that names none leaves what there was
        if (answer.Outcome == WorkItemOutcome.Applied && input is DesignRecorded { Files.Count: > 0 } d && !c.State.FootprintFromPullRequest)
            c.State.Footprint = [.. d.Files!];
        return answer;
    }

    public override StepAnswer? Gate(StepContext c) => DocsReviews.Gate(c);
}

/// <summary>Develop on the feature plan: reviews, a pull request, its files, and the head that moves after the PASS.</summary>
public class FeatureDevelopStep : DevelopStep
{
    public override void OnEnter(StepContext c)
    {
        DocsReviews.Reset(c);
        c.AskSlot(StepKind.Develop);
    }

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is ReviewRecorded r) return DocsReviews.Record(c, r, c.Policy.DocsRules.MaxReviewRounds);
        var before = c.State.Head;
        var answer = base.Handle(c, input);
        if (answer.Outcome != WorkItemOutcome.Applied || input is not PullRequestChanged p || c.State.Plan != WorkPlans.Feature) return answer;
        FeatureFlow.PullRequest(c, p, before);
        return answer;
    }

    public override StepAnswer? Gate(StepContext c)
    {
        if (DocsReviews.Gate(c) is { } review) return review;
        if (c.State.PullRequest <= 0) return StepAnswer.Refused("no-open-pr: no pull request is recorded for this work item");
        return null;
    }
}

/// <summary>
/// Functional Testing with CI alongside. The verdicts are the docs plan's (a PASS covers the head it names and moves the item to Merge, a FAIL is D5's FailVerdict row); on top of them a PASS on a head whose checks are
/// red is a fix-forward round, and the checks the watcher reports are the saga's (<see cref="FeatureFlow.Checks"/>).
/// </summary>
public class FeatureVerifyStep : DocsVerifyStep
{
    public override void OnEnter(StepContext c) => c.AskSlot(StepKind.Verify);

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        var answer = base.Handle(c, input);
        // red CI on the head under test "before or after the PASS": a PASS on a red head does not go to the merge, it is a fix-forward round
        if (answer.Outcome == WorkItemOutcome.Applied && input is VerdictRecorded { Passed: true } && c.State.Step == StepKind.Merge
            && c.State.ChecksState == FeatureFlow.Red && MergeSteps.SameHead(c.State.ChecksHead, c.State.VerdictHead))
            FeatureFlow.FixForward(c, "CI is red on the head the PASS covers");
        return answer;
    }
}

/// <summary>Merge on the feature plan: the docs plan's merge calls, asked for once the head the PASS covers has green checks, as a code change in the merge queue.</summary>
public class FeatureMergeStep : DocsMergeStep
{
    public override void OnEnter(StepContext c)
    {
        var s = c.State;
        s.MergeAttempt = 0; s.MergeAnswers = 0; s.MergeBlockedReason = ""; s.MergeAwaitingChecks = false;
        if (s.ChecksState == FeatureFlow.Green && MergeSteps.SameHead(s.ChecksHead, s.VerdictHead)) { MergeSteps.Request(c, ""); return; }
        // the PASS came first: the merge needs the checks too, and the watcher says when they are green (or red: a fix-forward round)
        s.MergeAwaitingChecks = true;
        c.Decide(WorkItemActions.MergeBlocked, "checks-pending", $"the checks of {MergeSteps.Short(s.VerdictHead)} are {(s.ChecksState.Length == 0 ? "not known yet" : s.ChecksState)}; the merge waits for them");
    }
}

/// <summary>The rules the feature plan's steps share: the checks of the head, the fix-forward round, the pull request, and the item's slot.</summary>
static class FeatureFlow
{
    public const string Pending = "pending", Green = "green", Red = "red";

    static string Name(ChecksVerdict v) => v switch { ChecksVerdict.Green => Green, ChecksVerdict.Red => Red, _ => Pending };

    static bool SessionStep(WorkItemState s) => s.Step is StepKind.Design or StepKind.Develop or StepKind.Verify && s.Status == WorkItemStatus.Open;

    // ---- the pull request ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>A new head: what was observed of the old one no longer counts, a PASS given for another head does not cover it, and the files of the pull request are the footprint now.</summary>
    public static void PullRequest(StepContext c, PullRequestChanged p, string before)
    {
        var s = c.State;
        if (p.Head.Length > 0 && !MergeSteps.SameHead(before, p.Head))
        {
            s.ChecksHead = ""; s.ChecksState = ""; s.ChecksFailed = [];
            // a new head after the PASS: the verdict covered another one, so the merge is not asked for and the item is validated again
            if (s.Step == StepKind.Merge && !MergeSteps.SameHead(s.VerdictHead, p.Head))
            {
                s.VerdictHead = "";
                c.Enter(StepKind.Verify, $"the head moved to {MergeSteps.Short(p.Head)} after the PASS");
            }
        }
        if (p.Files.Count > 0 && c.Policy.Schedules(s.Plan))
        {
            s.Footprint = [.. p.Files]; s.FootprintFromPullRequest = true;
            if (s.SlotKnown) c.Send(new ChangeFootprint($"footprint|{s.Item}|{s.Generation}|{p.Key}", [.. p.Files]));
        }
    }

    // ---- the checks ----------------------------------------------------------------------------------------------------------------------------------

    public static StepAnswer Checks(StepContext c, ChecksObserved o)
    {
        var s = c.State;
        if (!c.Policy.HasFeatureSteps || s.Plan != WorkPlans.Feature) return StepAnswer.Refused($"the {s.Plan} plan watches no checks");
        if (s.Parked) return StepAnswer.Refused("the item is parked");
        if (s.Status != WorkItemStatus.Open || s.Step > StepKind.Merge) return StepAnswer.Stale("the item is past its merge");
        var head = o.Head.ToLowerInvariant();
        if (head.Length == 0) return StepAnswer.Refused("the observation names no head");
        // the checks of a head the item is no longer on (a push since the watcher read them) say nothing of the head under test
        if (s.Head.Length == 0) return StepAnswer.Stale("the item has no pull request head yet");
        if (!MergeSteps.SameHead(s.Head, head) && !MergeSteps.SameHead(s.VerdictHead, head)) return StepAnswer.Stale($"the checks are those of {MergeSteps.Short(head)}, the head is {MergeSteps.Short(s.Head)}");

        var state = Name(o.Verdict);
        var changed = !MergeSteps.SameHead(s.ChecksHead, head) || s.ChecksState != state;
        s.ChecksHead = head; s.ChecksState = state; s.ChecksFailed = [.. o.Failed.Take(10)];
        var tested = s.Step is StepKind.Verify or StepKind.Merge;
        switch (o.Verdict)
        {
            case ChecksVerdict.Pending:
                // CI still runs: a sign of life of the step that waits for it (a step with a lease; one that waits for a slot has none running)
                if (tested && s.LeaseDue is not null && c.StepOf(s.Step).Lease(c.Policy) is { } lease) { s.LeaseDue = c.Now + lease; c.TimerChanged = true; }
                break;
            case ChecksVerdict.Green:
                s.FixForwardRounds = 0;   // (v1: the green checks end the rounds, `$s.Remove('fix_forward_rounds')`)
                if (changed) c.Decide(WorkItemActions.ChecksGreen, $"head {MergeSteps.Short(head)}");
                // the PASS was given first: the merge was waiting for exactly these checks
                if (s.Step == StepKind.Merge && s.MergeAwaitingChecks && MergeSteps.SameHead(s.VerdictHead, head)) { s.MergeAwaitingChecks = false; MergeSteps.Request(c, ""); }
                break;
            case ChecksVerdict.Red:
                if (changed) c.Decide(WorkItemActions.ChecksRed, $"head {MergeSteps.Short(head)}", string.Join(", ", s.ChecksFailed));
                // only the head under test, in the steps where it is tested or merged, costs a round; red in Development is the owner's own fix
                if (tested && MergeSteps.SameHead(s.Head, head)) FixForward(c, "CI is red on the head under test");
                break;
        }
        return StepAnswer.Applied;
    }

    /// <summary>D5's CiRed row: the owner fixes in Development while the rounds last (the PASS on the head is void), then the item is parked for a person.</summary>
    public static void FixForward(StepContext c, string why)
    {
        var s = c.State;
        var d = RecoveryPolicy.Decide(RecoveryCause.CiRed, new RecoveryCounters(FixForwardRounds: s.FixForwardRounds), c.Policy.Recovery);
        if (d.Action == RecoveryAction.Park)
        {
            c.Park($"{d.ParkReason}: CI stayed red after {s.FixForwardRounds} fix-forward rounds ({string.Join(", ", s.ChecksFailed)})");
            return;
        }
        s.FixForwardRounds++;
        s.VerdictHead = ""; s.MergeAwaitingChecks = false;
        c.Decide(WorkItemActions.FixForward, $"round {s.FixForwardRounds} of {d.Budget}", why);
        c.Enter(StepKind.Develop, $"fix-forward: {why}");
    }

    // ---- the item's slot -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The scheduler granted a slot: the session may be offered and the step's lease starts. A grant the item can no longer use is given back at once.</summary>
    public static StepAnswer Assigned(StepContext c, SlotAssigned a)
    {
        var s = c.State;
        if (a.At < s.SlotStamp) return StepAnswer.Stale("an older word of the scheduler than the last applied");
        if (!c.Policy.Schedules(s.Plan) || !SessionStep(s) || s.Parked)
        {
            // the slot arrived after the item needed it: it goes back (the session never started)
            if (s.SlotId != a.SlotId) c.Send(new EndSession($"end|{s.Item}|{a.SlotId}", a.SlotId));
            return StepAnswer.Applied;
        }
        if (s.SlotId == a.SlotId) return StepAnswer.Applied;
        if (s.SlotId.Length > 0) c.Send(new EndSession($"end|{s.Item}|{s.SlotId}", s.SlotId));   // (a newer slot replaces the one the item held)
        s.SlotId = a.SlotId; s.SlotRuntime = a.Runtime; s.SlotHost = a.Host ?? "";
        s.EverGranted = true; s.SlotAsked = false; s.SlotWait = ""; s.SlotStamp = a.At;
        if (s.LeaseDue is null && c.StepOf(s.Step).Lease(c.Policy) is { } lease) { s.LeaseDue = c.Now + lease; c.TimerChanged = true; }
        c.Decide(WorkItemActions.SlotGranted, a.Host is { Length: > 0 } ? $"{a.Runtime} on {a.Host}" : a.Runtime, a.Phase);
        return StepAnswer.Applied;
    }

    /// <summary>The scheduler says why the item waits (a file overlap with another item, a runtime that is off or full). A slot the item held is gone when the wait is a hold; the lease stops until the grant.</summary>
    public static StepAnswer Held(StepContext c, SlotHeld h)
    {
        var s = c.State;
        if (!c.Policy.Schedules(s.Plan) || !SessionStep(s) || s.Parked) return StepAnswer.Refused("the item waits for no slot");
        if (h.At < s.SlotStamp) return StepAnswer.Stale("an older word of the scheduler than the last applied");
        var text = h.Waiting;
        var gaveUpSlot = s.SlotId.Length > 0;
        if (!gaveUpSlot && s.SlotAsked && s.SlotWait == text) { s.SlotStamp = h.At; return StepAnswer.Applied; }
        s.SlotId = ""; s.SlotRuntime = ""; s.SlotHost = "";
        s.SlotAsked = true; s.SlotWait = text; s.SlotStamp = h.At;
        if (s.LeaseDue is not null) { s.LeaseDue = null; c.TimerChanged = true; }
        c.Decide(WorkItemActions.Held, text);
        return StepAnswer.Applied;
    }

    /// <summary>The session of the slot is over while the step is not: the item asks again as an owned item (rank 2). The step's budgets are the lease's and the launches', not this message's.</summary>
    public static StepAnswer Lost(StepContext c, SessionLost l)
    {
        var s = c.State;
        if (!c.Policy.Schedules(s.Plan) || !SessionStep(s) || s.Parked) return StepAnswer.Refused("the item has no session to lose");
        if (l.At < s.SlotStamp) return StepAnswer.Stale("an older word than the last applied");
        if (s.SlotId.Length == 0) return StepAnswer.Refused("the item holds no slot");
        if (l.SlotId.Length > 0 && l.SlotId != s.SlotId) return StepAnswer.Stale($"the slot is {s.SlotId}");
        c.Decide(WorkItemActions.SessionLost, l.Reason, s.SlotId);
        s.SlotId = ""; s.SlotRuntime = ""; s.SlotHost = ""; s.SlotStamp = l.At;
        c.AskSlot(s.Step);
        return StepAnswer.Applied;
    }
}
