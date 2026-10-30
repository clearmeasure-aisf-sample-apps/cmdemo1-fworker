namespace Fworker.Workflows.WorkItem;

// W9.10o: the repair of the window between the save of a saga and the publish of what the save asked for. WorkItemService saves the state first and the handler returns the decisions, the commands and the timers
// afterwards (through the outbox of the host), so a process that dies between the two leaves a saved state whose timer or command was never sent. The redelivery of the input heals it only while that input is still the
// item's last one (WorkItemSaga: a repeated key is a Duplicate, and its messages are lost); if another message for the item is handled first, nothing else does. A lost SlotWanted is the worst case: the item has asked for a
// slot and has none, so its lease is not running and it does not even show as lease-overdue.
//
// The alternative, one transaction for the save and the outbox (WolverineFx.EntityFrameworkCore on FactoryDbContext), would put the saga's store inside Wolverine's unit of work and the hand-written service behind
// it; this job does the same repair from outside and also covers a crash of the process that Wolverine's outbox would not (the message that was never produced). It publishes again, for each open item that is not parked:
//  - a timer that is overdue by more than OverdueGrace (the durable timers poll every few seconds, so a timer that late is lost): the timer's message itself, which the saga takes as the timer would have been (a lease that
//    ran out, a merge retry, the end of the settle window, the end of a park, the master-green wait that ran out; a timer that already fired is Stale or a repeated key, and does nothing);
//  - the commands of the item's last transition (the slot request, the end of a session, the merge call), when the item has not changed for SilentFor and the transition asked for any: every one of them is idempotent by
//    its key (the scheduler and the merge line answer a repeat as they answered the first), and the next input of the item empties the list.

/// <summary>An open item with the time its row was last written.</summary>
public sealed record OpenItem(WorkItemState State, DateTimeOffset UpdatedAt);

/// <summary>The open items with their ages: the part of the store the re-arm needs (the store's own interface stays as it is).</summary>
public interface IWorkItemAges
{
    Task<IReadOnlyList<OpenItem>> OpenItems(CancellationToken ct);
}

/// <summary>What one run of the re-arm looked at and sent.</summary>
public sealed record RearmReport(DateTimeOffset At, int Open, int Timers, int Commands, int NoLease, int Failed);

public sealed class WorkItemRearm(IWorkItemAges ages, WorkItemPolicy policy, Func<object, Task<bool>> publish, Func<DateTimeOffset>? clock = null)
{
    /// <summary>A timer this much past its time is taken as lost.</summary>
    public static readonly TimeSpan OverdueGrace = TimeSpan.FromMinutes(2);
    /// <summary>An item unchanged for this long whose last transition asked for commands has them sent again.</summary>
    public static readonly TimeSpan SilentFor = TimeSpan.FromMinutes(10);

    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    volatile RearmReport? last;

    /// <summary>The report of the latest run (null before the first).</summary>
    public RearmReport? Last => last;

    public async Task<RearmReport> Run(CancellationToken ct)
    {
        var now = clock();
        int open = 0, timers = 0, commands = 0, noLease = 0, failed = 0;
        foreach (var item in await ages.OpenItems(ct))
        {
            ct.ThrowIfCancellationRequested();
            var s = item.State;
            if (s.Status == WorkItemStatus.Completed) continue;
            if (s.Parked)
            {
                // W9.10f: a parked item has the timers of its park (its end, the owner's patience): a lost one would leave it parked for ever
                foreach (var t in WorkItemSaga.TimersOf(s).Where(t => t.Due <= now - OverdueGrace))
                    if (await Send(t.Message)) timers++; else failed++;
                continue;
            }
            open++;
            foreach (var t in WorkItemSaga.TimersOf(s).Where(t => t.Due <= now - OverdueGrace))
                if (await Send(t.Message)) timers++; else failed++;
            // an open step that has a lease and neither runs it nor waits for a slot has lost its timer in a way no message repairs (the next input of the item gives it one): it is counted so that the status shows it
            if (s.Status == WorkItemStatus.Open && s.LeaseDue is null && !s.SlotAsked && Steps.For(s.CurrentPlan, s.Step, policy).Lease(policy) is not null) noLease++;
            if ((s.Outbox.Count > 0 || s.MergeRequestEmitted) && item.UpdatedAt <= now - SilentFor)
                foreach (var command in WorkItemSaga.CommandsOf(s))
                    if (await Send(command)) commands++; else failed++;
        }
        return last = new RearmReport(now, open, timers, commands, noLease, failed);
    }

    // a host that is not up takes nothing (false); the next run tries again
    async Task<bool> Send(object message)
    {
        try { return await publish(message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }
}
