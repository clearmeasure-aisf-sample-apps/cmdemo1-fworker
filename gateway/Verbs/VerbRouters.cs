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

// The engine branch: composed once at start from EngineOptions (config/instance.json engine lists, FACTORY_ENVIRONMENT, FACTORY_V2_OFF). v2 does not exist
// yet, so every mode resolves to v1: a v2 or shadow environment still runs v1 alone, and nothing routes to v2. The v2 router joins the switch with the
// wave that builds it; the branch is here so that it is chosen per environment and not in the pipeline.
static class VerbRouterFactory
{
    public static IVerbRouter For(EngineOptions engine, Runner runner) => engine.Mode switch
    {
        EngineMode.V2 => new V1VerbRouter(runner),      // v2 is not built: v1 acts
        EngineMode.Shadow => new V1VerbRouter(runner),  // v1 acts; the shadow side joins with the event ingress (W1.6)
        _ => new V1VerbRouter(runner),
    };
}

// Cutover review: the router of a gateway whose environment is configured for v2 and has no database (EngineOptions.NoDatabase). v2 cannot act and v1 must not take its place (no "v1-ran" marker can be written), so every verb that
// changes anything is refused with code "database" and exit 20 (the caller retries later, as for a budget); the verbs that only read pass through. Nothing a session could do on a saga item reaches v1's scripts.
sealed class InertVerbRouter(IVerbRouter below) : IVerbRouter
{
    /// <summary>The verbs that read and write nothing (kebab-case, as on the wire).</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "get-item", "my-items", "inspect-facts", "pr-checks", "wait-for-checks", "metrics", "qc-view", "qc-list", "get-delivery", "backlog", "item-pr", "revocations", "operator-releases", "transcript-load", "plant-snapshot-load",
    };

    public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) =>
        ReadOnly.Contains(verb) ? below.RunAsync(verb, stdin, timeout)
        : Task.FromResult(BoardVerbAnswer.Rejected(verb, 20, "database", "v2 is configured for this environment and there is no database (FACTORY_DB_CONNECTION): nothing acts until there is one, or FACTORY_V2_OFF=1", new System.Text.Json.Nodes.JsonObject { ["retryAfterSeconds"] = 60 }));
}

// The leader lease as the loops of a gateway with no database see it: it is held (status, hand-over), but nothing may act.
sealed class InertLeadership(ILeadership inner) : ILeadership
{
    public bool On => inner.On;
    public bool MayAct => false;
    public bool Replaced => inner.Replaced;
    public long Epoch => inner.Epoch;
    public System.Text.Json.Nodes.JsonObject Status() => inner.Status();
}
