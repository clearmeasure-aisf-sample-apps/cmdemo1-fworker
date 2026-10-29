using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Merge;

/// <summary>A train's state with the revision it was read at; a save passes it back.</summary>
public sealed record MergeTrainRecord(MergeTrainState State, int Revision);

/// <summary>The merge trains' state. A save succeeds only for the revision that was read: a false answer means another writer was first and the caller runs again on the new state.</summary>
public interface IMergeTrainStore
{
    Task<MergeTrainRecord?> Load(string key, CancellationToken ct);
    Task<bool> Save(string key, MergeTrainState state, int expectedRevision, CancellationToken ct);
}

public sealed class SqlMergeTrainStore(Func<FactoryDbContext> open) : IMergeTrainStore
{
    public async Task<MergeTrainRecord?> Load(string key, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.MergeTrains.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return row is null ? null : new MergeTrainRecord(MergeTrainState.FromJson(row.StateJson), row.Revision);
    }

    public async Task<bool> Save(string key, MergeTrainState state, int expectedRevision, CancellationToken ct)
    {
        await using var db = open();
        try
        {
            if (expectedRevision == 0)
            {
                db.MergeTrains.Add(new MergeTrainRow { Key = key, Revision = 1, StateJson = state.ToJson(), UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                return true;
            }
            var row = await db.MergeTrains.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null || row.Revision != expectedRevision) return false;
            row.Revision = expectedRevision + 1; row.StateJson = state.ToJson(); row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) { return false; }   // the key was taken by another writer first (insert), or the revision moved (update)
    }
}

public sealed class InMemoryMergeTrainStore : IMergeTrainStore
{
    readonly Dictionary<string, (string Json, int Revision)> rows = [];
    public IReadOnlyDictionary<string, MergeTrainState> States { get { lock (rows) return rows.ToDictionary(kv => kv.Key, kv => MergeTrainState.FromJson(kv.Value.Json)); } }
    /// <summary>Runs before every save (a test that moves the row under the writer).</summary>
    public Action<string>? BeforeSave { get; set; }

    public Task<MergeTrainRecord?> Load(string key, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(key, out var r) ? new MergeTrainRecord(MergeTrainState.FromJson(r.Json), r.Revision) : null);
    }

    public Task<bool> Save(string key, MergeTrainState state, int expectedRevision, CancellationToken ct)
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

/// <summary>Two writers moved the same train at once more often than the retries allow; the message is retried by Wolverine.</summary>
public sealed class MergeTrainConflictException(string key) : Exception($"the merge train {key} kept changing under the handler");

/// <summary>
/// The merge train in a host: loads the repository's state, applies the input (<see cref="MergeTrain.Apply"/>), saves it with the revision it was read at and answers with the messages to publish. A save that lost to
/// another writer applies the input again to the state that won; the input is never applied twice, because only the winning save counts. Nothing is sent until the state is saved; a crash between the save and the
/// publish is healed by the redelivery of the input, which the train answers the same way (a function of the saved state).
/// </summary>
public sealed class MergeTrainService(IMergeTrainStore store, MergeTrainSettings settings, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    const int Attempts = 5;

    public MergeTrainSettings Settings => settings;

    public async Task<IReadOnlyList<object>> Handle(MergeTrainInput input, CancellationToken ct = default)
    {
        var key = input.Key;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await store.Load(key, ct);
            if (rec is null && input is MergeTrainLeft) return [];   // nobody waits in a train that does not exist
            var state = rec?.State ?? MergeTrainState.New(key);
            var before = rec is null ? null : state.ToJson();
            var messages = MergeTrain.Apply(state, input, settings, clock());
            // a state that did not change is not saved (a late input for a line that has no such entry): the answer stands
            if (before is not null && before == state.ToJson()) return messages;
            if (await store.Save(key, state, rec?.Revision ?? 0, ct)) return messages;
        }
        throw new MergeTrainConflictException(key);
    }
}
