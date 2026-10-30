using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;

// W9.3 (#355): the gateway's side of the merge train verbs on v2 (Fworker.Workflows.Merge): the reads of GitHub that `merge`, `base-watch` and `base-update-branch` make, and the saga's and QC's tables that give them an
// item's facts. The rules and the answers are in the Workflows project; these adapters only fetch. A fault throws, and the verb answers the rule script's internal error (exit 20, retry after 30 seconds); a 404 or 422
// that the rule script allows (Invoke-Gh -Allow) is an answer here too.

/// <summary>The labels of an item, read from its GitHub issue (the labels are the person's: a hold, the Auto Merge label).</summary>
public sealed class GitHubItemLabels(GitHubClientFactory clients) : IItemLabels
{
    public async Task<IReadOnlyList<string>?> LabelsOf(ItemId item, CancellationToken ct)
    {
        var client = await clients.For(item.Repository, ct);
        using var resp = await client.Send(HttpMethod.Get, $"issues/{item.Number}", null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode} to a read of issue #{item.Number}");
        var issue = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (issue?["labels"] as JsonArray ?? []).Select(l => l is JsonValue v && v.TryGetValue<string>(out var s) ? s : l?["name"]?.ToString() ?? "").Where(n => n.Length > 0).ToList();
    }
}

/// <summary>The calls of BaseUpdate.ps1 against the home repository: branches, compare, pulls, the files of a pull request and GitHub's update branch.</summary>
public sealed class GitHubBaseUpdate(GitHubClientFactory clients, RepoId home, string defaultBranch) : IBaseUpdateGitHub
{
    async Task<(int Status, JsonNode? Json)> Call(HttpMethod method, string relative, JsonObject? body, CancellationToken ct)
    {
        var client = await clients.For(home, ct);
        using var content = body is null ? null : new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.Send(method, relative, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        if (text.Length > 0) { try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { } }
        return ((int)resp.StatusCode, json);
    }

    static Exception Failed(string what, int status) => new HttpRequestException($"GitHub answered {status} to {what}");

    public async Task<string> BaseHead(CancellationToken ct)
    {
        var (status, json) = await Call(HttpMethod.Get, $"branches/{Uri.EscapeDataString(defaultBranch)}", null, ct);
        if (status is < 200 or >= 300) throw Failed("a read of the default branch", status);
        return json?["commit"]?["sha"]?.ToString() ?? "";
    }

    public async Task<IReadOnlyList<string>?> ChangedFiles(string from, string to, CancellationToken ct)
    {
        var (status, json) = await Call(HttpMethod.Get, $"compare/{from}...{to}", null, ct);
        if (status is 404 or 422) return null;
        if (status is < 200 or >= 300) throw Failed("a compare of the default branch", status);
        return (json?["files"] as JsonArray ?? []).Take(300).Select(f => f?["filename"]?.ToString() ?? "").ToList();
    }

    public async Task<IReadOnlyList<BasePull>> OpenPullRequests(CancellationToken ct)
    {
        var (status, json) = await Call(HttpMethod.Get, "pulls?state=open&per_page=100", null, ct);
        if (status is < 200 or >= 300) throw Failed("a read of the open pull requests", status);
        return (json as JsonArray ?? []).Select(Pull).ToList();
    }

    public async Task<BasePull> PullRequest(int number, CancellationToken ct)
    {
        var (status, json) = await Call(HttpMethod.Get, $"pulls/{number}", null, ct);
        if (status is < 200 or >= 300 || json is null) throw Failed($"a read of PR #{number}", status);
        return Pull(json);
    }

    static BasePull Pull(JsonNode? p) => new(p?["number"]?.GetValue<int>() ?? 0, p?["state"]?.ToString() ?? "", p?["head"]?["ref"]?.ToString() ?? "", p?["head"]?["sha"]?.ToString() ?? "",
        p?["mergeable"] is JsonValue m && m.TryGetValue<bool>(out var b) ? b : null, p?["mergeable_state"]?.ToString() ?? "");

    public async Task<IReadOnlyList<string>> PullRequestFiles(int number, CancellationToken ct)
    {
        var all = new List<string>();
        for (var page = 1; ; page++)
        {
            var (status, json) = await Call(HttpMethod.Get, $"pulls/{number}/files?per_page=100&page={page}", null, ct);
            if (status is < 200 or >= 300) throw Failed($"a read of the files of PR #{number}", status);
            var chunk = json as JsonArray ?? [];
            all.AddRange(chunk.Select(f => f?["filename"]?.ToString() ?? ""));
            if (chunk.Count < 100) return all;
        }
    }

    public async Task<int> BehindBy(string branch, CancellationToken ct)
    {
        var (status, json) = await Call(HttpMethod.Get, $"compare/{Uri.EscapeDataString(defaultBranch)}...{branch}?per_page=1", null, ct);
        if (status is 404 or 422) return -1;
        if (status is < 200 or >= 300) throw Failed("a compare with the default branch", status);
        return json?["behind_by"] is JsonValue v && v.TryGetValue<int>(out var behind) ? behind : 0;
    }

    public async Task<int> UpdateBranch(int pullRequest, string expectedHead, CancellationToken ct)
    {
        var (status, _) = await Call(HttpMethod.Put, $"pulls/{pullRequest}/update-branch", new JsonObject { ["expected_head_sha"] = expectedHead }, ct);
        if (status is 202 or 409 or 422) return status;
        throw Failed($"the update of PR #{pullRequest}", status);
    }
}

/// <summary>The facts of an item from the saga's table, and the QC record of a pull request of the home repository from QC's.</summary>
public sealed class SagaBaseUpdateItems(IWorkItemStore store, RepoId home, Func<FactoryDbContext>? open) : IBaseUpdateItems
{
    public async Task<BaseItemFacts?> FactsOf(int item, CancellationToken ct) =>
        (await store.Load(new ItemId(home, item).ToString(), ct))?.State is { } state ? BaseItemFacts.From(state) : null;

    public async Task<string?> QcRecord(int pullRequest, CancellationToken ct)
    {
        if (open is null) return null;
        await using var db = open();
        var key = new ItemId(home, pullRequest).ToPullRequestString();
        return await db.QcEnvironments.AsNoTracking().Where(x => x.PullRequestKey == key).Select(x => x.StateJson).FirstOrDefaultAsync(ct);
    }
}
