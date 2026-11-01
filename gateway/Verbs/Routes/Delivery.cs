// W9.4 (#356): the delivery train verbs, owned by the DeliveryTrainSaga (Fworker.Workflows.Delivery). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
// settle, publish-demo, ci-release, copilot-settle, copilot-owner and private-build are answered by Fworker.Workflows.Delivery.Verbs (ported one for one from Verbs.Delivery.ps1 and Verbs.Pr.ps1) through
// Ports/DeliveryVerbHost.cs. auto-promote is in the group (docs/design/verb-inventory.md) but is not a route: the promotion is the train's own (DeliveryTrainSaga with the executor of PromoteRelease and
// AnswerSignOff, Ports/DeliveryExecutorPort.cs), and the loop that called the verb (AutoPromoter) does not start where the engine acts.
static class DeliveryRoutes
{
    public const string Group = "delivery";

    public static readonly IReadOnlyList<string> Verbs = ["settle", "publish-demo", "ci-release", "copilot-settle", "copilot-owner", "private-build"];

    /// <summary>The verbs this group answers on v2 (auto-promote is the executor's, not a route).</summary>
    public static readonly IReadOnlyList<string> Routed = ["settle", "publish-demo", "ci-release", "copilot-settle", "copilot-owner", "private-build"];

    /// <summary>
    /// Registers each routed verb. The host (GitHub, the state comment, Octopus) is made at the first call, not here: the table is built while the container is. The gateway's own calls of these verbs
    /// (the CI release, the Copilot owner and settle, the demo upload) reach the routes through the Runner (W9.9a).
    /// </summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        var host = new Lazy<DeliveryVerbHost>(() => DeliveryVerbHost.Make(services), LazyThreadSafetyMode.PublicationOnly);   // (a failed construction is not kept: the next call tries again)
        foreach (var verb in Routed)
        {
            var name = verb;
            routes.Add(Verbs, name, (stdin, timeout) => host.Value.Run(name, stdin, timeout));
        }
    }
}
