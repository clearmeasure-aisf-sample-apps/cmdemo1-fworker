using Fworker.Contracts.Ids;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.WorkItem;

// W9.10h (docs/design/fworker-wolverine-implementation.md W9.10h): the producers of the saga's inputs that are the gateway's own paths, not a verb's or a webhook's. Where the engine acts an item with a saga has its
// park, its budgets and the start of its session decided by the saga and the scheduler; the paths below used to change v1's state only (the rule script's park, the runtime manager's counters), so the saga went on
// offering an item v1 had parked, never took an unparked one up again, and counted none of the failures of its sessions. Each path now tells the saga as well, after it did its own work:
//  - the park of the runtime adapters after a failed launch (ParkWorkItem "runtime-failed") and of the Copilot credit guard ("credit-guard");
//  - the operator's unpark through the admin endpoint (ResumeWorkItem), which makes V2Ownership offer the item again at the next read of the saga's table;
//  - the budgets of the runtime manager's sessions (BudgetSpent: a launch, a failure, an idle follow-up, a session that connected or pushed), under the arm of the step the item is on;
//  - a start the host or the runtime refused (the scheduler's StartRefused: "host-full" is a wait, any other outcome frees the slot and drops the request).
// An item without a saga costs one look at the ownership. A message the host does not take is tried again twice and then dropped with a log line: the saga's lease runs out and its budget parks the item, the way a lost
// session ends. Nothing here throws into the caller.

/// <summary>
/// The business keys of the park and unpark inputs. The rule verb's answer names the park it made or lifted (<c>park.at</c>), and ItemAdminFeed (the verb route) keys its inputs by it: the same key from here makes a park
/// that both paths tell the saga of one input. An answer that names none (a script of an older shape) falls back to the moment of the call, which is unique to this call.
/// </summary>
static class SagaKeys
{
    public static string ParkKey(string verbOutput, string code, string phase) => $"park|{At(verbOutput) ?? $"{code}|{phase}|{DateTime.UtcNow.Ticks}"}";
    public static string ResumeKey(string verbOutput) => $"resume|{At(verbOutput) ?? DateTime.UtcNow.Ticks.ToString()}";

    static string? At(string output)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(output)?["park"]?["at"]?.ToString() is { Length: > 0 } at ? at : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>The saga inputs the gateway's own paths produce. Only built where the engine acts (WorkItemParts exists).</summary>
sealed class SagaInputs(V2Ownership ownership, IWorkItemStore store, RepoId home, Func<WorkItemInput, Task<bool>> publish, Func<StartRefused, Task<bool>> publishRefusal, Func<TimeSpan, Task>? delay = null)
{
    static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
    readonly Func<TimeSpan, Task> wait = delay ?? Task.Delay;

    public Task<bool> Owns(int item) => ownership.OwnsOrFindAsync(item);

    /// <summary>The item was parked outside the saga (the runtime adapters, the credit guard). A repeated park of the same moment is the saga's duplicate.</summary>
    public Task<bool> Parked(int item, string reason, string key) =>
        Send(item, "park", () => new ParkWorkItem(new ItemId(home, item), 0, key, reason.Length == 0 ? "parked" : reason));

    /// <summary>The item was unparked outside the saga (the admin endpoint). The key names the park that was lifted, so a second unpark of a later park is not mistaken for the first.</summary>
    public Task<bool> Resumed(int item, string key) =>
        Send(item, "resume", () => new ResumeWorkItem(new ItemId(home, item), 0, key));

    /// <summary>
    /// The operator unparks an item whose park only the saga holds (its budget parked it: after a restart the runtime manager's counters start at 0 while the saga's persist, and a fix-forward round clears the manager's launches but
    /// not the saga's), so the script found no park in v1's state comment. True when the saga was parked and the resume was taken; false for an item the saga does not own, one that is not parked, or a host that is down.
    /// The key is of the moment of the call: there is no v1 park to name.
    /// </summary>
    public async Task<bool> ResumeIfParked(int item, string? key = null)
    {
        if (!await ownership.OwnsOrFindAsync(item)) return false;
        var id = new ItemId(home, item);
        try
        {
            if ((await store.Load(id.ToString(), CancellationToken.None))?.State is not { IsOpen: true, Parked: true }) return false;
            return await Deliver(id, "resume", new ResumeWorkItem(id, 0, key ?? $"resume|admin|{DateTime.UtcNow.Ticks}"));
        }
        catch (Exception e) { Console.Error.WriteLine($"v2 resume feed for {id}: {e.GetType().Name}"); return false; }
    }

    /// <summary>One count against the step's budgets, under the arm of the step the item is on now (a budget message for another arm would be stale). Nothing for an item whose saga is not open.</summary>
    public async Task<bool> Budget(int item, BudgetKind kind, string key, string reason)
    {
        if (!await ownership.OwnsOrFindAsync(item)) return false;
        var id = new ItemId(home, item);
        try
        {
            if ((await store.Load(id.ToString(), CancellationToken.None))?.State is not { IsOpen: true } state) return false;
            return await Deliver(id, "budget", new BudgetSpent(id, state.Generation, state.ArmId, key, kind, reason));
        }
        catch (Exception e) { Console.Error.WriteLine($"v2 budget feed for {id}: {e.GetType().Name}"); return false; }
    }

    /// <summary>The start of the item's session was refused. For an item the scheduler holds nothing of, the scheduler answers NotFound and nothing more.</summary>
    public async Task<bool> StartRefused(int item, string key, string outcome)
    {
        if (!await ownership.OwnsOrFindAsync(item)) return false;
        var id = new ItemId(home, item);
        try { return await Deliver(id, "start-refused", () => publishRefusal(new StartRefused(id, key, "", outcome))); }
        catch (Exception e) { Console.Error.WriteLine($"v2 start-refused feed for {id}: {e.GetType().Name}"); return false; }
    }

    async Task<bool> Send(int item, string what, Func<WorkItemInput> make)
    {
        if (!await ownership.OwnsOrFindAsync(item)) return false;
        var id = new ItemId(home, item);
        try { return await Deliver(id, what, make()); }
        catch (Exception e) { Console.Error.WriteLine($"v2 {what} feed for {id}: {e.GetType().Name}"); return false; }
    }

    Task<bool> Deliver(ItemId id, string what, WorkItemInput message) => Deliver(id, what, () => publish(message));

    async Task<bool> Deliver(ItemId id, string what, Func<Task<bool>> send)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { if (await send()) return true; }
            catch (Exception e) when (attempt < Pauses.Length) { Console.Error.WriteLine($"v2 {what} feed for {id}: {e.GetType().Name}"); }
            if (attempt >= Pauses.Length) { Console.Error.WriteLine($"v2 {what} feed for {id} dropped: the Wolverine host did not take it"); return false; }
            await wait(Pauses[attempt]);
        }
    }
}
