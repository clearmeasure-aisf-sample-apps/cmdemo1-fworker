using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Qc;

/// <summary>
/// What the QC verbs read of the configuration and the environment: the instance (config/instance.json as Get-Cfg reads it, with the FACTORY_TIMING_&lt;name&gt; overrides of the tests), the QC block
/// (Get-QcCfg), the gate's two requirements (Get-QcGate) and the gateway's address and secret. Built once from the parsed file; a value the file lacks is what the script's cast of a missing value gives.
/// </summary>
public sealed class QcConfig
{
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    /// <summary>The factory's name: the prefix of the item branches (name/N-...) and of the state comment's marker.</summary>
    public string Name { get; init; } = "";
    /// <summary>The number of the organization's project (the board) the cards are on.</summary>
    public int Project { get; init; }
    public string ValidationColumn { get; init; } = "";
    public string Label { get; init; } = "";
    public string RivalLabel { get; init; } = "";
    public string AutoMergeLabel { get; init; } = "";
    public string MergePolicy { get; init; } = "";
    public IReadOnlyList<string> HumanApprovers { get; init; } = [];
    public IReadOnlyList<string> DocsOnlyPaths { get; init; } = [];
    public IReadOnlyList<string> TrustedAuthors { get; init; } = [];
    /// <summary>The patterns Protect-JournalText redacts, the catalogue's patterns then its redactOnly; empty means the fallback list.</summary>
    public IReadOnlyList<string> SecretPatterns { get; init; } = [];
    public bool AllowLoopbackForTests { get; init; }
    /// <summary>qc.requireRun and qc.requireApproval when the file sets them (null: the default, which is on exactly under peer-approved).</summary>
    public bool? RequireRun { get; init; }
    public bool? RequireApproval { get; init; }
    /// <summary>The timing block, with the environment's overrides applied to the keys the file has.</summary>
    public IReadOnlyDictionary<string, double> Timing { get; init; } = new Dictionary<string, double>();
    /// <summary>"env:&lt;environment&gt;" when the environment scopes its items (Get-EnvLabel), else empty.</summary>
    public string EnvLabel { get; init; } = "";
    /// <summary>FACTORY_PUBLIC_URL, trimmed, without a trailing slash.</summary>
    public string PublicUrl { get; init; } = "";
    public string TokenSecret { get; init; } = "";
    public bool OwnerMode { get; init; }

    /// <summary>The repository path of the REST calls ("repos/owner/repo").</summary>
    public string RepoPath => $"repos/{Owner}/{Repo}";
    public string FullName => $"{Owner}/{Repo}";

    public bool HasTiming(string name) => Timing.ContainsKey(name);
    /// <summary>[double]$timing.name: 0 for a key the file lacks.</summary>
    public double TimingOr0(string name) => Timing.TryGetValue(name, out var v) ? v : 0;

    /// <summary>The lifetime cap in minutes: timing.qcLifetimeMinutes, else timing.qcLifetimeHours * 60, else 8 hours (Get-QcLifetimeMinutes).</summary>
    public double LifetimeMinutes
    {
        get
        {
            if (Timing.TryGetValue("qcLifetimeMinutes", out var m)) return m;
            if (Timing.TryGetValue("qcLifetimeHours", out var h)) return h * 60;
            return 480.0;
        }
    }

    /// <summary>Test-QcEnabled: owner mode with a token secret of at least 16 characters.</summary>
    public bool QcEnabled => OwnerMode && TokenSecret.Length >= 16;

    /// <summary>Get-QcGate: the requirements of the merge gate (design 3.6).</summary>
    public (bool RequireRun, bool RequireApproval) Gate
    {
        get
        {
            var peer = MergePolicy == "peer-approved";
            return (RequireRun ?? peer, RequireApproval ?? peer);
        }
    }

    /// <summary>Test-QcGateActive: QC exists, the policy is peer-approved, and at least one requirement is on.</summary>
    public bool GateActive => QcEnabled && MergePolicy == "peer-approved" && (Gate.RequireRun || Gate.RequireApproval);

    public static bool IsLoopbackHost(string host)
    {
        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "0.0.0.0" or "::1" or "::" || h.EndsWith(".localhost", StringComparison.Ordinal) || Regex.IsMatch(h, "^127\\.", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Get-QcCfg's two guards, which refuse every verb that calls it (Reject 30 'config-invalid'): the loopback exception is for tests and loads only when the gateway's address is a loopback address, and the
    /// link token must outlive the build cap and the lifetime.
    /// </summary>
    public void Validate()
    {
        if (AllowLoopbackForTests)
        {
            var host = Uri.TryCreate(PublicUrl, UriKind.Absolute, out var u) ? u.Host : "";
            if (!IsLoopbackHost(host)) throw new QcReject(30, "config-invalid", "qc.allowLoopbackForTests is for tests only and loads only when FACTORY_PUBLIC_URL is a loopback address");
        }
        if (HasTiming("qcTokenHours") && HasTiming("qcBuildCapMinutes") && Timing["qcTokenHours"] <= Timing["qcBuildCapMinutes"] / 60 + LifetimeMinutes / 60)
            throw new QcReject(30, "config-invalid", "timing.qcTokenHours must be above the build cap plus timing.qcLifetimeMinutes, so a live environment never outlives its token");
    }

    /// <summary>Get-EnvLabel: scoping is on in the environments the file's envScope lists, FACTORY_ENV_SCOPE (1 or 0) overrides; the environment is FACTORY_ENVIRONMENT, else tdd.</summary>
    static string EnvLabelOf(JsonNode? root, Func<string, string?> env)
    {
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) e = "tdd";
        var flag = env("FACTORY_ENV_SCOPE");
        bool on;
        if (flag is "0" or "1") on = flag == "1";
        else on = root?["envScope"] is JsonArray scope && scope.Any(x => string.Equals((x?.ToString() ?? "").ToLowerInvariant(), e, StringComparison.Ordinal));
        return on ? $"env:{e}" : "";
    }

    static string S(JsonNode? n, string key) { try { return n?[key] is JsonValue v ? QcJson.Text(v) : ""; } catch { return ""; } }

    static List<string> List(JsonNode? n, string key)
    {
        var l = new List<string>();
        try { if (n?[key] is JsonArray a) foreach (var x in a) if (x is not null) l.Add(QcJson.Text(x)); } catch { }
        return l;
    }

    static bool? NullableBool(JsonNode? n, string key)
    {
        try { return n?[key] is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? v.GetValue<bool>() : null; } catch { return null; }
    }

    /// <summary>Reads the parsed file (null: an empty configuration) and the environment.</summary>
    public static QcConfig From(JsonNode? root, Func<string, string?> env)
    {
        var timing = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            if (root?["timing"] is JsonObject t)
                foreach (var kv in t)
                {
                    if (kv.Value is not JsonValue v) continue;
                    double d;
                    // FACTORY_TIMING_<name>: the tests' override of a key the file has (Get-Cfg)
                    if (env("FACTORY_TIMING_" + kv.Key) is { Length: > 0 } o && double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out var od)) d = od;
                    else if (v.TryGetValue<double>(out var vd)) d = vd;
                    else if (v.TryGetValue<string>(out var vs) && double.TryParse(vs, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)) d = sd;
                    else continue;
                    timing[kv.Key] = d;
                }
        }
        catch { }
        var patterns = new List<string>();
        try
        {
            var secrets = root?["secrets"];
            foreach (var group in new[] { "patterns", "redactOnly" }) patterns.AddRange(List(secrets, group).Where(x => x.Length > 0));
        }
        catch { }
        var qc = root?["qc"];
        return new QcConfig
        {
            Owner = S(root, "owner"), Repo = S(root, "repo"), Name = S(root, "name"), Project = int.TryParse(S(root, "project"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var project) ? project : 0,
            ValidationColumn = S(root?["columns"], "validation"), Label = S(root, "label"), RivalLabel = S(root, "rivalLabel"), AutoMergeLabel = S(root, "autoMergeLabel"),
            MergePolicy = S(root, "mergePolicy"), HumanApprovers = List(root, "humanApprovers"), DocsOnlyPaths = List(root, "docsOnlyPaths"),
            TrustedAuthors = List(qc, "trustedAuthors"), SecretPatterns = patterns,
            AllowLoopbackForTests = NullableBool(qc, "allowLoopbackForTests") ?? false,
            RequireRun = NullableBool(qc, "requireRun"), RequireApproval = NullableBool(qc, "requireApproval"),
            Timing = timing, EnvLabel = EnvLabelOf(root, env),
            PublicUrl = (env("FACTORY_PUBLIC_URL") ?? "").Trim().TrimEnd('/'), TokenSecret = env("FACTORY_TOKEN_SECRET") ?? "",
            OwnerMode = env("FACTORY_OWNER_MODE") == "1",
        };
    }
}
