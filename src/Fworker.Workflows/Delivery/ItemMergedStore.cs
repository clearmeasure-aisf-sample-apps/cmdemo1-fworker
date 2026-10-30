using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Persistence;
using Fworker.Workflows.Faults;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Delivery;

/// <summary>The merged-items table of the delivery train (W5.0): the claim that makes the webhook's and the reconciler's derivations one. The claim is per item and merge commit, never per commit alone.</summary>
public interface IMergedItemStore
{
    /// <summary>True when a message other than <paramref name="messageId"/> already recorded the row of (item, commit): this item needs no read. The other items of the commit are not affected.</summary>
    Task<bool> TakenByOther(string itemKey, string commit, Guid messageId, CancellationToken ct);

    /// <summary>
    /// True when a message other than <paramref name="messageId"/> recorded a row of the item for the pull request: for an event that names the item and the pull request but not the merge commit
    /// (the pr.merged webhook), so that it is answered before any read of GitHub. A pull request merges once, so the pair stands for (item, commit).
    /// </summary>
    Task<bool> PullRequestTakenByOther(string itemKey, int pullRequest, Guid messageId, CancellationToken ct);

    /// <summary>
    /// Records the rows of one derivation together and returns those that are this message's: new, or recorded by the same message before (a redelivery goes on). A row another message holds is left
    /// out. The rows are written in one transaction, so a derivation that fails leaves none of its items claimed for a message that may never come back.
    /// </summary>
    Task<IReadOnlyList<MergedItem>> TryRecordAll(IReadOnlyList<MergedItem> rows, CancellationToken ct);
}

public sealed class SqlMergedItemStore(Func<FactoryDbContext> open) : IMergedItemStore
{
    public async Task<bool> TakenByOther(string itemKey, string commit, Guid messageId, CancellationToken ct)
    {
        await using var db = open();
        return await db.MergedItems.AnyAsync(x => x.ItemKey == itemKey && x.Commit == commit && x.MessageId != messageId, ct);
    }

    public async Task<bool> PullRequestTakenByOther(string itemKey, int pullRequest, Guid messageId, CancellationToken ct)
    {
        await using var db = open();
        return await db.MergedItems.AnyAsync(x => x.ItemKey == itemKey && x.PullRequest == pullRequest && x.MessageId != messageId, ct);
    }

    public async Task<IReadOnlyList<MergedItem>> TryRecordAll(IReadOnlyList<MergedItem> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var mine = new HashSet<(string, string)>();
        await using (var db = open())
        {
            var added = new List<MergedItem>();
            foreach (var row in rows)
            {
                if (await db.MergedItems.FindAsync([row.ItemKey, row.Commit], ct) is { } held) { if (held.MessageId == row.MessageId) mine.Add((row.ItemKey, row.Commit)); continue; }
                db.MergedItems.Add(row); added.Add(row);
            }
            try
            {
                await db.SaveChangesAsync(ct);
                foreach (var row in added) mine.Add((row.ItemKey, row.Commit));
                return rows.Where(r => mine.Contains((r.ItemKey, r.Commit))).ToList();
            }
            catch (Exception e) when (SqlFaults.IsConflict(e)) { }   // (a fault of the database is thrown: the message is retried, SqlFaults)
        }
        // another message recorded one of the rows between the look and the save (the primary key): one by one, and whoever holds each row decides
        var kept = new List<MergedItem>();
        foreach (var row in rows) if (await TryRecord(row, ct)) kept.Add(row);
        return kept;
    }

    async Task<bool> TryRecord(MergedItem row, CancellationToken ct)
    {
        await using var db = open();
        if (await db.MergedItems.FindAsync([row.ItemKey, row.Commit], ct) is { } held) return held.MessageId == row.MessageId;
        db.MergedItems.Add(row);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (Exception ex) when (SqlFaults.IsConflict(ex))
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

    /// <summary>A test control: called before each batch is recorded; it may throw to stand for a database that fails.</summary>
    public Action<IReadOnlyList<MergedItem>>? BeforeRecord { get; set; }

    public Task<bool> TakenByOther(string itemKey, string commit, Guid messageId, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue((itemKey, commit), out var held) && held.MessageId != messageId);
    }

    public Task<bool> PullRequestTakenByOther(string itemKey, int pullRequest, Guid messageId, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.Values.Any(x => x.ItemKey == itemKey && x.PullRequest == pullRequest && x.MessageId != messageId));
    }

    public Task<IReadOnlyList<MergedItem>> TryRecordAll(IReadOnlyList<MergedItem> batch, CancellationToken ct)
    {
        BeforeRecord?.Invoke(batch);
        lock (rows)
        {
            var kept = new List<MergedItem>();
            foreach (var row in batch)
            {
                if (rows.TryGetValue((row.ItemKey, row.Commit), out var held)) { if (held.MessageId == row.MessageId) kept.Add(row); continue; }
                rows[(row.ItemKey, row.Commit)] = row; kept.Add(row);
            }
            return Task.FromResult<IReadOnlyList<MergedItem>>(kept);
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
        catch (Exception ex) when (SqlFaults.IsConflict(ex))
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

    /// <summary>The items no merge was recorded for, in any commit (W7.7: a dependency that is merged settles without a release where its repository has nothing to deliver).</summary>
    public async Task<IReadOnlyList<ItemId>> NotYetMerged(IEnumerable<ItemId> items, CancellationToken ct = default)
    {
        var list = items.ToList(); var keys = list.Select(d => d.ToString()).ToList();
        await using var db = open();
        var have = (await db.MergedItems.Where(x => keys.Contains(x.ItemKey)).Select(x => x.ItemKey).Distinct().ToListAsync(ct)).ToHashSet();
        return list.Where(d => !have.Contains(d.ToString())).ToList();
    }
}
