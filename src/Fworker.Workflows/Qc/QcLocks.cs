using System.Text.Json.Nodes;

namespace Fworker.Workflows.Qc;

/// <summary>
/// Use-ItemLock: one verb at a time per item in this process. v1's lock is a file of one machine, taken for the whole verb; the revision of the state comment is what stops two machines. A verb that cannot get the lock
/// within the time is refused (20 'item-busy', retry in 5 seconds), as the script refuses it.
/// </summary>
public sealed class QcLocks
{
    sealed class Gate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }

    readonly Dictionary<int, Gate> gates = [];

    public async Task<T> Run<T>(int item, TimeSpan wait, Func<Task<T>> body, CancellationToken ct = default)
    {
        Gate g;
        lock (gates) { if (!gates.TryGetValue(item, out g!)) gates[item] = g = new Gate(); g.Users++; }
        var held = false;
        try
        {
            held = await g.Semaphore.WaitAsync(wait, ct);
            if (!held) throw new QcReject(20, "item-busy", $"another operation on item {item} is running", new JsonObject { ["retryAfterSeconds"] = 5 });
            return await body();
        }
        finally
        {
            if (held) g.Semaphore.Release();
            lock (gates) { if (--g.Users == 0) gates.Remove(item); }
        }
    }
}
