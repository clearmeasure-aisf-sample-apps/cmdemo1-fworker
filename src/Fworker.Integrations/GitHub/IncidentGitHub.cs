using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;

namespace Fworker.Integrations.GitHub;

// W8.1: what the incident saga writes to GitHub: the incident issue and its comments, the hotfix item and its place on the board. Every call is idempotent by a marker (an HTML comment) that the first call puts into the
// issue or comment: the same command run again finds what the first made and makes nothing twice. A fault that a later try gets past is an HttpRequestException (as the projections raise it); a refusal for good is a
// ProjectionRefusedException. The gateway's adapter (IncidentPort) maps both to the Workflows port's exceptions. It needs no more than the issues API and the Projects v2 mutation that adds an issue to the board.

public sealed class IncidentGitHub(GitHubClientFactory clients, HttpClient http, IGitHubCredentialSource credentials, Func<BoardRef?> board)
{
    const string AddToBoardQuery = "query($o:String!,$r:String!,$n:Int!,$g:String!,$p:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id } } organization(login:$g){ projectV2(number:$p){ id } } }";
    const string AddToBoardMutation = "mutation($p:ID!,$c:ID!){ addProjectV2ItemById(input:{projectId:$p,contentId:$c}){ item{ id } } }";

    /// <summary>The marker of an issue or comment: "kind" is "incident", "hotfix" or "comment", and the key names what it is for.</summary>
    public static string Marker(string kind, string key) => $"<!-- fworker-{kind}:{key} -->";

    /// <summary>The number of the issue that carries the marker among the repository's issues of that label (newest first, two pages), or 0.</summary>
    public async Task<int> Find(RepoId repo, string marker, string label, CancellationToken ct)
    {
        var client = await clients.For(repo, ct);
        for (var page = 1; page <= 2; page++)
        {
            using var resp = await client.Send(HttpMethod.Get, $"issues?labels={Uri.EscapeDataString(label)}&state=all&per_page=100&page={page}&sort=created&direction=desc", null, ct);
            var text = await ProjectionHttp.Read(resp, "GET issues", false, ct);
            if (JsonNode.Parse(text ?? "[]") is not JsonArray array) return 0;
            foreach (var issue in array)
                if (issue is JsonObject o && o["pull_request"] is null && o["body"]?.ToString() is { } body && body.Contains(marker, StringComparison.Ordinal) && o["number"] is JsonValue n && n.TryGetValue<int>(out var number))
                    return number;
            if (array.Count < 100) break;
        }
        return 0;
    }

    /// <summary>Creates the issue unless one with the marker exists; answers its number.</summary>
    public async Task<int> EnsureIssue(RepoId repo, string marker, string title, string body, IReadOnlyList<string> labels, string findLabel, CancellationToken ct)
    {
        if (await Find(repo, marker, findLabel, ct) is > 0 and var found) return found;
        var client = await clients.For(repo, ct);
        var json = new JsonObject { ["title"] = title, ["body"] = body.TrimEnd() + "\n\n" + marker, ["labels"] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) };
        using var resp = await client.Send(HttpMethod.Post, "issues", new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await ProjectionHttp.Read(resp, "POST issues", false, ct);
        return JsonNode.Parse(text ?? "{}")?["number"] is JsonValue v && v.TryGetValue<int>(out var number) && number > 0 ? number : throw new ProjectionRefusedException("GitHub answered no issue number to the creation of the issue");
    }

    /// <summary>Writes the comment unless one with its marker exists on the issue (the last hundred comments are looked at).</summary>
    public async Task Comment(RepoId repo, int number, string commentKey, string body, CancellationToken ct)
    {
        var client = await clients.For(repo, ct);
        var marker = Marker("comment", commentKey);
        using (var list = await client.Send(HttpMethod.Get, $"issues/{number}/comments?per_page=100", null, ct))
        {
            var text = await ProjectionHttp.Read(list, "GET comments", false, ct);
            if (JsonNode.Parse(text ?? "[]") is JsonArray array && array.Any(c => c?["body"]?.ToString().Contains(marker, StringComparison.Ordinal) == true)) return;
        }
        var json = new JsonObject { ["body"] = body.TrimEnd() + "\n\n" + marker };
        using var resp = await client.Send(HttpMethod.Post, $"issues/{number}/comments", new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        await ProjectionHttp.Read(resp, "POST comment", false, ct);
    }

    /// <summary>Closes the issue (completed). An issue that is closed already is left as it is.</summary>
    public async Task Close(RepoId repo, int number, CancellationToken ct)
    {
        var client = await clients.For(repo, ct);
        var json = new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" };
        using var resp = await client.Send(HttpMethod.Patch, $"issues/{number}", new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        await ProjectionHttp.Read(resp, "PATCH issue", false, ct);
    }

    /// <summary>
    /// Puts the issue on the board (the mutation does nothing for an item that is on it already). Nothing when the gateway knows no board. The card's column is the saga's projection, which refuses an item that is
    /// not on the board, so the hotfix item is put there before its saga starts.
    /// </summary>
    public async Task AddToBoard(ItemId item, CancellationToken ct)
    {
        if (board() is not { } b) return;
        var cred = await credentials.For(b.Owner, ct) ?? throw new NoInstallationException(b.Owner);
        var data = await ProjectionHttp.GraphQl(http, cred, AddToBoardQuery,
            new JsonObject { ["o"] = item.Repository.Owner, ["r"] = item.Repository.Repo, ["n"] = item.Number, ["g"] = b.Owner, ["p"] = b.Number }, ct);
        var content = data["repository"]?["issue"]?["id"]?.ToString() ?? throw new ProjectionRefusedException($"issue {item} not found");
        var project = data["organization"]?["projectV2"]?["id"]?.ToString() ?? throw new ProjectionRefusedException("the board was not found");
        await ProjectionHttp.GraphQl(http, cred, AddToBoardMutation, new JsonObject { ["p"] = project, ["c"] = content }, ct);
    }
}
