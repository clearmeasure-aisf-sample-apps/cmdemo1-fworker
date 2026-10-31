using Fworker.Contracts.Ids;
using Fworker.Domain;

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
        MergeRetryDue m => $"mergeretry|{m.ArmId}|{m.Due.UtcTicks}",
        MasterWatchElapsed w => $"masterwatch|{w.Due.UtcTicks}",
        ParkExpired p => $"parkexpired|{p.Due.UtcTicks}",
        OwnerIdleElapsed i => $"owneridle|{i.Due.UtcTicks}",
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
        state.MergeRequestEmitted = false;   // (set again by the step that asks for a merge call in this transition)
        state.Outbox = [];                    // (what this transition asks of the scheduler and the merge line is added by the steps)
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
                if (c.StepOf(state.Step).Gate(c) is { } gate) return gate;
                c.Advance($"{state.Step} done");
                return StepAnswer.Applied;
            case BudgetSpent b: return Budget(c, b);
            case LeaseRenewed:
                if (state.Parked || state.LeaseDue is null) return StepAnswer.Refused("the step has no lease to renew");
                state.LeaseDue = c.Now + (c.StepOf(state.Step).Lease(policy) ?? TimeSpan.Zero);
                c.TimerChanged = true;
                return StepAnswer.Applied;
            case PhaseLeaseExpired l: return LeaseExpired(c, l);
            case ParkWorkItem p:
                if (state.Parked) return StepAnswer.Refused("the item is parked");
                c.Park(p.Reason.Length == 0 ? "parked" : p.Reason, p.Minutes > 0 ? TimeSpan.FromMinutes(p.Minutes) : null);
                return StepAnswer.Applied;
            case ResumeWorkItem:
                return Resume(c);
            case ItemClosed:
                // W9.10h: the merge decides for an item that is merged or merging (the merge closes its issue); a delivered item is in its settle window
                if (state.Status == WorkItemStatus.Settling) return StepAnswer.Refused("the item is delivered");
                if (state.MergeCommit.Length > 0) return StepAnswer.Refused("the item was merged");
                if (state.Step >= StepKind.Merge && !state.Parked) return StepAnswer.Refused("the merge step decides: the merge closes the issue");
                c.Complete("the issue was closed");
                return StepAnswer.Applied;
            case HoldObserved h:
                if (h.Hold is { Length: > 0 } hold)
                {
                    if (state.Parked) return StepAnswer.Refused("the item is parked");
                    if (state.Status != WorkItemStatus.Open || state.Step == StepKind.Deliver) return StepAnswer.Refused("the item is delivered or being delivered");
                    c.Park($"{HoldParkPrefix}{hold}");
                    return StepAnswer.Applied;
                }
                if (!state.Parked || !state.ParkReason.StartsWith(HoldParkPrefix, StringComparison.Ordinal)) return StepAnswer.Refused("the item is not held");
                return Resume(c);
            case CardMoved m: return Moved(c, m);
            case StartWorkItem or ReopenWorkItem:
                return StepAnswer.Refused("the item is open");
            case MasterObserved m: return SweepFlow.Master(c, m);
            case MasterWatchElapsed w: return SweepFlow.MasterElapsed(c, w);
            case ParkExpired e: return SweepFlow.Expired(c, e);
            case OwnerIdleElapsed i: return SweepFlow.OwnerIdle(c, i);
            case PullRequestClosed pc: return SweepFlow.Closed(c, pc);
            case ApprovalSeen a: return SweepFlow.Approved(c, a);
            case RearmSettle r: return SweepFlow.Rearm(c, r);
            case ChecksObserved o: return FeatureFlow.Checks(c, o);
            case SlotAssigned a: return FeatureFlow.Assigned(c, a);
            case SlotHeld h: return FeatureFlow.Held(c, h);
            case SessionLost l: return FeatureFlow.Lost(c, l);
            case ReviewRecorded:
                if (state.Parked) return StepAnswer.Refused("the item is parked");
                return c.StepOf(state.Step).Handle(c, input);
        }
        var home = Steps.HomeOf(input);
        if (home is null) return StepAnswer.Refused("no step takes the message");
        // a message about the item's progress is not taken while it is parked, except the facts that happened outside (the merge, a delivery, the pull request)
        if (state.Parked && input is DesignRecorded) return StepAnswer.Refused("the item is parked");
        return c.StepOf(home.Value).Handle(c, input);
    }

    /// <summary>The park reason of a hold label (W9.10h): a removal of the label lifts exactly this park.</summary>
    public const string HoldParkPrefix = "hold: ";

    // the budgets of the phase start again and the step is armed anew
    static StepAnswer Resume(StepContext c)
    {
        var state = c.State;
        if (!state.Parked) return StepAnswer.Refused("the item is not parked");
        state.Failures.Remove(state.Step.ToString()); state.Launches.Remove(state.Step.ToString()); state.IdleFollowUps = 0;
        c.Decide(WorkItemActions.Resumed, $"was parked: {state.ParkReason}");
        // an item parked inside its settle window is delivered: it goes on with the window, it does not return to its step
        if (state.Status == WorkItemStatus.Settling) { state.Parked = false; state.ParkReason = ""; c.ClearParkTimers(); c.TimerChanged = true; return StepAnswer.Applied; }
        c.Enter(state.Step, "resumed");
        return StepAnswer.Applied;
    }

    // W9.10h: a person moved the card back to the column of an earlier step. The merge happens in the validation column, so an item on its Merge step stands in the Verify column.
    static StepAnswer Moved(StepContext c, CardMoved m)
    {
        var state = c.State;
        if (state.Status != WorkItemStatus.Open || state.Parked) return StepAnswer.Refused("the item is parked or delivered");
        if (m.Step is not { } target) return StepAnswer.Refused($"'{m.Column}' is the column of no step");
        if (state.MergeCommit.Length > 0 || state.Step > StepKind.Merge) return StepAnswer.Refused("the item was merged");
        var standsIn = state.Step == StepKind.Merge ? StepKind.Verify : state.Step;
        if (target >= standsIn) return StepAnswer.Refused("the card is in the column of the step or ahead of it");
        if (!c.Plan.Has(target)) return StepAnswer.Refused($"the {c.Plan.Name} plan has no {target} step");
        c.Decide(WorkItemActions.MovedBack, $"a person moved the card to {m.Column}");
        c.Enter(target, $"a person moved the card back to {m.Column}");
        return StepAnswer.Applied;
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
                if (FailuresSpent(state.Failures[phase]) is { } spent) c.Park($"{spent}: {state.Failures[phase]} failures in {phase}{Why(b.Reason)}");
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
                if (state.IdleFollowUps >= policy.Recovery.MaxFailuresPerPhase) c.Park($"followups-idle: {state.IdleFollowUps} follow-up sessions in a row ended without connecting to the gateway or pushing");
                return StepAnswer.Applied;
            default: return StepAnswer.Refused("unknown budget");
        }
    }

    static string Why(string reason) => reason.Length == 0 ? "" : $" ({reason})";

    // the recovery table's row for a lost session (D5): while the failures counted so far (this one included) are below the budget the item goes on, once they reach it the item is parked with the table's reason
    string? FailuresSpent(int failures)
    {
        var d = RecoveryPolicy.Decide(RecoveryCause.SessionLost, new RecoveryCounters(SessionFailuresInPhase: failures), policy.Recovery);
        return d.Action == RecoveryAction.Park ? d.ParkReason : null;
    }

    StepAnswer LeaseExpired(StepContext c, PhaseLeaseExpired l)
    {
        var state = c.State; var phase = state.Step.ToString();
        // the lease may have been renewed since the timer was scheduled: the renewal has a timer of its own
        if (state.Parked || state.Status != WorkItemStatus.Open || state.LeaseDue != l.Due) return StepAnswer.Stale("the lease is not the one that ran out");
        c.Decide(WorkItemActions.LeaseExpired, $"{phase} had no sign of life by {l.Due:O}");
        state.Failures[phase] = state.FailuresOf(state.Step) + 1;
        if (FailuresSpent(state.Failures[phase]) is { } spent) c.Park($"{spent}: {state.Failures[phase]} failures in {phase} (lease expired)");
        else { c.EndSession(); c.Enter(state.Step, "lease expired"); }   // (the session that gave no sign of life is over: its slot goes back to the scheduler, the step asks again)
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
        return new WorkItemTransition(state, true, new WorkItemResult(decision, c.Decisions, timers, CommandsOf(state)));
    }

    /// <summary>The timers the state asks for: the lease of an open step that has one, the next merge call of the docs plan, the end of the settle window.</summary>
    public static IReadOnlyList<WorkItemTimer> TimersOf(WorkItemState state)
    {
        var item = state.ItemId;
        var timers = new List<WorkItemTimer>();
        if (state.Status == WorkItemStatus.Open && !state.Parked && state.LeaseDue is { } due) timers.Add(new WorkItemTimer(new PhaseLeaseExpired(item, state.Generation, state.ArmId, due), due));
        if (state.Status == WorkItemStatus.Open && !state.Parked && state.MergeRetryAt is { } retry) timers.Add(new WorkItemTimer(new MergeRetryDue(item, state.Generation, state.ArmId, retry), retry));
        if (state.Status == WorkItemStatus.Settling && state.SettleUntil is { } until) timers.Add(new WorkItemTimer(new SettleWindowElapsed(item, state.Generation, state.ArmId), until));
        // W9.10f: the master-green wait runs out, the park ends, the owner of a held PASS loses patience
        if (state.Status == WorkItemStatus.Open && !state.Parked && state.MasterState == "waiting" && state.MasterDue is { } masterDue) timers.Add(new WorkItemTimer(new MasterWatchElapsed(item, state.Generation, masterDue), masterDue));
        if (state.Status != WorkItemStatus.Completed && state.Parked && state.ParkUntil is { } parkUntil) timers.Add(new WorkItemTimer(new ParkExpired(item, state.Generation, parkUntil), parkUntil));
        if (state.Status == WorkItemStatus.Open && state.Parked && !state.OwnerReleased && state.OwnerIdleDue is { } idleDue) timers.Add(new WorkItemTimer(new OwnerIdleElapsed(item, state.Generation, idleDue), idleDue));
        return timers;
    }

    /// <summary>The commands the last applied input asked for (the merge call of the docs plan): sent with its decisions, and again with them when the input is redelivered.</summary>
    public static IReadOnlyList<object> CommandsOf(WorkItemState state)
    {
        var commands = new List<object>();
        var item = state.ItemId;
        // what the item asks of the scheduler and the merge line (W7.3), in the order the steps asked: an end of a session before the request that replaces it
        foreach (var c in state.Outbox)
            commands.Add(c switch
            {
                AskSlot a => new SlotWanted(item, state.Generation, a.ArmId, a.Key, a.Phase, a.Resume, a.ColumnDepth, a.Footprint, a.FromPullRequest, a.At, a.TopRank),
                EndSession e => new Capacity.SessionEnded(item, e.Key, e.SlotId),
                ClearSlot x => new Capacity.ClearItem(item, x.Key, x.Reason),
                StartSession st => new SessionStartRequested(item, state.Generation, st.ArmId, st.Key, st.SlotId, st.Runtime, st.Host, st.Phase),
                ChangeFootprint f => new Capacity.FootprintChanged(item, f.Key, f.Files),
                LeaveTrain l => new Merge.MergeTrainLeft(item.Repository.ToString(), item, l.Reason),
                ReleaseOwner r => new OwnerReleaseRequested(item, r.Key, r.Reason),
                _ => throw new InvalidOperationException($"no message for the command {c.GetType().Name}"),
            });
        if (state.MergeRequestEmitted && state.MergeRequest is { } m && state.Status == WorkItemStatus.Open && !state.Parked)
            commands.Add(new MergeRequested(state.ItemId, state.Generation, state.ArmId, m.Attempt, m.PullRequest, m.Head, m.UpdatesSoFar, m.UpdatedFrom, state.Plan));
        return commands;
    }

    WorkItemTransition Reemit(WorkItemState saved, WorkItemState copy)
    {
        var decision = new WorkItemDecision(WorkItemOutcome.Duplicate, saved.Generation, saved.Plan, saved.Status == WorkItemStatus.Completed ? null : saved.Step.ToString(), saved.ArmId.Length == 0 ? null : saved.ArmId, "published again");
        return new WorkItemTransition(copy, false, new WorkItemResult(decision, saved.Emitted, TimersOf(saved), CommandsOf(saved)));
    }

    static WorkItemTransition Nothing(WorkItemState? state, WorkItemOutcome outcome, string reason)
    {
        var d = state is null ? new WorkItemDecision(outcome, 0, "", null, null, reason)
            : new WorkItemDecision(outcome, state.Generation, state.Plan, state.Status == WorkItemStatus.Completed ? null : state.Step.ToString(), state.ArmId.Length == 0 ? null : state.ArmId, reason);
        return new WorkItemTransition(state, false, WorkItemResult.Quiet(d));
    }
}
