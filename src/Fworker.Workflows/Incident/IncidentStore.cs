using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Incident;

/// <summary>An incident's state with the revision it was read at; a save passes it back.</summary>
public sealed record IncidentRecord(IncidentState State, int Revision);

/// <summary>
/// The incidents' state, one row per (deployable, environment). A save succeeds only for the revision that was read: a false answer means another writer was first and the caller runs again on the new state.
/// The row is also the open-instance index: <see cref="OpenOfHotfix"/> finds the incident an item is the hotfix of.
/// </summary>
public interface IIncidentStore
{
    Task<IncidentRecord?> Load(string key, CancellationToken ct);
    Task<bool> Save(string key, IncidentState state, int expectedRevision, CancellationToken ct);

    /// <summary>The keys of the open incidents whose hotfix is <paramref name="item"/> ("owner/repo#N", lower case).</summary>
    Task<IReadOnlyList<string>> OpenOfHotfix(string item, CancellationToken ct);

    /// <summary>The state of every incident that is not quiet.</summary>
    Task<IReadOnlyList<IncidentState>> Open(CancellationToken ct);
}

public sealed class SqlIncidentStore(Func<FactoryDbContext> open) : IIncidentStore
{
    public async Task<IncidentRecord?> Load(string key, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return row is null ? null : new IncidentRecord(IncidentState.FromJson(row.StateJson), row.Revision);
    }

    public async Task<bool> Save(string key, IncidentState state, int expectedRevision, CancellationToken ct)
    {
        await using var db = open();
        try
        {
            if (expectedRevision == 0)
            {
                db.Incidents.Add(Fill(new IncidentRow { Key = key, Revision = 1 }, state));
                await db.SaveChangesAsync(ct);
                return true;
            }
            var row = await db.Incidents.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null || row.Revision != expectedRevision) return false;
            row.Revision = expectedRevision + 1;
            Fill(row, state);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) { return false; }   // the key was taken by another writer first (insert), or the revision moved (update)
    }

    static IncidentRow Fill(IncidentRow row, IncidentState state)
    {
        row.Generation = state.Generation; row.Status = state.Status; row.HotfixItem = state.HotfixColumn;
        row.StateJson = state.ToJson(); row.UpdatedAt = DateTimeOffset.UtcNow;
        return row;
    }

    public async Task<IReadOnlyList<string>> OpenOfHotfix(string item, CancellationToken ct)
    {
        await using var db = open();
        return await db.Incidents.AsNoTracking().Where(x => x.HotfixItem == item && x.Status != IncidentStatus.Quiet).Select(x => x.Key).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<IncidentState>> Open(CancellationToken ct)
    {
        await using var db = open();
        var rows = await db.Incidents.AsNoTracking().Where(x => x.Status != IncidentStatus.Quiet).Select(x => x.StateJson).ToListAsync(ct);
        return rows.Select(IncidentState.FromJson).ToList();
    }
}

public sealed class InMemoryIncidentStore : IIncidentStore
{
    readonly Dictionary<string, (string Json, int Revision)> rows = [];
    public IReadOnlyDictionary<string, IncidentState> States { get { lock (rows) return rows.ToDictionary(kv => kv.Key, kv => IncidentState.FromJson(kv.Value.Json)); } }
    /// <summary>Runs before every save (a test that moves the row under the writer).</summary>
    public Action<string>? BeforeSave { get; set; }

    public Task<IncidentRecord?> Load(string key, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(key, out var r) ? new IncidentRecord(IncidentState.FromJson(r.Json), r.Revision) : null);
    }

    public Task<bool> Save(string key, IncidentState state, int expectedRevision, CancellationToken ct)
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

    public Task<IReadOnlyList<string>> OpenOfHotfix(string item, CancellationToken ct)
    {
        lock (rows) return Task.FromResult<IReadOnlyList<string>>(rows.Where(kv => IncidentState.FromJson(kv.Value.Json) is { IsOpen: true } s && s.HotfixItem == item).Select(kv => kv.Key).ToList());
    }

    public Task<IReadOnlyList<IncidentState>> Open(CancellationToken ct)
    {
        lock (rows) return Task.FromResult<IReadOnlyList<IncidentState>>(rows.Values.Select(r => IncidentState.FromJson(r.Json)).Where(s => s.IsOpen).ToList());
    }
}

/// <summary>Two writers moved the same incident at once more often than the retries allow; the message is retried by Wolverine.</summary>
public sealed class IncidentConflictException(string key) : Exception($"the incident {key} kept changing under the handler");

/// <summary>
/// The incident saga in a host: loads the pair's state, applies the input (<see cref="IncidentSaga.Apply"/>), saves it with the revision it was read at and answers. A save that lost to another writer applies the
/// input again to the state that won; the input is never applied twice, because only the winning save counts. Delivery is at least once, so what the saga answers for a repeated input is already in
/// <see cref="IncidentSaga"/>: the same decisions, messages and timer again for a redelivery, nothing for a repeated key. What the result holds is published by the caller after this returns.
/// </summary>
public sealed class IncidentService(IIncidentStore store, IncidentSaga saga, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    const int Attempts = 5;

    public IIncidentStore Store => store;
    public IncidentSettings Settings => saga.Settings;

    public async Task<IncidentResult> Handle(IncidentInput input, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await store.Load(input.Key, ct);
            var t = saga.Apply(rec?.State, input, clock());
            if (!t.Changed) return t.Result;
            if (await store.Save(input.Key, t.State!, rec?.Revision ?? 0, ct)) return t.Result;
        }
        throw new IncidentConflictException(input.Key);
    }
}
