// W9.3 (#355): the merge train verbs, owned by the MergeTrainSaga (Fworker.Workflows.Merge). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
// The handlers are gateway/Verbs/MergeVerbs.cs: each verb is v2's for the items the WorkItemSaga owns and, in the hybrid composition, the rule script's for every other item and for merge policy peer-approved.
static class MergeRoutes
{
    public const string Group = "merge";

    public static readonly IReadOnlyList<string> Verbs = ["approve-pr", "merge", "clear-hold", "base-watch", "base-update-branch"];

    /// <summary>Registers the five verbs. Nothing is resolved here: the saga's parts need the Wolverine host, which is built after this table, so the handlers find them at their first call.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        var handlers = MergeVerbHandlers.For(services);
        routes.Add(Verbs, "approve-pr", handlers.ApprovePr);
        routes.Add(Verbs, "merge", handlers.Merge);
        routes.Add(Verbs, "clear-hold", handlers.ClearHold);
        routes.Add(Verbs, "base-watch", handlers.BaseWatch);
        routes.Add(Verbs, "base-update-branch", handlers.BaseUpdateBranch);
    }
}
