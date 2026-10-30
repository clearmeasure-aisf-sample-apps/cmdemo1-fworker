using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4: the helpers of the rule scripts that the delivery verbs use, ported one for one (Core.ps1 Get-Cfg and Get-RepoPath; Board.ps1 Get-ItemInfo, Get-ProjectMeta, Move-Card, Close-Issue, Add-Comment, Get-Park, Set-Park;
// Prs.ps1 Find-ItemPr, Get-Pr, Get-PrFiles, Get-ChecksFor, Get-MasterIgnoredChecks, Get-PublicChecks; Verbs.Item.ps1 Remove-ClaimRef). The same GitHub calls in the same order, because the golden writes are the oracle.

/// <summary>The board's Status column options and ids, read once and kept ten minutes (v1: project-meta.json). Shared by every call of the gateway.</summary>
public sealed class BoardMetaCache
{
    public sealed record Meta(string ProjectId, string FieldId, IReadOnlyList<(string Name, string Id)> Options, DateTime At);

    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    readonly object gate = new();
    Meta? meta;

    public Meta? Current(DateTime now) { lock (gate) return meta is { } m && now - m.At < Ttl ? m : null; }
    public void Set(Meta m) { lock (gate) meta = m; }
}

/// <summary>A pull request check run summary (Get-ChecksFor): what the verbs read of the checks of a commit.</summary>
public sealed class ChecksFor
{
    public string Status { get; init; } = "pending";
    public int Total { get; init; }
    public int Failed { get; init; }
    public List<string> FailedChecks { get; init; } = [];
    public List<string> PendingChecks { get; init; } = [];
    public List<KeyValuePair<string, string>> Gates { get; init; } = [];
    public Dictionary<string, JsonNode> Runs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> NongateFailed { get; init; } = [];

    /// <summary>Get-PublicChecks.</summary>
    public JsonObject Public()
    {
        var gates = new JsonObject(); foreach (var g in Gates) gates[g.Key] = g.Value;
        return new JsonObject
        {
            ["status"] = Status, ["total"] = Total, ["failed"] = Failed, ["failed_checks"] = J.Node(FailedChecks), ["pending_checks"] = J.Node(PendingChecks),
            ["gates"] = gates, ["nongate_failed"] = J.Node(NongateFailed),
        };
    }
}

/// <summary>The rule scripts' helpers over the ports. One per verb call (it carries the AI session whose footer ends a comment); the board's cache is shared.</summary>
public sealed class RuleKit(IGhTransport gh, IItemStateStore store, JsonObject config, BoardMetaCache boardMeta, Func<string, string?> env, Func<DateTime>? now = null, JsonNode? session = null)
{
    readonly Func<DateTime> clock = now ?? (() => DateTime.UtcNow);

    public IGhTransport Gh => gh;
    public JsonObject Cfg => config;
    public DateTime UtcNow => clock();

    /// <summary>The same kit for the verb call of an AI session (its link ends the comments the call writes, Add-SessionFooter).</summary>
    public RuleKit ForSession(JsonNode? s) => new(gh, store, config, boardMeta, env, clock, s);

    // ---- configuration (Get-Cfg) ----------------------------------------------------------------------------------------------------------------------------

    public string Name => J.S(config, "name");
    public string Owner => J.S(config, "owner");
    public string Repo => J.S(config, "repo");
    public string DefaultBranch => J.S(config, "defaultBranch");
    /// <summary>Get-RepoPath.</summary>
    public string RepoPath => $"repos/{Owner}/{Repo}";
    public JsonObject Timing => J.Obj(config, "timing");
    public double TimingNumber(string key, double fallback = 0) => J.Has(Timing, key) && Timing[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.TryParse(J.S(Timing, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : fallback;
    public string Column(string key) => J.S(J.Obj(config, "columns"), key);
    public string[] CiGates => J.Strings(config["ciGates"]).Where(g => g.Length > 0).ToArray();

    /// <summary>Get-Cfg with the test overrides of the rule script: FACTORY_TIMING_&lt;name&gt; replaces a number of the timing block.</summary>
    public static JsonObject WithTimingOverrides(JsonObject root, Func<string, string?> env)
    {
        var c = (JsonObject)root.DeepClone();
        if (c["timing"] is JsonObject t)
            foreach (var k in t.Select(kv => kv.Key).ToList())
                if (env("FACTORY_TIMING_" + k) is { Length: > 0 } v && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) t[k] = d;
        return c;
    }

    // ---- the board (Board.ps1) ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Get-EnvLabel: '' with scoping off.</summary>
    public string EnvLabel()
    {
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) e = "tdd";
        bool on;
        var scope = env("FACTORY_ENV_SCOPE");
        if (scope is "0" or "1") on = scope == "1";
        else on = J.Has(config, "envScope") && J.Items(config["envScope"]).Any(x => J.S(x).ToLowerInvariant() == e);
        return on ? "env:" + e : "";
    }

    bool InScope(IEnumerable<string> labels)
    {
        var mine = EnvLabel();
        if (mine.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && string.Equals(envs[0], mine, StringComparison.OrdinalIgnoreCase);
    }

    const string ItemQuery = """
        query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
          labels(first:50){ nodes{ name } }
          projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
        """;

    /// <summary>Get-ItemInfo.</summary>
    public async Task<ItemInfo> GetItemInfo(int item, CancellationToken ct)
    {
        var d = await GhApi.GraphQl(gh, ItemQuery.Trim(), new JsonObject { ["o"] = Owner, ["r"] = Repo, ["n"] = item }, ct);
        var i = d["repository"]?["issue"];
        if (i is not JsonObject) throw new VerbReject(12, "item-not-found", $"issue #{item} not found");
        var labels = J.Items(i["labels"]?["nodes"]).Select(n => J.S(n, "name")).ToList();
        if (!InScope(labels))
            throw new VerbReject(10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{EnvLabel()}'");
        var project = J.I(config, "project");
        var card = J.Items(i["projectItems"]?["nodes"]).FirstOrDefault(n => J.I(n?["project"], "number") == project);
        string? status = null, cardId = null;
        if (card is not null) { cardId = J.S(card, "id"); if (card["fieldValueByName"] is JsonObject f) status = J.S(f, "name"); }
        return new ItemInfo
        {
            Number = J.I(i, "number"), NodeId = J.S(i, "id"), Open = J.S(i, "state") == "OPEN", Title = J.S(i, "title"), Body = J.S(i, "body"), Url = J.S(i, "url"), CreatedAt = J.S(i, "createdAt"),
            Labels = labels, CardId = cardId, Column = status,
        };
    }

    const string MetaQuery = "query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }";

    /// <summary>Get-ProjectMeta.</summary>
    public async Task<BoardMetaCache.Meta> ProjectMeta(CancellationToken ct)
    {
        if (boardMeta.Current(UtcNow) is { } cached) return cached;
        var d = await GhApi.GraphQl(gh, MetaQuery, new JsonObject { ["o"] = Owner, ["n"] = J.I(config, "project") }, ct);
        var p = d["organization"]?["projectV2"];
        var f = J.Items(p?["fields"]?["nodes"]).FirstOrDefault(n => n is JsonObject o && J.Has(o, "name") && J.S(o, "name") == "Status");
        var options = J.Items(f?["options"]).Select(o => (J.S(o, "name"), J.S(o, "id"))).ToList();
        var m = new BoardMetaCache.Meta(J.S(p, "id"), J.S(f, "id"), options, UtcNow);
        boardMeta.Set(m);
        return m;
    }

    /// <summary>Get-LastColumn: the board's last Status option.</summary>
    public async Task<string> LastColumn(CancellationToken ct) { var m = await ProjectMeta(ct); return m.Options[^1].Name; }

    /// <summary>Move-Card.</summary>
    public async Task MoveCard(ItemInfo info, string column, CancellationToken ct)
    {
        var meta = await ProjectMeta(ct);
        var option = meta.Options.FirstOrDefault(o => string.Equals(o.Name, column, StringComparison.OrdinalIgnoreCase));
        if (option.Id is null) throw new VerbReject(10, "unknown-column", $"board has no column '{column}'");
        if (string.IsNullOrEmpty(info.CardId)) throw new VerbReject(10, "not-on-board", $"issue #{info.Number} is not on the board");
        const string m = "mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }";
        await GhApi.GraphQl(gh, m, new JsonObject { ["p"] = meta.ProjectId, ["i"] = info.CardId, ["f"] = meta.FieldId, ["o"] = option.Id }, ct);
    }

    /// <summary>Close-Issue.</summary>
    public Task CloseIssue(int item, CancellationToken ct) =>
        GhApi.Call(gh, HttpMethod.Patch, $"{RepoPath}/issues/{item}", new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" }, ct);

    /// <summary>Get-SessionFooter.</summary>
    string SessionFooter()
    {
        if (session is not JsonObject s) return "";
        var name = J.S(s, "name");
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")) return "";
        var u = J.S(s, "url");
        if (u.Length > 0 && Regex.IsMatch(u, @"^https?://[^\s?#()<>\[\]]+$")) return $"AI session: [{name}]({u})";
        return $"AI session: {name}";
    }

    /// <summary>Add-SessionFooter.</summary>
    public string WithSessionFooter(string body)
    {
        var f = SessionFooter();
        if (f.Length == 0 || body.TrimEnd().EndsWith(f, StringComparison.Ordinal)) return body;
        return $"{body}\n\n{f}";
    }

    /// <summary>Add-Comment.</summary>
    public Task AddComment(int item, string text, CancellationToken ct) =>
        GhApi.Call(gh, HttpMethod.Post, $"{RepoPath}/issues/{item}/comments",
            new JsonObject { ["body"] = $"\U0001F916 {Name}: {WithSessionFooter(text)}\n\n<!-- factory-journal:{Name} -->" }, ct);

    // ---- the state comment (Get-State, Save-State, Get-Park, Set-Park) -------------------------------------------------------------------------------------

    public Task<StateHolder> GetState(int item, CancellationToken ct) => store.Read(item, ct);
    public Task SaveState(int item, StateHolder holder, CancellationToken ct) => store.Save(item, holder, ct);

    /// <summary>Get-Park: the park, unless it expired.</summary>
    public JsonObject? GetPark(JsonObject state)
    {
        if (!J.Has(state, "park")) return null;
        var park = state["park"] as JsonObject;
        var until = J.FromIso(park, "until");
        if (until is { } u && u < UtcNow) return null;
        return park;
    }

    /// <summary>Set-Park.</summary>
    public void SetPark(JsonObject state, string reason, double minutes) =>
        state["park"] = new JsonObject { ["reason"] = reason, ["at"] = J.Iso(UtcNow), ["until"] = J.Iso(UtcNow.AddMinutes(minutes)) };

    /// <summary>Test-ClaimLive.</summary>
    public bool ClaimLive(JsonObject state)
    {
        if (!J.Has(state, "claim")) return false;
        var u = J.FromIso(state["claim"], "lease_until");
        return u is { } until && until > UtcNow;
    }

    /// <summary>Remove-ClaimRef.</summary>
    public Task RemoveClaimRef(int item, CancellationToken ct) =>
        GhApi.Call(gh, HttpMethod.Delete, $"{RepoPath}/git/refs/factory/{Name}/claims/{item}", null, ct, [404, 422]);

    // ---- pull requests (Prs.ps1) ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Get-Pr.</summary>
    public async Task<JsonNode> GetPr(int number, CancellationToken ct) => (await GhApi.Get(gh, $"{RepoPath}/pulls/{number}", ct)).Body ?? new JsonObject();

    /// <summary>Find-ItemPr: the item's open pull request, or null.</summary>
    public async Task<JsonNode?> FindItemPr(int item, CancellationToken ct)
    {
        var open = J.Items((await GhApi.Get(gh, $"{RepoPath}/pulls?state=open&per_page=100", ct)).Body).Where(p => p is not null).ToList();
        string Ref(JsonNode? p) => J.S(p?["head"], "ref");
        var mine = open.Where(p => Regex.IsMatch(Ref(p), $"^{Regex.Escape(Name)}/{item}-", RegexOptions.IgnoreCase)).ToList();
        if (mine.Count == 0) mine = open.Where(p => Regex.IsMatch(Ref(p), $"/{item}-", RegexOptions.IgnoreCase)).ToList();
        // a Copilot cloud agent's branch (copilot/<slug>) is the item's when the state records it
        if (mine.Count == 0 && open.Any(p => Ref(p).StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? sp = null;
            try { sp = (await GetState(item, ct)).State; } catch (Exception e) when (e is VerbReject or HttpRequestException) { }
            if (sp is not null && J.Has(sp, "pr") && J.Has(sp["pr"], "branch"))
                mine = open.Where(p => string.Equals(Ref(p), J.S(sp["pr"], "branch"), StringComparison.OrdinalIgnoreCase) && J.I(p, "number") == J.I(sp["pr"], "number")).ToList();
        }
        return mine.Count == 0 ? null : mine[0];
    }

    /// <summary>Get-PrFiles.</summary>
    public async Task<List<JsonNode?>> GetPrFiles(int number, CancellationToken ct)
    {
        var all = new List<JsonNode?>();
        for (var page = 1; ; page++)
        {
            var chunk = J.Items((await GhApi.Get(gh, $"{RepoPath}/pulls/{number}/files?per_page=100&page={page}", ct)).Body).ToList();
            all.AddRange(chunk);
            if (chunk.Count != 100) return all;
        }
    }

    /// <summary>The docs-only rules' settings (D4, Fworker.Domain.DocsOnly) from this configuration.</summary>
    public Fworker.Domain.DocsOnlySettings DocsSettings() => new(
        J.Strings(config["docsOnlyPaths"]), J.S(config, "docsOnlyMarker"), J.FromIso(Timing, "docsOnlyLegacyBefore") is { } cut ? new DateTimeOffset(DateTime.SpecifyKind(cut, DateTimeKind.Utc)) : null,
        J.Obj(config, "requiredBodySection").Select(kv => kv.Key).ToList(), J.S(config, "videoLabel"), J.Strings(config["masterCodeChecks"]));

    /// <summary>Test-ItemDocsOnly: what the design step recorded in the state, else (for a legacy item past its design column) the marker in the body.</summary>
    public bool ItemDocsOnly(JsonObject state, ItemInfo? info)
    {
        bool? design = J.Has(state, "design") && J.Has(state["design"], "docs_only") ? J.B(state["design"], "docs_only") : null;
        DateTimeOffset? made = DateTime.TryParse(info?.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? new DateTimeOffset(d, TimeSpan.Zero) : null;
        return Fworker.Domain.DocsOnly.ItemIsDocsOnly(design, info is null ? null : new(info.Column, info.Body, made, info.Labels), DocsSettings());
    }

    /// <summary>Test-DocsOnlyFiles over the files of a pull request.</summary>
    public bool FilesAreDocs(IEnumerable<JsonNode?> files) => Fworker.Domain.DocsOnly.FilesAreDocumentation(files.Select(f => J.S(f, "filename")).ToList(), J.Strings(config["docsOnlyPaths"]));

    // ---- checks (Get-ChecksFor) -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Get-MasterIgnoredChecks.</summary>
    public IReadOnlyList<string> MasterIgnoredChecks() => J.Has(config, "masterIgnoredChecks") ? J.Strings(config["masterIgnoredChecks"]) : ["release", "packages", "architecture"];

    static bool NameIn(string name, IEnumerable<string> list) => list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Get-ChecksFor: strict classification of the check runs of a commit; only the gates (ciGates) decide, an ignored check is left out of the verdict.</summary>
    public async Task<ChecksFor> GetChecksFor(string sha, CancellationToken ct, IReadOnlyList<string>? ignore = null)
    {
        ignore ??= [];
        var r = await GhApi.Get(gh, $"{RepoPath}/commits/{sha}/check-runs?per_page=100", ct);
        var runs = J.Items(r.Body?["check_runs"]).Where(x => x is not null).ToList();
        var latest = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var run in runs)
        {
            var k = J.S(run, "name");
            if (!latest.TryGetValue(k, out var have)) { latest[k] = run!; order.Add(k); continue; }
            if ((J.FromIso(run, "started_at") ?? DateTime.MinValue) > (J.FromIso(have, "started_at") ?? DateTime.MinValue)) latest[k] = run!;
        }
        var failed = new List<string>(); var pending = new List<string>(); var cancelled = new List<string>(); var ok = new List<string>();
        var judged = order.Where(k => !NameIn(k, ignore)).ToList();
        foreach (var k in judged)
        {
            var run = latest[k];
            if (J.S(run, "status") != "completed") { pending.Add(k); continue; }
            switch (J.S(run, "conclusion"))
            {
                case "failure" or "timed_out" or "action_required" or "startup_failure": failed.Add(k); break;
                case "cancelled": pending.Add(k); cancelled.Add(k); break;
                default: ok.Add(k); break;
            }
        }
        var gateList = CiGates;
        var gates = new List<KeyValuePair<string, string>>();
        foreach (var g in gateList)
        {
            if (NameIn(g, ignore)) continue;   // an ignored check is no gate either
            string v;
            if (!latest.ContainsKey(g)) v = "absent";
            else if (NameIn(g, failed)) v = "failure";
            else if (NameIn(g, pending)) v = "pending";
            else v = "success";
            gates.Add(new(g, v));
        }
        var status = "success";
        if (gateList.Length == 0)
        {
            if (failed.Count > 0) status = "failure";
            else if (judged.Count == 0 || pending.Count > 0) status = "pending";
        }
        else if (gates.Any(g => g.Value == "failure")) status = "failure";
        else if (judged.Count == 0 || gates.Any(g => g.Value is "absent" or "pending")) status = "pending";
        var nongate = failed.Where(f => !NameIn(f, gateList)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return new ChecksFor { Status = status, Total = judged.Count, Failed = failed.Count, FailedChecks = failed, PendingChecks = pending, Gates = gates, Runs = latest, NongateFailed = nongate };
    }
}
