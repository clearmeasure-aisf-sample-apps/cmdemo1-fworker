using System.Collections.Concurrent;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.Commands;

/// <summary>
/// <see cref="IItemLock"/> inside one process: the lock a <see cref="JournalLedger"/> takes when it is given none (a test, a tool). The gateway never uses it; its ports share the file lock of gateway/Ports/ItemLocks.cs
/// (W9.10l: one lock for every port, and an architecture test keeps it so).
/// </summary>
public sealed class InProcessItemLock : IItemLock
{
    sealed class Slot { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }
    readonly ConcurrentDictionary<int, Slot> slots = new();

    public async Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct)
    {
        Slot l;
        lock (slots) { l = slots.GetOrAdd(item, _ => new Slot()); l.Users++; }
        var entered = false;
        try
        {
            entered = await l.Gate.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct);
            if (!entered) throw new ItemBusyException(item);
            return await body();
        }
        finally
        {
            if (entered) l.Gate.Release();
            lock (slots) { if (--l.Users == 0) slots.TryRemove(item, out _); }
        }
    }
}
