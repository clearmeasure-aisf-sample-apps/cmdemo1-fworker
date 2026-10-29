// W9.0 stub, filled by W9.7 (#359): the services (revocations, git credentials, operator releases, metrics) of gateway/Identity and gateway/Operator. These touch credentials and revoke: the owner reviews that pull
// request (CLAUDE.md, protected areas). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
static class ServiceRoutes
{
    public const string Group = "services";

    public static readonly IReadOnlyList<string> Verbs = ["revocations", "revoke-worker", "git-credential", "operator-releases", "operator-release", "metrics"];

    /// <summary>W9.7 registers each verb here with <c>routes.Add(Verbs, "revocations", handler)</c>. Nothing is registered yet: every service verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
