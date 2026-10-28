using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.3: the labels of an item as a versioned projection, through REST. The projection owns a set of label names (<c>managed</c>: the factory's own, such as the env and
/// priority labels) and never touches another: it reads the issue's labels and adds only what is missing from <c>desired</c>, removes only a managed label that is no
/// longer wanted. Add-ItemLabels and Remove-ItemLabel of Board.ps1 are the calls: one POST with the names to add, one DELETE per name to remove (a 404 is no failure).
/// Names compare case-insensitively, as GitHub's do. A label the issue carries already is never written again, so it keeps the casing GitHub has for it even when <c>desired</c>
/// spells it differently: the casing belongs to the repository's label (renaming it is a label PATCH, not a projection of an item), which is a deliberate limit.
/// </summary>
public sealed class LabelProjection(GitHubRepoClient client, IProjectionVersions versions, ProjectionEcho echo, Func<DateTimeOffset>? now = null)
{
    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<ProjectionOutcome> Apply(ItemId item, long version, IReadOnlyCollection<string> desired, IReadOnlyCollection<string> managed, CancellationToken ct = default)
    {
        var key = item.ToString();
        if (version <= await versions.Applied(key, ProjectionTargets.Labels, ct)) return ProjectionOutcome.Stale;
        var cmp = StringComparer.OrdinalIgnoreCase;
        var current = await Current(item.Number, ct);
        var want = desired.ToHashSet(cmp); var own = managed.ToHashSet(cmp); var have = current.ToHashSet(cmp);
        var add = desired.Where(l => !have.Contains(l)).Distinct(cmp).ToList();
        var remove = current.Where(l => own.Contains(l) && !want.Contains(l)).ToList();
        var changed = add.Count > 0 || remove.Count > 0;
        if (changed)
        {
            var after = current.Where(l => !remove.Contains(l, cmp)).Concat(add).ToList();
            // the echo is recorded before the writes: the first label event can arrive before the last write has answered
            // a failure part way (the POST done, a DELETE refused) takes the record back: the labels are then neither the old nor the planned set, and whatever event arrives is
            // processed once (the reads are idempotent) instead of a later human change to the planned set being dropped as an echo
            var mark = echo.Record(key, ProjectionTargets.Labels, version, ProjectionEcho.LabelsFingerprint(after));
            try
            {
                if (add.Count > 0)
                    await Call(HttpMethod.Post, $"issues/{item.Number}/labels", new JsonObject { ["labels"] = new JsonArray(add.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) }, false, ct);
                foreach (var l in remove)
                    await Call(HttpMethod.Delete, $"issues/{item.Number}/labels/{Uri.EscapeDataString(l)}", null, true, ct);
            }
            catch { echo.Unrecord(mark); throw; }
        }
        await versions.Advance(key, ProjectionTargets.Labels, version, now(), ct);
        return changed ? ProjectionOutcome.Applied : ProjectionOutcome.Unchanged;
    }

    async Task<List<string>> Current(int number, CancellationToken ct)
    {
        var all = new List<string>();
        for (var page = 1; ; page++)
        {
            var text = await Call(HttpMethod.Get, $"issues/{number}/labels?per_page=100&page={page}", null, false, ct);
            var arr = JsonNode.Parse(text ?? "[]") as JsonArray ?? [];
            foreach (var l in arr) if (l?["name"]?.ToString() is { Length: > 0 } n) all.Add(n);
            if (arr.Count != 100) return all;
        }
    }

    async Task<string?> Call(HttpMethod method, string relative, JsonNode? body, bool allow404, CancellationToken ct)
    {
        using var content = body == null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.Send(method, relative, content, ct);
        return await ProjectionHttp.Read(resp, $"{method} {relative}", allow404, ct);
    }
}
