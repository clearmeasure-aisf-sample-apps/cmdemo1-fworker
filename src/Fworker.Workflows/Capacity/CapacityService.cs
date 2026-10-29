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
