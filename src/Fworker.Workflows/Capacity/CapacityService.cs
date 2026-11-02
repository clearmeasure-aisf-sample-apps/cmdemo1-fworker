namespace Fworker.Workflows.Capacity;

/// <summary>Two writers moved the ledger at once more often than the retries allow; the message is retried by Wolverine.</summary>
public sealed class CapacityConflictException() : Exception("the capacity ledger kept changing under the handler");

/// <summary>
/// The scheduler in a host: loads the ledger, applies the input (<see cref="CapacityScheduler.Apply"/>), saves it with the revision it was read at and answers. A save that lost to another writer applies the input
/// again to the state that won; the input is never applied twice, because only the winning save counts. A database that does not answer throws, and the message is retried: a transient fault is never turned
/// into an answer. The events and the timer of the result are published by the caller after this returns.
/// </summary>
public sealed class CapacityService(ICapacityStore store, CapacityScheduler scheduler, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    const int Attempts = 5;

    public ICapacityStore Store => store;
    public CapacityScheduler Scheduler => scheduler;

    /// <summary>V1.2: the free capacity of a runtime for the intake, from the ledger as saved (<see cref="CapacityScheduler.FreeSlots"/>). A ledger that cannot be read throws; the caller counts none.</summary>
    public async Task<int> FreeSlots(string runtime, CancellationToken ct = default) => scheduler.FreeSlots((await store.Load(CapacityKeys.Factory, ct))?.State, runtime, clock());

    public async Task<CapacityResult> Handle(CapacityInput input, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await store.Load(CapacityKeys.Factory, ct);
            var t = scheduler.Apply(rec?.State, input, clock());
            if (!t.Changed) return t.Result;
            if (await store.Save(CapacityKeys.Factory, t.State!, rec?.Revision ?? 0, ct)) return t.Result;
        }
        throw new CapacityConflictException();
    }
}
