using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// W9.9b: the ports of the adoption run (AdoptionRunner). The pure mapping, the rule of the upsert and the fencing are W9.10m's and W9.10n's; what this file names is what the run reads (GitHub, Octopus) and the stores it writes.
// The reads are the gateway's (gateway/Ports/AdoptionPort.cs over the HTTP readers); a test passes fakes and the in-memory stores.

/// <summary>A Sign-off Octopus shows a deployment waiting at, with the environment key (tdd, uat, prod) of the deployment.</summary>
public sealed record OctopusSignOff(string Environment, PendingSignOff SignOff);

public interface IAdoptionReads
{
    /// <summary>The cards of the board (every repository of the catalog); the run keeps those of the home repository.</summary>
    Task<IReadOnlyList<BoardCard>> Cards(CancellationToken ct);

    /// <summary>The item's issue and state comment, or null when the issue does not exist.</summary>
    Task<V1ItemRecord?> ReadItem(ItemId item, CancellationToken ct);

    /// <summary>The pull request (state, head, merge commit, files when it is open), or null when it does not exist.</summary>
    Task<AdoptionPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct);

    /// <summary>The Sign-offs Octopus shows now, over the environments of the trains; empty where Octopus is not read.</summary>
    Task<IReadOnlyList<OctopusSignOff>> PendingSignOffs(CancellationToken ct);

    /// <summary>The newest pull requests merged into the default branch of the home repository (at most <paramref name="take"/>), newest first; only those after <paramref name="afterCommit"/> when it is given and GitHub can compare.</summary>
    Task<IReadOnlyList<MergedPullRequest>> RecentMergedPulls(int take, string? afterCommit, CancellationToken ct);
}

/// <summary>What the run writes and reads beside the saga rows. Optional parts are null where the gateway has none (no GH_TOKEN: no deriver; no Octopus: no Sign-offs).</summary>
public sealed record AdoptionPorts(
    IAdoptionReads Reads, IWorkItemStore Items, IProjectedSequences Projected, ICapacityStore Capacity, IMergeTrainStore MergeTrains, ITrainStore Trains,
    IQcEnvironmentStore Qc, IMergedItemStore Merged, IDeliveredBackfill Delivered, ItemMergedDeriver? Deriver,
    /// <summary>Publishes a message to the host (the decision, the commands, the ItemMerged for the train); false when the host is not up.</summary>
    Func<object, Task<bool>> Publish,
    /// <summary>Schedules a message for its due time (the timers of an adopted row); false when the host is not up.</summary>
    Func<object, DateTimeOffset, Task<bool>> Schedule);

/// <summary>The versions the projections of the saga's decisions have applied for an item (the table ProjectionVersions): what the rule of the upsert compares the row's sequences with.</summary>
public interface IProjectedSequences
{
    /// <summary>The sequence of the last decision written to the state comment, and of the last that moved the card; 0 for a target never projected.</summary>
    Task<(long Comment, long Card)> Of(string itemKey, CancellationToken ct);

    /// <summary>Brings both targets to the sequence of the adoption's decision (a version only grows): the comment and the card are where the adoption read the item from, so there is nothing of the decision to project.</summary>
    Task Advance(string itemKey, long sequence, DateTimeOffset at, CancellationToken ct);
}

/// <summary>The delivered-to table as the adoption writes it: only where the table has no row for the item and the environment.</summary>
public interface IDeliveredBackfill
{
    /// <summary>True when a row was written.</summary>
    Task<bool> RecordIfAbsent(DeliveredTo e, DateTimeOffset at, CancellationToken ct);
}

public sealed class DeliveredBackfillTable(DeliveredToTable table) : IDeliveredBackfill
{
    public Task<bool> RecordIfAbsent(DeliveredTo e, DateTimeOffset at, CancellationToken ct) => table.RecordIfAbsent(e, at, ct);
}

public sealed class InMemoryDeliveredBackfill : IDeliveredBackfill
{
    readonly Dictionary<(string, string), (string Release, DateTimeOffset At)> rows = [];
    public IReadOnlyDictionary<(string Item, string Environment), (string Release, DateTimeOffset At)> Rows { get { lock (rows) return new Dictionary<(string, string), (string, DateTimeOffset)>(rows); } }

    public Task<bool> RecordIfAbsent(DeliveredTo e, DateTimeOffset at, CancellationToken ct)
    {
        lock (rows) return Task.FromResult(rows.TryAdd((e.Item.ToString(), e.Environment.ToLowerInvariant()), (e.Release, at)));
    }
}

/// <summary>What the run needs of the configuration: the home repository, the mapping's settings, the delivery trains of the home deployables ("deployable/environment"), the environment the acting train delivers to, and the merge queue depth.</summary>
public sealed record AdoptionOptions(RepoId Home, AdoptionSettings Mapping, Func<IReadOnlyList<string>> TrainKeysOf, string TrainEnvironment = "tdd", int MergeQueueDepth = 0)
{
    /// <summary>Cutover review: the gap a restore noted (no gateway was up between Since and Until). An owner's claim whose lease ran out inside it is live, as the sweep's renewal (Invoke-GapRenew, issue 94) would make it, which the adoption now comes before.</summary>
    public Func<(DateTimeOffset Since, DateTimeOffset Until)?>? Gap { get; init; }

    /// <summary>The trains' keys, "deployable/environment", as the catalog names the home repository's deployables now.</summary>
    public IReadOnlyList<string> TrainKeys => TrainKeysOf();

    /// <summary>The merge line of the home repository ("owner/repo", lower case).</summary>
    public string MergeLineKey => Home.ToString().ToLowerInvariant();

    /// <summary>How many of the newest merged pull requests the backfill of the merged items looks at: the queue (merged items waiting for their release) and a few more that a release in flight may still carry.</summary>
    public int MergedWindow => Math.Clamp(MergeQueueDepth + 4, 4, 12);
}
