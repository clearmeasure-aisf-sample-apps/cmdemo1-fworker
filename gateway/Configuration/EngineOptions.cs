using System.Text.Json.Nodes;

// The engine switch (task W0.0, docs/design/fworker-wolverine-implementation.md 3): which engine acts in this process, chosen per environment, once, at start.
// config/instance.json holds engine: { "v2Environments": [], "shadowEnvironments": [] }, read for the gateway's environment (FACTORY_ENVIRONMENT, as GatewayOptions
// resolves it). An environment in v2Environments runs v2; one in shadowEnvironments runs v1 with v2 beside it, its commands going to a recording sink; any other
// runs v1 alone. There is no kill switch (the owner's decision D1 of 2026-11-01): FACTORY_V2_OFF is not read; the way back is a redeploy of the previous release.
// Nothing acts on the mode yet: v2 does not exist, so the switch is only reported (/api/v1/status, "engine") until the waves that read it.
enum EngineMode { V1, Shadow, V2 }

sealed class EngineOptions
{
    /// <summary>The environment the lists were read for (lower case, trimmed).</summary>
    public string Environment { get; init; } = "";
    /// <summary>What the configuration names for this environment.</summary>
    public EngineMode Configured { get; init; }
    /// <summary>What runs: what the configuration names.</summary>
    public EngineMode Mode => Configured;
    /// <summary>
    /// The file names v2 for this environment, and there is no usable database (FACTORY_DB_CONNECTION missing or malformed). v2 cannot act, and v1 must not take its place: with no database no "v1-ran" marker can be
    /// written, so a later adoption would not know that v1 had acted. The gateway stays inert: nothing sweeps, /ready answers 503 "database", /health stays 200. Set by <see cref="ForDatabase"/>.
    /// </summary>
    public bool NoDatabase { get; init; }
    public bool V2Acts => Mode == EngineMode.V2 && !NoDatabase;

    /// <summary>The same options, with <see cref="NoDatabase"/> decided by whether a database is configured.</summary>
    public EngineOptions ForDatabase(bool databaseConfigured) => new() { Environment = Environment, Configured = Configured, NoDatabase = Mode == EngineMode.V2 && !databaseConfigured };
    public bool ShadowRuns => Mode == EngineMode.Shadow;

    public static EngineOptions From(InstanceConfig cfg, string environment, Func<string, string?> env)
    {
        var name = (environment ?? "").Trim().ToLowerInvariant();
        var configured = EngineMode.V1;
        if (name.Length > 0)
        {
            if (Names(cfg.Root, "shadowEnvironments").Contains(name)) configured = EngineMode.Shadow;
            if (Names(cfg.Root, "v2Environments").Contains(name)) configured = EngineMode.V2;   // v2 wins where a name is in both
        }
        return new EngineOptions { Environment = name, Configured = configured };
    }

    // /api/v1/status carries the block when the environment is not plain v1: the file names shadow or v2
    public bool Reported => Configured != EngineMode.V1;

    // the block of /api/v1/status
    public JsonObject Status()
    {
        var o = new JsonObject { ["mode"] = Label(Mode), ["configured"] = Label(Configured), ["environment"] = Environment };
        if (NoDatabase) { o["inert"] = true; o["waitingFor"] = "database"; }   // v2 is configured and cannot act; v1 does not act in its place
        return o;
    }

    public static string Label(EngineMode m) => m switch { EngineMode.V2 => "v2", EngineMode.Shadow => "shadow", _ => "v1" };

    // engine.<list> as lower case names; a missing or malformed block, or a non-string entry, names nothing
    static HashSet<string> Names(JsonNode? root, string list)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (root?["engine"]?[list] is JsonArray a)
                foreach (var n in a) if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0) set.Add(s.Trim().ToLowerInvariant());
        }
        catch { }
        return set;
    }
}
