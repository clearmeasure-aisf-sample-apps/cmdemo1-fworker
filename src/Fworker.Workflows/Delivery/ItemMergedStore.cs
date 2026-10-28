using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Delivery;

/// <summary>The merged-items table of the delivery train (W5.0): the claim that makes the webhook's and the reconciler's derivations one.</summary>
public interface IMergedItemStore
{
    /// <summary>True when a message other than <paramref name="messageId"/> already recorded a row for the commit: the derivation skips the reads.</summary>
    Task<bool> CommitTakenByOther(string commit, Guid messageId, CancellationToken ct);

    /// <summary>True when the row is new or was recorded by the same message (a redelivery goes on); false when another message holds the (item, commit).</summary>
    Task<bool> TryRecord(MergedItem row, CancellationToken ct);
}

public sealed class SqlMergedItemStore(Func<FactoryDbContext> open) : IMergedItemStore
{
    public async Task<bool> CommitTakenByOther(string commit, Guid messageId, CancellationToken ct)
    {
        await using var db = open();
        return await db.MergedItems.AnyAsync(x => x.Commit == commit && x.MessageId != messageId, ct);
    }

    public async Task<bool> TryRecord(MergedItem row, CancellationToken ct)
    {
        await using var db = open();
        if (await db.MergedItems.FindAsync([row.ItemKey, row.Commit], ct) is { } held) return held.MessageId == row.MessageId;
        db.MergedItems.Add(row);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException)
        {
            // lost the race to another message (the primary key): whoever holds the row decides
            await using var again = open();
            return await again.MergedItems.AnyAsync(x => x.ItemKey == row.ItemKey && x.Commit == row.Commit && x.MessageId == row.MessageId, ct);
        }
    }
}

public sealed class InMemoryMergedItemStore : IMergedItemStore
{
    readonly Dictionary<(string, string), MergedItem> rows = [];
    public IReadOnlyCollection<MergedItem> Rows { get { lock (rows) return rows.Values.ToList(); } }

    public Task<bool> CommitTakenByOther(string commit, Guid messageId, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.Values.Any(x => x.Commit == commit && x.MessageId != messageId));
    }

    public Task<bool> TryRecord(MergedItem row, CancellationToken ct)
    {
        lock (rows)
        {
            if (rows.TryGetValue((row.ItemKey, row.Commit), out var held)) return Task.FromResult(held.MessageId == row.MessageId);
            rows[(row.ItemKey, row.Commit)] = row;
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// The delivered-to table (system-gateway.md H5): the release an item reached in an environment, kept from <see cref="DeliveredTo"/> and <see cref="Undelivered"/>. It answers
/// the delivery train's dependencies-delivered(env) slot; an event only triggers a re-check. A later delivery of the item to the same environment replaces the release.
/// </summary>
public sealed class DeliveredToTable(Func<FactoryDbContext> open) : IDeliveredLookup
{
    public async Task Record(DeliveredTo e, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = open();
        var key = e.Item.ToString(); var env = e.Environment.ToLowerInvariant();
        if (await db.DeliveredTo.FindAsync([key, env], ct) is { } row) { row.Release = e.Release; row.DeliveredAt = at; }
        else db.DeliveredTo.Add(new DeliveredToEntry { ItemKey = key, Environment = env, Release = e.Release, DeliveredAt = at });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // the same row written by another node at once: update it
            await using var again = open();
            var row2 = await again.DeliveredTo.FindAsync([key, env], ct);
            if (row2 is null) throw;
            row2.Release = e.Release; row2.DeliveredAt = at;
            await again.SaveChangesAsync(ct);
        }
    }

    public async Task Remove(Undelivered e, CancellationToken ct = default)
    {
        await using var db = open();
        await db.DeliveredTo.Where(x => x.ItemKey == e.Item.ToString() && x.Environment == e.Environment.ToLowerInvariant()).ExecuteDeleteAsync(ct);
    }

    /// <summary>The release the item reached in the environment; null when it has not (or no longer).</summary>
    public async Task<string?> ReleaseOf(ItemId item, string environment, CancellationToken ct = default)
    {
        await using var db = open();
        var key = item.ToString(); var env = environment.ToLowerInvariant();
        return await db.DeliveredTo.Where(x => x.ItemKey == key && x.Environment == env).Select(x => x.Release).FirstOrDefaultAsync(ct);
    }

    /// <summary>The dependencies not yet delivered to the environment: empty when the item's dependencies-delivered(env) evidence holds. A dependency that settles without a release is the caller's to leave out.</summary>
    public async Task<IReadOnlyList<ItemId>> NotYetDelivered(IEnumerable<ItemId> dependencies, string environment, CancellationToken ct = default)
    {
        var deps = dependencies.ToList(); var keys = deps.Select(d => d.ToString()).ToList(); var env = environment.ToLowerInvariant();
        await using var db = open();
        var have = (await db.DeliveredTo.Where(x => x.Environment == env && keys.Contains(x.ItemKey)).Select(x => x.ItemKey).ToListAsync(ct)).ToHashSet();
        return deps.Where(d => !have.Contains(d.ToString())).ToList();
    }
}
