using Fworker.Contracts.Ids;

namespace Fworker.Workflows.WorkItem;

// W6.1: WorkItemSaga, one instance per work item and generation (docs/design/wolverine-factory.md 3.1). This is its state machine as a pure function: Apply takes the state an item has (or none), one input and the
// time, and answers the new state, the decisions to publish and the timers to ask for. Nothing here reads or writes anything; WorkItemService loads and saves the state with a revision and the Wolverine handlers
// publish the result, so the whole machine is tested without a database.
//
//  - The id is the item plus a generation. A closed item that is reopened starts the next one; every timer and every session message carries the generation and the arm id of the step it answers, so a message from an
//    earlier instance or an earlier lease finds a different generation or arm and is ignored (Stale, not an error). A message for an item that has no open instance (never started, completed, another generation)
//    is NotFound and ends quietly: a late webhook or timer does not end in the dead letters.
//  - Business keys: every input that changes the state carries one (phase, round, head, session...); a key applied before does nothing. An input whose key is the key of the last applied one is a redelivery after a
//    crash between the save and the publish: it publishes what that transition published again (the decisions carry sequence numbers, the timers are checked against the arm), so no decision or lease timer is lost.
//  - The budgets (failures and launches per phase, idle follow-ups per item) are in the state, not in memory: a restart grants no extra round.
//  - What the saga decides leaves as WorkItemDecided messages; in the hybrid composition v1's plant still offers the sessions and runs the verbs v2 does not own yet, and this saga decides the docs-only items.

/// <summary>What Apply made of an input: the state to save (the same object when nothing changed), whether it changed, and the result.</summary>
public sealed record WorkItemTransition(WorkItemState? State, bool Changed, WorkItemResult Result);

public sealed class WorkItemSaga(IDocsOnlyPort docs, WorkItemPolicy? policy = null)
{
    readonly WorkItemPolicy policy = policy ?? WorkItemPolicy.Default;

    public WorkItemPolicy Policy => policy;

    /// <summary>The business key of an input, or "" when it has none.</summary>
    public static string KeyOf(WorkItemInput input) => input is IKeyedInput k ? k.Key : "";

    // what a redelivery of the input is recognised by: the key, or for the unkeyed inputs a key made of what identifies them
    static string ReplayKeyOf(WorkItemInput input) => input switch
    {
        IKeyedInput k => k.Key,
        StartWorkItem => "start",
        PhaseLeaseExpired l => $"lease|{l.ArmId}|{l.Due.UtcTicks}",
        SettleWindowElapsed s => $"settle|{s.ArmId}",
        _ => "",
    };

    public WorkItemTransition Apply(WorkItemState? existing, WorkItemInput input, DateTimeOffset now)
    {
        switch (input)
        {
            case StartWorkItem s: return Start(existing, s, now);
            case ReopenWorkItem r when existing is { IsOpen: false }: return NewGeneration(input.Item, existing, r.Reason.Length == 0 ? "reopened" : $"reopened: {r.Reason}", PlanOfReopen(existing), now, r.Key, WorkItemActions.Reopened);
            case DeliveryLost l when existing is { IsOpen: false }: return Redeliver(existing, l, now);
        }
        if (existing is null) return Nothing(null, WorkItemOutcome.NotFound, "the item has no saga");
        // a copy: whatever a refused or stale input touched is dropped with it
        var state = WorkItemState.FromJson(existing.ToJson());
        var replay = ReplayKeyOf(input);
        if (replay.Length > 0 && replay == existing.LastKey) return Reemit(existing, state);
        if (!state.IsOpen) return Nothing(existing, WorkItemOutcome.NotFound, "the instance is completed");
        if (input is IGenerationInput g && g.Generation != 0 && g.Generation != state.Generation) return Nothing(existing, WorkItemOutcome.NotFound, $"the item is on generation {state.Generation}");
        if (input is IKeyedInput { Key.Length: > 0 } keyed && state.Keys.Contains(keyed.Key)) return Nothing(existing, WorkItemOutcome.Duplicate, "the key was applied");
        if (input is IArmedInput armed && armed.ArmId != state.ArmId) return Nothing(existing, WorkItemOutcome.Stale, $"the arm is {state.ArmId}");

        var c = new StepContext(state, policy, docs, now);
        var answer = Route(c, input);
        if (answer.Outcome != WorkItemOutcome.Applied) return Nothing(existing, answer.Outcome, answer.Reason);
        return Seal(c, input);
    }

    StepAnswer Route(StepContext c, WorkItemInput input)
    {
        var state = c.State;
        switch (input)
        {
            case StepCompleted:
                if (state.Parked) return StepAnswer.Refused("the item is parked");
                if (state.Status == WorkItemStatus.Settling) return StepAnswer.Refused("the item is inside its settle window");
                if (state.Step == StepKind.Deliver) return StepAnswer.Refused("the deliver step ends with the delivery, not with a report");
                c.Advance($"{state.Step} done");
                return StepAnswer.Applied;
            case BudgetSpent b: return Budget(c, b);
            case LeaseRenewed:
                if (state.Parked || state.LeaseDue is null) return StepAnswer.Refused("the step has no lease to renew");
                state.LeaseDue = c.Now + (Steps.Of(state.Step).Lease(policy) ?? TimeSpan.Zero);
                c.TimerChanged = true;
                return StepAnswer.Applied;
            case PhaseLeaseExpired l: return LeaseExpired(c, l);
            case ParkWorkItem p:
                if (state.Parked) return StepAnswer.Refused("the item is parked");
                c.Park(p.Reason.Length == 0 ? "parked" : p.Reason);
                return StepAnswer.Applied;
            case ResumeWorkItem:
                if (!state.Parked) return StepAnswer.Refused("the item is not parked");
                state.Failures.Remove(state.Step.ToString()); state.Launches.Remove(state.Step.ToString()); state.IdleFollowUps = 0;
                c.Decide(WorkItemActions.Resumed, $"was parked: {state.ParkReason}");
                // an item parked inside its settle window is delivered: it goes on with the window, it does not return to its step
                if (state.Status == WorkItemStatus.Settling) { state.Parked = false; state.ParkReason = ""; return StepAnswer.Applied; }
                c.Enter(state.Step, "resumed");
                return StepAnswer.Applied;
            case StartWorkItem or ReopenWorkItem:
                return StepAnswer.Refused("the item is open");
        }
        var home = Steps.HomeOf(input);
        if (home is null) return StepAnswer.Refused("no step takes the message");
        // a message about the item's progress is not taken while it is parked, except the facts that happened outside (the merge, a delivery, the pull request)
        if (state.Parked && input is DesignRecorded) return StepAnswer.Refused("the item is parked");
        return Steps.Of(home.Value).Handle(c, input);
    }

    StepAnswer Budget(StepContext c, BudgetSpent b)
    {
        var state = c.State; var phase = state.Step.ToString();
        if (state.Parked) return StepAnswer.Refused("the item is parked");
        switch (b.Kind)
        {
            case BudgetKind.Registered:
                state.IdleFollowUps = 0;
                return StepAnswer.Applied;
            case BudgetKind.Failure:
                state.Failures[phase] = state.FailuresOf(state.Step) + 1;
                if (state.Failures[phase] >= policy.MaxFailuresPerPhase) c.Park($"failures-exhausted: {state.Failures[phase]} failures in {phase}{Why(b.Reason)}");
                return StepAnswer.Applied;
            case BudgetKind.Launch:
                // the launch is asked for: one that would pass the limit is refused and parks the item (v1: Exhausted is asked before Provision); another phase starting begins the count again
                foreach (var other in state.Launches.Keys.Where(k => k != phase).ToList()) state.Launches.Remove(other);
                if (policy.MaxLaunchesPerPhase > 0 && state.LaunchesOf(state.Step) >= policy.MaxLaunchesPerPhase)
                {
                    c.Park($"launches-exhausted: {policy.MaxLaunchesPerPhase} executions in a row without finishing {phase}");
                    return StepAnswer.Applied;
                }
                state.Launches[phase] = state.LaunchesOf(state.Step) + 1;
                return StepAnswer.Applied;
            case BudgetKind.IdleFollowUp:
                state.IdleFollowUps++;
                if (state.IdleFollowUps >= policy.MaxIdleFollowUps) c.Park($"followups-idle: {state.IdleFollowUps} follow-up sessions in a row ended without connecting to the gateway or pushing");
                return StepAnswer.Applied;
            default: return StepAnswer.Refused("unknown budget");
        }
    }

    static string Why(string reason) => reason.Length == 0 ? "" : $" ({reason})";

    StepAnswer LeaseExpired(StepContext c, PhaseLeaseExpired l)
    {
        var state = c.State; var phase = state.Step.ToString();
        // the lease may have been renewed since the timer was scheduled: the renewal has a timer of its own
        if (state.Parked || state.Status != WorkItemStatus.Open || state.LeaseDue != l.Due) return StepAnswer.Stale("the lease is not the one that ran out");
        c.Decide(WorkItemActions.LeaseExpired, $"{phase} had no sign of life by {l.Due:O}");
        state.Failures[phase] = state.FailuresOf(state.Step) + 1;
        if (state.Failures[phase] >= policy.MaxFailuresPerPhase) c.Park($"failures-exhausted: {state.Failures[phase]} failures in {phase} (lease expired)");
        else c.Enter(state.Step, "lease expired");
        return StepAnswer.Applied;
    }

    // ---- starting, reopening ----------------------------------------------------------------------------------------------------------------

    WorkItemTransition Start(WorkItemState? existing, StartWorkItem s, DateTimeOffset now)
    {
        var plan = WorkPlans.Choose(s.Plan, s.Hotfix);
        if (plan is null || plan.Name == WorkPlans.Redeliver) return Nothing(existing, WorkItemOutcome.Refused, $"there is no plan '{s.Plan}' to start on");
        if (existing is { IsOpen: true })
        {
            // a redelivery of the start finds its own transition still the last and publishes it again; a start of an item that has moved on changes nothing
            if (existing.LastKey == "start") return Reemit(existing, WorkItemState.FromJson(existing.ToJson()));
            return Nothing(existing, WorkItemOutcome.Refused, "the item is open");
        }
        return NewGeneration(s.Item, existing, s.Source.Length == 0 ? "started" : s.Source, plan.Name, now, "start", WorkItemActions.Started);
    }

    static string PlanOfReopen(WorkItemState completed) => completed.Plan == WorkPlans.Redeliver ? WorkPlans.Feature : completed.Plan;

    // the next instance: a fresh state on the plan's first step; the decision sequence goes on across generations
    WorkItemTransition NewGeneration(ItemId item, WorkItemState? previous, string source, string planName, DateTimeOffset now, string key, string action)
    {
        var plan = WorkPlans.Get(planName);
        var state = new WorkItemState { Item = item.ToString(), Generation = (previous?.Generation ?? 0) + 1, Plan = plan.Name, StartedAt = now, Source = source, Sequence = previous?.Sequence ?? 0 };
        return Begin(state, plan, source, now, key, action);
    }

    WorkItemTransition Redeliver(WorkItemState completed, DeliveryLost l, DateTimeOffset now)
    {
        // a later Undelivered of a closed item starts a new generation on the redeliver plan (wolverine-factory.md 3.1)
        if (completed.Delivered.GetValueOrDefault(l.Environment.ToLowerInvariant()) is null) return Nothing(completed, WorkItemOutcome.NotFound, "the completed instance was not delivered there");
        var state = new WorkItemState { Item = completed.Item, Generation = completed.Generation + 1, Plan = WorkPlans.Redeliver, StartedAt = now, Source = $"undelivered from {l.Environment}", Sequence = completed.Sequence };
        foreach (var kv in completed.Delivered) if (kv.Key != l.Environment.ToLowerInvariant()) state.Delivered[kv.Key] = kv.Value;
        state.MergeCommit = completed.MergeCommit; state.PullRequest = completed.PullRequest;
        return Begin(state, WorkPlans.RedeliverPlan, state.Source, now, l.Key, WorkItemActions.Reopened);
    }

    WorkItemTransition Begin(WorkItemState state, WorkPlan plan, string source, DateTimeOffset now, string key, string action)
    {
        var c = new StepContext(state, policy, docs, now);
        state.Step = plan.First;
        c.Decide(action, source);
        c.Enter(plan.First, "first step of the plan");
        return Seal(c, null, key);
    }

    // ---- the end of every transition ----------------------------------------------------------------------------------------------------------

    WorkItemTransition Seal(StepContext c, WorkItemInput? input, string? key = null)
    {
        var state = c.State;
        var k = input is null ? key ?? "" : KeyOf(input);
        if (k.Length > 0 && input is not null) { state.Keys.Add(k); if (state.Keys.Count > policy.KeepKeys) state.Keys.RemoveRange(0, state.Keys.Count - policy.KeepKeys); }
        state.LastKey = input is null ? (key ?? "") : ReplayKeyOf(input);
        state.Emitted = c.Decisions;
        var timers = c.TimerChanged ? TimersOf(state) : [];
        var decision = new WorkItemDecision(WorkItemOutcome.Applied, state.Generation, state.Plan, state.Status == WorkItemStatus.Completed ? null : state.Step.ToString(), state.ArmId.Length == 0 ? null : state.ArmId);
        return new WorkItemTransition(state, true, new WorkItemResult(decision, c.Decisions, timers));
    }

    /// <summary>The timers the state asks for: the lease of an open step that has one, the end of the settle window.</summary>
    public static IReadOnlyList<WorkItemTimer> TimersOf(WorkItemState state)
    {
        var item = state.ItemId;
        if (state.Status == WorkItemStatus.Open && !state.Parked && state.LeaseDue is { } due) return [new WorkItemTimer(new PhaseLeaseExpired(item, state.Generation, state.ArmId, due), due)];
        if (state.Status == WorkItemStatus.Settling && state.SettleUntil is { } until) return [new WorkItemTimer(new SettleWindowElapsed(item, state.Generation, state.ArmId), until)];
        return [];
    }

    WorkItemTransition Reemit(WorkItemState saved, WorkItemState copy)
    {
        var decision = new WorkItemDecision(WorkItemOutcome.Duplicate, saved.Generation, saved.Plan, saved.Status == WorkItemStatus.Completed ? null : saved.Step.ToString(), saved.ArmId.Length == 0 ? null : saved.ArmId, "published again");
        return new WorkItemTransition(copy, false, new WorkItemResult(decision, saved.Emitted, TimersOf(saved)));
    }

    static WorkItemTransition Nothing(WorkItemState? state, WorkItemOutcome outcome, string reason)
    {
        var d = state is null ? new WorkItemDecision(outcome, 0, "", null, null, reason)
            : new WorkItemDecision(outcome, state.Generation, state.Plan, state.Status == WorkItemStatus.Completed ? null : state.Step.ToString(), state.ArmId.Length == 0 ? null : state.ArmId, reason);
        return new WorkItemTransition(state, false, WorkItemResult.Quiet(d));
    }
}
