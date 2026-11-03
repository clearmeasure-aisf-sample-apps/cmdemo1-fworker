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

// Cutover review: what a gateway whose environment is configured for v2 and has no database (EngineOptions.NoDatabase) lets through. v2 cannot act and v1 must not take its place (no "v1-ran" marker can be written), so every verb that
// changes anything is refused with code "database" and exit 20 (the caller retries later, as for a budget). Two places refuse: the router in front of the verb endpoint (InertVerbRouter) and, as the choke point of every call of the
// rule script whoever makes it (the QC hub, the item admin endpoints, the demo endpoint, the webhook's ingest-event, the loops), Runner.RunScriptAsync.
static class InertVerbs
{
    /// <summary>The verbs that write nothing at all (kebab-case, as on the wire). Not item-pr (it can retarget, rename, close and ready a pull request and saves state) and not wait-for-checks (it re-runs a job and saves the re-run).</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "get-item", "my-items", "inspect-facts", "pr-checks", "metrics", "qc-view", "qc-list", "get-delivery", "backlog", "revocations", "operator-releases", "transcript-load", "plant-snapshot-load",
    };

    /// <summary>The gateway's own housekeeping, which keeps the process and its lease alive and writes nothing about an item: the leader lease, the plant snapshot, the revocations (reading and revoking a worker: a credential action that writes no item), the operator's release record, the automatic promotion of uat and prod, the transcripts.</summary>
    public static readonly IReadOnlySet<string> Housekeeping = new HashSet<string>(StringComparer.Ordinal)
    {
        "leader-lease", "plant-snapshot-save", "plant-snapshot-load", "plant-snapshot-await", "plant-snapshot-clear", "revocations", "revoke-worker", "operator-releases", "operator-release", "transcript-save", "transcript-load", "transcript-prune",
    };

    public static bool RunnerMayRun(string verb) => ReadOnly.Contains(verb) || Housekeeping.Contains(verb);

    public static VerbAnswer Refusal(string verb) =>
        BoardVerbAnswer.Rejected(verb, 20, "database", "v2 is configured for this environment and there is no database (FACTORY_DB_CONNECTION): nothing acts until there is one", new System.Text.Json.Nodes.JsonObject { ["retryAfterSeconds"] = 60 });
}

// The router in front of the verb endpoint of such a gateway: the verbs that only read pass through, every other is refused.
sealed class InertVerbRouter(IVerbRouter below) : IVerbRouter
{
    /// <summary>The verbs that read and write nothing.</summary>
    public static IReadOnlySet<string> ReadOnly => InertVerbs.ReadOnly;

    public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) =>
        InertVerbs.ReadOnly.Contains(verb) ? below.RunAsync(verb, stdin, timeout) : Task.FromResult(InertVerbs.Refusal(verb));
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
