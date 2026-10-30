using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Incident;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

// W8.1 (#349, docs/design/wolverine-factory.md 3.3): the IncidentSaga in the gateway, only where the engine acts (Engine.V2Acts), in the environments that incidents.environments names (config/instance.json; the owner's
// decision of 2026-10-26: tdd). What the gateway adds to the saga:
//  - the facts it listens to: the Octopus watcher's deployment events (a deployment running suppresses the alerts and health reads; a release deployed shortly before the incident correlates with it), the hotfix item's
//    saga (merged, parked) and the delivery train's DeliveredTo, the train's answer to the rollback request;
//  - IncidentHealthJob: the health reads of the confirmation rule, once a minute on the leader (the same endpoint and overall status the delivery train reads), as HealthObserved;
//  - IncidentIssues: the incident issue, its comments and the hotfix item on GitHub (Integrations' IncidentGitHub), idempotent by markers; without a credential they do nothing and say so in the log.
// What is NOT here: the alert webhook (W8.2), the train's execution of the rollback (W8.3: until then the train answers that it cannot, and the incident goes to its owner), the analyst session of the triage
// (TriageRequested has no consumer yet) and the attachment of the delivery train in a host where the engine acts (W9.9: until then the train's hold and rollback messages have no route and are dropped).

/// <summary>The health reads of the watched environments, as the confirmation rule takes them: one read of the health endpoint's overall status per environment that has a health URL.</summary>
public sealed class IncidentHealthJob(IncidentSettings settings, IDeliveryPolicies policies, HttpClient app, Func<HealthObserved, Task<bool>> publish, Func<bool> mayAct, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<string> Run(CancellationToken ct)
    {
        if (!mayAct()) return "not-leader";
        var read = 0;
        foreach (var p in policies.All().Where(p => settings.Watches(p.Environment) && p.UrlOf(p.Environment) is not null))
        {
            ct.ThrowIfCancellationRequested();
            var (status, detail) = await Probe(p.UrlOf(p.Environment)!, p.HealthPath, ct);
            // (a host that is not up takes no read: the next run reads again, a read is a moment and is not kept)
            if (await publish(new HealthObserved(TrainKey.Of(p.Deployable, p.Environment), status, detail, now()))) read++;
        }
        return read == 0 ? "none" : $"{read} read";
    }

    /// <summary>The overall status of the health endpoint; "unreachable" when it cannot be read at all.</summary>
    internal async Task<(string Status, string Detail)> Probe(string url, string path, CancellationToken ct)
    {
        try
        {
            using var h = await app.GetAsync(url + path, ct);
            var text = await h.Content.ReadAsStringAsync(ct);
            JsonObject? o = null; try { o = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
            if (o?["overallStatus"]?.ToString() is { Length: > 0 } s) return (s, "");
            return ($"status {(int)h.StatusCode}", "no overallStatus in the answer");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return ("unreachable", e.GetType().Name); }
    }
}

/// <summary>The incident issue and the hotfix item on GitHub, in the home repository. A fault of GitHub that a later try gets past is an IncidentIssueFault (the command is run again with growing pauses).</summary>
public sealed class GitHubIncidentIssues(IncidentGitHub github, RepoId home, IReadOnlyList<string> hotfixLabels) : IIncidentIssues
{
    const string HotfixLabel = "hotfix";

    static string IncidentMarker(string key, int generation) => IncidentGitHub.Marker("incident", $"{key}#{generation}");

    public async Task<int> Open(string key, int generation, string title, string body, string label, CancellationToken ct)
        => await Guard(() => github.EnsureIssue(home, IncidentMarker(key, generation), title, body, [label], label, ct));

    public Task Comment(string key, int generation, int number, string commentKey, string body, CancellationToken ct)
        => Guard(async () =>
        {
            var n = number > 0 ? number : await Locate(key, generation, ct);
            if (n > 0) await github.Comment(home, n, commentKey, body, ct);
        });

    public Task Close(string key, int generation, int number, string note, CancellationToken ct)
        => Guard(async () =>
        {
            var n = number > 0 ? number : await Locate(key, generation, ct);
            if (n == 0) return;
            await github.Comment(home, n, $"close|{key}#{generation}", note, ct);
            await github.Close(home, n, ct);
        });

    public async Task<ItemId> CreateHotfix(string key, int generation, string title, string body, CancellationToken ct)
        => await Guard(async () =>
        {
            var number = await github.EnsureIssue(home, IncidentGitHub.Marker("hotfix", $"{key}#{generation}"), title, body, hotfixLabels, HotfixLabel, ct);
            var item = new ItemId(home, number);
            // the card is moved by the saga's projection, which refuses an item that is not on the board
            await github.AddToBoard(item, ct);
            return item;
        });

    Task<int> Locate(string key, int generation, CancellationToken ct) => github.Find(home, IncidentMarker(key, generation), "incident", ct);

    static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (HttpRequestException e) { throw new IncidentIssueFault($"GitHub: {e.Message}", e); }
    }

    static async Task Guard(Func<Task> call)
    {
        try { await call(); }
        catch (HttpRequestException e) { throw new IncidentIssueFault($"GitHub: {e.Message}", e); }
    }
}

/// <summary>What the gateway builds for the incident saga.</summary>
sealed record IncidentParts(IncidentService Service, IncidentHistory History, IncidentSettings Settings, IncidentRouter Router, IncidentIssuePort Issues, IDeliveryPolicies Policies, IncidentHealthJob Health);

static class IncidentWiring
{
    /// <summary>The settings of the configuration: whether any environment is watched is known before the host is built.</summary>
    public static IncidentSettings SettingsOf(GatewayComposition c) => IncidentSettings.From(c.Instance.Root);

    /// <summary>
    /// The incident saga's parts on the database of the host; null when no environment is watched or the catalog names no deployable. <paramref name="publish"/> hands a health read to the host (false: the host is
    /// not up); <paramref name="board"/> is the board the hotfix item is put on.
    /// </summary>
    public static IncidentParts? Make(GatewayComposition c, ISystemCatalog catalog, string connectionString, Func<string, string?> env, Func<HealthObserved, Task<bool>> publish, Func<BoardRef?>? board = null, HttpClient? github = null, HttpClient? app = null)
    {
        var settings = SettingsOf(c);
        if (settings.Environments.Count == 0) return null;
        Func<FactoryDbContext> open = () => WorkItemWiring.Open(connectionString);
        var store = new SqlIncidentStore(open);
        var policies = new CatalogDeliveryPolicies(() => catalog.Current, DeliveryShadowSettings.For(c.Instance, () => catalog.Current, c.Engine.V2Acts));
        var service = new IncidentService(store, new IncidentSaga(settings));
        var router = new IncidentRouter(settings, policies, store);
        var health = new IncidentHealthJob(settings, policies, app ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, publish, () => c.Leader.MayAct);
        return new IncidentParts(service, new IncidentHistory(open), settings, router, new IncidentIssuePort(Issues(c, env, board ?? (() => catalog.Current.Board), github)), policies, health);
    }

    /// <summary>The GitHub side, or null without a credential (GH_TOKEN or the author App) or a home repository.</summary>
    static IIncidentIssues? Issues(GatewayComposition c, Func<string, string?> env, Func<BoardRef?> board, HttpClient? http)
    {
        var tokens = GitHubTokenProvider.FromEnvironment(env);
        if (tokens is null || c.Instance.Home is not { } home) return null;
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var credentials = new ProviderCredentialSource(tokens);
        var clients = new GitHubClientFactory(http, credentials, new Fworker.Integrations.GitHub.GitHubBudget());
        var root = c.Instance.Root;
        string Text(string name, string fallback) { try { return root?[name]?.ToString() is { Length: > 0 } s ? s : fallback; } catch { return fallback; } }
        string Top() { try { return root?["priorityLabels"]?["top"]?.ToString() is { Length: > 0 } s ? s : "priority:top"; } catch { return "priority:top"; } }
        // the hotfix is labelled to merge by itself once its gates hold and to rank first for the v1 plant too
        return new GitHubIncidentIssues(new IncidentGitHub(clients, http, credentials, board), home, ["hotfix", Top(), Text("autoMergeLabel", "Auto Merge")]);
    }

    public static void Attach(WolverineHostService host, IncidentParts p)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(p.Service);
            services.AddSingleton(p.History);
            services.AddSingleton(p.Settings);
            services.AddSingleton(p.Router);
            services.AddSingleton(p.Issues);
            services.TryAddSingleton(p.Policies);   // (the trains' view of the environments; the delivery train's own wiring adds the same)
            w.Discovery.IncludeType(typeof(IncidentHandlers));
            w.Discovery.IncludeType(typeof(IncidentRouting));
            w.Discovery.IncludeType(typeof(IncidentHistoryHandler));
            w.Discovery.IncludeType(typeof(IncidentIssueHandlers));
            IncidentTopology.Route(w);
            // The Octopus watcher's deployment events reach the incidents through one queue, in the order the watcher published them (a Started handled after its Succeeded would suppress the reads for good). Where the
            // delivery train is attached to an acting host (W9.9) it routes all six Octopus events through DeliveryTopology.RouteOctopusEvents, which includes this one: remove this line then.
            w.PublishMessage<Fworker.Contracts.Seams.OctopusDeploymentChanged>().ToLocalQueue(DeliveryTopology.OctopusQueue);
            w.LocalQueue(DeliveryTopology.OctopusQueue).Sequential();
        };
    }
}
