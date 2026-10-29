// W9.0 stub, filled by W9.5 (#357): the read models (Fworker.Workflows.ReadModels): checks, delivery, model tier, secrets. Registered to the v2 path only where the engine acts (V2VerbRoutes.Build); v1 routing is unchanged.
static class ReadModelRoutes
{
    public const string Group = "read-models";

    public static readonly IReadOnlyList<string> Verbs = ["pr-checks", "wait-for-checks", "inspect-facts", "get-delivery", "model-tier", "check-secrets"];

    /// <summary>W9.5 registers each verb here with <c>routes.Add(Verbs, "pr-checks", handler)</c>. Nothing is registered yet: every read model verb still runs on v1.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services) { }
}
