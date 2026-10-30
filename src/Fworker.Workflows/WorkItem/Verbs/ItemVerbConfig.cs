using System.Globalization;
using System.Text.Json.Nodes;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>
/// What the item verbs read of config/instance.json: <c>Get-Cfg</c> of Core.ps1 for the keys these verbs use, with the defaults the scripts write inline. The timing values take the test override of the script, the
/// environment variable <c>FACTORY_TIMING_&lt;name&gt;</c>. The things that are not in the file (the environment label of <c>Get-EnvLabel</c>, whether messaging is on, whether the QC merge gate is active) are decided by
/// the gateway and given to <see cref="From"/>.
/// </summary>
public sealed class ItemVerbConfig
{
    readonly JsonObject root;
    readonly JsonObject timing;
    readonly Func<string, string?> env;

    ItemVerbConfig(JsonObject root, Func<string, string?> env, string envLabel, bool messagingOn, bool qcGateActive)
    {
        this.root = root; this.env = env;
        timing = root["timing"] as JsonObject ?? new JsonObject();
        EnvLabel = envLabel; MessagingOn = messagingOn; QcGateActive = qcGateActive;
    }

    /// <summary>The configuration as the rule scripts read it. A document that is not an object has none of the keys: every read then answers its default (the scripts would die of the missing configuration).</summary>
    public static ItemVerbConfig From(JsonNode? root, Func<string, string?> env, string envLabel = "", bool messagingOn = false, bool qcGateActive = false) =>
        new(root as JsonObject ?? new JsonObject(), env, envLabel, messagingOn, qcGateActive);

    public JsonObject Root => root;

    /// <summary><c>Get-EnvLabel</c>: "env:&lt;environment&gt;" while the environment scope is on, else "".</summary>
    public string EnvLabel { get; }
    /// <summary><c>Test-MessagingOn</c>.</summary>
    public bool MessagingOn { get; }
    /// <summary><c>Test-QcGateActive</c>.</summary>
    public bool QcGateActive { get; }

    public string Text(string key, string fallback = "") => Ps.Has(root, key) ? Ps.Text(root, key) : fallback;
    public string Name => Text("name");
    public string Owner => Text("owner");
    public string Repo => Text("repo");
    public int Project => Ps.Has(root, "project") ? Ps.Int(root, "project") : 0;
    public string DefaultBranch => Text("defaultBranch", "master");
    public string Label => Text("label");
    public string RivalLabel => Text("rivalLabel");
    public string DemoLabel => Text("demoLabel");
    public string VideoLabel => Text("videoLabel");
    public string AutoMergeLabel => Text("autoMergeLabel");
    public string MergePolicy => Text("mergePolicy");
    public string DocsOnlyMarker => Text("docsOnlyMarker");

    /// <summary><c>Get-RepoPath</c>.</summary>
    public string RepoPath => $"repos/{Owner}/{Repo}";

    JsonObject Columns => root["columns"] as JsonObject ?? new JsonObject();
    public IReadOnlyList<string> ColumnsAi => Ps.Strings(Columns["ai"]).ToList();
    public IReadOnlyList<string> ColumnsHold => Ps.Strings(Columns["hold"]).ToList();
    public string ColumnDevelopment => Ps.Text(Columns, "development");
    public string ColumnValidation => Ps.Text(Columns, "validation");

    /// <summary>requiredBodySection: the column and the heading its completion needs in the body, in the file's order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequiredBodySection =>
        (root["requiredBodySection"] as JsonObject)?.Select(kv => new KeyValuePair<string, string>(kv.Key, Ps.Text(kv.Value))).ToList() ?? [];
    public string? SectionOf(string column) { foreach (var kv in RequiredBodySection) if (Ps.Eq(kv.Key, column)) return kv.Value; return null; }
    public bool HasSection(string column) => RequiredBodySection.Any(kv => Ps.Eq(kv.Key, column));
    public IReadOnlyList<string> ReviewVerdictColumns => Ps.Strings(root["reviewVerdictColumns"]).ToList();
    public IReadOnlyList<string> DocsOnlyPaths => Ps.Strings(root["docsOnlyPaths"]).ToList();
    public IReadOnlyList<string> StateAuthors => Ps.Strings(root["stateAuthors"]).ToList();

    /// <summary><c>holdLabels</c>, else the default "hold:owner".</summary>
    public IReadOnlyList<string> HoldLabels => Ps.Has(root, "holdLabels") ? Ps.Strings(root["holdLabels"]).ToList() : ["hold:owner"];

    // ---- timing (a number of minutes, rounds, ...; the override is the script's FACTORY_TIMING_<name>) -------------------------------------------------

    /// <summary>The timing value as the script reads it, or null when the configuration has none.</summary>
    public double? TimingOrNull(string name)
    {
        // the test override of Get-Cfg: only a key the configuration has is overridden
        if (timing.ContainsKey(name) && env($"FACTORY_TIMING_{name}") is { Length: > 0 } o && double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out var ov)) return ov;
        return Ps.Has(timing, name) ? Ps.Double(timing[name]) : null;
    }

    public double Timing(string name, double fallback) => TimingOrNull(name) ?? fallback;
    public bool HasTiming(string name) => TimingOrNull(name) is not null;

    public double LeaseMinutes => Timing("leaseMinutes", 0);
    public double ParkHours => Timing("parkHours", 0);
    public double ParkMinutes => ParkHours * 60;
    public int MaxRelaunches => (int)Timing("maxRelaunches", 0);
    public int MaxSessionRelaunches => HasTiming("maxSessionRelaunches") ? (int)Timing("maxSessionRelaunches", 10) : 10;
    public int MaxReviewRounds => (int)Timing("maxReviewRounds", 0);
    public int DocsOnlyMaxReviewRounds => HasTiming("docsOnlyMaxReviewRounds") ? (int)Timing("docsOnlyMaxReviewRounds", 2) : 2;
    public int MaxDemotions => (int)Timing("maxDemotions", 0);
    public double AwaitingChecksParkMinutes => Timing("awaitingChecksParkMinutes", 0);
    public int RedChecksGate => HasTiming("redChecksGate") ? (int)Timing("redChecksGate", 2) : 2;
    public double ReleaseCandidateTimeoutMinutes => Timing("releaseCandidateTimeoutMinutes", 0);

    /// <summary><c>docsOnlyLegacyBefore</c>: when the items an older version designed end; null when unset.</summary>
    public DateTime? LegacyCutoff => Ps.Has(timing, "docsOnlyLegacyBefore") ? Ps.FromIso(timing["docsOnlyLegacyBefore"]) : null;

    // ---- the model ladder (Models.ps1) ---------------------------------------------------------------------------------------------------------------

    public JsonObject Escalation => root["escalation"] as JsonObject ?? new JsonObject();
    public string TestLabel => Text("testLabel");

    // ---- the kind of item ----------------------------------------------------------------------------------------------------------------------------

    /// <summary><c>Test-ItemInScope</c>: with scoping on the item carries exactly one env: label and it is this environment's.</summary>
    public bool InScope(IEnumerable<string> labels)
    {
        if (EnvLabel.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && Ps.Eq(envs[0], EnvLabel);
    }

    /// <summary><c>Get-HoldLabel</c>: the first hold label the item carries, or "".</summary>
    public string HoldLabelOf(IEnumerable<string> labels)
    {
        var have = labels.ToList();
        foreach (var h in HoldLabels) if (h.Length > 0 && Ps.NameIn(h, have)) return h;
        return "";
    }
}
