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
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

// W5.0 (docs/design/fworker-wolverine-implementation.md W5): ItemMerged in the Wolverine host. Where the engine acts (Engine.V2Acts) and, since W5.2, in the shadow run of the delivery train (tdd: DeliveryShadowWiring); v1 registers none of it, and
// v1 emits nothing: no `delivery` flag, no change to Verbs.Merge.ps1 or Verbs.Delivery.ps1. The message is derived from what the ingress inbox takes in, a merge webhook
// ("base.merged", "pr.merged") or the board reconciler's poll of a closed card, by ItemMergedDeriver (Fworker.Workflows.Delivery), which reads the pull request and the item from GitHub with
// GH_TOKEN, as the board reconciler does, and claims a row per (item, merge commit) in the merged-items table so the two sources give one message. The message is recorded in ItemHistory
// through the shadow-history queue; the delivery train (W5.1) consumes it.

/// <summary>Derives ItemMerged from an inbox message. A second handler of IngressInboxMessage beside <see cref="IngressInboxHandler"/>: it needs no accepted event, the merged-items table is its own deduplication. Public because Wolverine's generated code refers to it.</summary>
public static class ItemMergedDerivationHandler
{
    /// <summary>
    /// W9.10g: whether the last step is a retry every hour for ever (where the engine acts) or the dead letters after the hour (the shadow run, as before). Set by <see cref="ItemMergedWiring.Attach"/> before the host builds its handler chains.
    /// </summary>
    public static volatile bool RetryIndefinitely;

    /// <summary>
    /// A GitHub that does not answer (a network fault, a 5xx or a rate limit, which the reader throws as <see cref="MergeFactsException"/>) is tried again with growing pauses and then every few minutes, for
    /// this handler only: the merge is derived from the message or from nothing (the poll publishes a closed card once), so a transient fault must not end in the dead letters after three quick attempts
    /// (#494). W9.10g, where the engine acts: the last step is a retry every hour, for ever, instead of the dead letters: a GitHub that stays away for a day must not lose a merge (the poll publishes a closed card once and the webhook once), and a
    /// derivation that keeps failing shows in /api/v1/status (scheduled envelopes, oldest overdue age), not in a pile of dead letters nobody replays. A permanent failure (an app that is not installed) is not in the list: it is not retried
    /// at all and goes to the dead letters at once, where it can be seen.
    /// </summary>
    public static void Configure(HandlerChain chain)
    {
        var rule = chain.OnException<HttpRequestException>().Or<MergeFactsException>().Or<TaskCanceledException>().Or<TimeoutException>()
            .RetryWithCooldown(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)).Then.ScheduleRetry(TimeSpan.FromMinutes(5)).Then.ScheduleRetry(TimeSpan.FromMinutes(15));
        if (RetryIndefinitely) rule.Then.ScheduleRetryIndefinitely(TimeSpan.FromHours(1));
        else rule.Then.ScheduleRetry(TimeSpan.FromHours(1)).Then.MoveToErrorQueue();
    }

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
        var tokens = GitHubTokenProvider.FromEnvironment(env);   // GH_TOKEN, else the author App's installation token
        if (tokens is null) return null;
        var root = c.Instance.Root;
        string Str(string name, string fallback) => root?[name]?.ToString() is { Length: > 0 } s ? s : fallback;
        var holds = (root?["holdLabels"] as JsonArray)?.Select(h => h?.ToString() ?? "").Where(h => h != "").ToList();
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var budget = c.GitHubBudget;
        var gates = new ItemGateConfig(Str("autoMergeLabel", "Auto Merge"), Str("videoLabel", "video"), holds);
        var reader = new HttpMergeFactsReader(http, new ProviderCredentialSource(tokens), Str("name", "factory-worker"), gates.VideoLabel,
            (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } }));
        var store = new SqlMergedItemStore(() => Open(connectionString));
        var branch = Str("defaultBranch", "master");
        return new ItemMergedDeriver(reader, store, home, () => branch, gates);
    }

    public static FactoryDbContext Open(string connectionString) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString).Options);

    public static void Attach(WolverineHostService host, ItemMergedDeriver deriver, DeliveredToTable table, bool acting = false)
    {
        if (acting) ItemMergedDerivationHandler.RetryIndefinitely = true;   // (W9.10g: read when the host builds the handler chains, which is after this)
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
