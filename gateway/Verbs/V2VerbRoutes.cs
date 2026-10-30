using Fworker.Contracts.Seams;

// W9.0 (docs/design/fworker-wolverine-implementation.md W9): where the verbs that v2 answers are registered. One file per verb group in gateway/Verbs/Routes/ (Item, Merge, Delivery, ReadModels,
// Keeper, Services, Board) lists the verbs of its group (the owners of docs/design/verb-inventory.md) and has a Register that the item W9.2 to W9.8 of that group fills. The QC verbs (qc-*) are W9.1's,
// registered with QC itself. A verb that no group registered runs on v1, as before; there is no flag that moves a verb or an item to v2 (revision of 2026-10-26): a verb is on v2 when the engine acts
// (Engine.V2Acts, the environments of v2Environments) and its group registered it, and the kill switch FACTORY_V2_OFF makes the engine v1 again, with every route gone.
// The table is built once at the start. Where the engine does not act (v1, the shadow run) it is empty and Register is not even called, so a stub or a group not yet filled changes nothing anywhere.

/// <summary>What answers one registered verb on v2: the JSON body the rule script would have read, the time allowed, and the answer in the shape the rule script printed.</summary>
delegate Task<VerbAnswer> V2VerbHandler(string stdin, TimeSpan timeout);

/// <summary>The verbs v2 answers, by name. Filled by the route files; a verb is registered once, and only by the group that owns it.</summary>
sealed class V2VerbRoutes
{
    readonly Dictionary<string, V2VerbHandler> routes = new(StringComparer.Ordinal);

    public static readonly V2VerbRoutes None = new();

    public int Count => routes.Count;
    public IReadOnlyCollection<string> Verbs => routes.Keys;

    public bool TryGet(string verb, out V2VerbHandler handler) => routes.TryGetValue(verb, out handler!);

    /// <summary>Registers one verb of a group. A verb the group does not own, or one registered twice, is a defect of the route file and stops the start.</summary>
    public void Add(IReadOnlyCollection<string> groupVerbs, string verb, V2VerbHandler handler)
    {
        if (!groupVerbs.Contains(verb)) throw new InvalidOperationException($"verb '{verb}' is not a verb of this group");
        if (!routes.TryAdd(verb, handler)) throw new InvalidOperationException($"verb '{verb}' is registered twice");
    }

    /// <summary>The verb groups, in the order they register.</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<string> Verbs)> Groups =
    [
        (ItemRoutes.Group, ItemRoutes.Verbs),
        (MergeRoutes.Group, MergeRoutes.Verbs),
        (DeliveryRoutes.Group, DeliveryRoutes.Verbs),
        (ReadModelRoutes.Group, ReadModelRoutes.Verbs),
        (KeeperRoutes.Group, KeeperRoutes.Verbs),
        (ServiceRoutes.Group, ServiceRoutes.Verbs),
        (BoardRoutes.Group, BoardRoutes.Verbs),
    ];

    /// <summary>The table of this process: empty unless the engine acts; otherwise what every group registers.</summary>
    public static V2VerbRoutes Build(EngineOptions engine, IServiceProvider services)
    {
        if (!engine.V2Acts) return None;
        var routes = new V2VerbRoutes();
        ItemRoutes.Register(routes, services);
        MergeRoutes.Register(routes, services);
        DeliveryRoutes.Register(routes, services);
        ReadModelRoutes.Register(routes, services);
        KeeperRoutes.Register(routes, services);
        ServiceRoutes.Register(routes, services);
        BoardRoutes.Register(routes, services);
        QcRoutes.Register(routes, services);   // W9.1: the qc-* verbs, in no group above (registered with QC itself)
        return routes;
    }
}

/// <summary>S1 with v2 routes: a registered verb is answered by v2, every other verb by the router below (v1, possibly with the saga feed).</summary>
sealed class V2VerbRouter(IVerbRouter below, V2VerbRoutes routes) : IVerbRouter
{
    /// <summary>The router the unregistered verbs go to (the saga feed over v1, or v1).</summary>
    public IVerbRouter Below => below;

    public Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout) =>
        routes.TryGet(verb, out var handler) ? handler(stdin, timeout) : below.RunAsync(verb, stdin, timeout);
}
