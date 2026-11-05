using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10p (#555) in a real Wolverine host: the registration the gateway makes where the engine acts (<c>DeliveryActingWiring.Configure</c>, the same one <c>Attach</c> calls). The train promotes uat and prod: its
/// PromoteRelease and its AnswerSignOff reach the executor. The host has no database: the train's state is in memory and the queues are not durable (the tests of the SQL parts are DeliveryTrainTrackedTests).
/// </summary>
public sealed class DeliveryActingHostTests
{
    static readonly RepoId Repo = new("acme", "orders");
    static string Sha(char c) => new(c, 40);
    static DateTimeOffset Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    sealed class Reads : ITrainReads
    {
        public readonly Dictionary<(string, string), CompareRange> Ranges = [];
        public readonly Dictionary<string, string> Running = [];
        public Task<CompareRange?> Compare(RepoId repository, string deployedCommit, string releaseCommit, CancellationToken ct) => Task.FromResult(Ranges.TryGetValue((deployedCommit, releaseCommit), out var r) ? r : null);
        public Task<CommitItemLookup> LookupCommit(RepoId repository, RangeCommit commit, CancellationToken ct) => Task.FromResult(new CommitItemLookup(0, ""));
        public Task<ItemMergedGates?> ReadGates(ItemId item, CancellationToken ct, DateTimeOffset? freshSince = null) => Task.FromResult<ItemMergedGates?>(null);
        public Task<EnvProbe> Probe(string deployable, string environment, CancellationToken ct) => Task.FromResult(new EnvProbe($"https://{environment}.example", Running.GetValueOrDefault(environment), "Healthy", DateTimeOffset.UtcNow));
    }

    /// <summary>The policy source with the short pauses a test can wait for.</summary>
    sealed class FastPolicies(IDeliveryPolicies inner) : IDeliveryPolicies
    {
        DeliveryPolicy Fast(DeliveryPolicy p) => p with { MinReadGapSeconds = 0, PollSeconds = 1 };
        public DeliveryPolicy? For(string deployable, string environment) => inner.For(deployable, environment) is { } p ? Fast(p) : null;
        public IReadOnlyList<DeliveryPolicy> All() => inner.All().Select(Fast).ToList();
        public IReadOnlyList<DeliveryPolicy> ForOctopusEvents() => inner.ForOctopusEvents().Select(Fast).ToList();
        public IReadOnlyList<DeliveryPolicy> ForRepository(RepoId repository) => inner.ForRepository(repository).Select(Fast).ToList();
    }

    sealed class Capture(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new Sink(lines, category);
        public void Dispose() { }
        sealed class Sink(List<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            {
                var text = format(state, error);
                if (level >= LogLevel.Warning || text.Contains("Train", StringComparison.Ordinal))
                    lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {level} {category.Split('.').Last()}: {text}{(error is null ? "" : " | " + error.GetType().Name + ": " + error.Message)}");
            }
        }
    }

    sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = request.RequestUri!.AbsolutePath switch
            {
                "/api/Spaces-1/projects/orders-project" => (200, """{"Id":"Projects-1"}"""),
                "/api/Spaces-1/projects/Projects-1/releases/1.0.2" => (200, """{"Id":"R2"}"""),
                "/api/Spaces-1/environments" => (200, """{"Items":[]}"""),
                "/api/Spaces-1/releases/R2/deployments" => (200, """{"Items":[]}"""),
                "/api/Spaces-1/deployments/create/untenanted/v1" => (200, """{"DeploymentServerTasks":[{"ServerTaskId":"T-2-Uat"}]}"""),
                _ => (404, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    sealed class Rig : IAsyncDisposable
    {
        public readonly IHost Host;
        public readonly List<string> Log = [];   // (what the host logged about the train's messages, shown when a command does not arrive)
        public int ExecutorCalls;   // each PromoteRelease or AnswerSignOff that reaches the executor asks for the Octopus key once (none is set, so none reaches Octopus)
        readonly Reads reads;

        public Rig()
        {
            // the policy as DeliveryShadowSettings.From makes it: the engine acts, uat and prod are named by autoPromote
            var settings = new DeliveryPolicySettings("orders-project", Promoter.Gateway, ["uat", "prod"],
                new Dictionary<string, string> { ["tdd"] = "Tdd", ["uat"] = "Uat", ["prod"] = "Prod" }, new Dictionary<string, string> { ["tdd"] = "https://tdd.example", ["uat"] = "https://uat.example", ["prod"] = "https://prod.example" },
                null, new HealthWindow(0.01, 0.02), 2, false);
            var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Repo, null, [new RepositoryEntry(Repo, [new DeployableEntry("orders", Repo, null, null, null, null, false)])], []);
            var policies = new FastPolicies(new CatalogDeliveryPolicies(() => snapshot, n => n == "orders" ? settings : null));
            reads = new Reads();
            var delivered = new DeliveredToTable(() => throw new InvalidOperationException("no database in this host"));
            var parts = new DeliveryShadowParts(policies, reads, delivered, "");
            // (the key is counted, and the deployment is created: a promotion the executor is refused is taken back by the train, which then ignores the Sign-off of a deployment it did not ask for)
            var octopus = new OctopusPromoter(new HttpClient(new Answers()) { BaseAddress = new Uri("https://octopus.example/") }, new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string>(), () => { Interlocked.Increment(ref ExecutorCalls); return "k"; }));
            var ledger = new InMemoryIdempotencyLedger();
            var executor = new DeliveryExecutorService(new PromoteReleaseHandler(ledger, octopus), new AnswerSignOffHandler(ledger, octopus));
            Host = new HostBuilder()
                .ConfigureLogging(l => { l.SetMinimumLevel(LogLevel.Debug); l.AddProvider(new Capture(Log)); })
                .UseWolverine(w =>
                {
                    // as the gateway's host (WolverineHostService): no handler is found by convention, Configure names the ones it needs. Without this the host scans the assembly Wolverine takes for the application, which depends on what
                    // earlier tests in the process did, and finds the gateway's SignOffWaitHandler, whose recorder this host does not register: the chain of TrainDecided then fails to compile and the decision is moved to the error queue.
                    w.Discovery.DisableConventionalDiscovery();
                    w.Services.AddSingleton(delivered);   // (the gateway's ItemMergedWiring.Attach registers it)
                    DeliveryActingWiring.Configure(w.Services, w, parts, new InMemoryTrainStore(), executor, durable: false);
                })
                .Build();
        }

        public Task Start() => Host.StartAsync();
        public IMessageBus Bus => Host.Services.GetRequiredService<IMessageBus>();

        /// <summary>Release 1.0.1 runs in every environment; 1.0.2 with item 7 is built, deployed to tdd and succeeds there: the train then decides to promote it to uat.</summary>
        public async Task ReleaseGoesThroughTdd()
        {
            foreach (var key in new[] { "orders/tdd", "orders/uat", "orders/prod" })
            {
                await Bus.PublishAsync(new TrainReleaseSeen(key, "R-1", "1.0.1", Sha('d'), Ago(1)));
                foreach (var env in new[] { "Tdd", "Uat", "Prod" })
                {
                    await Bus.PublishAsync(new TrainDeploymentSeen(key, $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Started", Sha('d'), Ago(1)));
                    await Bus.PublishAsync(new TrainDeploymentSeen(key, $"D-1-{env}", $"T-1-{env}", "R-1", "1.0.1", env, "Succeeded", Sha('d'), Ago(1)));
                }
            }
            reads.Ranges[(Sha('d'), Sha('a'))] = new CompareRange("ahead", [new RangeCommit(Sha('a'), [Sha('d')], null)], 1);
            reads.Running["tdd"] = "1.0.2";
            await Bus.PublishAsync(new ItemMerged(new ItemId(Repo, 7), Sha('a'), 107, new ItemMergedGates(true, "", true, false, false, []), [], "webhook"));
            await Bus.PublishAsync(new OctopusReleaseCreated("R-2", "1.0.2", Sha('a')));
            await Bus.PublishAsync(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Started, Sha('a')));
            await Bus.PublishAsync(new OctopusDeploymentChanged("D-2-Tdd", "T-2-Tdd", "R-2", "1.0.2", "Tdd", OctopusDeploymentPhase.Succeeded, Sha('a')));
        }

        public async ValueTask DisposeAsync() { try { await Host.StopAsync(); } catch { } Host.Dispose(); }
    }

    static async Task<bool> Until(Func<bool> cond, int seconds = 40)
    {
        for (var i = 0; i < seconds * 20; i++) { if (cond()) return true; await Task.Delay(50); }
        return false;
    }

    [Fact]
    public async Task WhereTheTrainPromotes_ASignOffTheWatcherReports_ReachesTheTrain_AndItsAnswerReachesTheExecutor()
    {
        // V2.1: the Octopus watcher's SignOffRequested goes through the router to the train that asked for the deployment, which answers it
        await using var rig = new Rig();
        await rig.Start();
        await rig.ReleaseGoesThroughTdd();
        Assert.True(await Until(() => Volatile.Read(ref rig.ExecutorCalls) >= 1), "the PromoteRelease did not reach the executor");
        await rig.Bus.PublishAsync(new OctopusDeploymentChanged("D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", OctopusDeploymentPhase.Started, Sha('a')));
        await rig.Bus.PublishAsync(new SignOffRequested("I-2", "D-2-Uat", "T-2-Uat", "R-2", "1.0.2", "Uat", Sha('a')));
        Assert.True(await Until(() => Volatile.Read(ref rig.ExecutorCalls) >= 2), $"the AnswerSignOff did not reach the executor; log: {string.Join(" // ", rig.Log.TakeLast(40))}");
    }

    [Fact]
    public async Task WhereTheTrainPromotes_TheCommandReachesTheExecutor()
    {
        await using var rig = new Rig();
        await rig.Start();
        await rig.ReleaseGoesThroughTdd();
        Assert.True(await Until(() => Volatile.Read(ref rig.ExecutorCalls) >= 1), "the PromoteRelease did not reach the executor");
    }
}
