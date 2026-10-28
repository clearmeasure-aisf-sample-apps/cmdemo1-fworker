using System.Text.Json;
using Fworker.Contracts.Messaging;
using Fworker.Domain;

namespace Fworker.Workflows.Delivery;

// W5.1: the state of one delivery train, the (deployable, environment) pair: what it has seen of merges and releases, what Octopus shows, what it asked for and what it has told. It is a
// JSON document in the DeliveryTrains table (Fworker.Persistence), saved with a revision so two writers cannot both win. Plain mutable classes: System.Text.Json round-trips them.

public static class TrainKey
{
    /// <summary>"deployable/environment", lower case: the id of the train.</summary>
    public static string Of(string deployable, string environment) => $"{deployable.ToLowerInvariant()}/{environment.ToLowerInvariant()}";

    public static (string Deployable, string Environment) Split(string key)
    {
        var i = key.LastIndexOf('/');
        return (key[..i], key[(i + 1)..]);
    }
}

/// <summary>A release Octopus created: its id and version, the commit it was built from (null until known) and when the train first saw it (the order of releases).</summary>
public sealed class TrainRelease
{
    public string ReleaseId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Commit { get; set; }
    public DateTimeOffset Assembled { get; set; }
}

/// <summary>A merged item waiting to be delivered to the environment: its merge commit and the promotion gates read when it merged (changed later by <see cref="TrainGatesChanged"/>).</summary>
public sealed class TrainCandidate
{
    public string Item { get; set; } = "";
    public string Commit { get; set; } = "";
    public int PullRequest { get; set; }
    public ItemMergedGates Gates { get; set; } = new(false, "", false, false, false, []);
    public List<string> Dependencies { get; set; } = [];
    public DateTimeOffset MergedAt { get; set; }
}

/// <summary>One deployment of a release to an environment as Octopus last showed it.</summary>
public sealed class TrainDeployment
{
    public string DeploymentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    /// <summary>"Started", "Succeeded" or "Failed".</summary>
    public string Phase { get; set; } = "Started";
    /// <summary>When the train saw it start and finish (Octopus events carry no times; the finish time is the health evidence's start).</summary>
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Completed => Phase != "Started";
    public bool Succeeded => Phase == "Succeeded";
}

/// <summary>A Sign-off (manual intervention) a deployment of this environment waits at.</summary>
public sealed class TrainSignOff
{
    public string InterruptionId { get; set; } = "";
    public string DeploymentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public bool Answered { get; set; }
    /// <summary>When the answer went out, and what it was: the train sends it again if the deployment does not move on.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }
    public bool LastProceed { get; set; }
    public string LastNotes { get; set; } = "";
}

/// <summary>The health reads taken of the previous environment for one release, with the last evidence verdict (the clock of the read gap).</summary>
public sealed class TrainEvidence
{
    public List<TrainRead> Reads { get; set; } = [];
    public DateTimeOffset? LastReadAt { get; set; }
    public string LastGap { get; set; } = "";
}

public sealed class TrainRead
{
    public DateTimeOffset At { get; set; }
    public string Status { get; set; } = "";
}

public sealed class TrainState
{
    public string Key { get; set; } = "";
    public string Deployable { get; set; } = "";
    public string Environment { get; set; } = "";
    /// <summary>The releases by version.</summary>
    public Dictionary<string, TrainRelease> Releases { get; set; } = [];
    /// <summary>The merged items not yet delivered to the environment, by qualified item.</summary>
    public Dictionary<string, TrainCandidate> Candidates { get; set; } = [];
    /// <summary>The deployments by version, then environment key (tdd, uat, prod).</summary>
    public Dictionary<string, Dictionary<string, TrainDeployment>> Deployments { get; set; } = [];
    public List<TrainSignOff> SignOffs { get; set; } = [];
    /// <summary>The health evidence by "version|environment".</summary>
    public Dictionary<string, TrainEvidence> Evidence { get; set; } = [];
    /// <summary>The deployments this train asked for ("version"), so a later Sign-off of that release is ours to answer.</summary>
    public Dictionary<string, DateTimeOffset> Requested { get; set; } = [];
    /// <summary>When each release was first asked for: a request Octopus never shows is dropped after <see cref="DeliveryTrainService.RequestExpiry"/>, so it cannot block a newer release.</summary>
    public Dictionary<string, DateTimeOffset> FirstRequested { get; set; } = [];
    /// <summary>"item|commit" of the items already delivered here: a redelivered ItemMerged does not make them candidates again.</summary>
    public List<string> DeliveredItems { get; set; } = [];
    /// <summary>The releases that reached the environment by version and the release that carried it: "delivered in release X, superseding Y".</summary>
    public Dictionary<string, string> Superseded { get; set; } = [];
    /// <summary>The releases whose items were already delivered (the Succeeded deployment was settled).</summary>
    public List<string> Settled { get; set; } = [];
    /// <summary>The newest release that succeeded in this environment (the one the environment runs).</summary>
    public string? Current { get; set; }
    /// <summary>An incident holds the train: nothing is promoted while true.</summary>
    public bool Held { get; set; }
    public string HeldReason { get; set; } = "";
    /// <summary>When the one pending timer is due (null: none). A timer that fires with another due time is stale and clears nothing.</summary>
    public DateTimeOffset? WakeDue { get; set; }
    /// <summary>The fingerprint of the last decision published for each release ("" for those about none): a decision is published once per change.</summary>
    public Dictionary<string, string> Decisions { get; set; } = [];
    /// <summary>Set while the watcher's replay of Octopus's history is being applied: the train takes inputs in and decides nothing (it ends with the replay, or after <see cref="DeliveryTrainService.ReplayLimit"/>).</summary>
    public DateTimeOffset? ReplayingSince { get; set; }
    public long Sequence { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static TrainState FromJson(string json) => JsonSerializer.Deserialize<TrainState>(json, Json) ?? new TrainState();

    public static TrainState New(string key)
    {
        var (d, e) = TrainKey.Split(key);
        return new TrainState { Key = key, Deployable = d, Environment = e };
    }

    public TrainDeployment? DeploymentOf(string version, string envKey)
        => Deployments.TryGetValue(version, out var m) && m.TryGetValue(envKey, out var d) ? d : null;

    /// <summary>Where a release is deployed, as the D3 rule reads it.</summary>
    public IReadOnlyDictionary<string, EnvDeployment> EnvDeployments(string version)
        => Deployments.TryGetValue(version, out var m) ? m.ToDictionary(kv => kv.Key, kv => new EnvDeployment(kv.Value.Phase == "Succeeded" ? "Success" : kv.Value.Phase, kv.Value.Completed)) : new Dictionary<string, EnvDeployment>();

    /// <summary>The release and commit of the newest successful deployment to an environment: what runs there (Get-EnvDeployedCommit). Null when none, or when its release names no commit.</summary>
    public (string Version, string Commit)? RunningIn(string envKey)
    {
        var ok = Deployments.Where(kv => kv.Value.TryGetValue(envKey, out var d) && d.Succeeded)
            .Select(kv => (Version: kv.Key, Dep: kv.Value[envKey])).OrderByDescending(x => x.Dep.FinishedAt ?? x.Dep.StartedAt).ThenByDescending(x => x.Dep.StartedAt).FirstOrDefault();
        if (ok.Version is null) return null;
        return Releases.TryGetValue(ok.Version, out var r) && !string.IsNullOrEmpty(r.Commit) ? (ok.Version, r.Commit!) : null;
    }
}
