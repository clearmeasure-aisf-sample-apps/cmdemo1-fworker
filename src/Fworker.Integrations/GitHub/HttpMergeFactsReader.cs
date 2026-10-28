using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W5.0: reads what the derivation of ItemMerged needs, read only. A pull request by number (REST), the pull request that closed an issue (GraphQL closedByPullRequestsReferences),
/// and an item: its labels and body (REST issue), the gateway's state comment (the marker of <see cref="ItemStateComment"/> for <paramref name="instanceName"/>: the verdict and the
/// merge) and, for an item that carries <paramref name="videoLabel"/>, the release demo-N with its asset demo-N.mp4. A 404 is "not there" (null); any other failure throws, and the
/// inbox retries the message. The HttpClient's BaseAddress is the API root; the answer's rate-limit headers go to <paramref name="observe"/>.
/// </summary>
public sealed class HttpMergeFactsReader(HttpClient http, IGitHubCredentialSource credentials, string instanceName, string videoLabel, Action<string, string?, string?, string?>? observe = null) : IMergeFactsReader
{
    const string ClosedBy = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ closedByPullRequestsReferences(first:10, includeClosedPrs:true){ nodes{
          number merged baseRefName headRefName title body mergeCommit{ oid } } } } } }
        """;

    public async Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default)
    {
        var p = await Get(repository.Owner, $"repos/{repository}/pulls/{number}", ct);
        return p is null ? null : new MergedPullRequest(repository, number, p["merged"]?.GetValue<bool>() == true, p["base"]?["ref"]?.ToString(), p["head"]?["ref"]?.ToString(),
            p["title"]?.ToString(), p["body"]?.ToString(), Lower(p["merge_commit_sha"]?.ToString()));
    }

    public async Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default)
    {
        var cred = await credentials.For(item.Repository.Owner, ct) ?? throw new NoInstallationException(item.Repository.Owner);
        var body = new JsonObject { ["query"] = ClosedBy, ["variables"] = new JsonObject { ["o"] = item.Repository.Owner, ["r"] = item.Repository.Repo, ["n"] = item.Number } }.ToJsonString();
        using var req = Request(HttpMethod.Post, "graphql", cred); req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        Observe(resp);
        if (!resp.IsSuccessStatusCode) throw new MergeFactsException($"GitHub answered {(int)resp.StatusCode} to the pull request read of {item}");
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (root?["errors"] is JsonArray { Count: > 0 } errs) throw new MergeFactsException($"GitHub answered GraphQL errors to the pull request read of {item} ({errs[0]?["type"]})");
        // the newest merged one: the last in the list
        var nodes = root?["data"]?["repository"]?["issue"]?["closedByPullRequestsReferences"]?["nodes"] as JsonArray ?? [];
        var pr = nodes.LastOrDefault(n => n?["merged"]?.GetValue<bool>() == true);
        return pr is null ? null : new MergedPullRequest(item.Repository, pr["number"]!.GetValue<int>(), true, pr["baseRefName"]?.ToString(), pr["headRefName"]?.ToString(),
            pr["title"]?.ToString(), pr["body"]?.ToString(), Lower(pr["mergeCommit"]?["oid"]?.ToString()));
    }

    public async Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default)
    {
        var path = $"repos/{item.Repository}/issues/{item.Number}";
        var issue = await Get(item.Repository.Owner, path, ct);
        if (issue is null || issue["pull_request"] is not null) return null;   // a pull request is no item
        var labels = (issue["labels"] as JsonArray ?? []).Select(l => l?["name"]?.ToString() ?? "").Where(n => n != "").ToList();
        var marker = ItemStateComment.Marker(instanceName);
        JsonObject? state = null;
        for (var page = 1; page <= 10 && state is null; page++)
        {
            var comments = await GetArray(item.Repository.Owner, $"{path}/comments?per_page=100&page={page}", ct);
            foreach (var c in comments)
                if (c?["body"]?.ToString() is { } b && b.StartsWith(marker, StringComparison.Ordinal)) { state = ItemStateComment.Parse(b); break; }
            if (comments.Count < 100) break;
        }
        var verdict = state?["verdict"] as JsonObject; var merge = state?["merge"] as JsonObject;
        var video = false;
        if (labels.Any(l => string.Equals(l, videoLabel, StringComparison.OrdinalIgnoreCase)))
        {
            var rel = await Get(item.Repository.Owner, $"repos/{item.Repository}/releases/tags/demo-{item.Number}", ct);
            video = (rel?["assets"] as JsonArray ?? []).Any(a => a?["name"]?.ToString() == $"demo-{item.Number}.mp4");
        }
        return new ItemMergeFacts(item, state is not null, labels, issue["body"]?.ToString(),
            verdict?["passed"]?.GetValue<bool>() == true, Int(verdict?["pr"]), Int(merge?["pr"]), merge?["by"]?.ToString(), video);
    }

    async Task<JsonNode?> Get(string owner, string path, CancellationToken ct)
    {
        var cred = await credentials.For(owner, ct) ?? throw new NoInstallationException(owner);
        using var req = Request(HttpMethod.Get, path, cred);
        using var resp = await http.SendAsync(req, ct);
        Observe(resp);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new MergeFactsException($"GitHub answered {(int)resp.StatusCode} to a read of {path.Split('?')[0]}");
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    async Task<JsonArray> GetArray(string owner, string path, CancellationToken ct) => await Get(owner, path, ct) as JsonArray ?? [];

    static HttpRequestMessage Request(HttpMethod method, string path, GitHubCredential cred)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new("Bearer", cred.Token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        return req;
    }

    void Observe(HttpResponseMessage r)
    {
        static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        if (H(r, "x-ratelimit-resource") is { } res) observe?.Invoke(res, H(r, "x-ratelimit-remaining"), H(r, "x-ratelimit-reset"), H(r, "x-ratelimit-limit"));
    }

    static string? Lower(string? s) => string.IsNullOrEmpty(s) ? null : s.ToLowerInvariant();
    static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

/// <summary>GitHub answered a read of the merge derivation with an error (the message never carries the token).</summary>
public sealed class MergeFactsException(string message) : Exception(message);
