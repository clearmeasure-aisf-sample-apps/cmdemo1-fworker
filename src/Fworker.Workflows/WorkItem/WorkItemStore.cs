using Fworker.Contracts.Ids;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.WorkItem;

/// <summary>An item's state with the revision it was read at; a save passes it back (the saga's <c>IRevisioned</c>).</summary>
public sealed record WorkItemRecord(WorkItemState State, int Revision);

/// <summary>
/// The work items' state. A save succeeds only for the revision that was read: a false answer means another writer was first and the caller runs again on the new state. The open-instance index
/// (<see cref="OpenInstance"/>, the item to its current generation) is written in the same transaction as the state.
/// </summary>
public interface IWorkItemStore
{
    Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct);
    Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct);

    /// <summary>The items with a saga that has not completed (open or inside the settle window), as "owner/repo#N": the items the v2 engine owns.</summary>
    Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct);

    /// <summary>The items in flight by repository ("owner/repo"): the open ones, not those inside their settle window. The WIP intake rule counts with it.</summary>
    Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct);
}

public sealed class SqlWorkItemStore(Func<FactoryDbContext> open) : IWorkItemStore
{
    public async Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(x => x.ItemKey == itemKey, ct);
        return row is null ? null : new WorkItemRecord(WorkItemState.FromJson(row.StateJson), row.Revision);
    }

    public async Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct)
    {
        await using var db = open();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var repository = ItemId.Parse(itemKey).Repository.ToString();
            var instance = await db.OpenInstances.FirstOrDefaultAsync(x => x.ItemKey == itemKey, ct);
            if (expectedRevision == 0)
            {
                db.WorkItems.Add(Fill(new WorkItemRow { ItemKey = itemKey, Repository = repository, Revision = 1 }, state, now));
            }
            else
            {
                var row = await db.WorkItems.FirstOrDefaultAsync(x => x.ItemKey == itemKey, ct);
                if (row is null || row.Revision != expectedRevision) return false;
                row.Revision = expectedRevision + 1;
                Fill(row, state, now);
            }
            // the index of open instances, in the same transaction: a new generation opens it, the end of the instance closes it
            if (instance is null) db.OpenInstances.Add(new OpenInstance { ItemKey = itemKey, Generation = state.Generation, OpenedAt = state.StartedAt, ClosedAt = state.CompletedAt });
            else { if (instance.Generation != state.Generation) { instance.Generation = state.Generation; instance.OpenedAt = state.StartedAt; } instance.ClosedAt = state.CompletedAt; }
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) { return false; }   // the key was taken by another writer first (insert), or the revision moved (update)
    }

    static WorkItemRow Fill(WorkItemRow row, WorkItemState state, DateTimeOffset now)
    {
        row.Generation = state.Generation; row.Status = state.Status; row.Plan = state.Plan; row.Step = state.Step.ToString();
        row.StateJson = state.ToJson(); row.UpdatedAt = now;
        return row;
    }

    public async Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct)
    {
        await using var db = open();
        return await db.WorkItems.AsNoTracking().Where(x => x.Status != WorkItemStatus.Completed).Select(x => x.ItemKey).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct)
    {
        await using var db = open();
        return await db.WorkItems.AsNoTracking().Where(x => x.Status == WorkItemStatus.Open).GroupBy(x => x.Repository).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
    }
}

public sealed class InMemoryWorkItemStore : IWorkItemStore
{
    readonly Dictionary<string, (string Json, int Revision)> rows = [];
    public IReadOnlyDictionary<string, WorkItemState> States { get { lock (rows) return rows.ToDictionary(kv => kv.Key, kv => WorkItemState.FromJson(kv.Value.Json)); } }
    /// <summary>The revision of an item's row (0: none).</summary>
    public int RevisionOf(string itemKey) { lock (rows) return rows.TryGetValue(itemKey, out var r) ? r.Revision : 0; }
    /// <summary>Runs before every save (a test that moves the row under the writer).</summary>
    public Action<string>? BeforeSave { get; set; }

    public Task<WorkItemRecord?> Load(string itemKey, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(itemKey, out var r) ? new WorkItemRecord(WorkItemState.FromJson(r.Json), r.Revision) : null);
    }

    public Task<bool> Save(string itemKey, WorkItemState state, int expectedRevision, CancellationToken ct)
    {
        BeforeSave?.Invoke(itemKey);
        lock (rows)
        {
            var have = rows.TryGetValue(itemKey, out var r) ? r.Revision : 0;
            if (have != expectedRevision) return Task.FromResult(false);
            rows[itemKey] = (state.ToJson(), have + 1);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<string>> OwnedItems(CancellationToken ct)
    {
        lock (rows) return Task.FromResult<IReadOnlyList<string>>(rows.Where(kv => WorkItemState.FromJson(kv.Value.Json).IsOpen).Select(kv => kv.Key).ToList());
    }

    public Task<IReadOnlyDictionary<string, int>> InFlightByRepository(CancellationToken ct)
    {
        lock (rows)
            return Task.FromResult<IReadOnlyDictionary<string, int>>(rows.Select(kv => WorkItemState.FromJson(kv.Value.Json)).Where(s => s.Status == WorkItemStatus.Open)
                .GroupBy(s => ItemId.Parse(s.Item).Repository.ToString()).ToDictionary(g => g.Key, g => g.Count()));
    }
}
