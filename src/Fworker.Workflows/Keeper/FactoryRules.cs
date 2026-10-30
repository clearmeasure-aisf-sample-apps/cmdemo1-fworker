using System.Text.Json.Nodes;

namespace Fworker.Workflows.Keeper;

/// <summary>
/// What the keeper and manager verbs read of the configuration (config/instance.json, <c>Get-Cfg</c> of the rule scripts) and of the environment (<c>Get-EnvLabel</c>): the home repository, the labels,
/// the AI columns, the keeper's policies and protected criteria and the manager's limits. The defaults are the scripts' (<c>Get-KeeperCfg</c>, <c>Get-ManagerActionCfg</c>, <c>Get-PriorityLabels</c>).
/// </summary>
public sealed class FactoryRules
{
    public static readonly IReadOnlyList<string> DefaultKeeperPolicies = ["criterion-dispute", "ambiguous-wording", "oversized-item", "compatibility-conflict", "flaky-test", "backlog-low", "dependency-order", "operator-request"];

    public static readonly IReadOnlyList<string> DefaultProtectedCriteria =
    [
        @"(?i)\bmust\b", "(?i)secur", @"(?i)\bsecret", "(?i)credential", "(?i)password", @"(?i)\btoken", @"(?i)\bauth", "(?i)permission", @"(?i)\baccess control", "(?i)encrypt",
        @"(?i)privacy|personal data|\bPII\b", @"(?i)data[- ]loss|lose (any )?data|\bno data is lost", @"(?i)\bdelet", @"(?i)\bdrop(ped|s)?\b", "(?i)backup|restore",
        "(?i)irreversib|destructive", @"(?i)\baudit",
    ];

    public static readonly IReadOnlyList<string> DefaultManagerPolicies = ["park-resolved", "stalled-owner", "pinned-worker-absent", "stuck-offer", "outside-blocker", "flaky-check", "repeated-question", "backlog-order", "load-balance", "operator-request"];

    /// <summary>The instance name: the marker of the state comment, the journal comment's signature, the branch prefix and the claim refs.</summary>
    public string Name { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    public string Label { get; init; } = "";
    public string AutoMergeLabel { get; init; } = "";
    public string TestLabel { get; init; } = "";
    public string VideoLabel { get; init; } = "";
    public string OwnerHoldLabel { get; init; } = "hold:owner";
    public string PriorityTop { get; init; } = "priority:top";
    public string PriorityHigh { get; init; } = "priority:high";
    /// <summary>The columns an AI session works (<c>columns.ai</c>).</summary>
    public IReadOnlyList<string> AiColumns { get; init; } = [];
    /// <summary>The sections of a body that start after the acceptance criteria (<c>requiredBodySection</c> values).</summary>
    public IReadOnlyList<string> BodySections { get; init; } = [];
    public IReadOnlyList<string> KeeperPolicies { get; init; } = DefaultKeeperPolicies;
    public IReadOnlyList<string> ProtectedCriteria { get; init; } = DefaultProtectedCriteria;
    /// <summary>The column a new item is put in (<c>keeper.newItemColumn</c>).</summary>
    public string KeeperColumn { get; init; } = "Technical Design";
    public int ManagerPerItemPerDay { get; init; } = 3;
    public IReadOnlyList<string> ManagerPolicies { get; init; } = DefaultManagerPolicies;
    /// <summary><c>Get-EnvLabel</c>: "env:&lt;environment&gt;" with scoping on, else "".</summary>
    public string EnvLabel { get; init; } = "";

    public string PriorityLabel(string level) => string.Equals(level, "top", StringComparison.OrdinalIgnoreCase) ? PriorityTop : PriorityHigh;

    public static FactoryRules From(JsonNode? root, Func<string, string?> env)
    {
        string Text(JsonNode? n, string key, string fallback = "") { try { return n?[key] is JsonNode v && !(v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.Null) ? Ps.Str(v) : fallback; } catch { return fallback; } }
        IReadOnlyList<string>? List(JsonNode? n, string key) { try { return n?[key] is JsonArray a && a.Count > 0 ? a.Select(Ps.Str).ToList() : null; } catch { return null; } }
        JsonNode? Sub(JsonNode? n, string key) { try { return n?[key]; } catch { return null; } }

        var keeper = Sub(root, "keeper"); var priority = Sub(root, "priorityLabels"); var columns = Sub(root, "columns");
        var actions = Sub(Sub(root, "manager"), "actions");
        var sections = new List<string>();
        try { if (root?["requiredBodySection"] is JsonObject o) sections.AddRange(o.Select(kv => Ps.Str(kv.Value))); } catch { }
        int perDay = 3; try { if (actions?["perItemPerDay"] is JsonNode p) perDay = VerbArgs.ToInt(Ps.Str(p)); } catch { }
        return new FactoryRules
        {
            Name = Text(root, "name"), Owner = Text(root, "owner"), Repo = Text(root, "repo"), Label = Text(root, "label"),
            AutoMergeLabel = Text(root, "autoMergeLabel"), TestLabel = Text(root, "testLabel"), VideoLabel = Text(root, "videoLabel"),
            OwnerHoldLabel = Text(root, "ownerHoldLabel", "hold:owner"),
            PriorityTop = Text(priority, "top", "priority:top"), PriorityHigh = Text(priority, "high", "priority:high"),
            AiColumns = List(columns, "ai") ?? [], BodySections = sections,
            KeeperPolicies = List(keeper, "policies") ?? DefaultKeeperPolicies, ProtectedCriteria = List(keeper, "protectedCriteria") ?? DefaultProtectedCriteria,
            KeeperColumn = Text(keeper, "newItemColumn", "Technical Design"),
            ManagerPerItemPerDay = perDay, ManagerPolicies = List(actions, "policies") ?? DefaultManagerPolicies,
            EnvLabel = EnvLabelOf(root, env),
        };
    }

    /// <summary>
    /// <c>Get-EnvLabel</c> (Board.ps1): with scoping on, a gateway acts only on items that carry exactly one env: label and it is env:&lt;FACTORY_ENVIRONMENT&gt;. Scoping is on in the environments
    /// <c>envScope</c> lists; FACTORY_ENV_SCOPE 1 or 0 overrides the list.
    /// </summary>
    public static string EnvLabelOf(JsonNode? root, Func<string, string?> env)
    {
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) e = "tdd";
        bool on;
        var flag = env("FACTORY_ENV_SCOPE") ?? "";
        if (flag is "0" or "1") on = flag == "1";
        else
        {
            on = false;
            try { if (root?["envScope"] is JsonArray a) on = a.Any(x => Ps.Str(x).ToLowerInvariant() == e); } catch { }
        }
        return on ? "env:" + e : "";
    }

    /// <summary><c>Test-ItemInScope</c>: with scoping off every item is in; with it on, exactly one env: label and it is this environment's.</summary>
    public bool InScope(IEnumerable<string> labels)
    {
        if (EnvLabel.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && string.Equals(envs[0], EnvLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>Get-ItemPriority</c>: top, high or "".</summary>
    public string PriorityOf(IEnumerable<string> labels) => Ps.NameIn(PriorityTop, labels) ? "top" : Ps.NameIn(PriorityHigh, labels) ? "high" : "";
}
