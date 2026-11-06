using Fworker.Contracts.Seams;

// Seam S1 (task W1.4, docs/design/fworker-wolverine-refactor.md 4): the engines behind VerbPipeline and the choice of the one this process runs.

// v1: the rule scripts, through the Runner (pool or one process per call), exactly as VerbPipeline called it before the seam.
sealed class V1VerbRouter(Runner runner) : IVerbRouter
{
    public async Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        var (code, output) = await runner.RunAsync(verb, stdin, timeout);
        return new VerbAnswer(code, output);
    }
}

// The engine branch: composed once at start from EngineOptions (config/instance.json engine lists, FACTORY_ENVIRONMENT). v2 does not exist
// yet, so every mode resolves to v1: a v2 or shadow environment still runs v1 alone, and nothing routes to v2. The v2 router joins the switch with the
// wave that builds it; the branch is here so that it is chosen per environment and not in the pipeline.
static class VerbRouterFactory
{
    public static IVerbRouter For(EngineOptions engine, Runner runner) => engine.Mode switch
    {
        EngineMode.V2 => new V1VerbRouter(runner),      // v2 is not built: v1 acts
        _ => new V1VerbRouter(runner),
    };
}

// V3.3: what an inert gateway (EngineOptions.Inert: the engine does not act in its environment) answers to a verb a caller sends: a refusal, whoever asks and whatever the verb. There is no v1 to run in v2's place (the rule
// scripts of the server are gone, and with no database no "v1-ran" marker could be written either). Two places refuse: the router in front of the verb endpoint (InertVerbRouter) and, as the choke point of every
// call of the rule script whoever makes it (the QC hub, the item admin endpoints, the demo endpoint, the webhook, the loops), Runner.RunScriptAsync.
static class InertVerbs
{
    /// <summary>
    /// The gateway's own housekeeping, the one thing an inert gateway still runs through the rule script (never a verb a caller sends: the verb endpoint refuses those): it keeps the credentials and the
    /// records and writes nothing about an item. The revoked credentials are read, so a session credential is refused when it was revoked instead of waiting for a read that never comes; the operator can
    /// revoke a name; the operator's release record is read and written; the transcripts are the ref store's.
    /// </summary>
    public static readonly IReadOnlySet<string> Housekeeping = new HashSet<string>(StringComparer.Ordinal)
    {
        "revocations", "revoke-worker", "operator-releases", "operator-release", "transcript-save", "transcript-load", "transcript-prune",
    };

    /// <summary>The refusal, exit 20 (HTTP 503 at every endpoint): code "database" where v2 is named and there is no database (the caller retries once there is one); code "inert" where v2Environments does not name the environment (nothing changes until the configuration does: an hour).</summary>
    public static VerbAnswer Refusal(string verb, EngineOptions engine) => engine.NoDatabase
        ? BoardVerbAnswer.Rejected(verb, 20, "database", "v2 is configured for this environment and there is no database (FACTORY_DB_CONNECTION): nothing acts until there is one", new System.Text.Json.Nodes.JsonObject { ["retryAfterSeconds"] = 60 })
        : BoardVerbAnswer.Rejected(verb, 20, "inert", $"the v2 engine does not act in this environment (engine.v2Environments does not name '{engine.Environment}'): nothing acts here", new System.Text.Json.Nodes.JsonObject { ["retryAfterSeconds"] = 3600 });
}

// The router in front of the verb endpoint of an inert gateway: every verb is refused.
sealed class InertVerbRouter(EngineOptions engine) : IVerbRouter
{
    public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) => Task.FromResult(InertVerbs.Refusal(verb, engine));
}

// The leader lease as the loops of an inert gateway see it: it is held (status, hand-over), but nothing may act.
sealed class InertLeadership(ILeadership inner) : ILeadership
{
    public bool On => inner.On;
    public bool MayAct => false;
    public bool Replaced => inner.Replaced;
    public long Epoch => inner.Epoch;
    public System.Text.Json.Nodes.JsonObject Status() => inner.Status();
}
