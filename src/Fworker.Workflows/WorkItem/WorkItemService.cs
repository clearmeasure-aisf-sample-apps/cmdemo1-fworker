namespace Fworker.Workflows.WorkItem;

/// <summary>Two writers moved the same item at once more often than the retries allow; the message is retried by Wolverine.</summary>
public sealed class WorkItemConflictException(string item) : Exception($"the work item {item} kept changing under the handler");

/// <summary>
/// The saga in a host: loads the item's state, applies the input (<see cref="WorkItemSaga.Apply"/>), saves it with the revision it was read at and answers. A save that lost to another writer applies the input
/// again to the state that won; the input is never applied twice, because only the winning save counts. Delivery is at least once, so what the saga answers for a repeated input is already in
/// <see cref="WorkItemSaga"/>: the same decisions again for a redelivery, nothing for a repeated key. The messages and timers of the result are published by the caller after this returns.
/// </summary>
public sealed class WorkItemService(IWorkItemStore store, WorkItemSaga saga, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    const int Attempts = 5;

    public IWorkItemStore Store => store;

    public async Task<WorkItemResult> Handle(WorkItemInput input, CancellationToken ct = default)
    {
        var key = input.Item.ToString();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await store.Load(key, ct);
            var t = saga.Apply(rec?.State, input, clock());
            if (!t.Changed) return t.Result;
            if (await store.Save(key, t.State!, rec?.Revision ?? 0, ct)) return t.Result;
        }
        throw new WorkItemConflictException(key);
    }
}
