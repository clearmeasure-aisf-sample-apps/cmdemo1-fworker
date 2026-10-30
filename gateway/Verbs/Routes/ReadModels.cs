using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

// W9.5 (#357): the read models, owned by Fworker.Workflows.ReadModels: the checks of a pull request (pr-checks, wait-for-checks), the inspector's facts (inspect-facts), the delivery of a change (get-delivery), the model tier of an
// item (model-tier) and the probe of the gateway's secrets (check-secrets). Each answers exactly as its rule script does (docs/design/fworker-wolverine-implementation.md, "W9.5 as built"). Registered to the v2 path only where the engine acts
// (V2VerbRoutes.Build); v1, the shadow run and FACTORY_V2_OFF are unchanged. The wiring they read and write through is gateway/Ports/ReadModelsPort.cs.
static class ReadModelRoutes
{
    public const string Group = "read-models";

    public static readonly IReadOnlyList<string> Verbs = ["pr-checks", "wait-for-checks", "inspect-facts", "get-delivery", "model-tier", "check-secrets"];

    /// <summary>Registers the six verbs. A table built without the gateway's configuration (a test with an empty provider) registers nothing for this group; so does a configuration the read models cannot be made from: the verbs then stay v1's.</summary>
    public static void Register(V2VerbRoutes routes, IServiceProvider services)
    {
        if (services.GetService(typeof(InstanceConfig)) is not InstanceConfig instance) return;
        ReadModelHandlers handlers;
        try
        {
            var budget = services.GetService(typeof(GitHubBudget)) as GitHubBudget;
            Action<string, string?, string?, string?>? observe = budget is null ? null
                : (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } });
            handlers = new ReadModelHandlers(ReadModelParts.Build(instance, observe, locks: ItemLocks.From(services, Environment.GetEnvironmentVariable)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"read models (W9.5) not registered, the verbs stay on v1: {e.GetType().Name}: {e.Message}");
            return;
        }
        Register(routes, handlers);
    }

    /// <summary>The registration over ready handlers (the tests make their own).</summary>
    public static void Register(V2VerbRoutes routes, ReadModelHandlers h)
    {
        var p = h.Parts;
        routes.Add(Verbs, "pr-checks", (stdin, timeout) => h.Run("pr-checks", stdin, timeout, (a, ct) => p.Checks.PrChecks(a, ct)));
        routes.Add(Verbs, "wait-for-checks", (stdin, timeout) => h.Run("wait-for-checks", stdin, timeout, (a, ct) => p.Checks.WaitForChecks(a, ct)));
        routes.Add(Verbs, "inspect-facts", (stdin, timeout) => h.Run("inspect-facts", stdin, timeout, (a, ct) => p.Inspect.InspectFacts(a, ct)));
        routes.Add(Verbs, "get-delivery", (stdin, timeout) => h.Run("get-delivery", stdin, timeout, (a, ct) => p.Delivery.GetDelivery(a, ct)));
        routes.Add(Verbs, "model-tier", (stdin, timeout) => h.Run("model-tier", stdin, timeout, (a, ct) => p.Tier.ModelTier(a, ct)));
        routes.Add(Verbs, "check-secrets", (stdin, timeout) => h.Run("check-secrets", stdin, timeout, (_, ct) => p.Secrets.CheckSecrets(ct)));
    }
}
