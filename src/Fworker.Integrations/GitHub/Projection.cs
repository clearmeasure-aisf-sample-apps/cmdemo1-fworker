using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

// W6.3 (#330): the projections of the v2 engine onto GitHub: the item's state comment (in the format v1's Get-State reads, the rollback path) and its
// board column. Each write carries the version of the command that asks for it and is recorded against the item's projection version (the table of W2.1,
// behind IProjectionVersions): an older or repeated version is skipped, and the events GitHub sends back for a write of the projection itself are recognized
// (ProjectionEcho) so the engine does not treat its own write as news. The card is wired where the engine acts (DocsProjection; W9.10h: the ingress inbox consults the echo before a card event reaches the saga), the state
// comment by W9.10i. There is no label projection: the labels of an item are the rule scripts' and the people's (W9.10h deleted the unused LabelProjection; the factory's own labels are not the engine's to project).

/// <summary>The projected targets: the <c>Target</c> column of the projection versions table.</summary>
public static class ProjectionTargets
{
    public const string Card = "card";
    public const string Comment = "comment";
}

public enum ProjectionOutcome
{
    /// <summary>GitHub was written and the version recorded.</summary>
    Applied,
    /// <summary>GitHub already had the wanted state: nothing was written, the version was recorded.</summary>
    Unchanged,
    /// <summary>The version is not newer than the one applied: nothing was done.</summary>
    Stale,
}

/// <summary>GitHub refused a projection write for good, or the target does not exist (an unknown column, an item that is not on the board).</summary>
public sealed class ProjectionRefusedException(string message) : Exception(message);

/// <summary>The last version applied to one target of one item (table ProjectionVersions of W2.1). A version is a positive number that only grows per (item, target).</summary>
public interface IProjectionVersions
{
    /// <summary>The applied version; 0 when the target was never projected.</summary>
    Task<long> Applied(string itemKey, string target, CancellationToken ct = default);

    /// <summary>Records <paramref name="version"/> when it is newer than the applied one; false (and nothing changed) when it is not.</summary>
    Task<bool> Advance(string itemKey, string target, long version, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>For the tests and a gateway without a database: the versions in memory.</summary>
public sealed class InMemoryProjectionVersions : IProjectionVersions
{
    readonly ConcurrentDictionary<(string, string), long> applied = new();

    public Task<long> Applied(string itemKey, string target, CancellationToken ct = default) =>
        Task.FromResult(applied.TryGetValue((itemKey, target), out var v) ? v : 0);

    public Task<bool> Advance(string itemKey, string target, long version, DateTimeOffset at, CancellationToken ct = default)
    {
        while (true)
        {
            if (!applied.TryGetValue((itemKey, target), out var cur)) { if (applied.TryAdd((itemKey, target), version)) return Task.FromResult(true); continue; }
            if (version <= cur) return Task.FromResult(false);
            if (applied.TryUpdate((itemKey, target), version, cur)) return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Echo suppression. After a projection writes, GitHub tells the engine about it (a webhook, or the reconciler's next read). The projection records what it wrote as a
/// fingerprint (the state comment's revision, the column) beside the version of the write; an event whose fingerprint is that one, while the version is
/// still the newest applied, is the projection's own and is ignored. Any other fingerprint (a person, a v1 rule script, a worker) is news. The record is in memory: after
/// a restart an echo is processed once, which the engine's reads tolerate (they are idempotent), never lost.
/// </summary>
public sealed class ProjectionEcho(IProjectionVersions versions, Func<DateTimeOffset>? now = null)
{
    /// <summary>How many of a target's latest writes are remembered for <see cref="WroteRecently"/>.</summary>
    public const int RecentWrites = 8;

    readonly object gate = new();
    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<(string, string), (long Version, string Fingerprint)> last = [];
    // W9.10h: the latest few writes of a target with their time. An event for a write that is no longer the newest one (a card moved twice in a few seconds: the webhook of the first arrives after the second was
    // written) is still the projection's own; IsEcho alone, which compares with the newest write only, would take it for news.
    readonly Dictionary<(string, string), List<(string Fingerprint, DateTimeOffset At)>> recent = [];

    /// <summary>What <see cref="Record"/> did, for <see cref="Unrecord"/>: whether the record was taken and what it replaced.</summary>
    public readonly record struct Mark(string ItemKey, string Target, long Version, string Fingerprint, bool Taken, bool HadPrevious, long PreviousVersion, string? PreviousFingerprint);

    /// <summary>The state comment's fingerprint: its revision counter.</summary>
    public static string StateFingerprint(System.Text.Json.Nodes.JsonObject state) => RevisionFingerprint(ItemStateComment.RevOf(state));
    /// <summary>The same for a revision number: a projection knows the revision it is about to write before the write has answered.</summary>
    public static string RevisionFingerprint(int rev) => $"rev:{rev}";
    /// <summary>The card's fingerprint: the column name, case-insensitive.</summary>
    public static string ColumnFingerprint(string column) => column.ToLowerInvariant();

    /// <summary>
    /// Called by a projection BEFORE its write goes out (the event of the write can arrive before the write's own answer does). A record of a newer version is kept: an older
    /// apply that comes late never replaces it (compare and set; the returned mark says <c>Taken: false</c>). When the write then fails or is half done the projection hands the
    /// mark to <see cref="Unrecord"/>, so a later real change that happens to look like the planned write is not mistaken for an echo.
    /// </summary>
    public Mark Record(string itemKey, string target, long version, string fingerprint)
    {
        lock (gate)
        {
            var had = last.TryGetValue((itemKey, target), out var cur);
            if (had && cur.Version > version) return new Mark(itemKey, target, version, fingerprint, false, false, 0, null);
            last[(itemKey, target)] = (version, fingerprint);
            if (!recent.TryGetValue((itemKey, target), out var writes)) recent[(itemKey, target)] = writes = [];
            writes.Add((fingerprint, now()));
            if (writes.Count > RecentWrites) writes.RemoveAt(0);
            return new Mark(itemKey, target, version, fingerprint, true, had, had ? cur.Version : 0, had ? cur.Fingerprint : null);
        }
    }

    /// <summary>The write the mark was taken for did not (completely) happen: the record goes back to what it was, unless another apply has replaced it since.</summary>
    public void Unrecord(Mark mark)
    {
        if (!mark.Taken) return;
        lock (gate)
        {
            var k = (mark.ItemKey, mark.Target);
            if (!last.TryGetValue(k, out var cur) || cur.Version != mark.Version || cur.Fingerprint != mark.Fingerprint) return;
            if (mark.HadPrevious) last[k] = (mark.PreviousVersion, mark.PreviousFingerprint!); else last.Remove(k);
            if (recent.TryGetValue(k, out var writes) && writes.FindLastIndex(w => w.Fingerprint == mark.Fingerprint) is >= 0 and var at) writes.RemoveAt(at);   // (the write did not happen)
        }
    }

    /// <summary>
    /// W9.10h: was a state with this fingerprint among the target's latest writes, written no longer than <paramref name="within"/> ago? For a webhook, which arrives within seconds of the write it reports: the
    /// event of an earlier write of a quick run of writes is still the projection's. Not for a poll, which reports the state as it is now: an older state that a person put back is news.
    /// </summary>
    public bool WroteRecently(string itemKey, string target, string fingerprint, TimeSpan within)
    {
        lock (gate)
        {
            if (!recent.TryGetValue((itemKey, target), out var writes)) return false;
            var since = now() - within;
            return writes.Any(w => w.Fingerprint == fingerprint && w.At >= since);
        }
    }

    /// <summary>Is the event (the target's state as GitHub now reports it) the echo of the projection's own newest write?</summary>
    public async Task<bool> IsEcho(string itemKey, string target, string fingerprint, CancellationToken ct = default)
    {
        (long Version, string Fingerprint) mine;
        lock (gate) { if (!last.TryGetValue((itemKey, target), out mine) || mine.Fingerprint != fingerprint) return false; }
        return mine.Version >= await versions.Applied(itemKey, target, ct);
    }

    /// <summary>The echo check for a state comment body, as the webhook of an issue comment carries it.</summary>
    public Task<bool> IsStateCommentEcho(string itemKey, string commentBody, CancellationToken ct = default) =>
        ItemStateComment.Parse(commentBody) is { } s ? IsEcho(itemKey, ProjectionTargets.Comment, StateFingerprint(s), ct) : Task.FromResult(false);

    /// <summary>The item is finished: its record goes.</summary>
    public void Forget(string itemKey)
    {
        lock (gate)
        {
            foreach (var k in last.Keys.Where(k => k.Item1 == itemKey).ToList()) last.Remove(k);
            foreach (var k in recent.Keys.Where(k => k.Item1 == itemKey).ToList()) recent.Remove(k);
        }
    }
}

/// <summary>The REST and GraphQL calls of the projections, answered with Invoke-Gh's rules (Core.ps1): 2xx the body, an allowed 404 null, a worth-a-retry answer an HttpRequestException, any other refusal a ProjectionRefusedException.</summary>
static class ProjectionHttp
{
    public static async Task<string?> Send(HttpClient http, GitHubCredential cred, HttpMethod method, string uri, JsonNode? body, CancellationToken ct, bool allow404 = false)
    {
        using var req = new HttpRequestMessage(method, uri);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await http.SendAsync(req, ct);
        return await Read(resp, $"{method} {uri}", allow404, ct);
    }

    /// <summary>The body of a 2xx answer; null for an allowed 404; a retry-worthy answer is an HttpRequestException, any other a ProjectionRefusedException.</summary>
    public static async Task<string?> Read(HttpResponseMessage resp, string what, bool allow404, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;
        if (status is >= 200 and < 300) return text;
        if (status == 404 && allow404) return null;
        var detail = $"GitHub {status} on {what}";   // (never the token, never the body)
        int? remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var rv) && int.TryParse(rv.FirstOrDefault(), out var rem) ? rem : null;
        DateTimeOffset? reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var sv) && long.TryParse(sv.FirstOrDefault(), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        var c = ErrorClassifier.Classify(new HttpOutcome(status, text, resp.Headers.RetryAfter?.Delta, reset, remaining), idempotentCommand: false);
        if (c.Action is ErrorAction.Retry or ErrorAction.Reschedule) throw new HttpRequestException(detail, null, (HttpStatusCode)status);
        throw new ProjectionRefusedException(detail);
    }

    /// <summary>A GraphQL call to <c>graphql</c>; the <c>data</c> object. GraphQL errors are a refusal.</summary>
    public static async Task<JsonNode> GraphQl(HttpClient http, GitHubCredential cred, string query, JsonObject variables, CancellationToken ct)
    {
        var text = await Send(http, cred, HttpMethod.Post, "graphql", new JsonObject { ["query"] = query, ["variables"] = variables }, ct);
        var root = JsonNode.Parse(text ?? "{}");
        if (root?["errors"] is JsonArray { Count: > 0 } errs) throw new ProjectionRefusedException($"GitHub answered GraphQL errors ({errs[0]?["type"]})");
        return root?["data"] ?? throw new ProjectionRefusedException("GitHub answered no data to the GraphQL call");
    }
}
