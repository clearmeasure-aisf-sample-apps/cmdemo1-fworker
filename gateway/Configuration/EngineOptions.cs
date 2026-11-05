using System.Text.Json.Nodes;

// The engine switch (task W0.0, docs/design/fworker-wolverine-implementation.md 3): which engine acts in this process, chosen per environment, once, at start.
// config/instance.json holds engine: { "v2Environments": [] }, read for the gateway's environment (FACTORY_ENVIRONMENT, as GatewayOptions resolves it). An environment
// in v2Environments runs v2 where it has a database; any other is inert (V3.3: there is no v1 to run, the rule scripts of the server are gone): nothing acts, /ready answers 503 "engine".
// (fworker's own uat and prod are such environments; the owner approved the change for a later image promoted there, 2026-11-05.)
// There is no switch back: the shadow run and the kill switch FACTORY_V2_OFF are gone (V3.2; the rollback is a redeploy of the previous release).
enum EngineMode { V1, V2 }

sealed class EngineOptions
{
    /// <summary>The environment the lists were read for (lower case, trimmed).</summary>
    public string Environment { get; init; } = "";
    /// <summary>What the configuration names for this environment.</summary>
    public EngineMode Configured { get; init; }
    /// <summary>What runs.</summary>
    public EngineMode Mode => Configured;
    /// <summary>
    /// The file names v2 for this environment and there is no usable database (FACTORY_DB_CONNECTION missing or malformed). v2 cannot act, and v1 must not take its place: with no database no "v1-ran" marker can be
    /// written, so a later adoption would not know that v1 had acted. The gateway stays inert: nothing sweeps, /ready answers 503 "database", /health stays 200. Set by <see cref="ForDatabase"/>.
    /// </summary>
    public bool NoDatabase { get; init; }
    public bool V2Acts => Mode == EngineMode.V2 && !NoDatabase;

    /// <summary>
    /// V3.3: the engine does not act here, so nothing does: the environment is not in v2Environments ("engine"), or it is and has no usable database ("database", <see cref="NoDatabase"/>). The verb endpoint and
    /// the rule-script runner refuse every verb (<see cref="InertVerbs"/>), the loops that act on items see a lease that may not act, /ready answers 503 with <see cref="InertReason"/>, /health stays 200.
    /// </summary>
    public bool Inert => !V2Acts && !(TestScripts && Mode == EngineMode.V1);

    /// <summary>
    /// FACTORY_TEST_STUB_SCRIPTS=1, a test gateway's stand-in (tests/Gateway.Tests, whose stub pwsh answers every verb as the fixture says): an environment v2Environments does not name runs the verbs through the
    /// Runner, so the gateway's machinery (the pipeline, the plant's loops, the hooks) is tested without a database. Refused without messaging.testHooks; never set outside the unit tests; no effect where v2 is named.
    /// </summary>
    public bool TestScripts { get; init; }
    /// <summary>What an inert gateway waits for: "database" (v2 is named and there is no database) or "engine" (v2Environments does not name the environment); null where the engine acts.</summary>
    public string? InertReason => !Inert ? null : NoDatabase ? "database" : "engine";

    /// <summary>The same options, with <see cref="NoDatabase"/> decided by whether a database is configured.</summary>
    public EngineOptions ForDatabase(bool databaseConfigured) => new() { Environment = Environment, Configured = Configured, TestScripts = TestScripts, NoDatabase = Mode == EngineMode.V2 && !databaseConfigured };

    public static EngineOptions From(InstanceConfig cfg, string environment, Func<string, string?> env)
    {
        var name = (environment ?? "").Trim().ToLowerInvariant();
        var configured = EngineMode.V1;
        if (name.Length > 0)
        {
            if (Names(cfg.Root, "v2Environments").Contains(name)) configured = EngineMode.V2;
        }
        var testScripts = env("FACTORY_TEST_STUB_SCRIPTS") == "1";
        if (testScripts && !(cfg.Root?["messaging"]?["testHooks"] is JsonValue t && t.TryGetValue<bool>(out var hooks) && hooks))
            throw new InvalidOperationException("FACTORY_TEST_STUB_SCRIPTS is for test gateways only: it needs messaging.testHooks in the configuration");
        return new EngineOptions { Environment = name, Configured = configured, TestScripts = testScripts };
    }

    // /api/v1/status carries the block where v2 is named, and where the gateway is inert (V3.3: an environment the engine does not act in says so)
    public bool Reported => Configured != EngineMode.V1 || Inert;

    // the block of /api/v1/status
    public JsonObject Status()
    {
        var o = new JsonObject { ["mode"] = Label(Mode), ["configured"] = Label(Configured), ["environment"] = Environment };
        if (InertReason is { } why) { o["inert"] = true; o["waitingFor"] = why; }   // nothing acts here: no v1 runs in v2's place
        return o;
    }

    public static string Label(EngineMode m) => m switch { EngineMode.V2 => "v2", _ => "v1" };

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
