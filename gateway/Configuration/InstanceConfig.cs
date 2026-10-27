using System.Globalization;
using System.Text.Json.Nodes;

// The gateway's own reads of the configuration (FACTORY_CONFIG, else config/instance.json two levels above the scripts), read once at start (C3).
// Each accessor keeps the semantics of the helper in Program.cs it replaces: its own try/catch and its own fallback, so a malformed section
// (say "timing":[1]) falls back for that accessor only, and a missing or invalid file gives every fallback. The settings classes of other files
// (QcSettings.Load and the rest) still read the file themselves.
sealed class InstanceConfig
{
    readonly Func<string, string?> env;

    /// <summary>The parsed file; null when it is missing, unreadable or not JSON.</summary>
    public JsonNode? Root { get; }
    public string Path { get; }

    InstanceConfig(string path, JsonNode? root, Func<string, string?> env) { Path = path; Root = root; this.env = env; }

    public static string PathFor(string scriptsDir, Func<string, string?> env) => env("FACTORY_CONFIG") ?? System.IO.Path.Combine(scriptsDir, "..", "..", "config", "instance.json");

    public static InstanceConfig Load(string path, Func<string, string?> env)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch { root = null; }
        return new InstanceConfig(path, root, env);
    }

    // auth.legacyKinds (Auth.cs, agents-roles-launch.md 4): on unless the configuration says false; unreadable is on
    public bool LegacyKinds
    {
        get
        {
            try { return Root?["auth"]?["legacyKinds"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b; }
            catch { return true; }
        }
    }

    // a top-level true/false of the configuration; absent or unreadable is false
    bool Bool(string name)
    {
        try { return Root?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b; }
        catch { return false; }
    }

    public bool OwnerSessions => Bool("ownerSessions");

    // delivery.autoPromote (Verbs.Promote.ps1): on, and naming this gateway's environment (FACTORY_ENVIRONMENT); the promotion's loop runs only then
    public bool AutoPromoteHere
    {
        get
        {
            try
            {
                var ap = Root?["delivery"]?["autoPromote"];
                if (ap?["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var on) || !on) return false;
                var gwEnv = ap["gatewayEnvironment"]?.ToString() ?? "";
                return gwEnv.Length > 0 && string.Equals(env("FACTORY_ENVIRONMENT") ?? "", gwEnv, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // issue 97: the first column the factory works (columns.ai[0] of the configuration), its intake of new work
    public string IntakeColumn
    {
        get
        {
            try
            {
                if (Root?["columns"]?["ai"] is JsonArray ai && ai.Count > 0 && ai[0]?.ToString() is { Length: > 0 } c) return c;
            }
            catch { }
            return "Technical Design";
        }
    }

    // plant.oneItemPerSession (docs/launcher.md): FACTORY_ONE_ITEM_PER_SESSION (1 or 0), else the config, else on.
    public bool OneItemPerSession
    {
        get
        {
            var e = env("FACTORY_ONE_ITEM_PER_SESSION");
            if (e is "1" or "true") return true;
            if (e is "0" or "false") return false;
            try
            {
                if (Root?["plant"]?["oneItemPerSession"] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
            }
            catch { }
            return true;
        }
    }

    static bool Positive(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v > 0;
    static bool NonNegative(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;

    double Timing(string key, double fallback, bool zeroAllowed)
    {
        bool Good(string? text, out double v) => zeroAllowed ? NonNegative(text, out v) : Positive(text, out v);
        if (Good(env("FACTORY_TIMING_" + key), out var a)) return a;
        try
        {
            if (Good(Root?["timing"]?[key]?.ToString(), out a)) return a;
        }
        catch { }
        return fallback;
    }

    // timing.<name> of the config (FACTORY_TIMING_<name> first), a positive number, else the fallback
    public double TimingNumber(string key, double fallback) => Timing(key, fallback, zeroAllowed: false);

    // timing.<key> of the config, FACTORY_TIMING_<key> first (tests); a value that is not a number of 0 or more is the default
    public double TimingValue(string key, double fallback) => Timing(key, fallback, zeroAllowed: true);

    // The accept window of an offer: FACTORY_ASSIGN_ACK_SECONDS, else FACTORY_TIMING_assignAckSeconds, else timing.assignAckSeconds of the config, else 120.
    public double AckSeconds => Positive(env("FACTORY_ASSIGN_ACK_SECONDS"), out var a) ? a : TimingNumber("assignAckSeconds", 120);
}
