namespace Fworker.Workflows.WorkItem;

// W6.1: the steps of a plan (docs/design/wolverine-factory.md 3.1). A step is a class with its lease, what it does when the item enters and leaves it, and the messages it owns: the saga routes each message to the
// step it belongs to (a fact about the merge belongs to the merge step whichever step the item is on). The rules inside the steps are the core's: the docs-only decisions of the design and the pull request, the
// merge as a fact, the delivery and the settle window. W6.6 (Plans/DocsSteps.cs) gives the docs plan's steps their own rules on top of this: Steps.For(plan, kind) picks the step class of the plan.

/// <summary>What a step answered to a message: the outcome and, for a refusal, why.</summary>
public readonly record struct StepAnswer(WorkItemOutcome Outcome, string Reason = "")
{
    public static readonly StepAnswer Applied = new(WorkItemOutcome.Applied);
    public static StepAnswer Refused(string reason) => new(WorkItemOutcome.Refused, reason);
    public static StepAnswer Stale(string reason) => new(WorkItemOutcome.Stale, reason);
}

/// <summary>What a step may do to the item while it handles one message: change its place, switch its plan, park it, and publish a decision for each.</summary>
public sealed class StepContext(WorkItemState state, WorkItemPolicy policy, IDocsOnlyPort docs, DateTimeOffset now)
{
    public WorkItemState State { get; } = state;
    public WorkItemPolicy Policy { get; } = policy;
    public IDocsOnlyPort Docs { get; } = docs;
    public DateTimeOffset Now { get; } = now;
    /// <summary>The decisions of this transition, in order.</summary>
    public List<WorkItemDecided> Decisions { get; } = [];
    /// <summary>Set when the lease or the settle window changed: the saga asks for a new timer. A transition that leaves them alone asks for none.</summary>
    public bool TimerChanged { get; set; }

    public WorkPlan Plan => State.CurrentPlan;

    /// <summary>Publishes one decision, numbered after the saga's last.</summary>
    public WorkItemDecided Decide(string action, params string[] reasons)
    {
        var d = new WorkItemDecided(State.ItemId, State.Generation, ++State.Sequence, action, State.Plan, State.Status == WorkItemStatus.Completed ? null : State.Step.ToString(), State.ArmId.Length == 0 ? null : State.ArmId, reasons, Now);
        Decisions.Add(d);
        return d;
    }

    /// <summary>The item enters <paramref name="step"/> under a new arm: the lease starts, a parked item is parked no more.</summary>
    public void Enter(StepKind step, string reason)
    {
        State.Step = step;
        State.ArmSeq++;
        State.ArmId = $"{State.Generation}.{State.ArmSeq}";
        State.Status = WorkItemStatus.Open;
        State.Parked = false; State.ParkReason = "";
        State.SettleUntil = null;
        State.LeaseDue = Steps.For(Plan, step).Lease(Policy) is { } lease ? Now + lease : null;
        State.MergeRequest = null; State.MergeRetryAt = null;   // (a merge asked of an earlier arm is no longer waited for)
        TimerChanged = true;
        Decide(WorkItemActions.StepEntered, reason);
        Steps.For(Plan, step).OnEnter(this);   // (after the decision: what the step asks for, it asks for in the step it has entered)
    }

    /// <summary>The step is done: the next step of the plan, or the end of the item when it was the last.</summary>
    public void Advance(string reason)
    {
        Steps.For(Plan, State.Step).OnLeave(this);
        if (Plan.After(State.Step) is { } next) Enter(next, reason);
        else Complete(reason);
    }

    public void Complete(string reason)
    {
        State.Status = WorkItemStatus.Completed;
        State.LeaseDue = null; State.SettleUntil = null; State.Parked = false; State.ParkReason = "";
        State.CompletedAt = Now;
        State.MergeRequest = null; State.MergeRetryAt = null;
        TimerChanged = true;
        Decide(WorkItemActions.Completed, reason);
    }

    /// <summary>Switches the plan. The item stays on its step when the new plan has it; when it does not (the docs plan has no Deliver) the item has nothing left to do and completes.</summary>
    public void SwitchPlan(string to, string reason)
    {
        var from = State.Plan;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        State.Plan = to;
        State.MergeRequest = null; State.MergeRetryAt = null;   // (a merge asked of the docs plan is not this plan's)
        Decide(WorkItemActions.PlanSwitched, $"{from} to {to}", reason);
        if (!Policy.Carries(to)) HandBack($"the {to} plan is not carried by this engine");
        else if (!Plan.Has(State.Step)) Complete($"the {to} plan has no {State.Step} step");
    }

    /// <summary>
    /// The instance ends without the item being done: v1 takes the item up again (it is no longer owned, so its sweep reads it). Used when the item moves to a plan this engine does not carry; nothing about the board or
    /// the state comment changes, because v1's verbs kept them in step all along.
    /// </summary>
    public void HandBack(string reason)
    {
        State.Status = WorkItemStatus.Completed;
        State.LeaseDue = null; State.SettleUntil = null; State.Parked = false; State.ParkReason = "";
        State.MergeRequest = null; State.MergeRetryAt = null;
        State.CompletedAt = Now;
        TimerChanged = true;
        Decide(WorkItemActions.HandedBack, reason);
    }

    public void Park(string reason)
    {
        State.Parked = true; State.ParkReason = reason;
        State.LeaseDue = null; State.MergeRequest = null; State.MergeRetryAt = null;
        TimerChanged = true;
        Decide(WorkItemActions.Parked, reason);
    }

    /// <summary>The settle window starts under a new arm, so its timer is checked like a lease.</summary>
    public void Settle(string reason)
    {
        State.Status = WorkItemStatus.Settling;
        State.ArmSeq++;
        State.ArmId = $"{State.Generation}.{State.ArmSeq}";
        State.LeaseDue = null;
        State.SettleUntil = Now + Policy.Settle;
        TimerChanged = true;
        Decide(WorkItemActions.Settling, reason, $"until {State.SettleUntil:O}");
    }
}

public abstract class WorkStep
{
    public abstract StepKind Kind { get; }

    /// <summary>How long the step may go without a sign of life; null for a step that waits for events only.</summary>
    public virtual TimeSpan? Lease(WorkItemPolicy policy) => policy.LeaseOf(Kind);

    public virtual void OnEnter(StepContext c) { }
    public virtual void OnLeave(StepContext c) { }

    /// <summary>A refusal of the report "this step is done" (StepCompleted) when the step's own rule is not met, or null when the step may be left. The core steps leave on a report.</summary>
    public virtual StepAnswer? Gate(StepContext c) => null;

    /// <summary>Handles a message that belongs to this step. The saga has checked the generation, the key and, for an armed message, the arm.</summary>
    public virtual StepAnswer Handle(StepContext c, WorkItemInput input) => StepAnswer.Refused($"the {Kind} step takes no {input.GetType().Name}");
}

/// <summary>Design. What the latest design section recorded decides the plan: the docs-only marker switches a feature item to the docs plan and the lack of it a docs item back.</summary>
public class DesignStep : WorkStep
{
    public override StepKind Kind => StepKind.Design;

    // leaving the design column always leaves an explicit decision behind: no design recorded one, so the item is not docs-only (Verbs.Item.ps1, complete)
    public override void OnLeave(StepContext c) => c.State.DesignMarker ??= false;

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is not DesignRecorded d) return base.Handle(c, input);
        if (c.State.Step != StepKind.Design) return StepAnswer.Stale("the item is past its design");
        c.State.DesignMarker = d.DocsOnlyMarker;
        var docsOnly = c.Docs.ItemIsDocsOnly(d.DocsOnlyMarker);
        if (docsOnly && c.State.Plan == WorkPlans.Feature) c.SwitchPlan(WorkPlans.Docs, "the design carries the docs-only marker");
        else if (!docsOnly && c.State.Plan == WorkPlans.Docs) c.SwitchPlan(WorkPlans.Feature, "the latest design does not carry the docs-only marker");
        return StepAnswer.Applied;
    }
}

/// <summary>Develop. The pull request exists from here: its files may show that a docs item changes code.</summary>
public class DevelopStep : WorkStep
{
    public override StepKind Kind => StepKind.Develop;

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is not PullRequestChanged p) return base.Handle(c, input);
        if (c.State.Step > StepKind.Merge) return StepAnswer.Stale("the item is past its merge");
        if (p.PullRequest > 0) c.State.PullRequest = p.PullRequest;
        if (p.Head.Length > 0) c.State.Head = p.Head;
        // the pull request of a docs item that changes anything but documentation is a feature (Verbs.Item.ps1, development-docs-only-pr, in the other direction); only a definite "not documentation" switches
        if (c.State.Plan == WorkPlans.Docs && p.Files.Count > 0 && c.Docs.FilesAreDocumentation(p.Files) == false)
            c.SwitchPlan(WorkPlans.Feature, "the pull request changes files that are not documentation");
        return StepAnswer.Applied;
    }
}

/// <summary>Verify: CI and the Functional Testing verdict run beside it. The core has no rule of its own for it yet (W6.6 and W7 add them); the lease and the budgets apply.</summary>
public class VerifyStep : WorkStep
{
    public override StepKind Kind => StepKind.Verify;
}

/// <summary>Merge. The merge is a fact (ItemMerged): it completes this step whichever step the item was on, because a person or the merge queue may have merged it first.</summary>
public class MergeStep : WorkStep
{
    public override StepKind Kind => StepKind.Merge;

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        if (input is not MergeSeen m) return base.Handle(c, input);
        var plan = c.Plan;
        if (!plan.Has(StepKind.Merge)) return StepAnswer.Refused($"the {plan.Name} plan has no merge step");
        if (c.State.MergeCommit.Length > 0 && string.Equals(c.State.MergeCommit, m.Commit, StringComparison.OrdinalIgnoreCase)) return StepAnswer.Refused("the merge was recorded");
        if (plan.IndexOf(c.State.Step) > plan.IndexOf(StepKind.Merge)) return StepAnswer.Refused("the item is past its merge");
        c.State.MergeCommit = m.Commit.ToLowerInvariant();
        if (m.PullRequest > 0) c.State.PullRequest = m.PullRequest;
        c.Decide(WorkItemActions.Merged, $"commit {(m.Commit.Length > 7 ? m.Commit[..7] : m.Commit)}");
        c.State.Step = StepKind.Merge;   // (the steps between were skipped: the merge happened)
        c.Advance("merged");
        return StepAnswer.Applied;
    }
}

/// <summary>Deliver. The delivered-to events move it: reaching the final environment starts the settle window, an Undelivered inside the window returns the item, the window's end completes it.</summary>
public sealed class DeliverStep : WorkStep
{
    public override StepKind Kind => StepKind.Deliver;

    public override StepAnswer Handle(StepContext c, WorkItemInput input)
    {
        switch (input)
        {
            case DeliverySeen d: return Delivered(c, d);
            case DeliveryLost l: return Lost(c, l);
            case MergeFactsRead f: return MergeFacts(c, f);
            case SettleWindowElapsed:
                if (c.State.Status != WorkItemStatus.Settling) return StepAnswer.Stale("the item is not inside its settle window");
                c.Complete("the settle window ended");
                return StepAnswer.Applied;
            default: return base.Handle(c, input);
        }
    }

    static StepAnswer Delivered(StepContext c, DeliverySeen d)
    {
        var env = d.Environment.ToLowerInvariant();
        var already = c.State.Delivered.TryGetValue(env, out var release) && release == d.Release;
        c.State.Delivered[env] = d.Release;
        if (!c.Plan.Has(StepKind.Deliver)) return StepAnswer.Refused($"the {c.Plan.Name} plan has nothing to deliver");
        if (env != c.Policy.FinalEnvironment.ToLowerInvariant() || c.State.Status == WorkItemStatus.Settling) return already ? StepAnswer.Refused("the delivery was recorded") : StepAnswer.Applied;
        if (c.State.Step != StepKind.Deliver)
        {
            // it can only be delivered if it was merged: the steps between are skipped
            c.State.Step = StepKind.Deliver;
        }
        c.Settle($"delivered to {env} in {d.Release}");
        return StepAnswer.Applied;
    }

    static StepAnswer Lost(StepContext c, DeliveryLost l)
    {
        var env = l.Environment.ToLowerInvariant();
        var had = c.State.Delivered.Remove(env);
        if (c.State.Status == WorkItemStatus.Settling && env == c.Policy.FinalEnvironment.ToLowerInvariant())
        {
            c.Decide(WorkItemActions.Undelivered, $"no longer in {env}");
            c.Enter(StepKind.Deliver, $"undelivered from {env} inside the settle window");
            return StepAnswer.Applied;
        }
        return had ? StepAnswer.Applied : StepAnswer.Refused("the item was not delivered there");
    }

    // after the merge, a commit with no release built is settled as docs (Verbs.Delivery.ps1, Get-MergeDocsOnly): there is nothing to deliver
    static StepAnswer MergeFacts(StepContext c, MergeFactsRead f)
    {
        if (c.State.MergeCommit.Length == 0 || c.State.Step != StepKind.Deliver) return StepAnswer.Refused("the item has not been merged");
        if (c.State.Plan != WorkPlans.Feature) return StepAnswer.Refused($"the {c.State.Plan} plan is not settled by the merge's build");
        if (c.Docs.MergeIsDocsOnly(f.Checks, f.PullRequestFiles) is { DocsOnly: true } verdict)
        {
            c.SwitchPlan(WorkPlans.Docs, $"the merge is docs-only (from {verdict.From})");
            return StepAnswer.Applied;
        }
        return StepAnswer.Applied;
    }
}

/// <summary>The steps by kind.</summary>
public static class Steps
{
    static readonly Dictionary<StepKind, WorkStep> All = new WorkStep[] { new DesignStep(), new DevelopStep(), new VerifyStep(), new MergeStep(), new DeliverStep() }.ToDictionary(s => s.Kind);

    static readonly Dictionary<StepKind, WorkStep> DocsSteps = new WorkStep[] { new DocsDesignStep(), new DocsDevelopStep(), new DocsVerifyStep(), new DocsMergeStep() }.ToDictionary(s => s.Kind);

    /// <summary>The core step of that kind. A plan with rules of its own (the docs plan, W6.6) has its own class: <see cref="For"/>.</summary>
    public static WorkStep Of(StepKind kind) => All[kind];

    /// <summary>The step class of <paramref name="kind"/> on <paramref name="plan"/>.</summary>
    public static WorkStep For(WorkPlan plan, StepKind kind) => plan.Name == WorkPlans.Docs && DocsSteps.TryGetValue(kind, out var s) ? s : All[kind];

    /// <summary>The step a message belongs to, or null for a message of the saga itself (start, completion, budgets, leases, park).</summary>
    public static StepKind? HomeOf(WorkItemInput input) => input switch
    {
        DesignRecorded => StepKind.Design,
        PullRequestChanged => StepKind.Develop,
        MergeSeen or MergeAttempted or MergeRetryDue => StepKind.Merge,
        VerdictRecorded => StepKind.Verify,
        MergeFactsRead or DeliverySeen or DeliveryLost or SettleWindowElapsed => StepKind.Deliver,
        _ => null,
    };
}
