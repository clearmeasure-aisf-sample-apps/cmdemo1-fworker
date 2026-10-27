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
