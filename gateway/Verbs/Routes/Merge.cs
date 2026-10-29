// W9.0 stub, filled by W9.3 (#355): the merge train verbs, owned by the MergeTrainSaga (Fworker.Workflows.Merge). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
static class MergeRoutes
{
    public const string Group = "merge";

    public static readonly IReadOnlyList<string> Verbs = ["approve-pr", "merge", "clear-hold", "base-watch", "base-update-branch"];

    /// <summary>W9.3 registers each verb here with <c>routes.Add(Verbs, "merge", handler)</c>. Nothing is registered yet: every merge verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
