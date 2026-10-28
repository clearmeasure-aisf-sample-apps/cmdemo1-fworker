using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.3: the board column of an item as a versioned projection, through the Projects v2 GraphQL API, with the calls of Move-Card and Get-ProjectMeta of Board.ps1. The project's
/// id, the Status field's id and its option ids are looked up once and cached for <see cref="MetaTtl"/> (10 minutes, as v1's project-meta.json); a column the cache does not know
/// refreshes it once before it is refused. An item that is not on the board is refused (v1: not-on-board), never added. A card that is in the column already is not written.
/// The HttpClient's BaseAddress is the API root (GITHUB_API_URL or api.github.com).
/// </summary>
public sealed class BoardColumnProjection(HttpClient http, IGitHubCredentialSource credentials, BoardRef board, IProjectionVersions versions, ProjectionEcho echo,
    Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(10);

    /// <summary>The mutation of Move-Card, character for character (the golden writes record it).</summary>
    public const string MoveMutation = "mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }";
    const string MetaQuery = "query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }";
    const string CardQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id
          projectItems(first:20){ nodes{ id project{ id } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;

    sealed record Meta(string ProjectId, string FieldId, Dictionary<string, (string Name, string Id)> Options, DateTimeOffset At);

    readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim gate = new(1, 1);
    Meta? meta;

    public async Task<ProjectionOutcome> Move(ItemId item, long version, string column, CancellationToken ct = default)
    {
        var key = item.ToString();
        if (version <= await versions.Applied(key, ProjectionTargets.Card, ct)) return ProjectionOutcome.Stale;
        var cred = await credentials.For(board.Owner, ct) ?? throw new NoInstallationException(board.Owner);
        var (m, fresh) = await LoadMeta(cred, refresh: false, ct);
        if (!m.Options.ContainsKey(column) && !fresh) (m, _) = await LoadMeta(cred, refresh: true, ct);   // (a column added to the board since the cache was read)
        if (!m.Options.TryGetValue(column, out var option)) throw new ProjectionRefusedException($"board has no column '{column}'");

        var data = await ProjectionHttp.GraphQl(http, cred, CardQuery,
            new JsonObject { ["o"] = item.Repository.Owner, ["r"] = item.Repository.Repo, ["n"] = item.Number }, ct);
        var issue = data["repository"]?["issue"] ?? throw new ProjectionRefusedException($"issue {item} not found");
        var card = (issue["projectItems"]?["nodes"] as JsonArray ?? []).FirstOrDefault(n => n?["project"]?["id"]?.ToString() == m.ProjectId)
            ?? throw new ProjectionRefusedException($"issue {item} is not on the board");

        var changed = !string.Equals(card["fieldValueByName"]?["name"]?.ToString(), option.Name, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            // the echo is recorded before the write: the card's event can arrive before the answer does; a write that fails takes the record back, so a person's later move to
            // that column is news and not an echo
            var mark = echo.Record(key, ProjectionTargets.Card, version, ProjectionEcho.ColumnFingerprint(option.Name));
            try
            {
                await ProjectionHttp.GraphQl(http, cred, MoveMutation,
                    new JsonObject { ["p"] = m.ProjectId, ["i"] = card["id"]!.ToString(), ["f"] = m.FieldId, ["o"] = option.Id }, ct);
            }
            catch { echo.Unrecord(mark); throw; }
        }
        await versions.Advance(key, ProjectionTargets.Card, version, now(), ct);
        return changed ? ProjectionOutcome.Applied : ProjectionOutcome.Unchanged;
    }

    async Task<(Meta, bool Fresh)> LoadMeta(GitHubCredential cred, bool refresh, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && meta is { } cached && now() - cached.At < MetaTtl) return (cached, false);
            var data = await ProjectionHttp.GraphQl(http, cred, MetaQuery, new JsonObject { ["o"] = board.Owner, ["n"] = board.Number }, ct);
            var project = data["organization"]?["projectV2"] ?? throw new ProjectionRefusedException("the board was not found");
            var status = (project["fields"]?["nodes"] as JsonArray ?? []).FirstOrDefault(f => f?["name"]?.ToString() == "Status" && f["options"] is JsonArray)
                ?? throw new ProjectionRefusedException("the board has no Status field");
            var options = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in (JsonArray)status["options"]!) if (o?["name"]?.ToString() is { } name && o["id"]?.ToString() is { } id) options[name] = (name, id);
            return (meta = new Meta(project["id"]!.ToString(), status["id"]!.ToString(), options, now()), true);
        }
        finally { gate.Release(); }
    }
}
