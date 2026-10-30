using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Wolverine;
using Wolverine.ErrorHandling;

// W5.2 (docs/design/fworker-wolverine-implementation.md W5): the delivery train in the gateway, in SHADOW only (Engine.ShadowRuns: tdd until the cutover). It is fed what v1 leaves visible:
// ItemMerged (derived from the merge webhook and the board poll by W5.0) and the events of the Octopus watcher (W4.2, read only). It decides as the train would and publishes TrainDecided,
// PromoteRelease and AnswerSignOff; the handlers of a shadow host (DeliveryShadowHandlers) only RECORD them, so nothing is executed: no Octopus write, no board, comment or Git write. v1
// keeps promoting with the rule scripts, and its pass's actions are given to the same DeliveryShadow, which compares them (DeliveryShadow, ShadowComparer) and stores the comparisons.
// Everything the shadow reads from GitHub and Octopus is a GET, within v1's budget rules (HttpTrainReads, OctopusWatchService).

/// <summary>The settings of the policy of each deployable, from v1's configuration (delivery.autoPromote and octopus.*): the home repository's deployables are the project v1 promotes.</summary>
static class DeliveryShadowSettings
{
    public static Func<string, DeliveryPolicySettings?> For(InstanceConfig cfg, Func<CatalogSnapshot> catalog, bool engineActs = false)
    {
        var settings = From(cfg, engineActs);
        return name => catalog().FindDeployable(name) is { } d && cfg.Home is { } home && d.Repository == home ? settings : null;
    }

    /// <summary>
    /// W7.7: <paramref name="engineActs"/> (Engine.V2Acts) turns the rule dependencies-delivered on, when delivery.dependenciesDelivered is. In the shadow run it stays off whatever the file says: v1 has no
    /// such rule, so a train that held an item would differ from v1's promotions for a reason that is no defect.
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
            // W9.10p: where the engine acts the train promotes the named environments only when delivery.autoPromote.trainActs is on (W9.12). Until then v1's AutoPromoter promotes them and the train decides them in shadow
            // (policy.Shadow: the decisions are recorded and compared with the AutoPromoter's, the commands are not executed). In the shadow run itself (the engine does not act) every named environment is v1's and recorded as before.
            var shadowed = engineActs && !cfg.TrainActs ? named : [];
            var environments = shadowed.Count > 0 ? [] : named;
            var names = new Dictionary<string, string>(); var urls = new Dictionary<string, string>();
            foreach (var k in DeliveryPolicy.Chain)
            {
                if (S(o?["environments"], k) is { Length: > 0 } n) names[k] = n;
                if (S(o, k + "Url") is { Length: > 0 } u) urls[k] = u;
            }
            double Num(JsonNode? n, string name, double fallback) => n?[name] is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? d : fallback;
            var window = new HealthWindow(Num(o?["health"], "spacingMinutes", 5), Num(o?["health"], "settleMinutes", 10));
            return new DeliveryPolicySettings(project, Promoter.Gateway, environments, names, urls, S(o, "healthPath") is { Length: > 0 } hp ? hp : null, window, Num(ap, "signoffWaitHours", 2), engineActs && cfg.DependenciesDelivered, shadowed);
        }
        catch { return null; }
    }
}

/// <summary>
/// S3 for the shadow run: v1's ingress did its part, and the events of the Octopus watcher are also published to the shadow host, where the router turns them into the trains' inputs. A problem with
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

/// <summary>The comparison tick of the shadow run (a hosted service of AddGatewayBackground): compares and stores every <c>interval</c>, and keeps the ingress deduplication short.</summary>
sealed class DeliveryShadowService(DeliveryShadowHolder holder, TimeSpan interval) : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    DateTime nextHousekeeping = DateTime.UtcNow.AddMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (holder.Shadow is { } shadow) await shadow.Tick(ct);
                // off this loop's thread, and never at the start: a database that does not answer holds a connect for up to its timeout, and the stop of the host must not wait for it
                if (holder.Housekeeping is { } purge && DateTime.UtcNow >= nextHousekeeping)
                {
                    nextHousekeeping = DateTime.UtcNow.AddHours(1);
                    _ = Task.Run(() => { try { purge(); } catch (Exception e) { Console.Error.WriteLine($"delivery shadow housekeeping: {e.GetType().Name}"); } });
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.Error.WriteLine($"delivery shadow tick: {e.GetType().Name}"); }
            catch (Exception) { return; }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

static class DeliveryShadowWiring
{
    public const string TrainQueue = DeliveryTopology.TrainQueue;

    /// <summary>The shadow run's parts for a gateway in shadow with a home repository and a database; null when the environment cannot read GitHub (no GH_TOKEN): the train is left out then.</summary>
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
        var shadow = new DeliveryShadow(policies, new SqlShadowComparisonStore(() => ItemMergedWiring.Open(connectionString)));
        return new DeliveryShadowParts(shadow, policies, reads, new DeliveredToTable(() => ItemMergedWiring.Open(connectionString)), connectionString);
    }

    /// <summary>Adds the train and the shadow's handlers to the host: the router, the train, the recording of its decisions and commands.</summary>
    public static void Attach(WolverineHostService host, DeliveryShadowParts p)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(p.Shadow);
            services.AddSingleton<IDeliveryPolicies>(p.Policies);
            services.AddSingleton<ITrainReads>(p.Reads);
            services.AddSingleton<IDeliveredLookup>(p.Delivered);
            services.AddSingleton<ITrainStore>(new SqlTrainStore(() => ItemMergedWiring.Open(p.ConnectionString)));
            services.AddSingleton<DeliveryTrainService>();
            services.AddSingleton<DeliveryTrainRouter>();
            w.Discovery.IncludeType(typeof(DeliveryTrainHandlers));
            w.Discovery.IncludeType(typeof(DeliveryTrainRouting));
            w.Discovery.IncludeType(typeof(DeliveryShadowHandlers));
            // the Octopus events and the trains' inputs each travel one queue, one at a time and in order (DeliveryTopology says why)
            DeliveryTopology.Route(w);
            // The train keeps a failed read from losing an input (it saves the input first and looks again by itself), so this is the backstop for one that still escapes: GitHub or the app
            // not answering is tried again with growing pauses and then scheduled again every few minutes; it never goes to the dead letters, because an input lost there is lost for good.
            // The same for a train that kept changing under the handler (TrainConflictException: the handler re-reads the state and tries again by itself, five times) and for a database that does not
            // answer (a connection that cannot be opened, a timeout): the input is applied again from the state as it is then, which the train's redelivery rule makes harmless.
            w.Policies.OnException<HttpRequestException>().Or<TaskCanceledException>().Or<TrainConflictException>().Or<TimeoutException>().Or<System.Data.Common.DbException>().RetryWithCooldown(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)).Then.ScheduleRetry(TimeSpan.FromMinutes(5));
        };
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

/// <summary>What the shadow run needs, made once at the start.</summary>
sealed record DeliveryShadowParts(DeliveryShadow Shadow, IDeliveryPolicies Policies, ITrainReads Reads, DeliveredToTable Delivered, string ConnectionString);

/// <summary>The way for the watcher's events into the shadow host; registered only in the shadow run. <see cref="Enabled"/> is false when the train could not be built (no GH_TOKEN): the events then go nowhere.</summary>
sealed class ShadowOctopusPort(Func<object, Task<bool>> publish, bool enabled)
{
    public bool Enabled => enabled;
    public Task<bool> Publish(object ev) => enabled ? publish(ev) : Task.FromResult(false);
}

/// <summary>The shadow run of the delivery train once the host has made it (null before, and where the train could not be built). Registered only in the shadow run.</summary>
sealed class DeliveryShadowHolder
{
    public DeliveryShadow? Shadow { get; set; }
    /// <summary>Hourly housekeeping of the ingress deduplication (the shadow run has no recurring jobs).</summary>
    public Action? Housekeeping { get; set; }
}
