using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Reconcilers;

// W9.8: the two reads the translation of a webhook needs (WebhookEventTranslator), by GraphQL with the gateway's own GitHub credential (GH_TOKEN or the author App), as Get-ItemInfo and the node query of
// Verb_ingest_event make them: the labels and Status column of an item, and the issue behind a board card's content node. The failures are the ones the script's Reject gave: no credential is exit 30 secret-missing,
// a refused call exit 20 github-graphql-error, a rate limit exit 20 github-rate-limited.
sealed class HttpWebhookLookups(HttpClient http, GitHubTokenProvider? tokens, RepoId home, int project, Action<string, string?, string?, string?>? observe = null) : IWebhookLookups
{
    const string ItemQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ labels(first:50){ nodes{ name } }
          projectItems(first:20){ nodes{ project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;
    const string NodeQuery = """
        query($id:ID!){ node(id:$id){ ... on Issue{ number repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } }
        """;

    public async Task<WebhookItemFacts?> ReadItem(int item, CancellationToken ct = default)
    {
        var data = await Query(ItemQuery, new JsonObject { ["o"] = home.Owner, ["r"] = home.Repo, ["n"] = item }, ct);
        if (data?["repository"]?["issue"] is not JsonObject issue) return null;
        var labels = Names(issue["labels"]?["nodes"]);
        var card = (issue["projectItems"]?["nodes"] as JsonArray ?? []).FirstOrDefault(n => n?["project"]?["number"]?.GetValue<int>() == project);
        return new WebhookItemFacts(labels, card?["fieldValueByName"]?["name"]?.ToString());
    }

    public async Task<WebhookIssueNode?> ReadIssueNode(string nodeId, CancellationToken ct = default)
    {
        var data = await Query(NodeQuery, new JsonObject { ["id"] = nodeId }, ct);
        // the node of a draft, a pull request or nothing has none of these fields
        if (data?["node"] is not JsonObject node || node["repository"]?["nameWithOwner"]?.ToString() is not { Length: > 0 } repo || node["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number)) return null;
        return new WebhookIssueNode(number, repo, Names(node["labels"]?["nodes"]));
    }

    static List<string> Names(JsonNode? nodes) => (nodes as JsonArray ?? []).Select(n => n?["name"]?.ToString() ?? "").ToList();

    async Task<JsonNode?> Query(string query, JsonObject variables, CancellationToken ct)
    {
        if (tokens is null) throw new WebhookLookupException(30, "secret-missing", "GH_TOKEN is not set on the gateway");
        string token;
        try { token = await tokens.Token(ct); }
        catch (GitHubTokenException e) { throw new WebhookLookupException(30, "secret-missing", e.Message); }
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql") { Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new WebhookLookupException(20, "github-graphql-error", "GraphQL request failed", 30); }
        using (resp)
        {
            static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
            if (H(resp, "x-ratelimit-resource") is { } res) observe?.Invoke(res, H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
            if (!resp.IsSuccessStatusCode) throw new WebhookLookupException(20, "github-graphql-error", $"GraphQL HTTP {(int)resp.StatusCode}", 30);
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (root?["errors"] is JsonArray { Count: > 0 } errors)
            {
                var message = string.Join("; ", errors.Select(e => e?["message"]?.ToString() ?? ""));
                if (errors.Any(e => e?["type"]?.ToString() == "RATE_LIMITED")) throw new WebhookLookupException(20, "github-rate-limited", message, 60);
                throw new WebhookLookupException(20, "github-graphql-error", message, 30);
            }
            return root?["data"];
        }
    }
}
