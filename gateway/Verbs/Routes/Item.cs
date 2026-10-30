using Fworker.Contracts.Seams;

// W9.2 (#354): the item verbs, owned by the WorkItemSaga (Fworker.Workflows.WorkItem.Verbs). Each answers exactly as its rule script does (same fields, same exit codes, same writes to GitHub, the board and the state comment, in
// the state comment's v1 format), and the saga hears what happened (V2VerbFeed for the decision verbs, ItemAdminFeed for a park and an unpark). A few calls are the script's still, through the runner: the accept of a QC environment (W9.1),
// the completion of Development with its CI rule and the pull request of the Copilot owner task (W9.4, W9.5). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1, the shadow run and FACTORY_V2_OFF are unchanged.
// The wiring they read and write through is gateway/Ports/ItemVerbsPort.cs.
static class ItemRoutes
{
    public const string Group = "item";

    public static readonly IReadOnlyList<string> Verbs = Fworker.Workflows.WorkItem.Verbs.ItemVerbs.Verbs;

    /// <summary>Registers the eighteen verbs. A table built without the gateway's configuration (a test with an empty provider) registers nothing for this group; so does a gateway that cannot reach GitHub: the verbs then stay v1's.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (ItemVerbsWiring.Make(services) is not { } router) return;
        Register(routes, router);
    }

    /// <summary>The registration over a ready router (the tests make their own).</summary>
    public static void Register(V2VerbRoutes routes, IVerbRouter router)
    {
        foreach (var verb in Verbs) routes.Add(Verbs, verb, (stdin, timeout) => router.RunAsync(verb, stdin, timeout));
    }
}
