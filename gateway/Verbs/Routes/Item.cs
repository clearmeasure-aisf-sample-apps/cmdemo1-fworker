// W9.0 stub, filled by W9.2 (#354): the item verbs, owned by the WorkItemSaga (Fworker.Workflows.WorkItem). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
static class ItemRoutes
{
    public const string Group = "item";

    public static readonly IReadOnlyList<string> Verbs =
    [
        "get-item", "append-body", "comment", "claim-item", "accept", "release", "my-items", "record-review", "move-next", "complete",
        "validation-verdict", "open-pr", "update-pr", "item-pr", "park-item", "unpark-item", "resettle-item", "expire-claim",
    ];

    /// <summary>W9.2 registers each verb here with <c>routes.Add(Verbs, "get-item", handler)</c>. Nothing is registered yet: every item verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
