using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Faults;
using Microsoft.EntityFrameworkCore;

namespace Fworker.Workflows.Delivery;

/// <summary>A train's state with the revision it was read at; a save passes it back.</summary>
public sealed record TrainRecord(TrainState State, int Revision);

/// <summary>The trains' state. A save succeeds only for the revision that was read: a false answer means another writer was first and the caller runs again on the new state.</summary>
public interface ITrainStore
{
    Task<TrainRecord?> Load(string key, CancellationToken ct);
    Task<bool> Save(string key, TrainState state, int expectedRevision, CancellationToken ct);
}

public sealed class SqlTrainStore(Func<FactoryDbContext> open) : ITrainStore
{
    public async Task<TrainRecord?> Load(string key, CancellationToken ct)
    {
        await using var db = open();
        var row = await db.DeliveryTrains.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return row is null ? null : new TrainRecord(TrainState.FromJson(row.StateJson), row.Revision);
    }

    public async Task<bool> Save(string key, TrainState state, int expectedRevision, CancellationToken ct)
    {
        await using var db = open();
        try
        {
            if (expectedRevision == 0)
            {
                db.DeliveryTrains.Add(new DeliveryTrainRow { Key = key, Revision = 1, StateJson = state.ToJson(), UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                return true;
            }
            var row = await db.DeliveryTrains.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null || row.Revision != expectedRevision) return false;
            row.Revision = expectedRevision + 1; row.StateJson = state.ToJson(); row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception e) when (SqlFaults.IsConflict(e)) { return false; }   // the key was taken by another writer first (insert), or the revision moved (update); a fault of the database is thrown (SqlFaults, W9.10o)
    }
}

public sealed class InMemoryTrainStore : ITrainStore
{
    readonly Dictionary<string, (string Json, int Revision)> rows = [];
    public IReadOnlyDictionary<string, TrainState> States { get { lock (rows) return rows.ToDictionary(kv => kv.Key, kv => TrainState.FromJson(kv.Value.Json)); } }

    public Task<TrainRecord?> Load(string key, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryGetValue(key, out var r) ? new TrainRecord(TrainState.FromJson(r.Json), r.Revision) : null);
    }

    public Task<bool> Save(string key, TrainState state, int expectedRevision, CancellationToken ct)
    {
        lock (rows)
        {
            var have = rows.TryGetValue(key, out var r) ? r.Revision : 0;
            if (have != expectedRevision) return Task.FromResult(false);
            rows[key] = (state.ToJson(), have + 1);
            return Task.FromResult(true);
        }
    }
}

/// <summary>What the same health endpoint answered: the running version of <c>/_build</c> (null: unreadable), the status of the health path and when it was read. <paramref name="Url"/> is null when the environment has no health URL.</summary>
public sealed record EnvProbe(string? Url, string? RunningVersion, string Status, DateTimeOffset At);

/// <summary>
/// The reads the train makes. Reads only: an Octopus or GitHub write is always an integration command. The implementations are the HTTP ones of the gateway (W5.2 wires them in
/// the shadow run); a test passes fakes.
/// </summary>
public interface ITrainReads
{
    /// <summary>GitHub's compare of deployed...release in the deployable's repository; null when GitHub cannot compare them (a 404 or 422).</summary>
    Task<CompareRange?> Compare(RepoId repository, string deployedCommit, string releaseCommit, CancellationToken ct);

    /// <summary>Find-CommitItem for a commit of the range that is no candidate's: the item that commit merged (0: none) and a waiting reason ('' when settled).</summary>
    Task<CommitItemLookup> LookupCommit(RepoId repository, RangeCommit commit, CancellationToken ct);

    /// <summary>The promotion gates of an item that is no candidate (a docs-only item found by its commit): Get-ItemPromotionGapsRead. Null when the item is gone.</summary>
    Task<Fworker.Contracts.Messaging.ItemMergedGates?> ReadGates(ItemId item, CancellationToken ct);

    /// <summary>One read of the environment's app: /_build and the health path (Test-EnvHealth, Read-EnvHealth).</summary>
    Task<EnvProbe> Probe(string deployable, string environment, CancellationToken ct);
}
