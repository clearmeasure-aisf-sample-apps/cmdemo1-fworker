using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>Everything one verb needs to know about an issue and its card (<c>Get-ItemInfo</c>).</summary>
public sealed record ItemInfo(int Number, string NodeId, bool Open, string Title, string Body, string Url, string CreatedAt, IReadOnlyList<string> Labels, string? CardId, string? Column)
{
    /// <summary><c>Test-Label</c>.</summary>
    public bool HasLabel(string name) => Ps.NameIn(name, Labels);
    public string ColumnText => Column ?? "";
}

/// <summary>A card of the board with the facts of its issue (<c>Get-BoardList</c>).</summary>
public sealed record ItemCard(string? CardId, int Number, bool Open, string Title, string Body, string? Column, IReadOnlyList<string> Labels);

/// <summary>
/// The board and the issue, as Board.ps1 reads and writes them: the Projects v2 meta (the project, the Status field and its options, kept for ten minutes as the script's project-meta.json is), the facts of an issue and its
/// card, the move of a card, the cards of the board, and the comments the factory writes (with the footer of the AI session the verb runs for).
/// </summary>
public sealed class ItemBoard(ItemGitHub gh, IItemClock clock)
{
    public static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(10);
    const string MetaQuery = "query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }";
    const string ItemQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
          labels(first:50){ nodes{ name } }
          projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;
    const string MoveMutation = "mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }";
    const string BoardQuery = """
        query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
          nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
            content{ ... on Issue{ number state title body repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } } } } } }
        """;

    sealed record Meta(string ProjectId, string FieldId, IReadOnlyList<KeyValuePair<string, string>> Options, DateTime At);

    readonly SemaphoreSlim gate = new(1, 1);
    Meta? meta;

    ItemVerbConfig Cfg => gh.Config;

    /// <summary><c>Get-ProjectMeta</c>, kept for <see cref="MetaTtl"/>.</summary>
    async Task<Meta> ProjectMeta(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (meta is { } m && clock.UtcNow - m.At < MetaTtl) return m;
            var d = await gh.GraphQL(MetaQuery, new JsonObject { ["o"] = Cfg.Owner, ["n"] = Cfg.Project }, ct);
            var p = Ps.Get(Ps.Get(d, "organization"), "projectV2");
            var nodes = Ps.Get(Ps.Get(p, "fields"), "nodes") as JsonArray ?? [];
            var status = nodes.FirstOrDefault(f => f is JsonObject o && o.TryGetPropertyValue("name", out var n) && n is not null && Ps.Text(n) == "Status");
            var options = new List<KeyValuePair<string, string>>();
            foreach (var o in Ps.Get(status, "options") as JsonArray ?? []) options.Add(new(Ps.Text(o, "name"), Ps.Text(o, "id")));
            return meta = new Meta(Ps.Text(p, "id"), Ps.Text(status, "id"), options, clock.UtcNow);
        }
        finally { gate.Release(); }
    }

    /// <summary><c>Get-BoardColumns</c>: the Status options in the board's order.</summary>
    public async Task<IReadOnlyList<string>> Columns(CancellationToken ct) => (await ProjectMeta(ct)).Options.Select(o => o.Key).ToList();

    /// <summary><c>Get-NextColumn</c>: null at the last column and for a column the board does not have.</summary>
    public async Task<string?> NextColumn(string column, CancellationToken ct)
    {
        var cols = await Columns(ct);
        for (var i = 0; i < cols.Count; i++) if (Ps.Eq(cols[i], column)) return i + 1 < cols.Count ? cols[i + 1] : null;
        return null;
    }

    /// <summary><c>Get-LastColumn</c>.</summary>
    public async Task<string> LastColumn(CancellationToken ct) { var cols = await Columns(ct); return cols[^1]; }

    /// <summary><c>Get-ColumnIndex</c>: the place in the AI columns, -1 when it is none of them.</summary>
    public int ColumnIndex(string name)
    {
        var ai = Cfg.ColumnsAi;
        for (var i = 0; i < ai.Count; i++) if (Ps.Eq(ai[i], name)) return i;
        return -1;
    }

    /// <summary><c>Get-ItemInfo</c>.</summary>
    public async Task<ItemInfo> GetItemInfo(int item, CancellationToken ct)
    {
        var d = await gh.GraphQL(ItemQuery, new JsonObject { ["o"] = Cfg.Owner, ["r"] = Cfg.Repo, ["n"] = item }, ct);
        var i = Ps.Get(Ps.Get(d, "repository"), "issue");
        if (i is null) throw new ItemRefusal(12, "item-not-found", $"issue #{item} not found");
        var labels = (Ps.Get(Ps.Get(i, "labels"), "nodes") as JsonArray ?? []).Select(n => Ps.Text(n, "name")).ToList();
        if (!Cfg.InScope(labels))
            throw new ItemRefusal(10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{Cfg.EnvLabel}'");
        var card = (Ps.Get(Ps.Get(i, "projectItems"), "nodes") as JsonArray ?? []).FirstOrDefault(c => c is not null && Ps.Has(Ps.Get(c, "project"), "number") && Ps.Int(Ps.Get(c, "project"), "number") == Cfg.Project);
        string? status = null, cardId = null;
        if (card is not null) { cardId = Ps.Text(card, "id"); if (Ps.Get(card, "fieldValueByName") is { } fv) status = Ps.Text(fv, "name"); }
        return new ItemInfo(Ps.Int(i, "number"), Ps.Text(i, "id"), Ps.Text(i, "state") == "OPEN", Ps.Text(i, "title"), Ps.Text(i, "body"), Ps.Text(i, "url"), Ps.Text(i, "createdAt"), labels, cardId, status);
    }

    /// <summary><c>Move-Card</c>.</summary>
    public async Task MoveCard(ItemInfo info, string column, CancellationToken ct)
    {
        var m = await ProjectMeta(ct);
        var option = m.Options.FirstOrDefault(o => Ps.Eq(o.Key, column));
        if (option.Key is null) throw new ItemRefusal(10, "unknown-column", $"board has no column '{column}'");
        if (string.IsNullOrEmpty(info.CardId)) throw new ItemRefusal(10, "not-on-board", $"issue #{info.Number} is not on the board");
        await gh.GraphQL(MoveMutation, new JsonObject { ["p"] = m.ProjectId, ["i"] = info.CardId, ["f"] = m.FieldId, ["o"] = option.Value }, ct);
    }

    /// <summary><c>Get-BoardList</c>: the cards of this repository the environment scope sees, one paged query.</summary>
    public async Task<IReadOnlyList<ItemCard>> GetBoardList(CancellationToken ct)
    {
        string? after = null; var all = new List<ItemCard>();
        while (true)
        {
            var d = await gh.GraphQL(BoardQuery, new JsonObject { ["o"] = Cfg.Owner, ["n"] = Cfg.Project, ["after"] = after }, ct);
            var items = Ps.Get(Ps.Get(Ps.Get(d, "organization"), "projectV2"), "items");
            foreach (var n in Ps.Get(items, "nodes") as JsonArray ?? [])
            {
                var content = Ps.Get(n, "content");
                if (content is null || !Ps.Has(content, "number")) continue;
                // a card of another repository linked to the board is never seen here
                var repo1 = Ps.Get(content, "repository") is { } rp ? Ps.Text(rp, "nameWithOwner") : "";
                if (!Ps.Eq(repo1, $"{Cfg.Owner}/{Cfg.Repo}")) continue;
                var labels = (Ps.Get(Ps.Get(content, "labels"), "nodes") as JsonArray ?? []).Select(x => Ps.Text(x, "name")).ToList();
                if (!Cfg.InScope(labels)) continue;
                all.Add(new ItemCard(Ps.Has(n, "id") ? Ps.Text(n, "id") : null, Ps.Int(content, "number"), Ps.Text(content, "state") == "OPEN", Ps.Text(content, "title"), Ps.Text(content, "body"),
                    Ps.Get(n, "fieldValueByName") is { } fv ? Ps.Text(fv, "name") : null, labels));
            }
            var page = Ps.Get(items, "pageInfo");
            if (!Ps.TruthyKey(page, "hasNextPage")) return all;
            after = Ps.Has(page, "endCursor") ? Ps.Text(page, "endCursor") : null;
        }
    }

    /// <summary><c>Get-Comments</c>: every comment of the issue.</summary>
    public Task<List<JsonNode>> GetComments(int item, CancellationToken ct) => gh.Pages($"{Cfg.RepoPath}/issues/{item}/comments", ct);

    /// <summary><c>Get-SessionFooter</c>: the last line of a comment written for an AI session's verb, or "".</summary>
    public static string SessionFooter(JsonObject? session)
    {
        if (session is null) return "";
        var name = Ps.Text(session, "name");
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)) return "";
        var url = Ps.Text(session, "url");
        if (url.Length > 0 && Regex.IsMatch(url, @"^https?://[^\s?#()<>\[\]]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return $"AI session: [{name}]({url})";
        return $"AI session: {name}";
    }

    /// <summary><c>Add-SessionFooter</c>: the text with the footer as its last line, once.</summary>
    public static string WithFooter(string body, JsonObject? session)
    {
        var f = SessionFooter(session);
        if (f.Length == 0 || body.TrimEnd().EndsWith(f, StringComparison.Ordinal)) return body;
        return $"{body}\n\n{f}";
    }

    /// <summary><c>Add-Comment</c>.</summary>
    public async Task AddComment(int item, string text, JsonObject? session, CancellationToken ct)
    {
        var name = Cfg.Name;
        var body = $"\U0001F916 {name}: {WithFooter(text, session)}\n\n<!-- factory-journal:{name} -->";
        await gh.Json("POST", $"{Cfg.RepoPath}/issues/{item}/comments", new JsonObject { ["body"] = body }, ct);
    }

    /// <summary><c>Remove-ClaimRef</c>: the claim ref of the item, gone (404 and 422 are no failure).</summary>
    public async Task RemoveClaimRef(int item, CancellationToken ct) =>
        await gh.Allow("DELETE", $"{Cfg.RepoPath}/git/refs/{ClaimRefPath}/{item}", [404, 422], null, ct);

    /// <summary><c>Get-RefPath</c>.</summary>
    public string ClaimRefPath => $"factory/{Cfg.Name}/claims";

    /// <summary>The text of the state marker (<c>Get-StateMarker</c>), for the comments that are not the state.</summary>
    public string StateMarker => $"<!-- factory-state:{Cfg.Name} v1 -->";
}
