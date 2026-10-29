// W9.0 stub, filled by W9.4 (#356): the delivery train verbs, owned by the DeliveryTrainSaga (Fworker.Workflows.Delivery). Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is
// unchanged. auto-promote is in the group although the plan's row does not name it (docs/design/verb-inventory.md): the inventory gives it to the delivery train with settle and publish-demo.
static class DeliveryRoutes
{
    public const string Group = "delivery";

    public static readonly IReadOnlyList<string> Verbs = ["settle", "publish-demo", "ci-release", "copilot-settle", "copilot-owner", "private-build", "auto-promote"];

    /// <summary>W9.4 registers each verb here with <c>routes.Add(Verbs, "settle", handler)</c>. Nothing is registered yet: every delivery verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
