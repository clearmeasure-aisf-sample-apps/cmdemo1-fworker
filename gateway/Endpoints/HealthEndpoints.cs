using System.Reflection;
using System.Text.RegularExpressions;
using Fworker.Database;

// What /_build says of this process (task C7a): the version and commit built into the assembly, and when the gateway started (made when the routes
// are mapped, right after Build(), where Program.cs took the time before).
sealed class BuildInfo
{
    public string Version { get; }
    // the source revision built into the informational version ("<version>+<sha>"), a full 40-hex commit or null; a hint for tools that compare
    // releases (the Octopus release stays the authority: a reused pull request image carries the commit its pull request build was given, not master's)
    public string? Commit { get; }
    public DateTime Started { get; } = DateTime.UtcNow;

    public BuildInfo()
    {
        var iv = (typeof(Program).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).FirstOrDefault() as AssemblyInformationalVersionAttribute)?.InformationalVersion;
        Version = iv?.Split('+')[0] ?? "0.0.0";
        Commit = iv is { } v && v.Split('+') is [_, var rev] && Regex.IsMatch(rev, "^[0-9a-f]{40}$") ? rev : null;
    }
}

// /health, /alive, /ready, / and /_build: open to every caller (the middleware authenticates /api and /mcp only).
sealed class HealthEndpoints(BuildInfo build, PluginArchive plugin, PromptStore prompts, GatewayLeader leader, RevocationService revocations, GatewayOptions gateway, FactorySchemaStatus schema) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Text("ok"));
        // liveness for the kit's hourly Health report (it asks every app for /alive, then /): the process answers, nothing else is checked
        routes.MapGet("/alive", () => Results.Text("ok"));
        routes.MapGet("/ready", () => Ready());
        routes.MapGet("/", () => Results.Redirect("/dashboard"));
        routes.MapGet("/_build", () => Results.Json(new { name = "factory-worker-gateway", version = build.Version, commit = build.Commit, pluginHash = plugin.Hash(), promptsHash = prompts.Hash,
            started = build.Started, runtime = Environment.Version.ToString(), ownerMode = gateway.OwnerMode, environment = gateway.EnvironmentName,
            schemaVersion = FactorySchema.LatestVersion.ToString("000") }));
    }

    // readiness: the revoked credentials are read (at once where there is nothing to read), so a session credential is answered without waiting
    // (503 says what the start waits for, how many reads have failed and since when: {"ready":false,"waitingFor":"revocations","attempts":N,"since":"..."});
    // the prompt files (gateway/Mcp/Prompts.cs, agents-roles-launch.md 5) are loaded once, and a broken one keeps /ready at 503;
    // where a database is configured and v2 acts (Engine.V2Acts; in shadow the schema shows in /api/v1/status only), the schema of the database is applied and checked first (FactorySchemaStatus):
    // {"ready":false,"waitingFor":"schema","state":"upgrading"|"failed","attempts":N,"since":"...","failedScript":...,"lastFailure":...}, until the first good verification of the process
    IResult Ready() => leader.Replaced ? Results.Json(new { ready = false, waitingFor = "nothing", replaced = true, reason = "a newer gateway process holds the lease" }, statusCode: 503)
        : prompts.Errors.Count > 0 ? Results.Json(new { ready = false, waitingFor = "prompts", errors = prompts.Errors }, statusCode: 503)
        : !revocations.IsLoaded ? Results.Json(new { ready = false, waitingFor = "revocations", attempts = revocations.FailedAttempts, since = revocations.Since.ToString("o") }, statusCode: 503)
        : schema.Waiting() is { } waiting ? Results.Json(waiting, statusCode: 503)
        : Results.Text("ok");
}
