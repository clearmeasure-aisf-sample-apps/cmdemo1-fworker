using System.Text.Json.Nodes;
using Fworker.Domain;

namespace Fworker.Workflows.ReadModels;

/// <summary>Everything one verb needs to know about an issue and its card (<c>Get-ItemInfo</c>).</summary>
public sealed record ItemInfo(int Number, string NodeId, bool Open, string Title, string Body, string Url, string CreatedAt, IReadOnlyList<string> Labels, string? CardId, string? Column)
{
    public bool HasLabel(string name) => Ps.NameIn(name, Labels);
}

/// <summary>
/// The reads the read models share, ported from Board.ps1 and Prs.ps1: the item and its card, the state comment, the pull request of an item and its files, and the docs-only test over them. All of them read the home repository through
/// <see cref="RuleGitHub"/> and answer, or refuse, as the script does.
/// </summary>
public sealed class ItemReads(RuleGitHub gh, IItemStateStore states)
{
    public RuleGitHub Gh => gh;
    public ReadModelSettings Settings => gh.Settings;

    const string ItemQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
          labels(first:50){ nodes{ name } }
          projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;

    /// <summary><c>Get-ItemInfo</c>: item-not-found (12) when GitHub has no such issue, out-of-scope (10) when the gateway's environment scope excludes it.</summary>
    public async Task<ItemInfo> Info(int item, CancellationToken ct)
    {
        var s = gh.Settings;
        var d = await gh.GraphQL(ItemQuery, new JsonObject { ["o"] = s.Owner, ["r"] = s.Repo, ["n"] = item }, ct);
        var i = Ps.At(d, "repository", "issue");
        if (i is null) throw new VerbRefusal(12, "item-not-found", $"issue #{item} not found");
        var labels = Ps.List(Ps.Prop(i, "labels"), "nodes").Select(n => Ps.Str(n, "name")).ToList();
        if (!s.InScope(labels))
            throw new VerbRefusal(10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{s.EnvLabel}'");
        var card = Ps.List(Ps.Prop(i, "projectItems"), "nodes").FirstOrDefault(n => Ps.At(n, "project", "number") is { } pn && Ps.ToInt(pn) == s.Project);
        string? status = null, cardId = null;
        if (card is not null) { cardId = Ps.Str(card, "id"); if (Ps.Prop(card, "fieldValueByName") is { } f) status = Ps.Str(f, "name"); }
        return new ItemInfo(Ps.ToInt(Ps.Prop(i, "number")), Ps.Str(i, "id"), Ps.Str(i, "state") == "OPEN", Ps.Str(i, "title"), Ps.Str(i, "body"), Ps.Str(i, "url"), Ps.Str(i, "createdAt"), labels, cardId, status);
    }

    /// <summary><c>Get-State</c>: the state object of the item.</summary>
    public Task<JsonObject> State(int item, CancellationToken ct) => states.Read(item, ct);

    /// <summary><c>Get-Pr</c>.</summary>
    public async Task<JsonNode> Pr(int number, CancellationToken ct) => await gh.Get($"{gh.RepoPath}/pulls/{number}", ct) ?? new JsonObject();

    /// <summary><c>Find-ItemPr</c>: the open pull request of the item by its branch name, else one named for it, else (a Copilot branch) the one the state records. Null when there is none.</summary>
    public async Task<JsonNode?> FindItemPr(int item, CancellationToken ct)
    {
        var name = gh.Settings.Name;
        var open = Ps.Items(await gh.Get($"{gh.RepoPath}/pulls?state=open&per_page=100", ct)).ToList();
        var mine = open.Where(p => Ps.Match(Ps.Str(Ps.Prop(p, "head"), "ref"), $"^{System.Text.RegularExpressions.Regex.Escape(name)}/{item}-")).ToList();
        if (mine.Count == 0) mine = open.Where(p => Ps.Match(Ps.Str(Ps.Prop(p, "head"), "ref"), $"/{item}-")).ToList();
        // a Copilot cloud agent's branch (copilot/<slug>) is the item's when the state records it (open-pr, Use-CopilotDraft)
        if (mine.Count == 0 && open.Any(p => IsCopilotBranch(p)))
        {
            JsonObject? sp = null;
            try { sp = await states.Read(item, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            var rec = Ps.Prop(sp, "pr");
            if (rec is not null && Ps.Prop(rec, "branch") is { } branch)
                mine = open.Where(p => Ps.Ieq(Ps.Str(Ps.Prop(p, "head"), "ref"), Ps.Text(branch)) && Ps.ToInt(Ps.Prop(p, "number")) == Ps.ToInt(Ps.Prop(rec, "number"))).ToList();
        }
        return mine.Count == 0 ? null : mine[0];
    }

    static bool IsCopilotBranch(JsonNode pr) => Ps.Str(Ps.Prop(pr, "head"), "ref").StartsWith("copilot/", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Test-PrBelongs</c>: the branch carries the item number, or (a Copilot branch) the state records this pull request, or the title names the item with a word boundary ('#12' is not '#123').</summary>
    public async Task<bool> PrBelongs(JsonNode pr, int item, CancellationToken ct)
    {
        var headRef = Ps.Str(Ps.Prop(pr, "head"), "ref");
        if (Ps.Match(headRef, $"/{item}-")) return true;
        if (IsCopilotBranch(pr))
        {
            JsonObject? sp = null;
            try { sp = await states.Read(item, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            var rec = Ps.Prop(sp, "pr");
            if (rec is not null && Ps.Prop(rec, "number") is { } n && Ps.ToInt(n) == Ps.ToInt(Ps.Prop(pr, "number"))) return true;
        }
        return Ps.Match(Ps.Str(pr, "title"), $"(^|[^0-9])#{item}([^0-9]|$)");
    }

    /// <summary><c>Get-PrFiles</c>: every changed file of a pull request, a hundred to a page.</summary>
    public async Task<IReadOnlyList<JsonNode>> PrFiles(int number, CancellationToken ct)
    {
        var all = new List<JsonNode>(); var page = 1;
        while (true)
        {
            var chunk = Ps.Items(await gh.Get($"{gh.RepoPath}/pulls/{number}/files?per_page=100&page={page}", ct)).ToList();
            all.AddRange(chunk); page++;
            if (chunk.Count != 100) break;
        }
        return all;
    }

    /// <summary><c>Test-DocsOnlyFiles</c> over the files of a pull request.</summary>
    public bool DocsOnlyFiles(IEnumerable<JsonNode> files) => Fworker.Domain.DocsOnly.FilesAreDocumentation(files.Select(f => Ps.Str(f, "filename")).ToList(), gh.Settings.DocsOnly.Paths);

    /// <summary><c>Test-ItemDocsOnly</c>: what the design step recorded in the state, else (an old item past its design column) the marker in the body.</summary>
    public bool ItemDocsOnly(JsonObject state, ItemInfo? info)
    {
        bool? designDocsOnly = Ps.At(state, "design", "docs_only") is { } d ? Ps.Bool(d) : null;
        DocsOnlyItem? item = info is null ? null : new DocsOnlyItem(info.Column, info.Body, ParseCreated(info.CreatedAt), info.Labels);
        return Fworker.Domain.DocsOnly.ItemIsDocsOnly(designDocsOnly, item, gh.Settings.DocsOnly);
    }

    static DateTimeOffset? ParseCreated(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary><c>Get-ServerLogin</c>: the login the gateway writes as. An App installation token cannot ask GitHub who it is (GET /user is 403 for it): the login of an App is its slug and [bot].</summary>
    public async Task<string> ServerLogin(CancellationToken ct)
    {
        var env = gh.Settings.Env;
        if (env("FACTORY_AUTHOR_APP") == "1")
        {
            var slug = env("FACTORY_FAKE_APPS") is { Length: > 0 } ? "factory-worker-author" : env("FACTORY_APP_AUTHOR_SLUG") ?? "";
            return slug + "[bot]";
        }
        return Ps.Str(await gh.Get("user", ct), "login");
    }
}
