namespace Fworker.Contracts.Seams;

// W9.10l: the one lock of an item. Before this there were two interfaces of the same name (the item verbs' with a wait in seconds, the read models' without), a file lock for each, and the journal ledger's own in-process
// semaphore, so a v2 verb, a read model, a journal line and a rule script that touched the same item did not all exclude one another (#543 records state-conflict flakiness that came from it). Every port and the
// JournalLedger take this interface; the gateway registers one implementation (gateway/Ports/ItemLocks.cs, the file `item-N.lock` of the state directory that the rule scripts lock too, for as long as scripts exist).

/// <summary>One verb at a time per item (<c>Use-ItemLock</c> of the rule scripts). A lock that stays busy for the wait is <see cref="ItemBusyException"/>, which a port turns into its refusal 20 item-busy.</summary>
public interface IItemLock
{
    /// <summary>The wait of <c>Use-ItemLock</c>.</summary>
    public const int DefaultWaitSeconds = 45;

    /// <summary>Runs <paramref name="body"/> while holding the lock of the item; waits at most <paramref name="waitSeconds"/> for it.</summary>
    Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct);
}

public static class ItemLockExtensions
{
    /// <summary><see cref="IItemLock.Use{T}"/> with the wait of <c>Use-ItemLock</c> (45 seconds).</summary>
    public static Task<T> Use<T>(this IItemLock locks, int item, Func<Task<T>> body, CancellationToken ct) => locks.Use(item, body, IItemLock.DefaultWaitSeconds, ct);
}

/// <summary>The lock of an item was not free within the wait. Thrown before the body ran.</summary>
public sealed class ItemBusyException(int item) : Exception($"another operation on item {item} is running")
{
    public int Item { get; } = item;
}
