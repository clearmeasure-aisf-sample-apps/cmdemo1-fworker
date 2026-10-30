using Fworker.Contracts.Seams;
using Fworker.Workflows.Keeper;

// W9.6 (#358): the backlog keeper's commands and the plant manager's flow verb on v2 (Fworker.Workflows.Keeper, the port of Verbs.Keeper.ps1 and Verbs.Manager.ps1; the gateway's side of it is Ports/KeeperPort.cs). Registered to the v2
// path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged. The pipeline in front of the route is the same for both engines: the caller's authorization, the keeper's slot and its daily limit of new items, the
// supervisor's step budget, the mode, the hourly limit and the ticket of a manager action, and the plant's hooks after the answer; the route answers what the rule script answered, field for field.
static class KeeperRoutes
{
    public const string Group = "keeper";

    public static readonly IReadOnlyList<string> Verbs = ["backlog", "amend-criteria", "split-item", "create-item", "set-priority", "link-dependency", "manager-action"];

    /// <summary>Registers the seven verbs. Without a GitHub identity or the instance's home repository nothing is registered and the verbs run on v1, as they did.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (KeeperWiring.Make(services) is not { } keeper) return;
        foreach (var verb in Verbs) routes.Add(Verbs, verb, (stdin, timeout) => keeper.Run(verb, stdin, timeout));
    }
}
