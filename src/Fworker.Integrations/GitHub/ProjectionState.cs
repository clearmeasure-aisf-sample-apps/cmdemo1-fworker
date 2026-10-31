using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.3: the state comment as a versioned projection. It is written in the format v1's Get-State reads (the marker line, the json fence, the revision counter, the 48,000
/// character budget): v2 uses the same <see cref="IJournalStore"/> as the journal, whose Read and Save are Get-State and Save-State, so the comment is the first one of the
/// gateway's login that carries the marker, created when there is none, updated otherwise, refused when its revision moved since the read. A revert or FACTORY_V2_OFF hands
/// every open item back to v1 with its state intact.
/// </summary>
public sealed class StateCommentProjection(IJournalStore store, IProjectionVersions versions, ProjectionEcho echo, Func<DateTimeOffset>? now = null, IItemLock? locks = null)
{
    /// <summary>How long a write waits for the item's lock (W9.10i): a verb of the item holds it for as long as the verb runs.</summary>
    public const int LockWaitSeconds = 30;

    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);

    // W9.10i: the read, the edit and the save are one step under the item's lock when the gateway gives one, the lock the rule scripts and the verbs take too, so that the projection and a verb of the item do not write the comment over
    // each other (the revision check of Save-State is a read and then a write). Without one (the tests) there is none.
    Task<T> Locked<T>(ItemId item, Func<Task<T>> body, CancellationToken ct) => locks is null ? body() : locks.Use(item.Number, body, LockWaitSeconds, ct);

    /// <summary>
    /// Update-State: reads the state, applies <paramref name="mutate"/>, saves; on a conflict (the comment's revision moved) it reads again and applies again, up to
    /// <paramref name="tries"/> times, then the <see cref="StateConflictException"/> is thrown. <paramref name="mutate"/> runs on a fresh read each time and must be repeatable.
    /// A version that is not newer than the applied one is skipped (<see cref="ProjectionOutcome.Stale"/>), before anything is read. A mutation that changes nothing writes nothing
    /// (<see cref="ProjectionOutcome.Unchanged"/>, W9.10i: the revision of a comment v1 reads moves only when its content does, so v1's own verbs do not collide with a write that says nothing new).
    /// </summary>
    public async Task<ProjectionOutcome> Apply(ItemId item, long version, Action<JsonObject> mutate, CancellationToken ct = default, int tries = 3)
    {
        if (version <= await versions.Applied(item.ToString(), ProjectionTargets.Comment, ct)) return ProjectionOutcome.Stale;
        return await Locked(item, () => ApplyUnder(item, version, mutate, ct, tries), ct);
    }

    async Task<ProjectionOutcome> ApplyUnder(ItemId item, long version, Action<JsonObject> mutate, CancellationToken ct, int tries)
    {
        var key = item.ToString();
        if (version <= await versions.Applied(key, ProjectionTargets.Comment, ct)) return ProjectionOutcome.Stale;   // (another writer of this version had the lock first)
        for (var i = 1; ; i++)
        {
            var holder = await store.Read(item.Number, ct);
            var before = holder.State.ToJsonString();
            mutate(holder.State);
            if (holder.State.ToJsonString() == before)
            {
                await versions.Advance(key, ProjectionTargets.Comment, version, now(), ct);
                return ProjectionOutcome.Unchanged;
            }
            // the echo is recorded BEFORE the save: the comment's webhook can arrive before Save returns. The revision Save writes is known beforehand (Save-State: the revision
            // read, or the state's own when it was not read, plus one). A save that fails takes the record back.
            var mark = echo.Record(key, ProjectionTargets.Comment, version, ProjectionEcho.RevisionFingerprint((holder.Rev ?? ItemStateComment.RevOf(holder.State)) + 1));
            try { await store.Save(item.Number, holder, ct); }
            catch (StateConflictException) when (i < tries) { echo.Unrecord(mark); continue; }
            catch { echo.Unrecord(mark); throw; }
            await versions.Advance(key, ProjectionTargets.Comment, version, now(), ct);
            return ProjectionOutcome.Applied;
        }
    }

    /// <summary>
    /// W9.10i: a change to the state comment that is not a decision of the saga and has no version of its own (the promotion records of the delivery train): the same read, mutation and save with the same retry on a
    /// conflict, and nothing written when the mutation changes nothing. The mutation must be repeatable and idempotent, since nothing here keeps a repeat out. No echo is recorded: no consumer takes a state comment
    /// event for news.
    /// </summary>
    public Task<ProjectionOutcome> Update(ItemId item, Action<JsonObject> mutate, CancellationToken ct = default, int tries = 3) =>
        Locked(item, () => UpdateUnder(item, mutate, ct, tries), ct);

    async Task<ProjectionOutcome> UpdateUnder(ItemId item, Action<JsonObject> mutate, CancellationToken ct, int tries)
    {
        for (var i = 1; ; i++)
        {
            var holder = await store.Read(item.Number, ct);
            var before = holder.State.ToJsonString();
            mutate(holder.State);
            if (holder.State.ToJsonString() == before) return ProjectionOutcome.Unchanged;
            try { await store.Save(item.Number, holder, ct); }
            catch (StateConflictException) when (i < tries) { continue; }
            return ProjectionOutcome.Applied;
        }
    }
}
