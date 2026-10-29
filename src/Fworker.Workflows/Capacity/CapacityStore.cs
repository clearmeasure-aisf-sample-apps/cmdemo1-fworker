using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Capacity;

/// <summary>The ledger with the revision it was read at; a save passes it back.</summary>
public sealed record CapacityRecord(CapacityState State, int Revision);

/// <summary>The scheduler's ledger. A save succeeds only for the revision that was read: a false answer means another writer was first and the caller runs again on the new state.</summary>
public interface ICapacityStore
{
    Task<CapacityRecord?> Load(string key, CancellationToken ct);
    Task<bool> Save(string key, CapacityState state, int expectedRevision, CancellationToken ct);
}

public sealed class SqlCapacityStore(Func<FactoryDbContext> open) : ICapacityStore
{
    public async Task<CapacityRecord?> Load(string key, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.CapacityLedgers.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return row is null ? null : new CapacityRecord(CapacityState.FromJson(row.StateJson), row.Revision);
    }

    public async Task<bool> Save(string key, CapacityState state, int expectedRevision, CancellationToken ct)
    {
        await using var db = open();
        try
        {
            if (expectedRevision == 0)
            {
                db.CapacityLedgers.Add(new CapacityLedgerRow { Key = key, Revision = 1, StateJson = state.ToJson(), UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                return true;
            }
            var row = await db.CapacityLedgers.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null || row.Revision != expectedRevision) return false;
            row.Revision = expectedRevision + 1; row.StateJson = state.ToJson(); row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }
        // the key was taken by another writer first (insert), or the revision moved (update). A database that does not answer is no conflict: it throws, and Wolverine retries the message.
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsDuplicateKey(e)) { return false; }
    }

    // SQL Server: 2627 (primary key violation) and 2601 (unique index): the insert lost to another writer. Any other database error is a fault, not a conflict.
    static bool IsDuplicateKey(DbUpdateException e) => e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 };
}

public sealed class InMemoryCapacityStore : ICapacityStore
{
    readonly Dictionary<string, (string Json, int Revision)> rows = [];
    public CapacityState? State(string key) { lock (rows) return rows.TryGetValue(key, out var r) ? CapacityState.FromJson(r.Json) : null; }
    public int RevisionOf(string key) { lock (rows) return rows.TryGetValue(key, out var r) ? r.Revision : 0; }
    /// <summary>Runs before every save (a test that moves the row under the writer).</summary>
    public Action<string>? BeforeSave { get; set; }

    public Task<CapacityRecord?> Load(string key, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(key, out var r) ? new CapacityRecord(CapacityState.FromJson(r.Json), r.Revision) : null);
    }

    public Task<bool> Save(string key, CapacityState state, int expectedRevision, CancellationToken ct)
    {
        BeforeSave?.Invoke(key);
        lock (rows)
        {
            var have = rows.TryGetValue(key, out var r) ? r.Revision : 0;
            if (have != expectedRevision) return Task.FromResult(false);
            rows[key] = (state.ToJson(), have + 1);
            return Task.FromResult(true);
        }
    }
}
