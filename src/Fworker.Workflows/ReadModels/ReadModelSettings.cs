using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Domain;

namespace Fworker.Workflows.ReadModels;

/// <summary>The patterns the inspector looks for in a change (config <c>inspector</c>).</summary>
public sealed record InspectorSettings(
    IReadOnlyList<string> ProtectedPaths, IReadOnlyList<string> PackagePaths, IReadOnlyList<string> MigrationPaths,
    int MaxFiles, int DocsMaxFiles, int MaxLines, IReadOnlyList<string> BinaryExtensions, IReadOnlyList<string> SecretPatterns, IReadOnlyList<string> AiDirectedText);

/// <summary>The model ladder's thresholds (config <c>escalation</c>): <c>thresholds[i]</c> is the value at which a signal asks for tier i+1; the minimum tier of a column.</summary>
public sealed record EscalationSettings(
    IReadOnlyList<double> Launches, IReadOnlyList<double> FindingsRounds, IReadOnlyList<double> Demotions, IReadOnlyList<double> FixForwardRounds, IReadOnlyList<double> Refusals,
    IReadOnlyDictionary<string, int> MinTierByColumn);

/// <summary>Octopus as the delivery reads see it (config <c>octopus</c>).</summary>
public sealed record OctopusSettings(string Url, string Space, string Project, string ProdEnvironment);

/// <summary>
/// What the read models take from <c>config/instance.json</c> (the script's <c>Get-Cfg</c>), read once: the repository and the board, the CI gates, the secret patterns the scripts redact with, the docs-only rule, the inspector's patterns,
/// the model ladder, Octopus and the environment scope. A missing value takes the default the script uses or the shipped configuration's; a value that is there but of the wrong type is read as the script would convert it (an error of the
/// verb when it cannot).
/// </summary>
public sealed class ReadModelSettings
{
    public required JsonNode Root { get; init; }
    public required Func<string, string?> Env { get; init; }
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    public string Name { get; init; } = "factory-worker";
    public string DefaultBranch { get; init; } = "master";
    public int Project { get; init; }
    public string WorkerLabel { get; init; } = "";
    public string TestLabel { get; init; } = "";
    public IReadOnlyList<string> CiGates { get; init; } = [];
    public IReadOnlyList<string> SecretPatterns { get; init; } = [];
    public IReadOnlyList<string> AiColumns { get; init; } = [];
    public IReadOnlyList<string> HoldColumns { get; init; } = [];
    public IReadOnlyList<string> DoneColumns { get; init; } = [];
    public DocsOnlySettings DocsOnly { get; init; } = new([], "", null, [], "", []);
    public InspectorSettings Inspector { get; init; } = new([], [], [], 0, 0, 0, [], [], []);
    public EscalationSettings Escalation { get; init; } = new([], [], [], [], [], new Dictionary<string, int>());
    public OctopusSettings Octopus { get; init; } = new("", "", "", "");
    public IReadOnlyList<string> EnvScope { get; init; } = [];
    public bool MessagingConfigured { get; init; }
    public bool ManagerConfigured { get; init; }

    /// <summary>The label a gateway scoped to its environment requires on an item (<c>Get-EnvLabel</c>): "" with scoping off.</summary>
    public string EnvLabel
    {
        get
        {
            var e = (Env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant();
            if (e.Length == 0) e = "tdd";
            var flag = Env("FACTORY_ENV_SCOPE");
            var on = flag is "0" or "1" ? flag == "1" : EnvScope.Any(x => x.ToLowerInvariant() == e);
            return on ? "env:" + e : "";
        }
    }

    /// <summary><c>Test-ItemInScope</c>: an item is in scope when scoping is off or it carries exactly one env: label and that is this environment's.</summary>
    public bool InScope(IEnumerable<string> labels)
    {
        var mine = EnvLabel;
        if (mine.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && Ps.Ieq(envs[0], mine);
    }

    /// <summary><c>Test-MessagingOn</c>: messaging or the manager is on in the configuration, as FACTORY_MESSAGING and FACTORY_MANAGER (0 or 1) override it.</summary>
    public bool MessagingOn
    {
        get
        {
            var msg = MessagingConfigured; var mgr = ManagerConfigured;
            if (Env("FACTORY_MESSAGING") is "0" or "1") msg = Env("FACTORY_MESSAGING") == "1";
            if (Env("FACTORY_MANAGER") is "0" or "1") mgr = Env("FACTORY_MANAGER") == "1";
            return msg || mgr;
        }
    }

    /// <summary>The fallback of Protect-JournalText when the configuration names no patterns (Journal.ps1).</summary>
    static IReadOnlyList<string> Fallback => JournalText.FallbackSecretPatterns;

    public static ReadModelSettings From(JsonNode? root, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        root ??= new JsonObject();
        string S(string name, string fallback) => Ps.Text(Ps.Prop(root, name)) is { Length: > 0 } t ? t : fallback;
        var insp = Ps.Prop(root, "inspector");
        var esc = Ps.Prop(root, "escalation");
        var oct = Ps.Prop(root, "octopus");
        var cols = Ps.Prop(root, "columns");
        var timing = Ps.Prop(root, "timing");
        var secrets = Ps.Prop(root, "secrets");

        var patterns = Strings(Ps.Prop(secrets, "patterns")).Concat(Strings(Ps.Prop(secrets, "redactOnly"))).Where(x => x.Length > 0).ToList();
        var legacy = Ps.Text(Ps.Prop(timing, "docsOnlyLegacyBefore"));
        // timing.<name> can be overridden by FACTORY_TIMING_<name> (Get-Cfg), as the legacy cutoff the docs-only rule reads can in a test
        if (env("FACTORY_TIMING_docsOnlyLegacyBefore") is { Length: > 0 } tl) legacy = tl;
        DateTimeOffset? cutoff = null;
        if (legacy.Length > 0 && DateTimeOffset.TryParse(legacy, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var c)) cutoff = c;

        var minTier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Ps.Prop(esc, "minTierByColumn") is JsonObject mt) foreach (var kv in mt) minTier[kv.Key] = Ps.ToInt(kv.Value);

        return new ReadModelSettings
        {
            Root = root, Env = env,
            Owner = S("owner", ""), Repo = S("repo", ""), Name = S("name", "factory-worker"), DefaultBranch = S("defaultBranch", "master"),
            Project = Ps.Prop(root, "project") is { } p ? Ps.ToInt(p) : 0,
            WorkerLabel = S("label", ""), TestLabel = S("testLabel", ""),
            CiGates = Strings(Ps.Prop(root, "ciGates")).Where(x => x.Length > 0).ToList(),
            SecretPatterns = patterns.Count > 0 ? patterns : Fallback,
            AiColumns = Strings(Ps.Prop(cols, "ai")).ToList(), HoldColumns = Strings(Ps.Prop(cols, "hold")).ToList(), DoneColumns = Strings(Ps.Prop(cols, "done")).ToList(),
            DocsOnly = new DocsOnlySettings(Strings(Ps.Prop(root, "docsOnlyPaths")).ToList(), S("docsOnlyMarker", ""), cutoff,
                Ps.Prop(root, "requiredBodySection") is JsonObject rb ? rb.Select(kv => kv.Key).ToList() : [], S("videoLabel", ""), Strings(Ps.Prop(root, "masterCodeChecks")).ToList()),
            Inspector = new InspectorSettings(Strings(Ps.Prop(insp, "protectedPaths")).ToList(), Strings(Ps.Prop(insp, "packagePaths")).ToList(), Strings(Ps.Prop(insp, "migrationPaths")).ToList(),
                Int(insp, "maxFiles", 15), Int(insp, "docsMaxFiles", 30), Int(insp, "maxLines", 400), Strings(Ps.Prop(insp, "binaryExtensions")).ToList(),
                Strings(Ps.Prop(insp, "secretPatterns")).ToList(), Strings(Ps.Prop(insp, "aiDirectedText")).ToList()),
            Escalation = new EscalationSettings(Numbers(Ps.Prop(esc, "launches")), Numbers(Ps.Prop(esc, "findingsRounds")), Numbers(Ps.Prop(esc, "demotions")),
                Numbers(Ps.Prop(esc, "fixForwardRounds")), Numbers(Ps.Prop(esc, "refusals")), minTier),
            Octopus = new OctopusSettings(Ps.Str(oct, "url"), Ps.Str(oct, "space"), Ps.Str(oct, "project"), Ps.Text(Ps.At(oct, "environments", "prod"))),
            EnvScope = Strings(Ps.Prop(root, "envScope")).ToList(),
            MessagingConfigured = Ps.Prop(Ps.Prop(root, "messaging"), "enabled") is { } me && Ps.Bool(me),
            ManagerConfigured = Ps.Prop(Ps.Prop(root, "manager"), "enabled") is { } ma && Ps.Bool(ma),
        };
    }

    /// <summary>The strings of a value that is a string or a list of them (<c>@($x)</c>).</summary>
    public static IEnumerable<string> Strings(JsonNode? node) => Ps.Items(node).Select(Ps.Text);

    static IReadOnlyList<double> Numbers(JsonNode? node) =>
        Ps.Items(node).Select(n => double.Parse(Ps.Text(n), CultureInfo.InvariantCulture)).ToList();

    static int Int(JsonNode? node, string name, int fallback) => Ps.Prop(node, name) is { } v ? Ps.ToInt(v) : fallback;
}
