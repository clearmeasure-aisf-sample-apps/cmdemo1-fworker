using System.Text.Json.Nodes;
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
/// W9.10p (#555, owner decision 2026-10-30) in a real Wolverine host: the registration the gateway makes where the engine acts (<c>DeliveryActingWiring.Configure</c>, the same one <c>Attach</c> calls). The train there promotes for tdd
/// only: in uat and prod, which v1's AutoPromoter still promotes, it decides in shadow, so the decision lands in the shadow's comparison (<see cref="DeliveryActingShadowHandlers"/>) and the command it would send leaves as a
/// ShadowCommand, which is recorded; no PromoteRelease or AnswerSignOff reaches the executor queue. The control: the same scenario with uat promoted by the train (W9.12) reaches the executor and records nothing.
/// The host has no database: the train's state is in memory and the queues are not durable (the tests of the SQL parts are DeliveryShadowTrackedTests).
/// </summary>
public sealed class DeliveryActingShadowHostTests
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
        public Task<ItemMergedGates?> ReadGates(ItemId item, CancellationToken ct) => Task.FromResult<ItemMergedGates?>(null);
        public Task<EnvProbe> Probe(string deployable, string environment, CancellationToken ct) => Task.FromResult(new EnvProbe($"https://{environment}.example", Running.GetValueOrDefault(environment), "Healthy", DateTimeOffset.UtcNow));
    }

    /// <summary>The policy source with the short pauses a test can wait for (the tracked tests of the shadow run do the same).</summary>
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
                if (level >= LogLevel.Warning || text.Contains("Train", StringComparison.Ordinal) || text.Contains("Shadow", StringComparison.Ordinal))
                    lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {level} {category.Split('.').Last()}: {text}{(error is null ? "" : " | " + error.GetType().Name + ": " + error.Message)}");
            }
        }
    }

    sealed class Rig : IAsyncDisposable
    {
        public readonly IHost Host;
        public readonly List<string> Log = [];   // (what the host logged about the train's messages, shown when the decision does not arrive)
        public readonly DeliveryShadow Shadow;
        public readonly InMemoryShadowComparisonStore Store = new();
        public int ExecutorCalls;   // each PromoteRelease or AnswerSignOff that reaches the executor asks for the Octopus key once (none is set, so none reaches Octopus)
        readonly Reads reads;

        public Rig(bool shadowed)
        {
            // the policy as DeliveryShadowSettings.From makes it: the engine acts, uat and prod are named by autoPromote; with trainActs off they are shadowed, with it on the train promotes them
            var settings = new DeliveryPolicySettings("orders-project", Promoter.Gateway, shadowed ? [] : ["uat", "prod"],
                new Dictionary<string, string> { ["tdd"] = "Tdd", ["uat"] = "Uat", ["prod"] = "Prod" }, new Dictionary<string, string> { ["tdd"] = "https://tdd.example", ["uat"] = "https://uat.example", ["prod"] = "https://prod.example" },
                null, new HealthWindow(0.01, 0.02), 2, false, shadowed ? ["uat", "prod"] : null);
            var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Repo, null, [new RepositoryEntry(Repo, [new DeployableEntry("orders", Repo, null, null, null, null, false)])], []);
            var policies = new FastPolicies(new CatalogDeliveryPolicies(() => snapshot, n => n == "orders" ? settings : null));
            reads = new Reads();
            Shadow = new DeliveryShadow(policies, Store, () => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            var delivered = new DeliveredToTable(() => throw new InvalidOperationException("no database in this host"));
            var parts = new DeliveryShadowParts(Shadow, policies, reads, delivered, "");
            var octopus = new OctopusPromoter(new HttpClient(), new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string>(), () => { Interlocked.Increment(ref ExecutorCalls); return null; }));
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
        public long Recorded => (long)Shadow.Status()["v2"]!["commandsRecorded"]!;
        public long Decisions => (long)Shadow.Status()["v2"]!["decisions"]!;

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
    public async Task InAShadowEnvironment_TheTrainsDecisionIsComparedWithTheAutoPromoters_ItsCommandIsRecorded_AndNoCommandReachesTheExecutor()
    {
        await using var rig = new Rig(shadowed: true);
        await rig.Start();
        await rig.ReleaseGoesThroughTdd();
        Assert.True(await Until(() => rig.Recorded >= 1), "the train did not decide to promote, or its ShadowCommand was not handled");
        // (the decision and the command are separate messages: the command can be recorded before the shadow has the decision, so the tick below waits for both)
        Assert.True(await Until(() => rig.Decisions >= 1), $"the train's decision did not reach the shadow; its status: {rig.Shadow.Status()["v2"]!.ToJsonString()}; log: {string.Join(" // ", rig.Log.TakeLast(40))}");
        // the AutoPromoter's pass says the same (what AddGatewayBackground's OnActions gives the holder's shadow)
        rig.Shadow.ObserveV1(new JsonArray(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { item = 7, action = "promoted", release = "1.0.2", environment = "Uat", task = "ServerTasks-9", items = new[] { 7 } }))));
        await rig.Shadow.Tick(default);
        var rows = rig.Store.All.Where(x => x.Release == "1.0.2" && x.V2Action == "promote").ToList();
        Assert.True(rows.Count == 1, $"rows: {rows.Count} of {rig.Store.All.Count}; status {rig.Shadow.Status()["v2"]!.ToJsonString()}; log: {string.Join(" // ", rig.Log.TakeLast(40))}");
        var row = rows[0];
        Assert.Equal(ShadowOutcomes.Match, row.Outcome);
        var report = await rig.Shadow.Report(10, null, default);
        var command = (JsonObject)((JsonArray)report["recentCommands"]!)[0]!;
        Assert.Equal("PromoteRelease", (string)command["command"]!);
        Assert.False((bool)command["executed"]!);
        // the executor's queue saw nothing: not now, and not after the retry pause of a command (a second, longer than the train's poll here)
        await Task.Delay(2500);
        Assert.Equal(0, rig.ExecutorCalls);
        Assert.Equal(0L, (long)rig.Shadow.Status()["v2"]!["commandsExecuted"]!);
    }

    [Fact]
    public async Task WhereTheTrainPromotes_TheCommandReachesTheExecutor_AndTheShadowRecordsNothing()
    {
        await using var rig = new Rig(shadowed: false);
        await rig.Start();
        await rig.ReleaseGoesThroughTdd();
        Assert.True(await Until(() => Volatile.Read(ref rig.ExecutorCalls) >= 1), "the PromoteRelease did not reach the executor");
        Assert.Equal(0L, rig.Recorded);
        Assert.Equal(0L, rig.Decisions);   // (a decision of an environment the train promotes is no shadow decision)
    }
}
