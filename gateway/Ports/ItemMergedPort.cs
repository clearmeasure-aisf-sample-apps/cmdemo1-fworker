using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Integrations.Correlation;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Delivery;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// W5.0 (docs/design/fworker-wolverine-implementation.md W5): ItemMerged in the Wolverine host. Where the engine acts (Engine.V2Acts) and, since W5.2, in the shadow run of the delivery train (tdd: DeliveryShadowWiring); v1 registers none of it, and
// v1 emits nothing: no `delivery` flag, no change to Verbs.Merge.ps1 or Verbs.Delivery.ps1. The message is derived from what the ingress inbox takes in, a merge webhook
// ("base.merged", "pr.merged") or the board reconciler's poll of a closed card, by ItemMergedDeriver (Fworker.Workflows.Delivery), which reads the pull request and the item from GitHub with
// GH_TOKEN, as the board reconciler does, and claims a row per (item, merge commit) in the merged-items table so the two sources give one message. The message is recorded in ItemHistory
// through the shadow-history queue; the delivery train (W5.1) consumes it.

/// <summary>Derives ItemMerged from an inbox message. A second handler of IngressInboxMessage beside <see cref="IngressInboxHandler"/>: it needs no accepted event, the merged-items table is its own deduplication. Public because Wolverine's generated code refers to it.</summary>
public static class ItemMergedDerivationHandler
{
    public static async Task<OutgoingMessages> Handle(IngressInboxMessage message, ItemMergedDeriver deriver, Envelope envelope, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var derivation = await deriver.Derive(message.Source, message.EventJson, envelope.Id, ct);   // a GitHub failure throws: the message is retried
        foreach (var m in derivation.Merged) outgoing.Add(m);
        return outgoing;
    }
}

/// <summary>The history record of an ItemMerged, which Wolverine cascades through the outbox. Public because Wolverine's generated code refers to it.</summary>
public static class ItemMergedHistoryHandler
{
    public static ShadowHistoryRecord Handle(ItemMerged m)
    {
        var detail = JsonSerializer.Serialize(new { m.Commit, m.PullRequest, m.Source, m.Gates, Dependencies = m.Dependencies.Select(d => d.ToString()) });
        return new ShadowHistoryRecord(m.Item.ToString(), "delivery.item-merged", detail.Length <= ShadowHistory.MaxDetailChars ? detail : detail[..ShadowHistory.MaxDetailChars], DateTimeOffset.UtcNow);
    }
}

/// <summary>The delivered-to table from the train's output. Public because Wolverine's generated code refers to it.</summary>
public static class DeliveredToHandler
{
    public static Task Handle(DeliveredTo message, DeliveredToTable table, CancellationToken ct) => table.Record(message, DateTimeOffset.UtcNow, ct);
    public static Task Handle(Undelivered message, DeliveredToTable table, CancellationToken ct) => table.Remove(message, ct);
}

static class ItemMergedWiring
{
    /// <summary>The deriver of the gateway; null when it cannot read GitHub (no GH_TOKEN), then the wiring is left out and the merged-items table stays empty.</summary>
    public static ItemMergedDeriver? Make(GatewayComposition c, RepoId home, string connectionString, Func<string, string?> env, HttpClient? http = null)
    {
        var token = env("GH_TOKEN");
        if (RevocationOptions.Unset(token)) return null;
        var root = c.Instance.Root;
        string Str(string name, string fallback) => root?[name]?.ToString() is { Length: > 0 } s ? s : fallback;
        var holds = (root?["holdLabels"] as JsonArray)?.Select(h => h?.ToString() ?? "").Where(h => h != "").ToList();
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var budget = c.GitHubBudget;
        var gates = new ItemGateConfig(Str("autoMergeLabel", "Auto Merge"), Str("videoLabel", "video"), holds);
        var reader = new HttpMergeFactsReader(http, new StaticCredentialSource([], new GitHubCredential("gateway", token!.Trim())), Str("name", "factory-worker"), gates.VideoLabel,
            (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } }));
        var store = new SqlMergedItemStore(() => Open(connectionString));
        var branch = Str("defaultBranch", "master");
        return new ItemMergedDeriver(reader, store, home, () => branch, gates);
    }

    public static FactoryDbContext Open(string connectionString) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString).Options);

    public static void Attach(WolverineHostService host, ItemMergedDeriver deriver, DeliveredToTable table)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(deriver);
            services.AddSingleton(table);
            w.Discovery.IncludeType(typeof(ItemMergedDerivationHandler));
            w.Discovery.IncludeType(typeof(ItemMergedHistoryHandler));
            w.Discovery.IncludeType(typeof(DeliveredToHandler));
        };
    }
}
