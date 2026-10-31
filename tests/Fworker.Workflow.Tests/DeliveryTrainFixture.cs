using Fworker.Contracts.Catalog;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Workflows.Delivery;

namespace Fworker.Workflow.Tests;

/// <summary>The fakes and the scenario helpers of the delivery train tests (W5.1): a catalog with one deployable "orders" in acme/orders, fake reads, a clock the test moves.</summary>
sealed class FakeTrainReads : ITrainReads
{
    public readonly Dictionary<(string, string), CompareRange> Ranges = [];
    public readonly Dictionary<string, CommitItemLookup> Lookups = [];
    public readonly Dictionary<string, ItemMergedGates> Gates = [];
    public readonly Dictionary<string, string> Running = [];
    public readonly Dictionary<string, string> Status = [];
    public Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
    public int Probes, Compares;
    /// <summary>When set, every read throws it (GitHub or the app does not answer).</summary>
    public Exception? Fail;
    /// <summary>Release commits whose compare fails (the range of that release alone cannot be read).</summary>
    public readonly HashSet<string> FailCompareOf = [];

    public Task<CompareRange?> Compare(RepoId repository, string deployedCommit, string releaseCommit, CancellationToken ct)
    {
        Compares++;
        if (Fail is not null) throw Fail;
        if (FailCompareOf.Contains(releaseCommit)) throw new HttpRequestException("the range cannot be read");
        return Task.FromResult(Ranges.TryGetValue((deployedCommit, releaseCommit), out var r) ? r : null);
    }

    public Task<CommitItemLookup> LookupCommit(RepoId repository, RangeCommit commit, CancellationToken ct)
        => Task.FromResult(Lookups.TryGetValue(commit.Sha, out var l) ? l : new CommitItemLookup(0, ""));

    public Task<ItemMergedGates?> ReadGates(ItemId item, CancellationToken ct) => Task.FromResult(Gates.TryGetValue(item.ToString(), out var g) ? g : null);

    public Task<EnvProbe> Probe(string deployable, string environment, CancellationToken ct)
    {
        Probes++;
        if (Fail is not null) throw Fail;
        return Task.FromResult(new EnvProbe($"https://{environment}.example", Running.GetValueOrDefault(environment), Status.GetValueOrDefault(environment, "Healthy"), Clock()));
    }
}

sealed class FakeDelivered : IDeliveredLookup
{
    public readonly HashSet<(string Item, string Env)> Rows = [];
    /// <summary>The items a merge was recorded for (the merged-items table).</summary>
    public readonly HashSet<string> Merged = [];
    /// <summary>When set, every lookup throws it (the database does not answer).</summary>
    public Exception? Fail;
    public int Lookups;
    public Task<IReadOnlyList<ItemId>> NotYetDelivered(IEnumerable<ItemId> dependencies, string environment, CancellationToken ct = default)
    {
        Lookups++;
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<ItemId>>(dependencies.Where(d => !Rows.Contains((d.ToString(), environment))).ToList());
    }

    public Task<IReadOnlyList<ItemId>> NotYetMerged(IEnumerable<ItemId> items, CancellationToken ct = default)
    {
        Lookups++;
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<ItemId>>(items.Where(d => !Merged.Contains(d.ToString())).ToList());
    }
}

sealed class TrainFixture
{
    public static readonly RepoId Repo = new("acme", "orders");
    /// <summary>A second repository with a deployable of its own (the fixture adds it with web: true); any other repository has none.</summary>
    public static readonly RepoId Web = new("acme", "web");
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    public static string Sha(char c) => new(c, 40);

    public readonly InMemoryTrainStore Store = new();
    public readonly FakeTrainReads Reads = new();
    public readonly FakeDelivered Delivered = new();
    public readonly string Env;
    public readonly DeliveryPolicies Policies;
    public readonly DeliveryTrainService Svc;
    public DateTimeOffset Now = T0;
    public readonly List<object> All = [];
    public string Key => TrainKey.Of("orders", Env);

    public TrainFixture(string env = "uat", Promoter promoter = Promoter.Gateway, string[]? environments = null, bool dependencies = false, double signoffHours = 2, HealthWindow? window = null, int minReadGapSeconds = 30, int pollSeconds = 120, bool web = false, bool shadow = false, IMigrationProbe? migrations = null, string[]? migrationPaths = null)
    {
        Env = env;
        // shadow (W9.10p): the engine acts but v1's AutoPromoter still promotes uat and prod, which the train decides without executing
        var settings = new DeliveryPolicySettings("orders-project", promoter, shadow ? [] : environments ?? ["uat", "prod"],
            new Dictionary<string, string> { ["tdd"] = "Tdd", ["uat"] = "Uat", ["prod"] = "Prod" },
            new Dictionary<string, string> { ["tdd"] = "https://tdd.example", ["uat"] = "https://uat.example", ["prod"] = "https://prod.example" },
            null, window, signoffHours, dependencies, shadow ? environments ?? ["uat", "prod"] : null, migrationPaths);
        var repositories = new List<RepositoryEntry> { new(Repo, [new DeployableEntry("orders", Repo, null, null, null, null, false)]) };
        if (web) repositories.Add(new(Web, [new DeployableEntry("web", Web, null, null, null, null, false)]));
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, T0, "acme", Repo, null, repositories, []);
        Policies = new DeliveryPolicies(new CatalogDeliveryPolicies(() => snapshot, n => n == "orders" ? settings : null), minReadGapSeconds, pollSeconds);
        Reads.Clock = () => Now;
        Svc = new DeliveryTrainService(Store, Policies, Reads, Delivered, () => Now, migrations);
        // the baseline: release 1.0.1 (commit d) runs in every environment
        Reads.Running["tdd"] = Reads.Running["uat"] = Reads.Running["prod"] = "1.0.1";
    }

    public async Task<TrainResult> Send(TrainInput input)
    {
        var r = await Svc.Handle(input);
        All.AddRange(r.Messages);
        return r;
    }

    public Task<TrainResult> Wake(DateTimeOffset? due = null) => Send(new TrainWake(Key, due));
    public IEnumerable<T> Of<T>() => All.OfType<T>();
    public TrainDecided? LastDecision => All.OfType<TrainDecided>().LastOrDefault();
    public TrainState State => Store.States[Key];

    public ItemMerged Merged(int n, char commit, string[]? gaps = null, string hold = "", string[]? dependencies = null)
        => new(new ItemId(Repo, n), Sha(commit), 100 + n, new ItemMergedGates(gaps is null, hold, gaps is null, false, false, gaps ?? []), (dependencies ?? []).Select(ItemId.Parse).ToList(), "webhook");

    public Task<TrainResult> Merge(int n, char commit, string[]? gaps = null, string[]? dependencies = null) => Send(new TrainCandidateAdded(Key, Merged(n, commit, gaps, dependencies: dependencies)));
    public Task<TrainResult> Release(string version, char commit) => Send(new TrainReleaseSeen(Key, "Releases-" + version, version, Sha(commit), Now));
    public Task<TrainResult> Deploy(string version, string environment, string phase, string? id = null, char? commit = null)
        => Send(new TrainDeploymentSeen(Key, id ?? $"D-{version}-{environment}", $"T-{version}-{environment}", "Releases-" + version, version, environment, phase, commit is null ? null : Sha(commit.Value), Now));
    public Task<TrainResult> SignOff(string version, string environment) => Send(new TrainSignOffAsked(Key, $"I-{version}", $"D-{version}-{environment}", $"T-{version}-{environment}", "Releases-" + version, version, environment, null, Now));

    /// <summary>Baseline: release 1.0.1 built from commit d, deployed to every environment, finished a day ago.</summary>
    public async Task Baseline()
    {
        var was = Now; Now = T0.AddDays(-1);
        await Release("1.0.1", 'd');
        foreach (var e in new[] { "Tdd", "Uat", "Prod" }) { await Deploy("1.0.1", e, "Started"); await Deploy("1.0.1", e, "Succeeded"); }
        Now = was; All.Clear();
    }

    /// <summary>The compare of deployed...release GitHub would answer: the commits after <paramref name="deployed"/> up to the last of <paramref name="shas"/>, first parent chain.</summary>
    public void Range(char deployed, params char[] shas)
    {
        var commits = new List<RangeCommit>(); var parent = Sha(deployed);
        foreach (var c in shas) { commits.Add(new RangeCommit(Sha(c), [parent], null)); parent = Sha(c); }
        Reads.Ranges[(Sha(deployed), Sha(shas[^1]))] = new CompareRange("ahead", commits, commits.Count);
    }

    /// <summary>A release in tdd and the reads of its health: built from <paramref name="commit"/>, tdd deployment finished at the current time (the health window starts there).</summary>
    public async Task ReleaseInPrevious(string version, char commit, string previous = "Tdd")
    {
        await Release(version, commit);
        await Deploy(version, previous, "Started");
        await Deploy(version, previous, "Succeeded");
    }

    /// <summary>The evidence of the previous environment matures: reads 5 and 10 minutes after the first.</summary>
    public async Task Mature() { await After(TimeSpan.FromMinutes(5)); await After(TimeSpan.FromMinutes(5)); }

    /// <summary>The timer a result asked for fires: the clock moves to its due time and the wake is the message the handler's Outgoing builds for it (stamped, or unstamped for a transient retry).</summary>
    public Task<TrainResult> Fire(TrainResult result) { Now = result.WakeAt!.Value; return Send(DeliveryTrainHandlers.WakeFor(result, Key)!); }

    /// <summary>Moves the clock and wakes the train, as the timer does.</summary>
    public Task<TrainResult> After(TimeSpan span) { Now += span; return Wake(); }
}

/// <summary>Adapts the real policy source: the minimum read gap is a train setting, here the test's.</summary>
sealed class DeliveryPolicies(IDeliveryPolicies inner, int minReadGapSeconds, int pollSeconds = 120) : IDeliveryPolicies
{
    DeliveryPolicy Gap(DeliveryPolicy p) => p with { MinReadGapSeconds = minReadGapSeconds, PollSeconds = pollSeconds };
    public DeliveryPolicy? For(string deployable, string environment) => inner.For(deployable, environment) is { } p ? Gap(p) : null;
    public IReadOnlyList<DeliveryPolicy> All() => inner.All().Select(Gap).ToList();
    public IReadOnlyList<DeliveryPolicy> ForOctopusEvents() => inner.ForOctopusEvents().Select(Gap).ToList();
    public IReadOnlyList<DeliveryPolicy> ForRepository(RepoId repository) => inner.ForRepository(repository).Select(Gap).ToList();
}
