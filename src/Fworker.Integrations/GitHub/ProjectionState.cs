using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.3: the state comment as a versioned projection. It is written in the format v1's Get-State reads (the marker line, the json fence, the revision counter, the 48,000
/// character budget): v2 uses the same <see cref="IJournalStore"/> as the journal, whose Read and Save are Get-State and Save-State, so the comment is the first one of the
/// gateway's login that carries the marker, created when there is none, updated otherwise, refused when its revision moved since the read. A revert or FACTORY_V2_OFF hands
/// every open item back to v1 with its state intact.
/// </summary>
public sealed class StateCommentProjection(IJournalStore store, IProjectionVersions versions, ProjectionEcho echo, Func<DateTimeOffset>? now = null)
{
    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Update-State: reads the state, applies <paramref name="mutate"/>, saves; on a conflict (the comment's revision moved) it reads again and applies again, up to
    /// <paramref name="tries"/> times, then the <see cref="StateConflictException"/> is thrown. <paramref name="mutate"/> runs on a fresh read each time and must be repeatable.
    /// A version that is not newer than the applied one is skipped (<see cref="ProjectionOutcome.Stale"/>), before anything is read.
    /// </summary>
    public async Task<ProjectionOutcome> Apply(ItemId item, long version, Action<JsonObject> mutate, CancellationToken ct = default, int tries = 3)
    {
        var key = item.ToString();
        if (version <= await versions.Applied(key, ProjectionTargets.Comment, ct)) return ProjectionOutcome.Stale;
        for (var i = 1; ; i++)
        {
            var holder = await store.Read(item.Number, ct);
            mutate(holder.State);
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
}
