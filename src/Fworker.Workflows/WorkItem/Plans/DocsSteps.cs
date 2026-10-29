using Fworker.Domain;

namespace Fworker.Workflows.WorkItem;

// W6.6 (#335): the docs plan's steps (Design, Develop, Verify, Merge; a documentation change has nothing to deliver). Each is the core step of WorkSteps.cs plus the rules v1's verbs apply to a docs-only item, so that the
// saga can decide what v1's verb scripts decide today:
//  - Design: record-review (Verbs.Item.ps1, Verb_record_review) limits the review rounds of the column, the fast path of a docs-only design to timing.docsOnlyMaxReviewRounds; complete leaves the column only after a
//    REVIEW: COMPLETE (reviewVerdictColumns). The docs-only marker of the design is the core's (DesignRecorded).
//  - Develop: complete needs the same review and a pull request (Verb_complete: no-open-pr). A pull request whose files are not documentation switches the item to the feature plan (the core's PullRequestChanged); a
//    new head after the PASS sends the item back to Verify, because the merge sends exactly the head the PASS covers.
//  - Verify: only a validation verdict leaves it (use-validation-verdict). PASS covers a head and moves the item to Merge; FAIL is D5's FailVerdict row (Invoke-Demote): back to Development while the demotions last, then a park.
//  - Merge: the step asks the gateway to merge (MergeRequested, the C# direct merge of W6.2) and decides what each answer means: merged ends it, blocked and transient answers ask again after a pause, a head that moved
//    sends the item to Verify for a new verdict, an out-of-date branch is D5's MechanicalFailure row (the owner merges master), any other refusal parks the item. The merge is also a fact the delivery train derives
//    (ItemMerged, MergeSeen): whichever of the two arrives first ends the step.
// What v1 decides with the checks of the head (Development's complete refuses a red or pending head, the CI rule) stays with the verbs that run first (V2VerbFeed); the merge's own gate is GitHub's required checks, which a
// blocked answer ("checks-pending") waits for. Nothing here is reached on another plan.

/// <summary>The review of a column (record-review) shared by the steps that have one.</summary>
static class DocsReviews
{
    public static StepAnswer Record(StepContext c, ReviewRecorded r, int limit)
    {
        var step = c.State.Step.ToString();
        var rounds = c.State.ReviewRounds.GetValueOrDefault(step);
        if (rounds >= limit) return StepAnswer.Refused($"review-rounds-exhausted: the review loop is limited to {limit} rounds");
        c.State.ReviewRounds[step] = rounds + 1;
        c.State.ReviewsComplete.Remove(step);
        if (r.Complete) c.State.ReviewsComplete.Add(step);
        c.Decide(WorkItemActions.Reviewed, $"round {rounds + 1} of {limit}", r.Complete ? "REVIEW: COMPLETE" : "REVIEW: FINDINGS");
        return StepAnswer.Applied;
    }

    public static StepAnswer? Gate(StepContext c) =>
        !c.Policy.DocsRules.NeedsReview(c.State.Step) || c.State.ReviewsComplete.Contains(c.State.Step.ToString()) ? null : StepAnswer.Refused($"reviewer-verdict-missing: the {c.State.Step} step is left only after a recorded REVIEW: COMPLETE");

    /// <summary>Entering a step again is new work: reviews of the column start empty, as they do when v1's owner moves to a column or a session claims the item (Owner.ps1, Verb_claim_item).</summary>
    public static void Reset(StepContext c) { c.State.ReviewsComplete.Remove(c.State.Step.ToString()); c.State.ReviewRounds.Remove(c.State.Step.ToString()); }
}

public sealed class DocsDesignStep : DesignStep
{
    public override void OnEnter(StepContext c) => DocsReviews.Reset(c);

    // docs-only fast path: once the latest design recorded the marker, the column has the smaller limit
    public override StepAnswer Handle(StepContext c, WorkItemInput input) =>
        input is ReviewRecorded r ? DocsReviews.Record(c, r, c.Policy.DocsRules.DesignRounds(c.State.DesignMarker)) : base.Handle(c, input);

    public override StepAnswer? Gate(StepContext c) => DocsReviews.Gate(c);
}

public sealed class DocsDevelopStep : DevelopStep
{
    public override void OnEnter(StepContext c) => DocsReviews.Reset(c);

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is ReviewRecorded r) return DocsReviews.Record(c, r, c.Policy.DocsRules.MaxReviewRounds);
        var answer = base.Handle(c, input);
        if (answer.Outcome != WorkItemOutcome.Applied || input is not PullRequestChanged p) return answer;
        // a new head after the PASS: the verdict covered another one, so the merge is not asked for and the item is validated again
        if (c.State.Plan == WorkPlans.Docs && c.State.Step == StepKind.Merge && p.Head.Length > 0 && !MergeSteps.SameHead(c.State.VerdictHead, p.Head))
        {
            c.State.VerdictHead = "";
            c.Enter(StepKind.Verify, $"the head moved to {MergeSteps.Short(p.Head)} after the PASS");
        }
        return answer;
    }

    public override StepAnswer? Gate(StepContext c)
    {
        if (DocsReviews.Gate(c) is { } review) return review;
        if (c.State.PullRequest <= 0) return StepAnswer.Refused("no-open-pr: no pull request is recorded for this work item");
        return null;
    }
}

public class DocsVerifyStep : VerifyStep
{
    public override StepAnswer? Gate(StepContext c) => StepAnswer.Refused("use-validation-verdict: the Verify step is left by a validation verdict, not by a completion");

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is not VerdictRecorded v) return base.Handle(c, input);
        var s = c.State;
        if (s.Step != StepKind.Verify) return StepAnswer.Stale("the item is not being validated");
        if (s.Parked) return StepAnswer.Refused("the item is parked");
        if (v.Head.Length > 0 && s.Head.Length > 0 && !MergeSteps.SameHead(v.Head, s.Head))
            return StepAnswer.Refused($"head-mismatch: the verdict was given for {MergeSteps.Short(v.Head)}, but the head of the pull request is {MergeSteps.Short(s.Head)}; test the current head");
        if (v.PullRequest > 0) s.PullRequest = v.PullRequest;
        var head = v.Head.Length > 0 ? v.Head : s.Head;
        if (!v.Passed)
        {
            // D5: a FAIL verdict is a quality setback; v1 counts the demotion first and parks past maxDemotions
            var d = RecoveryPolicy.Decide(RecoveryCause.FailVerdict, new RecoveryCounters(Demotions: s.Demotions), c.Policy.Recovery);
            c.Decide(WorkItemActions.Verdict, "FAIL", Clip(v.Summary));
            if (d.Action == RecoveryAction.Park) c.Park($"{d.ParkReason}: {s.Demotions + 1} FAIL verdicts, over the demotion limit of {d.Budget}");
            else
            {
                s.Demotions++;
                s.VerdictHead = "";
                c.Decide(WorkItemActions.Demoted, $"round-trip {s.Demotions} of {d.Budget}", "validation FAILED");
                c.Enter(StepKind.Develop, "demoted: the validation verdict failed");
            }
            return StepAnswer.Applied;
        }
        if (s.PullRequest <= 0) return StepAnswer.Refused("pass-requires-prNumber: a PASS verdict requires a valid prNumber");
        if (head.Length == 0) return StepAnswer.Refused("pass-requires-head: the PASS does not name the head it was given for");
        s.VerdictHead = head.ToLowerInvariant(); s.VerdictSummary = Clip(v.Summary);
        s.MergeUpdates = 0; s.MergeUpdatedFrom = "";   // (the updates counted are those of this verdict)
        c.Decide(WorkItemActions.Verdict, "PASS", $"head {MergeSteps.Short(head)}");
        c.Advance("PASS verdict");
        return StepAnswer.Applied;
    }

    static string Clip(string text) => text.Length <= 300 ? text : text[..300];
}

public class DocsMergeStep : MergeStep
{
    public override StepAnswer? Gate(StepContext c) => StepAnswer.Refused("the merge step ends with the merge, not with a report");

    public override void OnEnter(StepContext c)
    {
        c.State.MergeAttempt = 0; c.State.MergeBlockedReason = "";
        MergeSteps.Request(c, "");
    }

    public override StepAnswer Handle(StepContext c, WorkItemInput input) => input switch
    {
        MergeAttempted m => MergeSteps.Attempted(c, m),
        MergeRetryDue r => MergeSteps.RetryDue(c, r),
        _ => base.Handle(c, input),
    };
}

/// <summary>The merge step's decisions on the answers of the merge command.</summary>
static class MergeSteps
{
    public static bool SameHead(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Asks the gateway to merge the pull request with the head the PASS covers; past the attempts the rules allow, the item is parked instead.</summary>
    public static void Request(StepContext c, string lastReason)
    {
        var s = c.State;
        if (s.MergeAttempt >= c.Policy.DocsRules.MaxMergeAttempts)
        {
            c.Park($"merge-blocked: {s.MergeAttempt} merge calls without a merge{(lastReason.Length == 0 ? "" : $" ({lastReason})")}");
            return;
        }
        s.MergeAttempt++;
        s.MergeRequest = new MergeRequestState { Attempt = s.MergeAttempt, PullRequest = s.PullRequest, Head = s.VerdictHead, UpdatesSoFar = s.MergeUpdates, UpdatedFrom = s.MergeUpdatedFrom };
        s.MergeRetryAt = null; s.MergeRequestEmitted = true; s.TrainEntered = true;
        c.TimerChanged = true;
        c.Decide(WorkItemActions.MergeRequested, $"attempt {s.MergeAttempt}", $"PR #{s.PullRequest} at {Short(s.VerdictHead)}");
    }

    public static StepAnswer Attempted(StepContext c, MergeAttempted m)
    {
        var s = c.State;
        if (s.Step != StepKind.Merge) return StepAnswer.Stale("the item is not in its merge");
        if (s.MergeRequest is null || s.MergeRequest.Attempt != m.Attempt) return StepAnswer.Stale("no merge call waits for that answer");
        var r = m.Result;
        s.MergeRequest = null;
        if (r.UpdatesAdded > 0) s.MergeUpdates += r.UpdatesAdded;
        switch (r.Kind)
        {
            case MergeResultKind.Merged:
                if (r.Commit.Length > 0) s.MergeCommit = r.Commit.ToLowerInvariant();
                c.Decide(WorkItemActions.Merged, r.Commit.Length > 0 ? $"commit {Short(r.Commit)}" : "merged", r.Reason);
                c.Advance("merged");
                return StepAnswer.Applied;
            case MergeResultKind.HeadMoved:
                // the branch carries a head the PASS does not cover: nothing was merged, a new verdict is needed
                s.VerdictHead = "";
                c.Enter(StepKind.Verify, $"the head moved after the PASS: {r.Detail}");
                return StepAnswer.Applied;
            case MergeResultKind.Blocked:
                return Wait(c, r);
            default:
                return r.Transient ? Wait(c, r) : Refused(c, r);
        }
    }

    // nothing is wrong yet: ask again after a pause, which also counts as a sign of life for the lease (the attempts are bounded by the rules)
    static StepAnswer Wait(StepContext c, MergeResult r)
    {
        var s = c.State;
        if (r.Reason == "branch-updated")
        {
            // GitHub's own update of the branch: the new head is the one the PASS now covers; while it is not there yet, the next call names the head the update started from
            if (r.NewHead.Length > 0) { s.VerdictHead = r.NewHead.ToLowerInvariant(); s.Head = s.VerdictHead; s.MergeUpdatedFrom = ""; }
            else s.MergeUpdatedFrom = s.VerdictHead;
        }
        if (s.MergeAttempt >= c.Policy.DocsRules.MaxMergeAttempts)
        {
            c.Park($"merge-blocked: {s.MergeAttempt} merge calls without a merge ({r.Reason})");
            return StepAnswer.Applied;
        }
        if (c.StepOf(s.Step).Lease(c.Policy) is { } lease) s.LeaseDue = c.Now + lease;
        s.MergeRetryAt = c.Now + (r.RetryAfter is { } after && after > TimeSpan.Zero ? after : c.Policy.DocsRules.Retry);
        c.TimerChanged = true;
        if (s.MergeBlockedReason != r.Reason) { s.MergeBlockedReason = r.Reason; c.Decide(WorkItemActions.MergeBlocked, r.Reason, r.Detail); }
        return StepAnswer.Applied;
    }

    static StepAnswer Refused(StepContext c, MergeResult r)
    {
        var s = c.State;
        if (r.Reason == "merge-out-of-date")
        {
            // D5: conflict, merge group conflict or out of date is mechanical; the owner merges master and the item is validated again on the new head
            var d = RecoveryPolicy.Decide(RecoveryCause.MechanicalFailure, new RecoveryCounters(MechanicalFixes: s.MechanicalFixes), c.Policy.Recovery);
            if (d.Action == RecoveryAction.Park) { c.Park($"{d.ParkReason}: {r.Detail}"); return StepAnswer.Applied; }
            s.MechanicalFixes++;
            s.VerdictHead = "";
            c.Decide(WorkItemActions.Demoted, $"mechanical fix {s.MechanicalFixes} of {d.Budget}", r.Detail);
            c.Enter(StepKind.Develop, "the pull request is out of date: the owner merges master");
            return StepAnswer.Applied;
        }
        c.Park($"merge-failed: {r.Reason}{(r.Detail.Length == 0 ? "" : $": {(r.Detail.Length <= 200 ? r.Detail : r.Detail[..200])}")}");
        return StepAnswer.Applied;
    }

    public static StepAnswer RetryDue(StepContext c, MergeRetryDue r)
    {
        var s = c.State;
        if (s.Step != StepKind.Merge || s.Parked || s.MergeRetryAt != r.Due || s.MergeRequest is not null) return StepAnswer.Stale("the merge is not waiting for this call");
        s.MergeRetryAt = null;
        Request(c, s.MergeBlockedReason);
        return StepAnswer.Applied;
    }
}
