using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.GitHub;

/// <summary>GitHub answered the board read with an error or with GraphQL errors (the message never carries the token).</summary>
public sealed class BoardReadException(string message) : Exception(message);

/// <summary>
/// W4.3: reads the cards of the project board with the query of v1's Get-BoardList (Board.ps1): one paged GraphQL read of every card with an issue, its Status column,
/// state and repository. No label filter: the scope of an environment is the consumer's. The answer's rate-limit headers go to <paramref name="observe"/> (resource, remaining, reset, limit), which the gateway hands to its budget.
/// The HttpClient's BaseAddress is the API root (GITHUB_API_URL or api.github.com); at most <see cref="MaxPages"/> pages of 100 are read.
/// </summary>
public sealed class HttpBoardReader(HttpClient http, IGitHubCredentialSource credentials, Action<string, string?, string?, string?>? observe = null, bool withLabels = false) : IMeteredBoardReader
{
    public const int MaxPages = 20;
    long requests;
    /// <summary>The GraphQL requests made (a page of 100 cards each; one point of the GraphQL budget each).</summary>
    public long Requests => Interlocked.Read(ref requests);
    const string Query = """
        query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
          nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
            content{ ... on Issue{ number state title repository{ nameWithOwner } } } } } } } }
        """;
    // W9.10k: with the labels of each issue, for the reader of the reconciler where the engine acts (the plant reads the pins, priorities and runtime labels of the saga's items from them); one more nested connection per card
    const string QueryWithLabels = """
        query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
          nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
            content{ ... on Issue{ number state title repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } } } } } }
        """;

    public async Task<IReadOnlyList<BoardCard>> Read(BoardRef board, CancellationToken ct = default)
    {
        var cred = await credentials.For(board.Owner, ct) ?? throw new NoInstallationException(board.Owner);
        var cards = new List<BoardCard>(); string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var body = new JsonObject { ["query"] = withLabels ? QueryWithLabels : Query, ["variables"] = new JsonObject { ["o"] = board.Owner, ["n"] = board.Number, ["after"] = after } }.ToJsonString();
            using var req = new HttpRequestMessage(HttpMethod.Post, "graphql") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new("Bearer", cred.Token);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
            Interlocked.Increment(ref requests);
            using var resp = await http.SendAsync(req, ct);
            static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
            if (H(resp, "x-ratelimit-resource") is { } res) observe?.Invoke(res,H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
            if (!resp.IsSuccessStatusCode) throw new BoardReadException($"GitHub answered {(int)resp.StatusCode} to the board read");
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (root?["errors"] is JsonArray { Count: > 0 } errs) throw new BoardReadException($"GitHub answered GraphQL errors to the board read ({errs[0]?["type"]})");
            var items = root?["data"]?["organization"]?["projectV2"]?["items"] ?? throw new BoardReadException("the board was not found");
            foreach (var n in items["nodes"] as JsonArray ?? [])
            {
                var content = n?["content"];
                if (content?["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number)) continue;   // a draft card, a pull request without number, a redacted card
                if (!RepoId.TryParse(content["repository"]?["nameWithOwner"]?.ToString(), out var repo)) continue;
                var labels = withLabels ? (content["labels"]?["nodes"] as JsonArray ?? []).Select(l => l?["name"]?.ToString() ?? "").Where(l => l.Length > 0).ToList() : null;
                cards.Add(new BoardCard(repo, number, n?["fieldValueByName"]?["name"]?.ToString(), content["state"]?.ToString() == "OPEN", content["title"]?.ToString() ?? "", labels));
            }
            if (items["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true) return cards;
            after = items["pageInfo"]?["endCursor"]?.ToString();
        }
        throw new BoardReadException($"the board has more than {MaxPages * 100} cards; the read was cut");
    }
}
