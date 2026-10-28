using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>Who the gateway writes as and which state comments it trusts: the instance name of the marker, the login, and the logins of an earlier identity (stateAuthors).</summary>
public sealed record JournalIdentity(string InstanceName, Func<CancellationToken, Task<string>> Login, IReadOnlyList<string> StateAuthors);

/// <summary>
/// W3.2: <see cref="IJournalStore"/> over the GitHub REST API of one repository: Get-State, Get-Comments, Save-State and the journal's comment writes of the rule scripts,
/// with their calls in their order (list the comments, post, read the state comment's revision, patch it).
/// </summary>
public sealed class GitHubJournalStore : IJournalStore
{
    readonly GitHubRepoClient client;
    readonly JournalIdentity identity;
    readonly Func<DateTime> utcNow;
    readonly Action<string> log;

    public GitHubJournalStore(GitHubRepoClient client, JournalIdentity identity, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        this.client = client; this.identity = identity; this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.log = log ?? (m => Console.Error.WriteLine(m));
    }

    public async Task<ItemState> Read(int item, CancellationToken ct)
    {
        var marker = ItemStateComment.Marker(identity.InstanceName);
        var login = await identity.Login(ct);
        var all = new List<IssueComment>();
        for (var page = 1; ; page++)
        {
            var arr = Json(await Call(HttpMethod.Get, $"issues/{item}/comments?per_page=100&page={page}", null, ct)) as JsonArray ?? [];
            foreach (var c in arr) all.Add(new IssueComment(c?["id"]?.GetValue<long>() ?? 0, c?["user"]?["login"]?.ToString() ?? "", c?["body"]?.ToString() ?? ""));
            if (arr.Count != 100) break;
        }
        var found = all.FirstOrDefault(c => string.Equals(c.Login, login, StringComparison.OrdinalIgnoreCase) && c.Body.StartsWith(marker, StringComparison.Ordinal));
        if (found == null)
        {
            // a change of identity must not orphan the items in flight: adopt the newest state written by one of the logins the instance names in stateAuthors
            var old = identity.StateAuthors.Count == 0 ? null
                : all.LastOrDefault(c => identity.StateAuthors.Any(a => string.Equals(a, c.Login, StringComparison.OrdinalIgnoreCase)) && c.Body.StartsWith(marker, StringComparison.Ordinal));
            if (old != null && ItemStateComment.Parse(old.Body) is { } adopted) return new ItemState { CommentId = null, State = adopted, Comments = all, AdoptedFrom = old.Login };
            return new ItemState { CommentId = null, State = new JsonObject { ["schema"] = 1, ["item"] = item }, Comments = all };
        }
        var s = ItemStateComment.Parse(found.Body) ?? new JsonObject { ["schema"] = 1, ["item"] = item };
        return new ItemState { CommentId = found.Id, State = s, Rev = ItemStateComment.RevOf(s), Comments = all };
    }

    public async Task Save(int item, ItemState holder, CancellationToken ct)
    {
        var s = holder.State; s["item"] = item; s["updated"] = JournalJson.Iso(utcNow());
        var readRev = holder.Rev;
        if (holder.CommentId is { } cid && readRev != null)
        {
            var cur = await Call(HttpMethod.Get, $"issues/comments/{cid}", null, ct, allow404: true);
            if (cur != null)
            {
                var curState = ItemStateComment.Parse(Json(cur)?["body"]?.ToString() ?? "");
                var curRev = curState != null ? ItemStateComment.RevOf(curState) : readRev.Value;
                if (curRev != readRev) throw new StateConflictException($"the state of item {item} changed since it was read (revision {readRev}, now {curRev}); read it again and retry");
            }
        }
        s["rev"] = (readRev ?? ItemStateComment.RevOf(s)) + 1;
        var body = ItemStateComment.Body(identity.InstanceName, item, s);
        if (body.Length > ItemStateComment.Budget) { ItemStateComment.Limit(identity.InstanceName, item, s, utcNow(), log); body = ItemStateComment.Body(identity.InstanceName, item, s); }
        // a state comment that is gone since it was read (deleted by a person) is written again as a new one, never an error
        if (holder.CommentId is { } id)
        {
            var r0 = await Call(HttpMethod.Patch, $"issues/comments/{id}", new { body }, ct, allow404: true);
            if (r0 == null) holder.CommentId = null;
        }
        if (holder.CommentId == null)
        {
            var r = Json(await Call(HttpMethod.Post, $"issues/{item}/comments", new { body }, ct));
            holder.CommentId = r?["id"]?.GetValue<long>();
        }
        holder.Rev = s["rev"]!.GetValue<int>();
    }

    public async Task<long?> Post(int item, string body, CancellationToken ct)
    {
        var r = Json(await Call(HttpMethod.Post, $"issues/{item}/comments", new { body }, ct));
        return r?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null;
    }

    public async Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct)
    {
        try
        {
            var r = await Call(HttpMethod.Patch, $"issues/comments/{commentId}", new { body }, ct, allow404: true, allow410: true);
            return r == null ? PatchOutcome.Gone : PatchOutcome.Ok;
        }
        catch (GitHubForbiddenException) { return PatchOutcome.Forbidden; }
    }

    // ---- the call: Invoke-Gh's rules (Core.ps1) ----------------------------------------------------------------------------------------------

    sealed class GitHubForbiddenException(string message) : Exception(message);

    /// <summary>The body of a 2xx answer; null for a 404 (or 410) that the caller allows. Anything else throws: HttpRequestException when worth a retry, JournalRefusedException when not.</summary>
    async Task<string?> Call(HttpMethod method, string relative, object? body, CancellationToken ct, bool allow404 = false, bool allow410 = false)
    {
        using var content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await client.Send(method, relative, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;
        if (status is >= 200 and < 300) return text;
        if ((status == 404 && allow404) || (status == 410 && allow410)) return null;
        var detail = $"GitHub {status} on {method} {relative}";
        int? remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var rv) && int.TryParse(rv.FirstOrDefault(), out var rem) ? rem : null;
        DateTimeOffset? reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var sv) && long.TryParse(sv.FirstOrDefault(), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        var c = ErrorClassifier.Classify(new HttpOutcome(status, text, resp.Headers.RetryAfter?.Delta, reset, remaining), idempotentCommand: false);
        if (c.Action is ErrorAction.Retry or ErrorAction.Reschedule) throw new HttpRequestException(detail, null, (HttpStatusCode)status);
        if (status == 403) throw new GitHubForbiddenException(detail);
        throw new JournalRefusedException(detail);
    }

    static JsonNode? Json(string? text) => string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
}
