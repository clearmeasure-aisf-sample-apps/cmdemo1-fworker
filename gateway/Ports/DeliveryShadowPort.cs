using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;

// W5.2 (docs/design/fworker-wolverine-implementation.md W5): the parts of the delivery train that the acting train (DeliveryActingWiring) is built from: the policy settings, the reads, the
// delivered-to table, and the way the Octopus watcher's events (W4.2) reach the host. The names are those of the shadow run the train was first built for; V2.3 removed the shadow itself
// (the comparison with v1's promotion pass, its store, report and gate). Everything the train reads from GitHub and Octopus is a GET, within the budget rules (HttpTrainReads, OctopusWatchService).

/// <summary>The settings of the policy of each deployable, from v1's configuration (delivery.autoPromote and octopus.*): the home repository's deployables are the project v1 promotes.</summary>
static class DeliveryShadowSettings
{
    public static Func<string, DeliveryPolicySettings?> For(InstanceConfig cfg, Func<CatalogSnapshot> catalog, bool engineActs = false)
    {
        var settings = From(cfg, engineActs);
        return name => catalog().FindDeployable(name) is { } d && cfg.Home is { } home && d.Repository == home ? settings : null;
    }

    /// <summary>
    /// W7.7: <paramref name="engineActs"/> (Engine.V2Acts) turns the rule dependencies-delivered on, when delivery.dependenciesDelivered is; where the engine does not act it stays off whatever the file says.
    /// </summary>
    public static DeliveryPolicySettings? From(InstanceConfig cfg, bool engineActs = false)
    {
        try
        {
            var o = cfg.Root?["octopus"]; var ap = cfg.Root?["delivery"]?["autoPromote"];
            string S(JsonNode? n, string name) => n?[name]?.ToString() ?? "";
            var project = S(o, "project");
            if (project.Length == 0) return null;
            // v1 promotes only where autoPromote is on and names this gateway's environment (AutoPromoteHere); otherwise the train observes
            var acts = cfg.AutoPromoteHere;
            var named = acts && ap?["environments"] is JsonArray a ? a.Select(e => e?.ToString() ?? "").Where(e => e != "").ToList() : [];
            // the train promotes the named environments (v1's AutoPromoter is gone: V3.2)
            var environments = named;
            var names = new Dictionary<string, string>(); var urls = new Dictionary<string, string>();
            foreach (var k in DeliveryPolicy.Chain)
            {
                if (S(o?["environments"], k) is { Length: > 0 } n) names[k] = n;
                if (S(o, k + "Url") is { Length: > 0 } u) urls[k] = u;
            }
            double Num(JsonNode? n, string name, double fallback) => n?[name] is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? d : fallback;
            var window = new HealthWindow(Num(o?["health"], "spacingMinutes", 5), Num(o?["health"], "settleMinutes", 10));
            // W8.3: delivery.rollback.migrationPaths, the path fragments that mark a file as a database migration (the rollback's precondition); absent: DbUp's scripts/Update
            var migrations = (cfg.Root?["delivery"]?["rollback"]?["migrationPaths"] as JsonArray)?.Select(m => m?.ToString() ?? "").Where(m => m != "").ToList();
            return new DeliveryPolicySettings(project, Promoter.Gateway, environments, names, urls, S(o, "healthPath") is { Length: > 0 } hp ? hp : null, window, Num(ap, "signoffWaitHours", 2), engineActs && cfg.DependenciesDelivered, migrations,
                // V2.1: how often a train that waits for the clock or the app looks again, and the least gap between two health reads (v1 passes every 60 s and reads at once): 120 s and 30 s; a test shortens them
                (int)Num(ap, "pollSeconds", 120), (int)Num(ap, "readGapSeconds", 30),
                // v1's Read-EnvHealth: octopus.health.retries tries (3), retrySeconds (20) apart. The train makes one try per look and counts the failures in a row (TrainEvidence.Unreachable)
                (int)Num(o?["health"], "retries", 3), (int)Num(o?["health"], "retrySeconds", 20));
        }
        catch { return null; }
    }
}

/// <summary>
/// S3 for the train: v1's ingress did its part, and the events of the Octopus watcher are also published to the Wolverine host, where the router turns them into the trains' inputs. A problem with
/// the host never reaches the watcher.
/// </summary>
sealed class OctopusShadowIngress : IEventIngress, IDisposable
{
    public const int DefaultCapacity = 10000;
    public static readonly TimeSpan RetryFirst = TimeSpan.FromSeconds(1), RetryMax = TimeSpan.FromSeconds(30);

    readonly IEventIngress inner;
    readonly ShadowOctopusPort port;
    readonly CancellationTokenSource stop = new();
    // one consumer, in the order the watcher published: a poll's release, commit and deployment events must reach the host in that order
    readonly System.Threading.Channels.Channel<IngressEvent> queue;

    public OctopusShadowIngress(IEventIngress inner, ShadowOctopusPort port, int capacity = DefaultCapacity, TimeSpan? retryFirst = null, TimeSpan? retryMax = null)
    {
        this.inner = inner; this.port = port;
        queue = System.Threading.Channels.Channel.CreateBounded<IngressEvent>(new System.Threading.Channels.BoundedChannelOptions(capacity) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = true });
        var first = retryFirst ?? RetryFirst; var max = retryMax ?? RetryMax;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in queue.Reader.ReadAllAsync(stop.Token))
                {
                    // an event is handed to the host or tried again with growing pauses (a host that is restarting, a database that does not answer); the next event waits for it, so the order holds
                    for (var pause = first; !stop.IsCancellationRequested; pause = TimeSpan.FromTicks(Math.Min(max.Ticks, pause.Ticks * 2)))
                    {
                        try { if (await port.Publish(ev)) break; Console.Error.WriteLine($"delivery shadow: the host is not up, {ev.GetType().Name} is tried again"); }
                        catch (Exception e) { Console.Error.WriteLine($"delivery shadow: {e.GetType().Name} publishing {ev.GetType().Name}, tried again"); }
                        await Task.Delay(pause, stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Hands the event on. A queue that is full is not a silent drop: the exception reaches the watcher, which has not marked the event published and offers it again at its next poll (v1's part of
    /// this call is idempotent by the event's key).
    /// </summary>
    public void Ingest(IngressEvent ev)
    {
        inner.Ingest(ev);
        if (!port.Enabled || ev is not (OctopusReleaseCreated or ReleaseCommitResolved or OctopusDeploymentChanged or SignOffRequested or OctopusReplayBegan or OctopusReplayComplete)) return;
        if (!queue.Writer.TryWrite(ev)) throw new InvalidOperationException($"the delivery shadow's queue is full ({queue.Reader.Count} events): {ev.GetType().Name} is offered again at the next poll");
    }

    public void Dispose() { stop.Cancel(); (inner as IDisposable)?.Dispose(); }   // the ingress inside (the inbox's retry loop) stops with it
}

static class DeliveryShadowWiring
{
    public const string TrainQueue = DeliveryTopology.TrainQueue;

    /// <summary>The train's parts for a gateway with a home repository and a database; null when the environment cannot read GitHub (no GH_TOKEN): the train is left out then.</summary>
    public static DeliveryShadowParts? Make(GatewayComposition c, ISystemCatalog catalog, string connectionString, Func<string, string?> env, HttpClient? github = null, HttpClient? app = null)
    {
        var tokens = Fworker.Integrations.GitHub.GitHubTokenProvider.FromEnvironment(env);   // GH_TOKEN, else the author App's installation token
        if (tokens is null || c.Instance.Home is not { } home) return null;
        var root = c.Instance.Root;
        string Str(string name, string fallback) => root?[name]?.ToString() is { Length: > 0 } s ? s : fallback;
        var holds = (root?["holdLabels"] as JsonArray)?.Select(h => h?.ToString() ?? "").Where(h => h != "").ToList();
        var api = env("GITHUB_API_URL");
        if (github is null)
        {
            github = new HttpClient(new Fworker.Integrations.GitHub.GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
            github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            github.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
        }
        app ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var budget = c.GitHubBudget;
        void Observe(string resource, string? remaining, string? reset, string? limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } });
        var gates = new ItemGateConfig(Str("autoMergeLabel", "Auto Merge"), Str("videoLabel", "video"), holds);
        var facts = new Fworker.Integrations.GitHub.HttpMergeFactsReader(github, new Fworker.Integrations.GitHub.ProviderCredentialSource(tokens), Str("name", "factory-worker"), gates.VideoLabel,
            (resource, remaining, reset, limit) => Observe(resource, remaining, reset, limit));
        var policies = new CatalogDeliveryPolicies(() => catalog.Current, DeliveryShadowSettings.For(c.Instance, () => catalog.Current, c.Engine.V2Acts));
        var reads = new HttpTrainReads(github, app, Str("name", "factory-worker"), facts, gates, policies, Observe, null, () => budget.Low().Low);
        return new DeliveryShadowParts(policies, reads, new DeliveredToTable(() => ItemMergedWiring.Open(connectionString)), connectionString);
    }

    /// <summary>The Octopus events to the host; the type is the event's own so that Wolverine routes it by it.</summary>
    public static Task<bool> Publish(WolverineHostService host, object ev) => ev switch
    {
        OctopusReleaseCreated e => host.PublishAsync(e),
        ReleaseCommitResolved e => host.PublishAsync(e),
        OctopusDeploymentChanged e => host.PublishAsync(e),
        SignOffRequested e => host.PublishAsync(e),
        OctopusReplayBegan e => host.PublishAsync(e),
        OctopusReplayComplete e => host.PublishAsync(e),
        _ => Task.FromResult(false),
    };
}

/// <summary>What the train needs, made once at the start.</summary>
sealed record DeliveryShadowParts(IDeliveryPolicies Policies, ITrainReads Reads, DeliveredToTable Delivered, string ConnectionString);

/// <summary>The way for the watcher's events into the host, where the engine acts. <see cref="Enabled"/> is false when the train could not be built (no GH_TOKEN): the events then go nowhere.</summary>
sealed class ShadowOctopusPort(Func<object, Task<bool>> publish, bool enabled)
{
    public bool Enabled => enabled;
    public Task<bool> Publish(object ev) => enabled ? publish(ev) : Task.FromResult(false);

    /// <summary>
    /// The port when the incident saga is attached. The port of the acting train, when there is one that is enabled, already carries every Octopus event to the host, and the incident saga takes the deployments from the same publish:
    /// it is kept. (V2.1: the line that built the incident-only port replaced it unconditionally, and the releases, the replay markers and the Sign-offs of the watcher were dropped: a train that acts for an environment with a
    /// Sign-off never answered one.) Without a train the port carries the deployments to the incident saga and nothing else.
    /// </summary>
    public static ShadowOctopusPort WithIncidents(ShadowOctopusPort? train, Func<object, Task<bool>> publishToHost)
        => train is { Enabled: true } ? train : new ShadowOctopusPort(ev => ev is Fworker.Contracts.Seams.OctopusDeploymentChanged ? publishToHost(ev) : Task.FromResult(true), enabled: true);
}
