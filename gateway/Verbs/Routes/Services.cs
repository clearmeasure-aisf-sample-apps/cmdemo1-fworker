// W9.7 (#359): the services (revocations, git credentials, operator releases, metrics) of gateway/Identity and gateway/Operator on v2. These touch credentials and revoke: the owner reviews that pull request (CLAUDE.md,
// protected areas). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged. The six verbs answer as their rule scripts do (gateway/Identity/ServiceVerbs.cs); nothing here
// decides who may call them: VerbPolicy.AuthorizeVerb, the pipeline's own refusals (not-owner for git-credential, use-endpoint for the verbs that have an endpoint) and the endpoints' guards come first, as in v1.
static class ServiceRoutes
{
    public const string Group = "services";

    public static readonly IReadOnlyList<string> Verbs = ["revocations", "revoke-worker", "git-credential", "operator-releases", "operator-release", "metrics"];

    /// <summary>Registers each verb of the group. Nothing is registered where the service verbs are not composed (a provider without them), so a table built without the gateway's services stays empty.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (services.GetService(typeof(ServiceVerbs)) is not ServiceVerbs verbs) return;
        foreach (var verb in Verbs) routes.Add(Verbs, verb, verbs.Handler(verb));
    }
}
