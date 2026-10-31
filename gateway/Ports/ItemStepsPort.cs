using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Delivery.Verbs;
using Fworker.Workflows.WorkItem;

// W9.10f: the sweep's item steps in the gateway. The rules are the saga's (Fworker.Workflows.WorkItem.SweepFlow); this is what they reach GitHub and the item's owner through, where the engine acts:
//  - DeliveryKitSource: the kit of the delivery verbs (the same GitHub identity, transport and configuration) for the watcher's reads of master's CI and of a pull request (ItemStepReads); the re-run of the failed
//    jobs is its one write and is fenced by the leader lease.
//  - OwnerRelease: the saga's ReleaseOwner (OwnerReleaseRequested) gives the item's claim up, as the sweep's idle release did: the claim and its ref go through the verb `release`, and the plant forgets the owner. It goes
//    through the verb router (v2's item verbs where they answer, else the script), so it is no new call of the rule scripts. An item with no claim is released already.
//  - the watcher itself (ItemStepsJob) is wired in GatewayWolverineExtensions beside the checks watcher of the feature plan.

/// <summary><see cref="IRuleKitSource"/> over the delivery verbs' host, made when the first read needs it (a gateway without a GitHub credential fails the read, which the watcher tries again at its next run).</summary>
sealed class DeliveryKitSource(Func<DeliveryVerbHost> host) : IRuleKitSource
{
    public Task<T> With<T>(Func<RuleKit, Task<T>> body, CancellationToken ct) => host().WithKit(body, ct);
}

/// <summary>The release of an item's owner that waited out a hold. Never throws for an owner that is gone: the claim may be released already, the item closed, or another owner may hold it by now.</summary>
public sealed class OwnerRelease(Func<IVerbRouter?> router, Action<int, string>? forget = null)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<string> Run(OwnerReleaseRequested m, CancellationToken ct)
    {
        if (router() is not { } verbs) throw new InvalidOperationException("the gateway has no verb router yet");
        var item = m.Item.Number;
        var got = await verbs.RunAsync("get-item", new JsonObject { ["item"] = item }.ToJsonString(), Timeout);
        // an item that is not there (or not this gateway's) has no owner to release; any other refusal is tried again by the host
        if (got.Code is 11 or 12 or 10) return "no-item";
        if (got.Code != 0) throw new InvalidOperationException($"get-item answered {got.Code}");
        var claim = Parse(got.Output)?["state"]?["claim"] as JsonObject;
        var holder = claim?["holder"]?.ToString() ?? "";
        if (holder.Length == 0) return "no-claim";
        var released = await verbs.RunAsync("release", new JsonObject { ["item"] = item, ["holder"] = holder, ["reason"] = m.Reason }.ToJsonString(), Timeout);
        if (released.Code == 40) return "other-owner";   // claim-not-held: someone took the item meanwhile
        if (released.Code != 0) throw new InvalidOperationException($"release answered {released.Code}");
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["lease"] = "owner-released-idle", ["item"] = item, ["holder"] = holder, ["reason"] = m.Reason }.ToJsonString());
        try { forget?.Invoke(item, holder); } catch (Exception e) { Console.Error.WriteLine($"owner release of {m.Item}: the plant could not forget {holder}: {e.GetType().Name}"); }
        return "released";
    }

    static JsonObject? Parse(string text) { try { return JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return null; } }
}

/// <summary>The handler of the saga's request to release an owner. Public because Wolverine's generated code refers to it.</summary>
public static class OwnerReleaseHandler
{
    public static Task Handle(OwnerReleaseRequested m, OwnerRelease port, CancellationToken ct) => port.Run(m, ct);
}

static class ItemStepsWiring
{
    /// <summary>Adds the owner release and its handler to the host's services (where the engine acts).</summary>
    public static void Attach(WolverineHostService host, OwnerRelease release)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(release);
            w.Discovery.IncludeType(typeof(OwnerReleaseHandler));
        };
    }
}
