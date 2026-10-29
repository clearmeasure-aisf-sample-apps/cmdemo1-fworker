// W9.0 stub, filled by W9.6 (#358): the backlog keeper's commands and the plant manager's flow verb (Fworker.Workflows.Keeper, Manager.cs). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1
// routing is unchanged.
static class KeeperRoutes
{
    public const string Group = "keeper";

    public static readonly IReadOnlyList<string> Verbs = ["backlog", "amend-criteria", "split-item", "create-item", "set-priority", "link-dependency", "manager-action"];

    /// <summary>W9.6 registers each verb here with <c>routes.Add(Verbs, "backlog", handler)</c>. Nothing is registered yet: every keeper and manager verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
